using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Bloom.Crypto;
using Jellyfin.Plugin.Bloom.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Provider side: decides what a friend may see and builds the catalog of our shared libraries.
/// </summary>
public sealed class CatalogService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly ImageType[] ExportedImages = [ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb, ImageType.Banner];

    private readonly ILibraryManager _libraryManager;
    private readonly FederationStore _store;
    private readonly ILogger<CatalogService> _logger;
    private readonly ConcurrentDictionary<Guid, CachedLibrary> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="store">Store.</param>
    /// <param name="logger">Logger.</param>
    public CatalogService(ILibraryManager libraryManager, FederationStore store, ILogger<CatalogService> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Maps a Jellyfin collection type to the federated one.
    /// </summary>
    /// <param name="type">Collection type.</param>
    /// <returns>"movies", "tvshows" or null when not shareable.</returns>
    public static string? ToFederatedType(CollectionType? type) => type switch
    {
        CollectionType.movies => "movies",
        CollectionType.tvshows => "tvshows",
        _ => null
    };

    /// <summary>
    /// Drops cached catalogs (called when the library changes).
    /// </summary>
    public void Invalidate() => _cache.Clear();

    /// <summary>
    /// Whether a path lies inside the folder where we materialize friends' items.
    /// Such items are never re-shared: federation is strictly friend-to-friend, not transitive.
    /// </summary>
    /// <param name="path">Path.</param>
    /// <returns>Whether it is federated content.</returns>
    public bool IsFederatedPath(string? path)
        => !string.IsNullOrEmpty(path) && path.StartsWith(MediaRootPrefix(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// The friend a federated item comes from: its files live in <c>&lt;media root&gt;/&lt;friend id&gt;/</c>.
    /// </summary>
    /// <param name="path">Path of the item.</param>
    /// <returns>The friend's server id, or null for our own items.</returns>
    public string? PeerIdOf(string? path)
    {
        if (!IsFederatedPath(path))
        {
            return null;
        }

        var rest = path![MediaRootPrefix().Length..];
        var end = rest.IndexOf(Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return end > 0 ? rest[..end] : null;
    }

    private string MediaRootPrefix() => _store.GetMediaRoot().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>
    /// Lists the shareable libraries of this server.
    /// </summary>
    /// <returns>Libraries.</returns>
    public IReadOnlyList<CatalogLibrary> GetLocalShareableLibraries()
    {
        var result = new List<CatalogLibrary>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var id))
            {
                continue;
            }

            var type = folder.CollectionType switch
            {
                CollectionTypeOptions.movies => "movies",
                CollectionTypeOptions.tvshows => "tvshows",
                _ => null
            };

            // A library whose only folders are federated ones is a friend's library: not ours to share.
            if (type is null || (folder.Locations.Length > 0 && folder.Locations.All(IsFederatedPath)))
            {
                continue;
            }

            result.Add(new CatalogLibrary { Id = id.ToString("N"), Name = folder.Name, CollectionType = type });
        }

        return result;
    }

    /// <summary>
    /// Lists the libraries a friend may see.
    /// </summary>
    /// <param name="peer">Friend.</param>
    /// <returns>Libraries.</returns>
    public List<CatalogLibrary> GetSharedLibraries(PeerRecord peer)
    {
        var result = new List<CatalogLibrary>();
        foreach (var id in peer.SharedLibraryIds.Distinct())
        {
            if (_libraryManager.GetItemById(id) is not CollectionFolder folder)
            {
                continue;
            }

            var type = ToFederatedType(folder.CollectionType);
            if (type is null)
            {
                continue;
            }

            result.Add(new CatalogLibrary
            {
                Id = id.ToString("N"),
                Name = folder.Name,
                CollectionType = type,
                ItemCount = GetLibrary(folder, type).Items.Count
            });
        }

        return result;
    }

    /// <summary>
    /// Returns a page of a shared library, with stream tokens issued for this friend.
    /// </summary>
    /// <param name="peer">Friend.</param>
    /// <param name="libraryId">Library id.</param>
    /// <param name="start">Start index.</param>
    /// <param name="limit">Page size.</param>
    /// <returns>The page, or null if the library is not shared with this friend.</returns>
    public CatalogPage? GetPage(PeerRecord peer, Guid libraryId, int start, int limit)
    {
        if (!peer.SharedLibraryIds.Contains(libraryId) || _libraryManager.GetItemById(libraryId) is not CollectionFolder folder)
        {
            return null;
        }

        var type = ToFederatedType(folder.CollectionType);
        if (type is null)
        {
            return null;
        }

        var library = GetLibrary(folder, type);
        start = Math.Max(0, start);
        limit = Math.Clamp(limit, 1, 1000);
        var items = library.Items.Skip(start).Take(limit).Select(entry =>
        {
            var copy = entry.Item;
            if (entry.Playable)
            {
                copy = entry.Item.ShallowCopy();
                copy.StreamToken = StreamTokens.Compute(peer.StreamSecret, entry.Id);
            }

            return copy;
        }).ToList();

        return new CatalogPage
        {
            LibraryId = libraryId.ToString("N"),
            Start = start,
            Total = library.Items.Count,
            Version = library.Version,
            Items = items
        };
    }

    /// <summary>
    /// Whether a friend may access an item (stream, image, subtitle).
    /// </summary>
    /// <param name="peer">Friend.</param>
    /// <param name="item">Item.</param>
    /// <returns>Whether access is allowed.</returns>
    public bool IsItemShared(PeerRecord peer, BaseItem item)
    {
        if (item.IsVirtualItem || IsFederatedPath(item.Path) || item.IsShortcut)
        {
            return false;
        }

        var series = item switch
        {
            Episode e => e.Series,
            Season s => s.Series,
            _ => null
        };
        if (series is not null && IsFederatedPath(series.Path))
        {
            return false;
        }

        return _libraryManager.GetCollectionFolders(item).Any(f => peer.SharedLibraryIds.Contains(f.Id));
    }

    private static string? FormatDate(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private CachedLibrary GetLibrary(CollectionFolder folder, string type)
    {
        if (_cache.TryGetValue(folder.Id, out var cached) && DateTime.UtcNow - cached.BuiltAt < CacheTtl)
        {
            return cached;
        }

        var built = BuildLibrary(folder, type);
        _cache[folder.Id] = built;
        return built;
    }

    private CachedLibrary BuildLibrary(CollectionFolder folder, string type)
    {
        var started = DateTime.UtcNow;
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = type == "movies"
                ? [BaseItemKind.Movie]
                : [BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true)
        };

        var items = _libraryManager.GetItemList(query, [folder]);
        var entries = new List<CatalogEntry>(items.Count);
        var seriesIds = new HashSet<Guid>(items.OfType<Series>().Where(s => !IsFederatedPath(s.Path)).Select(s => s.Id));

        foreach (var item in items)
        {
            if (IsFederatedPath(item.Path) || item.IsShortcut)
            {
                continue;
            }

            var entry = item switch
            {
                Movie movie => ToEntry(movie, "Movie"),
                Series series => ToEntry(series, "Series"),
                Season season when seriesIds.Contains(season.SeriesId) => ToEntry(season, "Season"),
                Episode episode when seriesIds.Contains(episode.SeriesId) => ToEntry(episode, "Episode"),
                _ => null
            };

            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        // Stable order: parents first, then by id. Makes paging predictable.
        entries.Sort((a, b) =>
        {
            var byType = TypeRank(a.Item.Type).CompareTo(TypeRank(b.Item.Type));
            return byType != 0 ? byType : a.Id.CompareTo(b.Id);
        });

        _logger.LogDebug("Bloom: catalog of {Library} built ({Count} items, {Ms} ms)", folder.Name, entries.Count, (DateTime.UtcNow - started).TotalMilliseconds);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            hash.AppendData(entry.Id.ToByteArray());
        }

        return new CachedLibrary(DateTime.UtcNow, Convert.ToHexStringLower(hash.GetHashAndReset(), 0, 12), entries);
    }

    private static int TypeRank(string type) => type switch
    {
        "Series" => 0,
        "Season" => 1,
        _ => 2
    };

    private CatalogEntry ToEntry(BaseItem item, string type)
    {
        var ci = new CatalogItem
        {
            Id = item.Id.ToString("N"),
            Type = type,
            Name = item.Name ?? string.Empty,
            OriginalTitle = item.OriginalTitle,
            SortName = item.ForcedSortName,
            Year = item.ProductionYear,
            PremiereDate = FormatDate(item.PremiereDate),
            EndDate = FormatDate(item.EndDate),
            Overview = item.Overview,
            Tagline = item.Tagline,
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            CriticRating = item.CriticRating,
            RunTimeTicks = item.RunTimeTicks,
            Genres = item.Genres?.ToList() ?? [],
            Studios = item.Studios?.ToList() ?? [],
            Tags = item.Tags?.ToList() ?? [],
            ProviderIds = item.ProviderIds is null ? [] : new Dictionary<string, string>(item.ProviderIds.Where(p => !string.IsNullOrWhiteSpace(p.Value))),
        };

        foreach (var imageType in ExportedImages)
        {
            var info = item.GetImageInfo(imageType, 0);
            if (info is null || !info.IsLocalFile || string.IsNullOrEmpty(info.Path))
            {
                continue;
            }

            var ext = Path.GetExtension(info.Path).ToLowerInvariant();
            ci.Images.Add(new CatalogImage
            {
                Type = imageType.ToString(),
                Tag = info.DateModified.Ticks.ToString(CultureInfo.InvariantCulture),
                Ext = ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" ? ext : ".jpg"
            });
        }

        var playable = false;
        switch (item)
        {
            case Series series:
                ci.Status = series.Status?.ToString();
                AddPeople(ci, item, 25);
                break;
            case Season season:
                ci.SeriesId = season.SeriesId.ToString("N");
                ci.IndexNumber = season.IndexNumber;
                break;
            case Episode episode:
                ci.SeriesId = episode.SeriesId.ToString("N");
                ci.IndexNumber = episode.IndexNumber;
                ci.IndexNumberEnd = episode.IndexNumberEnd;
                ci.ParentIndexNumber = episode.ParentIndexNumber;
                playable = AddVideoInfo(ci, episode);
                break;
            case Movie movie:
                AddPeople(ci, item, 25);
                playable = AddVideoInfo(ci, movie);
                break;
        }

        return new CatalogEntry(item.Id, ci, playable);
    }

    private bool AddVideoInfo(CatalogItem ci, Video video)
    {
        // Only single-file videos can be streamed over plain HTTP (no DVD/Blu-ray folder rips).
        if (video.VideoType != VideoType.VideoFile || !video.IsFileProtocol || string.IsNullOrEmpty(video.Path))
        {
            return false;
        }

        ci.Container = video.Container;
        ci.Size = video.Size;
        if (ci.Size is null or <= 0)
        {
            try
            {
                ci.Size = new FileInfo(video.Path).Length;
            }
            catch (IOException)
            {
                ci.Size = null;
            }
        }

        var videoStream = video.GetDefaultVideoStream();
        ci.Width = videoStream?.Width;
        ci.Height = videoStream?.Height;
        ci.VideoRange = videoStream is null ? null : videoStream.VideoRangeType.ToString();
        foreach (var stream in video.GetMediaStreams())
        {
            if (stream.Type != MediaStreamType.Subtitle || !stream.IsExternal || string.IsNullOrEmpty(stream.Path))
            {
                continue;
            }

            var ext = Path.GetExtension(stream.Path).ToLowerInvariant();
            if (ext is not (".srt" or ".ass" or ".ssa" or ".vtt" or ".sub" or ".smi" or ".sup"))
            {
                continue;
            }

            ci.Subtitles.Add(new CatalogSubtitle
            {
                Index = stream.Index,
                Language = stream.Language,
                Ext = ext,
                Forced = stream.IsForced,
                HearingImpaired = stream.IsHearingImpaired
            });
        }

        return true;
    }

    private void AddPeople(CatalogItem ci, BaseItem item, int max)
    {
        try
        {
            foreach (var person in _libraryManager.GetPeople(item).Take(max))
            {
                if (!string.IsNullOrWhiteSpace(person.Name))
                {
                    ci.People.Add(new CatalogPerson { Name = person.Name, Role = person.Role, Type = person.Type.ToString() });
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "Bloom: no people for {Item}", item.Name);
        }
    }

    private sealed record CatalogEntry(Guid Id, CatalogItem Item, bool Playable);

    private sealed record CachedLibrary(DateTime BuiltAt, string Version, List<CatalogEntry> Items);
}
