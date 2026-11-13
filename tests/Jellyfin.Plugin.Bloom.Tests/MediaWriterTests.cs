using System.Xml.Linq;
using Jellyfin.Plugin.Bloom.Models;
using Jellyfin.Plugin.Bloom.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bloom.Tests;

public sealed class MediaWriterTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private readonly List<RemoteFile> _downloads = [];

    public void Dispose() => _dir.Delete(true);

    private string LibDir => Path.Combine(_dir.FullName, "lib");

    private MediaWriter Writer(PeerSyncState state, bool offerVersions = false) => new(
        LibDir,
        "lib/",
        state,
        (file, path, _) =>
        {
            lock (_downloads)
            {
                _downloads.Add(file);
            }

            File.WriteAllText(path, file.Kind);
            return Task.FromResult(true);
        },
        NullLogger.Instance)
    {
        OfferVersions = offerVersions
    };

    private static string Url(CatalogItem item) => "https://friend.example/Bloom/Peer/Stream/" + item.Id + "?p=me&t=" + item.StreamToken;

    private static CatalogItem Movie(string id, string name, int year, string? tmdb = null) => new()
    {
        Id = id,
        Type = "Movie",
        Name = name,
        Year = year,
        Overview = "Un film.",
        StreamToken = "tok" + id,
        ProviderIds = tmdb is null ? [] : new Dictionary<string, string> { ["Tmdb"] = tmdb, ["Imdb"] = "tt" + tmdb },
        Images = [new CatalogImage { Type = "Primary", Tag = "1", Ext = ".jpg" }, new CatalogImage { Type = "Backdrop", Tag = "1", Ext = ".png" }],
        Subtitles = [new CatalogSubtitle { Index = 3, Language = "fre", Ext = ".srt" }, new CatalogSubtitle { Index = 4, Language = "fre", Ext = ".srt", Forced = true }],
        People = [new CatalogPerson { Name = "Jean <Dujardin>", Role = "OSS 117", Type = "Actor" }]
    };

    [Fact]
    public async Task MoviesAreMaterializedAsStrmNfoAndArtwork()
    {
        Text.UseRequestLanguage("en");
        var state = new PeerSyncState();
        var items = new List<CatalogItem> { Movie("aaaaaaaaaaaa", "Le Dîner de cons", 1998, "9421"), Movie("bbbbbbbbbbbb", "What/If: Part?", 2020) };

        var result = await Writer(state).WriteAsync("movies", items, Url, "Alice", null, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Imported);
        var folder = Path.Combine(LibDir, "Le Dîner de cons (1998) [tmdbid-9421]");
        Assert.Equal(Url(items[0]) + "\n", File.ReadAllText(Path.Combine(folder, "Le Dîner de cons (1998) - Alice.strm")));
        Assert.True(File.Exists(Path.Combine(folder, "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "fanart.png")));
        Assert.True(File.Exists(Path.Combine(folder, "Le Dîner de cons (1998) - Alice.fre.srt")));
        Assert.True(File.Exists(Path.Combine(folder, "Le Dîner de cons (1998) - Alice.fre.forced.srt")));

        var nfo = XDocument.Load(Path.Combine(folder, "Le Dîner de cons (1998) - Alice.nfo"));
        Assert.Equal("movie", nfo.Root!.Name.LocalName);
        Assert.Equal("Le Dîner de cons", nfo.Root.Element("title")!.Value);
        Assert.Equal("9421", nfo.Root.Elements("uniqueid").First(e => (string?)e.Attribute("type") == "tmdb").Value);
        Assert.Equal("Jean <Dujardin>", nfo.Root.Element("actor")!.Element("name")!.Value);
        Assert.Contains(nfo.Root.Elements("tag"), t => t.Value == "Shared by Alice");

        // Unsafe characters never reach the file system, and no provider id falls back to the item id.
        Assert.True(Directory.Exists(Path.Combine(LibDir, "What If Part (2020) [jf-bbbbbbbb]")));
    }

    [Fact]
    public async Task SecondSyncIsIncrementalAndRemovesWhatDisappeared()
    {
        var state = new PeerSyncState();
        var a = Movie("aaaaaaaaaaaa", "A", 2001, "1");
        var b = Movie("bbbbbbbbbbbb", "B", 2002, "2");
        await Writer(state).WriteAsync("movies", [a, b], Url, "Alice", null, CancellationToken.None);
        var downloadsAfterFirst = _downloads.Count;

        // A Jellyfin-written file inside a kept folder must survive.
        File.WriteAllText(Path.Combine(LibDir, "A (2001) [tmdbid-1]", "A (2001)-trickplay.bif"), "x");

        var again = await Writer(state).WriteAsync("movies", [a], Url, "Alice", null, CancellationToken.None);

        Assert.True(again.Changed);
        Assert.Equal(downloadsAfterFirst, _downloads.Count);
        Assert.False(Directory.Exists(Path.Combine(LibDir, "B (2002) [tmdbid-2]")));
        Assert.True(File.Exists(Path.Combine(LibDir, "A (2001) [tmdbid-1]", "A (2001)-trickplay.bif")));
        Assert.DoesNotContain(state.Files.Keys, k => k.Contains("B (2002)", StringComparison.Ordinal));

        var unchanged = await Writer(state).WriteAsync("movies", [a], Url, "Alice", null, CancellationToken.None);
        Assert.False(unchanged.Changed);
    }

    [Fact]
    public async Task ARenamedFriendKeepsItsFileNames()
    {
        Text.UseRequestLanguage("en");
        var state = new PeerSyncState();
        var movie = Movie("aaaaaaaaaaaa", "A", 2001, "1");
        var folder = Path.Combine(LibDir, "A (2001) [tmdbid-1]");
        await Writer(state).WriteAsync("movies", [movie], Url, "Alice", null, CancellationToken.None);
        var downloadsAfterFirst = _downloads.Count;

        var renamed = await Writer(state).WriteAsync("movies", [movie], Url, "Bob", null, CancellationToken.None);

        // Same files, nothing downloaded again: Jellyfin keeps the items it knows.
        Assert.True(File.Exists(Path.Combine(folder, "A (2001) - Alice.strm")));
        Assert.True(File.Exists(Path.Combine(folder, "A (2001) - Alice.fre.srt")));
        Assert.Empty(Directory.GetFiles(folder, "*Bob*"));
        Assert.Equal(downloadsAfterFirst, _downloads.Count);

        // The tag follows the new name.
        Assert.True(renamed.Changed);
        var nfo = XDocument.Load(Path.Combine(folder, "A (2001) - Alice.nfo"));
        Assert.Contains(nfo.Root!.Elements("tag"), t => t.Value == "Shared by Bob");

        // Once nothing of the friend is left on disk, their items come back under the new name.
        await Writer(state).WriteAsync("movies", [], Url, "Bob", null, CancellationToken.None);
        await Writer(state).WriteAsync("movies", [movie], Url, "Bob", null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(folder, "A (2001) - Bob.strm")));
        Assert.Empty(Directory.GetFiles(folder, "*Alice*"));
    }

    [Fact]
    public async Task OwnedMoviesAreSkipped()
    {
        var owned = new OwnedIndex();
        owned.AddMovie(new Dictionary<string, string> { ["Imdb"] = "tt1" });
        var result = await Writer(new PeerSyncState()).WriteAsync("movies", [Movie("aaaaaaaaaaaa", "A", 2001, "1"), Movie("bbbbbbbbbbbb", "B", 2002, "2")], Url, "Alice", owned, CancellationToken.None);

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.False(Directory.Exists(Path.Combine(LibDir, "A (2001) [tmdbid-1]")));
    }

    [Fact]
    public async Task SeriesGetSeasonFoldersAndOnlyMissingEpisodesWhenOwned()
    {
        var series = new CatalogItem { Id = "s1", Type = "Series", Name = "Kaamelott", Year = 2005, ProviderIds = new() { ["Tvdb"] = "79175" }, Images = [new CatalogImage { Type = "Primary", Tag = "1" }] };
        var season = new CatalogItem { Id = "se1", Type = "Season", Name = "Livre I", SeriesId = "s1", IndexNumber = 1, Images = [new CatalogImage { Type = "Primary", Tag = "1" }] };
        CatalogItem Episode(string id, int s, int e) => new() { Id = id, Type = "Episode", Name = "Ep " + e, SeriesId = "s1", ParentIndexNumber = s, IndexNumber = e, StreamToken = "t" + id, Images = [new CatalogImage { Type = "Primary", Tag = "1" }] };
        var orphan = new CatalogItem { Id = "x", Type = "Episode", Name = "Orphan", SeriesId = "unknown", ParentIndexNumber = 1, IndexNumber = 1, StreamToken = "t" };

        var owned = new OwnedIndex();
        owned.AddEpisode(new Dictionary<string, string> { ["Tvdb"] = "79175" }, 1, 1, null);

        var result = await Writer(new PeerSyncState()).WriteAsync("tvshows", [series, season, Episode("e1", 1, 1), Episode("e2", 1, 2), Episode("e3", 2, 1), orphan], Url, "Alice", owned, CancellationToken.None);

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Skipped);
        var root = Path.Combine(LibDir, "Kaamelott (2005) [tvdbid-79175]");
        Assert.True(File.Exists(Path.Combine(root, "tvshow.nfo")));
        Assert.True(File.Exists(Path.Combine(root, "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(root, "Season 01", "season.nfo")));
        Assert.True(File.Exists(Path.Combine(root, "Season 01", "poster.jpg")));
        Assert.False(File.Exists(Path.Combine(root, "Season 01", "Kaamelott S01E01 - Alice.strm")));
        Assert.True(File.Exists(Path.Combine(root, "Season 01", "Kaamelott S01E02 - Alice.strm")));
        Assert.True(File.Exists(Path.Combine(root, "Season 01", "Kaamelott S01E02 - Alice-thumb.jpg")));
        Assert.True(File.Exists(Path.Combine(root, "Season 02", "Kaamelott S02E01 - Alice.strm")));

        var nfo = XDocument.Load(Path.Combine(root, "Season 02", "Kaamelott S02E01 - Alice.nfo"));
        Assert.Equal("episodedetails", nfo.Root!.Name.LocalName);
        Assert.Equal("2", nfo.Root.Element("season")!.Value);
        Assert.Equal("1", nfo.Root.Element("episode")!.Value);
        Assert.Equal("Kaamelott", nfo.Root.Element("showtitle")!.Value);
        Assert.Single(Directory.GetDirectories(LibDir));
    }

    [Fact]
    public async Task AMaliciousFriendCannotWriteOutsideItsFolder()
    {
        var evilImage = Movie("aaaaaaaaaaaa", "Evil", 2000, "1");
        evilImage.Images = [new CatalogImage { Type = "Primary", Tag = "1", Ext = "/../../../../escaped.txt" }];
        evilImage.Subtitles = [new CatalogSubtitle { Index = 1, Language = "../../x", Ext = "/../../escaped.sh" }];
        var evilId = Movie("../../../escaped", "Evil2", 2000);
        var evilEpisode = new CatalogItem { Id = "e1", Type = "Episode", Name = "x", SeriesId = "../..", ParentIndexNumber = 1, IndexNumber = 1, StreamToken = "t" };

        var result = await Writer(new PeerSyncState()).WriteAsync("movies", [evilImage, evilId, evilEpisode], Url, "Mallory", null, CancellationToken.None);

        Assert.Equal(1, result.Imported);
        var outside = Directory.GetFiles(_dir.FullName, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(LibDir + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Empty(outside);
        Assert.True(File.Exists(Path.Combine(LibDir, "Evil (2000) [tmdbid-1]", "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(LibDir, "Evil (2000) [tmdbid-1]", "Evil (2000) - Mallory.x.srt")));
    }

    [Theory]
    [InlineData("Mission: Impossible", "Mission Impossible")]
    [InlineData("  ...  ", "Sans titre")]
    [InlineData("AC/DC\tLive", "AC DC Live")]
    [InlineData("Dr. Strangelove.", "Dr. Strangelove")]
    public void NamesAreSafe(string input, string expected) => Assert.Equal(expected, MediaWriter.SafeName(input));

    [Fact]
    public async Task PostersShowTheFriendName()
    {
        // A plain blue poster, like the ones a friend would send.
        static void WritePoster(string path)
        {
            using var bitmap = new SkiaSharp.SKBitmap(400, 600);
            bitmap.Erase(new SkiaSharp.SKColor(30, 60, 200));
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(path, data.ToArray());
        }

        var downloads = new List<RemoteFile>();
        MediaWriter NewWriter(PeerSyncState state, string? badge) => new(
            LibDir,
            "lib/",
            state,
            (file, path, _) =>
            {
                lock (downloads)
                {
                    downloads.Add(file);
                }

                if (file.Kind is "Primary" or "Backdrop")
                {
                    WritePoster(path);
                }
                else
                {
                    File.WriteAllText(path, file.Kind);
                }

                return Task.FromResult(true);
            },
            NullLogger.Instance)
        { Badge = badge };

        var state = new PeerSyncState();
        var movie = Movie("aaaaaaaaaaaa", "Le Grand Test", 2020, "900001");
        await NewWriter(state, "Alice").WriteAsync("movies", [movie], Url, "Alice", null, CancellationToken.None);

        var folder = Path.Combine(LibDir, "Le Grand Test (2020) [tmdbid-900001]");
        SkiaSharp.SKColor Pixel(string file, int x, int y)
        {
            using var bitmap = SkiaSharp.SKBitmap.Decode(Path.Combine(folder, file));
            return bitmap.GetPixel(x, y);
        }

        // The badge darkens the bottom centre of the poster (the row through its middle); the rest is untouched.
        byte Darkest(string file, int y) => Enumerable.Range(100, 200).Min(x => Pixel(file, x, y).Blue);
        Assert.True(Darkest("poster.jpg", 552) < 120, "badge expected at the bottom of the poster");
        Assert.InRange(Pixel("poster.jpg", 30, 32).Blue, 185, 215);
        Assert.InRange(Pixel("poster.jpg", 200, 300).Blue, 185, 215);
        Assert.True(Darkest("fanart.png", 552) > 185, "backdrops are left alone");

        // Turning the badge off fetches the original poster again; nothing else is downloaded.
        downloads.Clear();
        await NewWriter(state, null).WriteAsync("movies", [movie], Url, "Alice", null, CancellationToken.None);
        Assert.Equal(["Primary"], downloads.Select(d => d.Kind));
        Assert.True(Darkest("poster.jpg", 552) > 185, "original poster expected");
    }

    [Fact]
    public void LongFriendNamesAreShortened()
    {
        Assert.Equal("Alice", PosterBadge.Shorten("  Alice "));
        Assert.Equal("Le serveur de Jea…", PosterBadge.Shorten("Le serveur de Jean-Michel"));
        Assert.Equal(18, PosterBadge.Shorten("Le serveur de Jean-Michel").Length);
    }

    [Fact]
    public async Task AnotherVersionOfAMovieWeOwnIsImportedButNotTheSameFile()
    {
        var owned = new OwnedIndex();
        owned.AddMovie(new Dictionary<string, string> { ["Tmdb"] = "1" }, 1_000);
        owned.AddMovie(new Dictionary<string, string> { ["Tmdb"] = "2" }, 2_000);
        var sameFile = Movie("aaaaaaaaaaaa", "A", 2001, "1");
        sameFile.Size = 1_000;
        var better = Movie("bbbbbbbbbbbb", "B", 2002, "2");
        better.Size = 8_000;
        better.Width = 3840;
        better.Height = 1600;
        better.VideoRange = "HDR10";

        var result = await Writer(new PeerSyncState(), offerVersions: true).WriteAsync("movies", [sameFile, better], Url, "Alice", owned, CancellationToken.None);

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Versions);
        Assert.Equal(1, result.Skipped);
        Assert.False(Directory.Exists(Path.Combine(LibDir, "A (2001) [tmdbid-1]")));
        Assert.True(File.Exists(Path.Combine(LibDir, "B (2002) [tmdbid-2]", "B (2002) - Alice 4K HDR.strm")));

        // Without versions, a movie we own is never imported.
        var off = await Writer(new PeerSyncState()).WriteAsync("movies", [sameFile, better], Url, "Alice", owned, CancellationToken.None);
        Assert.Equal(0, off.Imported);
        Assert.Equal(2, off.Skipped);
    }

    [Fact]
    public void OwnedMoviesAreComparedFileByFile()
    {
        var owned = new OwnedIndex();
        owned.AddMovie(new Dictionary<string, string> { ["Imdb"] = "tt1" }, 4_000_000_000);
        owned.AddMovie(new Dictionary<string, string> { ["Imdb"] = "tt1" }, 1_500_000_000);
        CatalogItem Copy(long? size, string imdb = "tt1") => new() { Id = "x", Type = "Movie", Size = size, ProviderIds = new() { ["Imdb"] = imdb } };

        Assert.Equal(OwnedMatch.SameFile, owned.MatchMovie(Copy(1_500_000_000)));
        Assert.Equal(OwnedMatch.OtherVersion, owned.MatchMovie(Copy(9_000_000_000)));
        Assert.Equal(OwnedMatch.SameFile, owned.MatchMovie(Copy(null)));
        Assert.Equal(OwnedMatch.None, owned.MatchMovie(Copy(1_500_000_000, "tt2")));
    }

    [Theory]
    [InlineData(3840, 2160, "HDR10", "Alice 4K HDR")]
    [InlineData(3840, 1600, "DOVIWithHDR10", "Alice 4K DV")]
    [InlineData(1920, 800, "SDR", "Alice 1080p")]
    [InlineData(1280, 720, null, "Alice 720p")]
    [InlineData(720, 576, "HDR10Plus", "Alice SD HDR10+")]
    [InlineData(null, null, null, "Alice")]
    public void VersionsAreLabelledByFriendAndQuality(int? width, int? height, string? range, string expected)
        => Assert.Equal(expected, MediaWriter.VersionLabel("Alice", new CatalogItem { Width = width, Height = height, VideoRange = range }));

    [Fact]
    public async Task EpisodesWeOwnInAnotherQualityBecomeVersions()
    {
        var tvdb = new Dictionary<string, string> { ["Tvdb"] = "79175" };
        var owned = new OwnedIndex();
        owned.AddEpisode(tvdb, 1, 1, null, 100);    // same file as the friend's
        owned.AddEpisode(tvdb, 1, 2, null, 200);    // the friend has it in 1080p
        owned.AddEpisode(tvdb, 1, 3, null, 300);    // the friend has E03-E04 in one file
        owned.AddEpisode(tvdb, 1, 5, 6, 500);       // we have E05-E06 in one file

        var series = new CatalogItem { Id = "s1", Type = "Series", Name = "Kaamelott", Year = 2005, ProviderIds = new() { ["Tvdb"] = "79175" } };
        CatalogItem Episode(string id, int e, long size, int? end = null) => new()
        {
            Id = id, Type = "Episode", Name = "Ep " + e, SeriesId = "s1", ParentIndexNumber = 1, IndexNumber = e, IndexNumberEnd = end,
            Size = size, Width = 1920, Height = 1080, StreamToken = "t" + id
        };
        var items = new List<CatalogItem> { series, Episode("e1", 1, 100), Episode("e2", 2, 999), Episode("e3", 3, 999, 4), Episode("e5", 5, 999), Episode("e7", 7, 999) };

        var result = await Writer(new PeerSyncState(), offerVersions: true).WriteAsync("tvshows", items, Url, "Alice", owned, CancellationToken.None);

        var season = Path.Combine(LibDir, "Kaamelott (2005) [tvdbid-79175]", "Season 01");
        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Versions);
        Assert.Equal(3, result.Skipped);
        Assert.Equal(
            ["Kaamelott S01E02 - Alice 1080p.strm", "Kaamelott S01E07 - Alice 1080p.strm"],
            Directory.GetFiles(season, "*.strm").Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // Without versions, only the missing episode comes in.
        var off = await Writer(new PeerSyncState()).WriteAsync("tvshows", items, Url, "Alice", owned, CancellationToken.None);
        Assert.Equal(1, off.Imported);
        Assert.Equal(4, off.Skipped);
    }
}
