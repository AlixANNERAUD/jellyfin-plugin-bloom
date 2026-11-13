using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Bloom.Crypto;
using Jellyfin.Plugin.Bloom.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Consumer side: pulls friends' catalogs and materializes them in our libraries.
/// </summary>
public sealed class SyncService : IDisposable
{
    private const int PageSize = 500;
    private const int MaxItemsPerLibrary = 250_000;
    private const long MaxImageBytes = 25L * 1024 * 1024;
    private const long MaxSubtitleBytes = 10L * 1024 * 1024;

    private static readonly TimeSpan SyncDeadline = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    private readonly FederationStore _store;
    private readonly PeerClient _client;
    private readonly LibraryIntegration _libraries;
    private readonly CatalogService _catalog;
    private readonly ILibraryManager _libraryManager;
    private readonly ServerIdentity _identity;
    private readonly ILogger<SyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _queued = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncService"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="client">Peer client.</param>
    /// <param name="libraries">Library integration.</param>
    /// <param name="catalog">Catalog service.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="identity">Our identity.</param>
    /// <param name="logger">Logger.</param>
    public SyncService(
        FederationStore store,
        PeerClient client,
        LibraryIntegration libraries,
        CatalogService catalog,
        ILibraryManager libraryManager,
        ServerIdentity identity,
        ILogger<SyncService> logger)
    {
        _store = store;
        _client = client;
        _libraries = libraries;
        _catalog = catalog;
        _libraryManager = libraryManager;
        _identity = identity;
        _logger = logger;
    }

    /// <summary>
    /// Whether a sync of this friend is running or queued.
    /// </summary>
    /// <param name="peerId">Server id.</param>
    /// <returns>Whether it is busy.</returns>
    public bool IsBusy(string peerId) => _running.ContainsKey(peerId) || _queued.ContainsKey(peerId);

    /// <summary>
    /// Schedules a sync of a friend after a delay; repeated calls are coalesced.
    /// </summary>
    /// <param name="peerId">Server id.</param>
    /// <param name="delay">Delay.</param>
    public void QueueSync(string peerId, TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        if (_queued.TryRemove(peerId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        _queued[peerId] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                _queued.TryRemove(new KeyValuePair<string, CancellationTokenSource>(peerId, cts));
                await SyncPeerAsync(peerId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bloom: background sync of {Peer} failed", peerId);
            }
        });
    }

    /// <summary>
    /// Syncs every enabled friend, except those whose server is asleep: they are synced when they say they are up.
    /// </summary>
    /// <param name="progress">Progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncAllAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var active = _store.Snapshot().Peers.Where(p => p.Enabled && !p.RevokedByPeer).ToList();
        foreach (var asleep in active.Where(p => p.IsAsleep(now)))
        {
            _logger.LogInformation("Bloom: {Peer} sleeps when idle, it will be synced when it wakes up", asleep.Name);
        }

        // What we own is the same for every friend (their items never count as ours): index it once for the
        // whole run. A failed attempt is not kept, the next friend tries again.
        var owned = new Lazy<OwnedIndex>(BuildOwnedIndex, LazyThreadSafetyMode.PublicationOnly);
        var peers = active.Where(p => !p.IsAsleep(now)).Select(p => p.Id).ToList();
        for (var i = 0; i < peers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncPeerAsync(peers[i], owned, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bloom: sync of {Peer} failed", peers[i]);
            }

            progress?.Report(100.0 * (i + 1) / peers.Count);
        }

        progress?.Report(100);
    }

    /// <summary>
    /// Syncs one friend: refreshes the list of shared libraries, writes items, attaches folders.
    /// Never throws for a friend-side problem: the error is stored on the friend and returned.
    /// </summary>
    /// <param name="peerId">Server id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null on success, or the error message.</returns>
    public Task<string?> SyncPeerAsync(string peerId, CancellationToken cancellationToken)
        => SyncPeerAsync(peerId, null, cancellationToken);

    private async Task<string?> SyncPeerAsync(string peerId, Lazy<OwnedIndex>? owned, CancellationToken cancellationToken)
    {
        // What a sync writes (tags in NFO files, the error shown on the friend's card) is in the server's language,
        // even when the admin page (in another language) started it: the page's language flows into this task.
        Text.UseRequestLanguage(null);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // The deadline counts from here: waiting behind another friend's sync does not use it up.
        cts.CancelAfter(SyncDeadline);
        _running[peerId] = cts;
        var run = new SyncRun();
        try
        {
            var peer = _store.FindPeer(peerId);
            if (peer is null)
            {
                return Text.Get("UnknownFriend");
            }

            if (!peer.Enabled || peer.RevokedByPeer)
            {
                await RemoveImportedContentAsync(peer).ConfigureAwait(false);
                return null;
            }

            string? error = null;
            try
            {
                await SyncPeerCoreAsync(peer, run, owned, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                error = Text.Get("SyncInterrupted");
            }
            catch (Exception ex) when (ex is PeerRequestException or FederationException)
            {
                error = ex.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Includes malformed catalogs from the friend: never let them break other friends' syncs.
                _logger.LogError(ex, "Bloom: sync with {Peer} failed", peer.Name);
                error = ex.Message;
            }
            finally
            {
                // Persist what was done even on failure (e.g. a library created before the error).
                _store.UpdatePeer(peerId, p =>
                {
                    MergeImports(p, peer.Imports, _libraries.Exists);
                    p.Sleeps = peer.Sleeps;
                    if (!string.IsNullOrWhiteSpace(peer.Name))
                    {
                        p.Name = peer.Name;
                    }
                });
            }

            if (error is null)
            {
                _store.UpdatePeer(peerId, p =>
                {
                    p.LastSync = DateTime.UtcNow;
                    p.LastSeen = DateTime.UtcNow;
                    p.LastError = null;
                });
            }
            else
            {
                _logger.LogWarning("Bloom: sync with {Peer} failed: {Message}", peer.Name, error);
                _store.UpdatePeer(peerId, p => p.LastError = error);
            }

            return error;
        }
        finally
        {
            _running.TryRemove(peerId, out _);
            _gate.Release();
            if (run.Changed)
            {
                _logger.LogInformation("Bloom: content of {Peer} changed, scanning libraries", peerId);
                _libraryManager.QueueLibraryScan();
            }
        }
    }

    /// <summary>
    /// Forgets a friend: interrupts its sync, removes everything imported from it (folders and
    /// library attachments) and deletes it from the store, using its latest state.
    /// </summary>
    /// <param name="peerId">Server id.</param>
    /// <returns>A task.</returns>
    public async Task RemovePeerAsync(string peerId)
    {
        if (_queued.TryRemove(peerId, out var queued))
        {
            queued.Cancel();
            queued.Dispose();
        }

        if (_running.TryGetValue(peerId, out var running))
        {
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var peer = _store.FindPeer(peerId);
            if (peer is null)
            {
                return;
            }

            await RemoveImportedContentAsync(peer).ConfigureAwait(false);
            _store.Update(s => s.Peers.RemoveAll(p => p.Id == peerId));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves friends' items to another folder: removes them from the current one (and from the libraries), applies
    /// the new folder, then imports them again. Their files cannot simply be moved: Jellyfin knows them by path.
    /// </summary>
    /// <param name="applyNewRoot">Saves the new folder setting.</param>
    /// <returns>A task.</returns>
    public async Task MoveMediaRootAsync(Action applyNewRoot)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var peer in _store.Snapshot().Peers)
            {
                await RemoveImportedContentAsync(peer).ConfigureAwait(false);
            }

            applyNewRoot();
            _catalog.Invalidate();
        }
        finally
        {
            _gate.Release();
        }

        // Asked for by the admin: friends that sleep are woken up too, or their items would be missing until then.
        foreach (var peer in _store.Snapshot().Peers.Where(p => p.Enabled && !p.RevokedByPeer))
        {
            QueueSync(peer.Id, TimeSpan.FromSeconds(2));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        foreach (var cts in _queued.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>
    /// Copies what a sync computed into the stored friend, keeping the import choices
    /// (mode, target) the admin may have changed while the sync was running.
    /// </summary>
    private static void MergeImports(PeerRecord stored, List<ImportRecord> synced, Func<string?, bool> libraryExists)
    {
        stored.Imports.RemoveAll(i => synced.All(s => s.RemoteLibraryId != i.RemoteLibraryId));
        foreach (var s in synced)
        {
            var current = stored.Imports.Find(i => i.RemoteLibraryId == s.RemoteLibraryId);
            if (current is null)
            {
                stored.Imports.Add(s);
                continue;
            }

            current.RemoteLibraryName = s.RemoteLibraryName;
            current.CollectionType = s.CollectionType;
            current.ImportedCount = s.ImportedCount;
            current.SkippedCount = s.SkippedCount;
            current.VersionCount = s.VersionCount;

            // What exists on the server, whatever the admin chose meanwhile (next sync reconciles).
            current.CreatedLibrary = s.CreatedLibrary;

            // Unless the admin picked one meanwhile, or the one stored was renamed (the sync found its new name).
            if (current.TargetLibrary is null || !libraryExists(current.TargetLibrary))
            {
                current.TargetLibrary = s.TargetLibrary;
            }
        }
    }

    private async Task RemoveImportedContentAsync(PeerRecord peer)
    {
        var changed = false;
        foreach (var import in peer.Imports)
        {
            changed |= await _libraries.DetachAsync(import, LibraryDir(peer.Id, import.RemoteLibraryId)).ConfigureAwait(false);
        }

        var peerDir = PeerDir(peer.Id);
        if (Directory.Exists(peerDir))
        {
            Directory.Delete(peerDir, true);
            changed = true;
        }

        _store.DeleteSyncState(peer.Id);
        _store.UpdatePeer(peer.Id, p =>
        {
            foreach (var import in p.Imports)
            {
                import.CreatedLibrary = null;
                import.ImportedCount = 0;
                import.SkippedCount = 0;
                import.VersionCount = 0;
            }
        });

        if (changed)
        {
            _libraryManager.QueueLibraryScan();
        }
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);

    private string PeerDir(string peerId) => Path.Combine(_store.GetMediaRoot(), peerId);

    private string LibraryDir(string peerId, string remoteLibraryId) => Path.Combine(PeerDir(peerId), new string(remoteLibraryId.Where(char.IsLetterOrDigit).ToArray()));

    private async Task SyncPeerCoreAsync(PeerRecord peer, SyncRun run, Lazy<OwnedIndex>? sharedOwned, CancellationToken cancellationToken)
    {
        var endpoint = PeerEndpoint.From(peer);
        var settings = _store.Snapshot().Settings;
        var hello = new HelloRequest { Name = _store.GetDisplayName(), Url = settings.PublicUrl ?? string.Empty, Sleeps = settings.SleepsWhenIdle };
        var libraries = await _client.SendJsonAsync<CatalogLibrariesResponse>(endpoint, HttpMethod.Post, "/Bloom/Peer/Libraries", hello, cancellationToken).ConfigureAwait(false);
        peer.Sleeps = libraries.Sleeps;
        libraries.Libraries = libraries.Libraries
            .Where(l => MediaWriter.IsSafeId(l.Id) && l.CollectionType is "movies" or "tvshows")
            .ToList();
        if (!string.IsNullOrWhiteSpace(libraries.ServerName))
        {
            peer.Name = libraries.ServerName.Trim();
        }

        // Libraries the friend stopped sharing.
        foreach (var gone in peer.Imports.Where(i => libraries.Libraries.All(l => l.Id != i.RemoteLibraryId)).ToList())
        {
            var dir = LibraryDir(peer.Id, gone.RemoteLibraryId);
            run.Changed |= await _libraries.DetachAsync(gone, dir).ConfigureAwait(false);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
                run.Changed = true;
            }

            peer.Imports.Remove(gone);
        }

        // New or renamed libraries.
        foreach (var library in libraries.Libraries)
        {
            var import = peer.Imports.Find(i => i.RemoteLibraryId == library.Id);
            if (import is null)
            {
                var target = _libraries.DefaultMergeTarget(library.CollectionType, _catalog.IsFederatedPath);
                import = new ImportRecord
                {
                    RemoteLibraryId = library.Id,
                    Mode = target is null ? ImportMode.Separate : ImportMode.Merge,
                    TargetLibrary = target
                };
                peer.Imports.Add(import);
            }

            import.RemoteLibraryName = library.Name;
            import.CollectionType = library.CollectionType;
        }

        var owned = settings.SkipOwnedItems ? sharedOwned?.Value ?? BuildOwnedIndex() : null;
        var syncState = _store.LoadSyncState(peer.Id);
        FederationException? failure = null;
        try
        {
            foreach (var import in peer.Imports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = LibraryDir(peer.Id, import.RemoteLibraryId);
                if (import.Mode == ImportMode.Ignore || (import.Mode == ImportMode.Merge && string.IsNullOrEmpty(import.TargetLibrary)))
                {
                    run.Changed |= await _libraries.DetachAsync(import, dir).ConfigureAwait(false);
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                        run.Changed = true;
                    }

                    foreach (var key in syncState.Files.Keys.Where(k => k.StartsWith(import.RemoteLibraryId + "/", StringComparison.Ordinal)).ToList())
                    {
                        syncState.Files.Remove(key);
                    }

                    import.ImportedCount = 0;
                    import.SkippedCount = 0;
                    import.VersionCount = 0;
                    continue;
                }

                var items = await FetchCatalogAsync(endpoint, import.RemoteLibraryId, cancellationToken).ConfigureAwait(false);
                var writer = new MediaWriter(
                    dir,
                    import.RemoteLibraryId + "/",
                    syncState,
                    (file, path, ct) => DownloadAsync(endpoint, file, path, ct),
                    _logger)
                {
                    Badge = settings.BadgePosters ? peer.Name : null,
                    OfferVersions = settings.GroupVersions
                };
                var result = await writer.WriteAsync(
                    import.CollectionType,
                    items,
                    item => StreamUrl(peer, item),
                    peer.Name,
                    owned,
                    cancellationToken).ConfigureAwait(false);

                import.ImportedCount = result.Imported;
                import.SkippedCount = result.Skipped;
                import.VersionCount = result.Versions;
                run.Changed |= result.Changed;
                try
                {
                    run.Changed |= await _libraries.EnsureAttachedAsync(import, peer.Name, dir).ConfigureAwait(false);
                }
                catch (FederationException ex)
                {
                    // One library gone (deleted target): still sync the friend's other libraries.
                    _logger.LogWarning("Bloom: {Peer}/{Library}: {Message}", peer.Name, import.RemoteLibraryName, ex.Message);
                    failure ??= ex;
                    continue;
                }

                _logger.LogInformation(
                    "Bloom: {Peer}/{Library}: {Imported} items, {Skipped} already owned",
                    peer.Name,
                    import.RemoteLibraryName,
                    result.Imported,
                    result.Skipped);
            }
        }
        finally
        {
            _store.SaveSyncState(peer.Id, syncState);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private string StreamUrl(PeerRecord peer, CatalogItem item)
        => PeerClient.Combine(peer.Url, $"/Bloom/Peer/Stream/{Encode(item.Id)}?p={_identity.ServerId}&t={Encode(item.StreamToken ?? string.Empty)}");

    private async Task<List<CatalogItem>> FetchCatalogAsync(PeerEndpoint endpoint, string libraryId, CancellationToken cancellationToken)
    {
        // Pages must all come from the same version of the friend's catalog, otherwise items that moved
        // between pages during a scan on its side would be missed (and then deleted here).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var items = new List<CatalogItem>();
            string? version = null;
            var start = 0;
            var consistent = true;
            while (true)
            {
                var path = string.Format(CultureInfo.InvariantCulture, "/Bloom/Peer/Catalog?library={0}&start={1}&limit={2}", Encode(libraryId), start, PageSize);
                var page = await _client.SendJsonAsync<CatalogPage>(endpoint, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
                if (page.Start != start || page.Total < 0 || page.Total > MaxItemsPerLibrary || page.Items is null)
                {
                    throw new PeerRequestException(Text.Get("InvalidCatalog"));
                }

                version ??= page.Version;
                if (!string.Equals(version, page.Version, StringComparison.Ordinal))
                {
                    consistent = false;
                    break;
                }

                items.AddRange(page.Items.Where(i => i is not null));
                start += PageSize;
                if (start >= page.Total || page.Items.Count == 0)
                {
                    break;
                }
            }

            if (consistent)
            {
                return items.GroupBy(i => i.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
            }

            await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
        }

        throw new PeerRequestException(Text.Get("CatalogChanging"));
    }

    private async Task<bool> DownloadAsync(PeerEndpoint endpoint, RemoteFile file, string destination, CancellationToken cancellationToken)
    {
        var path = file.Kind.StartsWith("sub:", StringComparison.Ordinal)
            ? $"/Bloom/Peer/Subtitle/{Encode(file.ItemId)}/{Encode(file.Kind[4..])}"
            : $"/Bloom/Peer/Image/{Encode(file.ItemId)}/{Encode(file.Kind)}";
        var maxBytes = file.Kind.StartsWith("sub:", StringComparison.Ordinal) ? MaxSubtitleBytes : MaxImageBytes;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        var tmp = destination + ".part";
        try
        {
            using var response = await _client.GetRawAsync(endpoint, path, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Bloom: {Path} returned {Status}", path, response.StatusCode);
                return false;
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return false;
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var output = File.Create(tmp);
                await using (output.ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > maxBytes)
                        {
                            throw new IOException("File too large.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }
                }
            }

            File.Move(tmp, destination, true);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Bloom: download of {Path} timed out", path);
            TryDelete(tmp);
            return false;
        }
        catch (PeerRequestException ex)
        {
            _logger.LogDebug(ex, "Bloom: cannot download {Path}", path);
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Bloom: cannot download {Path}", path);
            TryDelete(tmp);
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Bloom: cannot download {Path}", path);
            TryDelete(tmp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private OwnedIndex BuildOwnedIndex()
    {
        var index = new OwnedIndex();
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,

            // Every version of a movie we have counts (a 4K and a 1080p file are two versions of one movie).
            IncludeAlternateVersions = true,

            // Jellyfin only loads provider ids when asked to.
            DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds], EnableImages = false, EnableUserData = false }
        });

        var seriesIds = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        foreach (var series in items.OfType<Series>())
        {
            if (!_catalog.IsFederatedPath(series.Path))
            {
                seriesIds[series.Id] = series.ProviderIds;
                index.AddSeries(series.ProviderIds);
            }
        }

        foreach (var item in items)
        {
            if (_catalog.IsFederatedPath(item.Path))
            {
                continue;
            }

            switch (item)
            {
                case MediaBrowser.Controller.Entities.Movies.Movie movie:
                    index.AddMovie(movie.ProviderIds, FileSize(movie));
                    break;
                case Episode episode when episode.ParentIndexNumber is int season && episode.IndexNumber is int number
                    && seriesIds.TryGetValue(episode.SeriesId, out var seriesProviderIds):
                    index.AddEpisode(seriesProviderIds, season, number, episode.IndexNumberEnd, FileSize(episode));
                    break;
            }
        }

        return index;
    }

    private static long? FileSize(BaseItem item)
    {
        if (item.Size is > 0)
        {
            return item.Size;
        }

        try
        {
            return item.IsFileProtocol && File.Exists(item.Path) ? new FileInfo(item.Path).Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>What a sync did, kept even when it fails halfway.</summary>
    private sealed class SyncRun
    {
        public bool Changed { get; set; }
    }
}
