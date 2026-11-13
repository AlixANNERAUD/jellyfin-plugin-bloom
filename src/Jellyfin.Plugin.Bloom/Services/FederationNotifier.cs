using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Models;
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
/// </summary>
public sealed class FederationNotifier : IHostedService, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMinutes(2);

    private readonly ILibraryManager _libraryManager;
    private readonly FederationStore _store;
    private readonly CatalogService _catalog;
    private readonly PeerClient _client;
    private readonly SyncService _sync;
    private readonly ILogger<FederationNotifier> _logger;
    private readonly Timer _timer;
    private int _dirty;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationNotifier"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="store">Store.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="client">Peer client.</param>
    /// <param name="sync">Sync service.</param>
    /// <param name="logger">Logger.</param>
    public FederationNotifier(ILibraryManager libraryManager, FederationStore store, CatalogService catalog, PeerClient client, SyncService sync, ILogger<FederationNotifier> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _catalog = catalog;
        _client = client;
        _sync = sync;
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
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemChanged;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();

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
        // (or come back if we deleted ours).
        foreach (var peer in state.Peers.Where(p => p.Enabled && !p.RevokedByPeer))
        {
            _sync.QueueSync(peer.Id, TimeSpan.FromSeconds(30));
        }

        if (!state.Settings.NotifyPeersOnChange)
        {
            return;
        }

        foreach (var peer in state.Peers.Where(p => p.Enabled && !p.RevokedByPeer && p.SharedLibraryIds.Count > 0))
        {
            try
            {
                await _client.SendJsonAsync<object>(PeerEndpoint.From(peer), HttpMethod.Post, "/Bloom/Peer/Notify", new NotifyRequest { Reason = "library-changed" }, CancellationToken.None).ConfigureAwait(false);
                _logger.LogDebug("Bloom: told {Peer} our catalog changed", peer.Name);
            }
            catch (PeerRequestException ex)
            {
                _logger.LogDebug("Bloom: cannot notify {Peer}: {Message}", peer.Name, ex.Message);
            }
        }
    }
}
