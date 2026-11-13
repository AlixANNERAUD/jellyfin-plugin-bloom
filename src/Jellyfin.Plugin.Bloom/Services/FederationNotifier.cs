using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Watches our libraries and tells friends when what we share changed, so new movies
/// show up on their side within minutes instead of at the next scheduled sync.
/// Also says hello to friends when Jellyfin starts: both sides sync while we are up, which is how
/// a server that sleeps when idle catches up (friends never wake it on their own).
/// </summary>
public sealed class FederationNotifier : IHostedService, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxStartupWait = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraryManager;
    private readonly FederationStore _store;
    private readonly CatalogService _catalog;
    private readonly PeerClient _client;
    private readonly SyncService _sync;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<FederationNotifier> _logger;
    private readonly Timer _timer;
    private readonly CancellationTokenSource _stopping = new();
    private int _dirty;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationNotifier"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="store">Store.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="client">Peer client.</param>
    /// <param name="sync">Sync service.</param>
    /// <param name="appHost">Server host (tells when startup is complete).</param>
    /// <param name="logger">Logger.</param>
    public FederationNotifier(ILibraryManager libraryManager, FederationStore store, CatalogService catalog, PeerClient client, SyncService sync, IServerApplicationHost appHost, ILogger<FederationNotifier> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _catalog = catalog;
        _client = client;
        _sync = sync;
        _appHost = appHost;
        _logger = logger;
        _timer = new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Only additions/removals: metadata edits reach friends at the periodic sync. This also rules out
        // notification ping-pong between two servers refreshing each other.
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;
        _ = Task.Run(() => AnnounceStartupAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemChanged;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Dispose();
        _stopping.Dispose();
    }

    /// <summary>
    /// Friends that can be contacted without waking their server up.
    /// </summary>
    /// <param name="state">State.</param>
    /// <returns>Enabled friends that are not asleep.</returns>
    private static List<PeerRecord> AwakePeers(FederationState state)
    {
        var now = DateTime.UtcNow;
        return state.Peers.Where(p => p.Enabled && !p.RevokedByPeer && !p.IsAsleep(now)).ToList();
    }

    private async Task AnnounceStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Until startup is complete Jellyfin answers 503 to everything, so friends could not sync from us yet.
            var waited = Stopwatch.StartNew();
            while (!_appHost.CoreStartupHasCompleted && waited.Elapsed < MaxStartupWait)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }

            var peers = AwakePeers(_store.Snapshot());
            if (peers.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Bloom: started, saying hello to {Count} friend(s)", peers.Count);
            foreach (var peer in peers)
            {
                _sync.QueueSync(peer.Id, TimeSpan.FromSeconds(10));
            }

            await NotifyAsync(peers, "started", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bloom: hello to friends failed");
        }
    }

    private Task NotifyAsync(IEnumerable<PeerRecord> peers, string reason, CancellationToken cancellationToken)
    {
        var request = new NotifyRequest { Reason = reason, Sleeps = _store.Snapshot().Settings.SleepsWhenIdle };

        // In parallel: a friend that takes time to answer (starting up) does not hold the others back.
        return Task.WhenAll(peers.Select(async peer =>
        {
            try
            {
                await _client.SendJsonAsync<object>(PeerEndpoint.From(peer), HttpMethod.Post, "/Bloom/Peer/Notify", request, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Bloom: told {Peer} ({Reason})", peer.Name, reason);
            }
            catch (PeerRequestException ex)
            {
                _logger.LogDebug("Bloom: cannot notify {Peer}: {Message}", peer.Name, ex.Message);
            }
        }));
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not (Movie or Series or Season or Episode) || _catalog.IsFederatedPath(e.Item.Path))
        {
            return;
        }

        _catalog.Invalidate();
        Interlocked.Exchange(ref _dirty, 1);
        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    private async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        var state = _store.Snapshot();

        // What we own changed: re-sync friends so their copies of what we now have disappear
        // (or come back if we deleted ours). Friends that are asleep catch up when they wake up.
        var peers = AwakePeers(state);
        foreach (var peer in peers)
        {
            _sync.QueueSync(peer.Id, TimeSpan.FromSeconds(30));
        }

        if (!state.Settings.NotifyPeersOnChange)
        {
            return;
        }

        await NotifyAsync(peers.Where(p => p.SharedLibraryIds.Count > 0), "library-changed", CancellationToken.None).ConfigureAwait(false);
    }
}
