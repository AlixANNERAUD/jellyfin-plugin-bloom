using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// After each library scan, groups the copies of a movie or an episode (ours and our friends') into one item with
/// several versions, using Jellyfin's own "versions" feature: clients show a version picker, ours plays by default.
/// </summary>
public sealed class VersionLinker : ILibraryPostScanTask
{
    // Instances come from both the DI container and Jellyfin's post-scan discovery: share the lock.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly ILibraryManager _libraryManager;
    private readonly CatalogService _catalog;
    private readonly FederationStore _store;
    private readonly ILogger<VersionLinker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionLinker"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="catalog">Catalog service.</param>
    /// <param name="store">Federation store.</param>
    /// <param name="logger">Logger.</param>
    public VersionLinker(ILibraryManager libraryManager, CatalogService catalog, FederationStore store, ILogger<VersionLinker> logger)
    {
        _libraryManager = libraryManager;
        _catalog = catalog;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken) => LinkAsync(cancellationToken);

    /// <summary>
    /// Brings the versions of every movie library in line with the plan.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task LinkAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var enabled = _store.Snapshot().Settings.GroupVersions;
            foreach (var info in _libraryManager.GetVirtualFolders())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Guid.TryParse(info.ItemId, out var id)
                    && _libraryManager.GetItemById(id) is CollectionFolder folder
                    && CatalogService.ToFederatedType(folder.CollectionType) is string type)
                {
                    try
                    {
                        await LinkLibraryAsync(folder, type, enabled, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Bloom: could not group the versions of {Library}", folder.Name);
                    }
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static DtoOptions Light(bool providerIds) => new(false)
    {
        Fields = providerIds ? [ItemFields.ProviderIds] : [],
        EnableImages = false,
        EnableUserData = false
    };

    private List<Video> Query(CollectionFolder folder, BaseItemKind kind, bool providerIds) => _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [kind],
                Recursive = true,
                IsVirtualItem = false,
                IncludeAlternateVersions = true,
                DtoOptions = Light(providerIds)
            },
            [folder])
        .OfType<Video>()
        .DistinctBy(v => v.Id)
        .ToList();

    /// <summary>
    /// What identifies the same episode everywhere: any provider id of its series, plus its numbers.
    /// </summary>
    private Dictionary<Guid, List<string>> EpisodeKeys(CollectionFolder folder, List<Video> episodes)
    {
        var seriesKeys = _libraryManager.GetItemList(
                new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series], Recursive = true, IsVirtualItem = false, DtoOptions = Light(true) },
                [folder])
            .DistinctBy(s => s.Id)
            .ToDictionary(s => s.Id, s => OwnedIndex.Keys(s.ProviderIds).ToList());

        return episodes.ToDictionary(v => v.Id, v =>
        {
            if (v is not Episode { ParentIndexNumber: int season, IndexNumber: int number } episode
                || !seriesKeys.TryGetValue(episode.SeriesId, out var keys))
            {
                return new List<string>();
            }

            var episodeKey = OwnedIndex.EpisodeKey(season, number)
                + (episode.IndexNumberEnd is int end && end > number ? "-" + end.ToString(CultureInfo.InvariantCulture) : string.Empty);
            return keys.Select(k => k + "/" + episodeKey).ToList();
        });
    }

    private async Task LinkLibraryAsync(CollectionFolder folder, string type, bool enabled, CancellationToken cancellationToken)
    {
        var movies = type == "movies" ? Query(folder, BaseItemKind.Movie, true) : Query(folder, BaseItemKind.Episode, false);
        if (!movies.Any(m => _catalog.IsFederatedPath(m.Path)))
        {
            return;
        }

        var keys = type == "movies"
            ? movies.ToDictionary(m => m.Id, m => OwnedIndex.Keys(m.ProviderIds).ToList())
            : EpisodeKeys(folder, movies);
        var keyCounts = keys.Values.SelectMany(k => k).GroupBy(k => k, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var candidates = movies.Select(m => new VersionCandidate(
                m.Id,
                _catalog.IsFederatedPath(m.Path),
                keys[m.Id],

                // Reading the video stream costs a query: only for videos that have another copy.
                keys[m.Id].Any(k => keyCounts[k] > 1) ? m.GetDefaultVideoStream()?.Width ?? 0 : 0,
                m.PrimaryVersionId))
            .ToList();
        var plan = VersionPlanner.Plan(candidates, enabled);

        var light = movies.ToDictionary(m => m.Id);

        // The list above is partial (no images, locked fields...): saving it would erase them.
        // Only full items, loaded one by one, are edited and saved.
        var full = new Dictionary<Guid, Video>();
        Video? Full(Guid id)
        {
            if (!full.TryGetValue(id, out var video))
            {
                if (_libraryManager.GetItemById(id) is not Video loaded)
                {
                    return null;
                }

                video = loaded;
                full[id] = video;
            }

            return video;
        }

        var changed = new HashSet<Guid>();
        var moved = new List<(Guid From, Guid To)>();
        foreach (var (copyId, target) in plan)
        {
            var current = light[copyId].PrimaryVersionId;
            if (current != target)
            {
                if (current is Guid previous && Full(previous) is Video old)
                {
                    old.LinkedAlternateVersions = old.LinkedAlternateVersions.Where(l => l.ItemId != copyId).ToArray();
                    changed.Add(previous);
                }

                if (Full(copyId) is not Video copy)
                {
                    continue;
                }

                copy.SetPrimaryVersionId(target);
                changed.Add(copyId);
                if (target is Guid to)
                {
                    moved.Add((copyId, to));
                }
            }

            if (target is Guid primaryId
                && !light[primaryId].LinkedAlternateVersions.Any(l => l.ItemId == copyId)
                && Full(primaryId) is Video primary
                && !primary.LinkedAlternateVersions.Any(l => l.ItemId == copyId))
            {
                primary.LinkedAlternateVersions = [.. primary.LinkedAlternateVersions, new LinkedChild { ItemId = copyId, Type = LinkedChildType.LinkedAlternateVersion }];
                changed.Add(primaryId);
            }

            // A friend's copy lives in another folder of the library, so Jellyfin only hides it behind the movie it is a
            // version of by grouping them. "Recently added" keeps the newest of the group instead: a friend's copy must
            // never be newer than the movie it belongs to, or it would show up there instead of it.
            if (target is Guid groupId
                && light[copyId].DateCreated >= light[groupId].DateCreated
                && Full(copyId) is Video version
                && Full(groupId) is Video owner
                && version.DateCreated >= owner.DateCreated)
            {
                version.DateCreated = owner.DateCreated.AddSeconds(-1);
                changed.Add(copyId);
            }
        }

        // Links to a friend's copy that no longer belong (plan changed, version detached by hand...).
        bool Belongs(LinkedChild link, Guid owner) => link.ItemId is not Guid id || !plan.TryGetValue(id, out var target) || target == owner;
        foreach (var movie in movies.Where(m => m.LinkedAlternateVersions.Any(l => !Belongs(l, m.Id))))
        {
            if (Full(movie.Id) is Video owner)
            {
                owner.LinkedAlternateVersions = owner.LinkedAlternateVersions.Where(l => Belongs(l, owner.Id)).ToArray();
                changed.Add(owner.Id);
            }
        }

        // Versions first, then the movies they belong to (Jellyfin re-attaches the listed versions on save).
        foreach (var item in changed.Select(id => full[id]).OrderBy(v => v.PrimaryVersionId is null ? 1 : 0))
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        // Playlists and collections that pointed to a friend's copy now point to the movie it is a version of.
        foreach (var (from, to) in moved)
        {
            await _libraryManager.RerouteLinkedChildReferencesAsync(from, to).ConfigureAwait(false);
        }

        if (changed.Count > 0)
        {
            _logger.LogInformation(
                "Bloom: {Library}: {Grouped} friend's copies grouped as versions, {Updated} items updated",
                folder.Name,
                plan.Count(p => p.Value is not null),
                changed.Count);
        }
    }
}
