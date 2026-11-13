using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>How a friend's movie relates to what we already have.</summary>
public enum OwnedMatch
{
    /// <summary>We don't have it.</summary>
    None,

    /// <summary>We have exactly this file (same size to the byte): ours is preferred.</summary>
    SameFile,

    /// <summary>We have the movie, in another version (quality, cut...).</summary>
    OtherVersion
}

/// <summary>
/// What we already own locally, to avoid showing a friend's copy of the same movie/episode.
/// Keys are provider ids such as "tmdb:27205" or "imdb:tt1375666".
/// </summary>
public sealed class OwnedIndex
{
    private static readonly string[] KeyProviders = ["tmdb", "imdb", "tvdb"];

    private readonly Dictionary<string, HashSet<long>> _movies = new(StringComparer.OrdinalIgnoreCase);
    // Series key → episode key ("s1e2") → sizes of our files holding that episode.
    private readonly Dictionary<string, Dictionary<string, HashSet<long>>> _episodesBySeries = new(StringComparer.OrdinalIgnoreCase);

    // "series key/episode key" held by one of our multi-episode files (never offered as a version).
    private readonly HashSet<string> _multiEpisodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the comparable keys of a set of provider ids.
    /// </summary>
    /// <param name="providerIds">Provider ids.</param>
    /// <returns>Keys.</returns>
    public static IEnumerable<string> Keys(IReadOnlyDictionary<string, string>? providerIds)
    {
        if (providerIds is null)
        {
            yield break;
        }

        foreach (var (provider, id) in providerIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && KeyProviders.Contains(provider.ToLowerInvariant()))
            {
                yield return provider.ToLowerInvariant() + ":" + id.Trim();
            }
        }
    }

    /// <summary>
    /// Builds the key of an episode inside a series.
    /// </summary>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>The key.</returns>
    public static string EpisodeKey(int season, int episode) => string.Create(CultureInfo.InvariantCulture, $"s{season}e{episode}");

    /// <summary>
    /// Records a movie file we own.
    /// </summary>
    /// <param name="providerIds">Its provider ids.</param>
    /// <param name="size">Its size in bytes, if known.</param>
    public void AddMovie(IReadOnlyDictionary<string, string>? providerIds, long? size = null)
    {
        foreach (var key in Keys(providerIds))
        {
            if (!_movies.TryGetValue(key, out var sizes))
            {
                sizes = [];
                _movies[key] = sizes;
            }

            if (size is > 0)
            {
                sizes.Add(size.Value);
            }
        }
    }

    /// <summary>
    /// Records a series we own (even with no episode).
    /// </summary>
    /// <param name="providerIds">Its provider ids.</param>
    public void AddSeries(IReadOnlyDictionary<string, string>? providerIds)
    {
        foreach (var key in Keys(providerIds))
        {
            if (!_episodesBySeries.ContainsKey(key))
            {
                _episodesBySeries[key] = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Records an episode we own.
    /// </summary>
    /// <param name="seriesProviderIds">Provider ids of its series.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">First episode number.</param>
    /// <param name="episodeEnd">Last episode number (multi-episode files).</param>
    /// <param name="size">Size of the file in bytes, if known.</param>
    public void AddEpisode(IReadOnlyDictionary<string, string>? seriesProviderIds, int season, int episode, int? episodeEnd, long? size = null)
    {
        var last = Math.Max(episode, episodeEnd ?? episode);
        foreach (var key in Keys(seriesProviderIds))
        {
            if (!_episodesBySeries.TryGetValue(key, out var episodes))
            {
                episodes = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
                _episodesBySeries[key] = episodes;
            }

            for (var e = episode; e <= last; e++)
            {
                var episodeKey = EpisodeKey(season, e);
                if (!episodes.TryGetValue(episodeKey, out var sizes))
                {
                    sizes = [];
                    episodes[episodeKey] = sizes;
                }

                if (size is > 0)
                {
                    sizes.Add(size.Value);
                }

                if (last > episode)
                {
                    _multiEpisodes.Add(key + "/" + episodeKey);
                }
            }
        }
    }

    /// <summary>
    /// Whether we own this movie.
    /// </summary>
    /// <param name="movie">Friend's movie.</param>
    /// <returns>Whether it is a duplicate.</returns>
    public bool OwnsMovie(CatalogItem movie) => MatchMovie(movie) != OwnedMatch.None;

    /// <summary>
    /// Compares a friend's movie with ours.
    /// </summary>
    /// <param name="movie">Friend's movie.</param>
    /// <returns>Whether we have it, as the same file or as another version.</returns>
    public OwnedMatch MatchMovie(CatalogItem movie)
    {
        var owned = false;
        foreach (var key in Keys(movie.ProviderIds))
        {
            if (_movies.TryGetValue(key, out var sizes))
            {
                owned = true;

                // Unknown size (older friend): play it safe and keep ours only.
                if (movie.Size is not > 0 || sizes.Contains(movie.Size.Value))
                {
                    return OwnedMatch.SameFile;
                }
            }
        }

        return owned ? OwnedMatch.OtherVersion : OwnedMatch.None;
    }

    /// <summary>
    /// Whether we own this series (at least partly).
    /// </summary>
    /// <param name="series">Friend's series.</param>
    /// <returns>Whether we have it.</returns>
    public bool OwnsSeries(CatalogItem series) => Keys(series.ProviderIds).Any(_episodesBySeries.ContainsKey);

    /// <summary>
    /// Whether we own this episode.
    /// </summary>
    /// <param name="series">Friend's series.</param>
    /// <param name="episode">Friend's episode.</param>
    /// <returns>Whether it is a duplicate.</returns>
    public bool OwnsEpisode(CatalogItem series, CatalogItem episode) => MatchEpisode(series, episode) != OwnedMatch.None;

    /// <summary>
    /// Compares a friend's episode with ours.
    /// </summary>
    /// <param name="series">Friend's series.</param>
    /// <param name="episode">Friend's episode.</param>
    /// <returns>Whether we have it, as the same file or as another version.</returns>
    public OwnedMatch MatchEpisode(CatalogItem series, CatalogItem episode)
    {
        if (episode.ParentIndexNumber is not int season || episode.IndexNumber is not int number)
        {
            return OwnedMatch.None;
        }

        var key = EpisodeKey(season, number);
        var friendMulti = episode.IndexNumberEnd is int end && end > number;
        var owned = false;
        foreach (var seriesKey in Keys(series.ProviderIds))
        {
            if (_episodesBySeries.TryGetValue(seriesKey, out var episodes) && episodes.TryGetValue(key, out var sizes))
            {
                owned = true;

                // Multi-episode files do not map one to one: keep ours only. Same for an unknown size.
                if (friendMulti || _multiEpisodes.Contains(seriesKey + "/" + key) || episode.Size is not > 0 || sizes.Contains(episode.Size.Value))
                {
                    return OwnedMatch.SameFile;
                }
            }
        }

        return owned ? OwnedMatch.OtherVersion : OwnedMatch.None;
    }
}

/// <summary>A file to fetch from the friend.</summary>
/// <param name="ItemId">Remote item id.</param>
/// <param name="Kind">Image type ("Primary"...) or "sub:{index}".</param>
/// <param name="Tag">Version tag.</param>
/// <param name="Badge">Friend name to draw on the image, if any.</param>
public sealed record RemoteFile(string ItemId, string Kind, string Tag, string? Badge = null)
{
    /// <summary>Gets what identifies this exact file in the sync state (changes when it must be fetched again).</summary>
    public string StateTag => Badge is null ? $"{Kind}:{Tag}" : $"{Kind}:{Tag}#badge:{Badge}";
}

/// <summary>Outcome of writing one library.</summary>
public sealed class LibraryWriteResult
{
    /// <summary>Gets or sets a value indicating whether files changed.</summary>
    public bool Changed { get; set; }

    /// <summary>Gets or sets the number of playable items written.</summary>
    public int Imported { get; set; }

    /// <summary>Gets or sets the number of items skipped because we own them.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets the number of written movies or episodes that are another version of one we own.</summary>
    public int Versions { get; set; }
}

/// <summary>
/// Materializes a friend's library as a regular Jellyfin folder: one .strm (URL of the friend's stream)
/// plus .nfo, artwork and subtitles per item. Jellyfin then indexes it like any local media.
/// </summary>
public sealed partial class MediaWriter
{
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".smi", ".sup"];

    private static readonly Dictionary<string, string> MovieImageNames = new(StringComparer.Ordinal)
    {
        ["Primary"] = "poster",
        ["Backdrop"] = "fanart",
        ["Logo"] = "logo",
        ["Thumb"] = "landscape",
        ["Banner"] = "banner"
    };

    private readonly string _libDir;
    private readonly string _statePrefix;
    private readonly PeerSyncState _state;
    private readonly Func<RemoteFile, string, CancellationToken, Task<bool>> _download;
    private readonly ILogger _logger;
    private readonly object _stateLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaWriter"/> class.
    /// </summary>
    /// <param name="libDir">Folder of this library (exclusively ours).</param>
    /// <param name="statePrefix">Prefix of this library in the sync state.</param>
    /// <param name="state">Sync state of the friend.</param>
    /// <param name="download">Downloads a remote file to a path; returns false if unavailable.</param>
    /// <param name="logger">Logger.</param>
    public MediaWriter(string libDir, string statePrefix, PeerSyncState state, Func<RemoteFile, string, CancellationToken, Task<bool>> download, ILogger logger)
    {
        _libDir = libDir;
        _statePrefix = statePrefix;
        _state = state;
        _download = download;
        _logger = logger;
    }

    /// <summary>
    /// Gets the friend name drawn on posters and episode thumbnails (null: posters are left as is).
    /// </summary>
    public string? Badge { get; init; }

    /// <summary>
    /// Gets a value indicating whether a movie or episode we own in another version is imported (and later grouped with ours).
    /// </summary>
    public bool OfferVersions { get; init; }

    /// <summary>
    /// Names a friend's version of a movie, as shown in the version picker: "Alice 4K HDR".
    /// </summary>
    /// <param name="peerName">Friend name.</param>
    /// <param name="item">The friend's movie or episode.</param>
    /// <returns>The label.</returns>
    public static string VersionLabel(string peerName, CatalogItem item)
    {
        var width = item.Width ?? 0;
        var height = item.Height ?? 0;
        var resolution = width >= 3200 || height >= 1900 ? "4K"
            : width >= 1800 || height >= 1000 ? "1080p"
            : width >= 1200 || height >= 700 ? "720p"
            : width > 0 || height > 0 ? "SD"
            : null;
        var range = item.VideoRange switch
        {
            null or "" or "SDR" or "Unknown" or "DOVIWithSDR" or "DOVIInvalid" => null,
            var r when r.StartsWith("DOVI", StringComparison.Ordinal) => "DV",
            "HDR10Plus" => "HDR10+",
            _ => "HDR"
        };

        return SafeName(string.Join(' ', new[] { peerName, resolution, range }.Where(t => !string.IsNullOrWhiteSpace(t))));
    }

    /// <summary>
    /// Makes a string safe as a file name on every OS.
    /// </summary>
    /// <param name="name">Name.</param>
    /// <returns>Safe name.</returns>
    public static string SafeName(string? name)
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? string.Empty)
        {
            sb.Append(char.IsControl(c) || Array.IndexOf(InvalidChars, c) >= 0 ? ' ' : c);
        }

        var cleaned = Spaces().Replace(sb.ToString(), " ").Trim().TrimEnd('.').Trim();
        if (cleaned.Length > 100)
        {
            cleaned = cleaned[..100].Trim();
        }

        return cleaned.Length == 0 ? "Sans titre" : cleaned;
    }

    /// <summary>
    /// Writes the library and removes what disappeared.
    /// </summary>
    /// <param name="collectionType">"movies" or "tvshows".</param>
    /// <param name="items">All catalog items of the library.</param>
    /// <param name="streamUrl">Builds the stream URL of a playable item.</param>
    /// <param name="peerName">Friend name.</param>
    /// <param name="owned">What we own (null: import everything).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<LibraryWriteResult> WriteAsync(
        string collectionType,
        IReadOnlyList<CatalogItem> items,
        Func<CatalogItem, string> streamUrl,
        string peerName,
        OwnedIndex? owned,
        CancellationToken cancellationToken)
    {
        var result = new LibraryWriteResult();
        var desired = new Dictionary<string, object>(StringComparer.Ordinal);

        // Everything below comes from a friend's server: never let it pick paths.
        items = items.Where(i => i is not null && IsSafeId(i.Id) && (i.SeriesId is null || IsSafeId(i.SeriesId))).ToList();
        foreach (var item in items)
        {
            item.Name ??= string.Empty;
            item.Images = item.Images?.Where(x => x is not null && x.Type is not null && x.Tag is not null).ToList() ?? [];
            item.Subtitles = item.Subtitles?.Where(x => x is not null).ToList() ?? [];
            item.People = item.People?.Where(x => x is not null && x.Name is not null && x.Type is not null).ToList() ?? [];
            item.ProviderIds ??= [];
            item.Genres ??= [];
            item.Studios ??= [];
            item.Tags ??= [];
        }


        // File names keep the name the friend had when their items were first written: only a friend with nothing
        // on disk takes their current name. Badges and tags follow the current name.
        if (string.IsNullOrWhiteSpace(_state.FileLabel) || _state.Files.Count == 0)
        {
            _state.FileLabel = peerName;
        }

        var fileLabel = _state.FileLabel;
        if (collectionType == "tvshows")
        {
            PlanSeries(items, streamUrl, peerName, fileLabel, owned, desired, result);
        }
        else
        {
            PlanMovies(items, streamUrl, peerName, fileLabel, owned, desired, result);
        }

        var pending = new List<(string Rel, string Full, RemoteFile File)>();
        var root = Path.GetFullPath(_libDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var rel in desired.Keys.ToList())
        {
            if (!Path.GetFullPath(FullPath(rel)).StartsWith(root, StringComparison.Ordinal))
            {
                _logger.LogWarning("Bloom: ignoring unsafe path {Path}", rel);
                desired.Remove(rel);
            }
        }

        foreach (var (rel, value) in desired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = FullPath(rel);
            var key = _statePrefix + rel;
            if (value is string content)
            {
                var hash = Hash(content);
                if (_state.Files.TryGetValue(key, out var known) && known == hash && File.Exists(full))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                _state.Files[key] = hash;
                result.Changed = true;
            }
            else if (value is RemoteFile remote)
            {
                if (_state.Files.TryGetValue(key, out var known) && known == remote.StateTag && File.Exists(full))
                {
                    continue;
                }

                pending.Add((rel, full, remote));
            }
        }

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (p, ct) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p.Full)!);
                if (await _download(p.File, p.Full, ct).ConfigureAwait(false))
                {
                    if (p.File.Badge is not null)
                    {
                        PosterBadge.TryApply(p.Full, p.File.Badge, _logger);
                    }

                    lock (_stateLock)
                    {
                        _state.Files[_statePrefix + p.Rel] = p.File.StateTag;
                        result.Changed = true;
                    }
                }
            }).ConfigureAwait(false);

        if (Cleanup(desired))
        {
            result.Changed = true;
        }

        return result;
    }

    /// <summary>
    /// Whether a remote id is safe to use in file names (Jellyfin ids are 32 hex chars).
    /// </summary>
    /// <param name="id">Id.</param>
    /// <returns>Whether it is safe.</returns>
    public static bool IsSafeId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// Returns a safe file extension (".jpg"), or the fallback.
    /// </summary>
    /// <param name="ext">Extension sent by the friend.</param>
    /// <param name="allowed">Allowed extensions.</param>
    /// <param name="fallback">Fallback.</param>
    /// <returns>A safe extension.</returns>
    public static string SafeExt(string? ext, string[] allowed, string fallback)
    {
        var lower = (ext ?? string.Empty).ToLowerInvariant();
        return allowed.Contains(lower, StringComparer.Ordinal) ? lower : fallback;
    }

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)), 0, 12);

    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;

    private static string? GetProviderId(CatalogItem item, string provider)
    {
        foreach (var (key, value) in item.ProviderIds)
        {
            if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                var safe = new string(value.Where(char.IsLetterOrDigit).ToArray());
                return safe.Length == 0 ? null : safe;
            }
        }

        return null;
    }

    private static string FolderName(CatalogItem item, string baseName, string[] providers, HashSet<string> used)
    {
        foreach (var provider in providers)
        {
            var id = GetProviderId(item, provider);
            if (id is not null)
            {
                var candidate = $"{baseName} [{provider}id-{id}]";
                if (used.Add(candidate))
                {
                    return candidate;
                }

                break;
            }
        }

        var fallback = $"{baseName} [jf-{ShortId(item.Id)}]";
        if (used.Add(fallback))
        {
            return fallback;
        }

        fallback = $"{baseName} [jf-{item.Id}]";
        used.Add(fallback);
        return fallback;
    }

    private static string BaseName(CatalogItem item)
        => item.Year is int year ? $"{SafeName(item.Name)} ({year.ToString(CultureInfo.InvariantCulture)})" : SafeName(item.Name);

    private static void AddImages(Dictionary<string, object> desired, CatalogItem item, string dir, Dictionary<string, string> names, string? badge)
    {
        foreach (var image in item.Images)
        {
            if (names.TryGetValue(image.Type, out var name))
            {
                // Only the poster gets the badge: it is what lists show.
                var fileBadge = image.Type == "Primary" ? badge : null;
                desired[$"{dir}/{name}{SafeExt(image.Ext, ImageExtensions, ".jpg")}"] = new RemoteFile(item.Id, image.Type, image.Tag, fileBadge);
            }
        }
    }

    private static void AddSubtitles(Dictionary<string, object> desired, CatalogItem item, string dir, string baseName)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in item.Subtitles.OrderBy(s => s.Index))
        {
            var code = new string((sub.Language ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(12).ToArray());
            var lang = code.Length == 0 ? string.Empty : "." + code;
            var flags = (sub.Forced ? ".forced" : string.Empty) + (sub.HearingImpaired ? ".sdh" : string.Empty);
            var name = $"{baseName}{lang}{flags}";
            if (!used.Add(name))
            {
                name = $"{baseName}{lang}.{sub.Index.ToString(CultureInfo.InvariantCulture)}{flags}";
                used.Add(name);
            }

            desired[$"{dir}/{name}{SafeExt(sub.Ext, SubtitleExtensions, ".srt")}"] = new RemoteFile(item.Id, "sub:" + sub.Index.ToString(CultureInfo.InvariantCulture), "1");
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private void PlanMovies(IReadOnlyList<CatalogItem> items, Func<CatalogItem, string> streamUrl, string peerName, string fileLabel, OwnedIndex? owned, Dictionary<string, object> desired, LibraryWriteResult result)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in items.Where(i => i.Type == "Movie" && i.StreamToken is not null).OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            var match = owned?.MatchMovie(movie) ?? OwnedMatch.None;
            if (match == OwnedMatch.SameFile || (match == OwnedMatch.OtherVersion && !OfferVersions))
            {
                result.Skipped++;
                continue;
            }

            if (match == OwnedMatch.OtherVersion)
            {
                result.Versions++;
            }

            // "Title (2010) - Alice 4K": Jellyfin's naming for versions, so the picker shows "Alice 4K".
            var baseName = BaseName(movie);
            var fileName = $"{baseName} - {VersionLabel(fileLabel, movie)}";
            var folder = FolderName(movie, baseName, ["tmdb", "imdb"], used);
            desired[$"{folder}/{fileName}.strm"] = streamUrl(movie) + "\n";
            desired[$"{folder}/{fileName}.nfo"] = NfoBuilder.Movie(movie, peerName);
            AddImages(desired, movie, folder, MovieImageNames, Badge);
            AddSubtitles(desired, movie, folder, fileName);
            result.Imported++;
        }
    }

    private void PlanSeries(IReadOnlyList<CatalogItem> items, Func<CatalogItem, string> streamUrl, string peerName, string fileLabel, OwnedIndex? owned, Dictionary<string, object> desired, LibraryWriteResult result)
    {
        var seasons = items.Where(i => i.Type == "Season" && i.SeriesId is not null && i.IndexNumber is not null)
            .GroupBy(i => (i.SeriesId!, i.IndexNumber!.Value))
            .ToDictionary(g => g.Key, g => g.First());
        var episodes = items.Where(i => i.Type == "Episode" && i.StreamToken is not null && i.SeriesId is not null && i.ParentIndexNumber is not null && i.IndexNumber is not null)
            .GroupBy(i => i.SeriesId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var series in items.Where(i => i.Type == "Series").OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            if (!episodes.TryGetValue(series.Id, out var seriesEpisodes))
            {
                continue;
            }

            var kept = new List<CatalogItem>();
            foreach (var episode in seriesEpisodes)
            {
                var match = owned?.MatchEpisode(series, episode) ?? OwnedMatch.None;
                if (match == OwnedMatch.SameFile || (match == OwnedMatch.OtherVersion && !OfferVersions))
                {
                    result.Skipped++;
                    continue;
                }

                if (match == OwnedMatch.OtherVersion)
                {
                    result.Versions++;
                }

                kept.Add(episode);
            }

            if (kept.Count == 0)
            {
                continue;
            }

            // A series we already have is shown with our own poster (seasons are merged): only badge
            // the series and season posters when the whole series comes from the friend.
            var seriesBadge = owned?.OwnsSeries(series) == true ? null : Badge;
            var seriesName = SafeName(series.Name);
            var folder = FolderName(series, BaseName(series), ["tvdb", "tmdb", "imdb"], usedFolders);
            desired[$"{folder}/tvshow.nfo"] = NfoBuilder.Series(series, peerName);
            AddImages(desired, series, folder, MovieImageNames, seriesBadge);

            foreach (var seasonNumber in kept.Select(e => e.ParentIndexNumber!.Value).Distinct())
            {
                var seasonDir = $"{folder}/Season {seasonNumber.ToString("00", CultureInfo.InvariantCulture)}";
                if (seasons.TryGetValue((series.Id, seasonNumber), out var season))
                {
                    desired[$"{seasonDir}/season.nfo"] = NfoBuilder.Season(season);
                    AddImages(desired, season, seasonDir, MovieImageNames, seriesBadge);
                }
            }

            var usedEpisodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var episode in kept.OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                var s = episode.ParentIndexNumber!.Value;
                var e = episode.IndexNumber!.Value;
                var seasonDir = $"{folder}/Season {s.ToString("00", CultureInfo.InvariantCulture)}";
                var name = string.Create(CultureInfo.InvariantCulture, $"{seriesName} S{s:00}E{e:00}");
                if (episode.IndexNumberEnd is int end && end > e)
                {
                    name += string.Create(CultureInfo.InvariantCulture, $"-E{end:00}");
                }

                // "Show S01E02 - Alice 1080p": the version picker shows "Alice 1080p".
                name += " - " + VersionLabel(fileLabel, episode);

                if (!usedEpisodes.Add(seasonDir + "/" + name))
                {
                    name += $" [jf-{ShortId(episode.Id)}]";
                    usedEpisodes.Add(seasonDir + "/" + name);
                }

                desired[$"{seasonDir}/{name}.strm"] = streamUrl(episode) + "\n";
                desired[$"{seasonDir}/{name}.nfo"] = NfoBuilder.Episode(episode, series.Name);
                foreach (var image in episode.Images.Where(i => i.Type == "Primary"))
                {
                    desired[$"{seasonDir}/{name}-thumb{SafeExt(image.Ext, ImageExtensions, ".jpg")}"] = new RemoteFile(episode.Id, image.Type, image.Tag, Badge);
                }

                AddSubtitles(desired, episode, seasonDir, name);
                result.Imported++;
            }
        }
    }

    private string FullPath(string rel) => Path.Combine(_libDir, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Deletes what we created and no longer want. Files we did not create (e.g. written by Jellyfin)
    /// are left alone, except inside item folders that disappeared entirely.
    /// </summary>
    private bool Cleanup(Dictionary<string, object> desired)
    {
        var changed = false;
        var desiredTop = new HashSet<string>(desired.Keys.Select(k => k.Split('/')[0]), StringComparer.Ordinal);

        foreach (var key in _state.Files.Keys.Where(k => k.StartsWith(_statePrefix, StringComparison.Ordinal)).ToList())
        {
            var rel = key[_statePrefix.Length..];
            if (desired.ContainsKey(rel))
            {
                continue;
            }

            _state.Files.Remove(key);
            var full = FullPath(rel);
            if (File.Exists(full))
            {
                File.Delete(full);
                changed = true;
            }
        }

        if (!Directory.Exists(_libDir))
        {
            return changed;
        }

        foreach (var dir in Directory.GetDirectories(_libDir))
        {
            if (!desiredTop.Contains(Path.GetFileName(dir)))
            {
                _logger.LogDebug("Bloom: removing {Dir}", dir);
                Directory.Delete(dir, true);
                changed = true;
            }
        }

        foreach (var dir in Directory.GetDirectories(_libDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }

        return changed;
    }
}
