using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.Bloom.Models;

#pragma warning disable SA1402 // Small DTOs grouped on purpose.

/// <summary>JSON settings used on the wire and on disk.</summary>
public static class Json
{
    /// <summary>Gets the shared serializer options.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}

/// <summary>Content of an invite code ("bloom1:" + base64url(JSON)).</summary>
public sealed class InviteCode
{
    /// <summary>Gets or sets the protocol version.</summary>
    public int V { get; set; }

    /// <summary>Gets or sets the inviter's server id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the inviter's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the inviter's public URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the inviter's public key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the one-time token.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets the expiry (unix seconds).</summary>
    public long Exp { get; set; }
}

/// <summary>Sent by the invitee to the inviter to complete the key exchange.</summary>
public sealed class PairRequest
{
    /// <summary>Gets or sets the protocol version.</summary>
    public int V { get; set; }

    /// <summary>Gets or sets the invite token.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets the invitee's server id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the invitee's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the invitee's public URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the invitee's public key.</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>Inviter's answer to a pairing request.</summary>
public sealed class PairResponse
{
    /// <summary>Gets or sets the inviter's server id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the inviter's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the inviter's URL.</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>Public information about a server.</summary>
public sealed class ServerInfoResponse
{
    /// <summary>Gets or sets the server id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the fingerprint.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets the plugin version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the protocol version.</summary>
    public int Protocol { get; set; }
}

/// <summary>Information a friend gets about us on each call (keeps names/URLs fresh).</summary>
public sealed class HelloRequest
{
    /// <summary>Gets or sets the caller's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the caller's public URL.</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>A library shared with the caller.</summary>
public sealed class CatalogLibrary
{
    /// <summary>Gets or sets the library id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the library name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection type ("movies" or "tvshows").</summary>
    public string CollectionType { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of items (movies/series/seasons/episodes).</summary>
    public int ItemCount { get; set; }
}

/// <summary>Response of the libraries endpoint.</summary>
public sealed class CatalogLibrariesResponse
{
    /// <summary>Gets or sets the server name.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the libraries shared with the caller.</summary>
    public List<CatalogLibrary> Libraries { get; set; } = [];
}

/// <summary>A page of catalog items.</summary>
public sealed class CatalogPage
{
    /// <summary>Gets or sets the library id.</summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the start index.</summary>
    public int Start { get; set; }

    /// <summary>Gets or sets the total number of items.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the catalog version: every page of one sync must have the same.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the items.</summary>
    public List<CatalogItem> Items { get; set; } = [];
}

/// <summary>An image available for an item.</summary>
public sealed class CatalogImage
{
    /// <summary>Gets or sets the image type (Primary, Backdrop, Logo, Thumb, Banner).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets a tag that changes when the image changes.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Gets or sets the file extension (".jpg").</summary>
    public string Ext { get; set; } = ".jpg";
}

/// <summary>A cast or crew member.</summary>
public sealed class CatalogPerson
{
    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the role (character).</summary>
    public string? Role { get; set; }

    /// <summary>Gets or sets the kind (Actor, Director, Writer...).</summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>An external subtitle file of an item.</summary>
public sealed class CatalogSubtitle
{
    /// <summary>Gets or sets the stream index.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the language (ISO 639-2).</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the file extension (".srt").</summary>
    public string Ext { get; set; } = ".srt";

    /// <summary>Gets or sets a value indicating whether the subtitle is forced.</summary>
    public bool Forced { get; set; }

    /// <summary>Gets or sets a value indicating whether it is for the hearing impaired.</summary>
    public bool HearingImpaired { get; set; }
}

/// <summary>A movie, series, season or episode.</summary>
public sealed class CatalogItem
{
    /// <summary>Gets or sets the item id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the type: Movie, Series, Season or Episode.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the original title.</summary>
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the sort name.</summary>
    public string? SortName { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the premiere date (yyyy-MM-dd).</summary>
    public string? PremiereDate { get; set; }

    /// <summary>Gets or sets the end date (series, yyyy-MM-dd).</summary>
    public string? EndDate { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the tagline.</summary>
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the official (parental) rating.</summary>
    public string? OfficialRating { get; set; }

    /// <summary>Gets or sets the community rating.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets the critic rating.</summary>
    public float? CriticRating { get; set; }

    /// <summary>Gets or sets the runtime in ticks.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the series status (Continuing/Ended).</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    public List<string> Genres { get; set; } = [];

    /// <summary>Gets or sets the studios.</summary>
    public List<string> Studios { get; set; } = [];

    /// <summary>Gets or sets the tags.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Gets or sets the provider ids (Tmdb, Imdb, Tvdb...).</summary>
    public Dictionary<string, string> ProviderIds { get; set; } = [];

    /// <summary>Gets or sets the people.</summary>
    public List<CatalogPerson> People { get; set; } = [];

    /// <summary>Gets or sets the images.</summary>
    public List<CatalogImage> Images { get; set; } = [];

    /// <summary>Gets or sets the series id (seasons, episodes).</summary>
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets the index number (season or episode number).</summary>
    public int? IndexNumber { get; set; }

    /// <summary>Gets or sets the last episode number of a multi-episode file.</summary>
    public int? IndexNumberEnd { get; set; }

    /// <summary>Gets or sets the season number of an episode.</summary>
    public int? ParentIndexNumber { get; set; }

    /// <summary>Gets or sets the container (mkv, mp4...).</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the file size in bytes (tells "the same file" from "another version").</summary>
    public long? Size { get; set; }

    /// <summary>Gets or sets the video width.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the video height.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the dynamic range (SDR, HDR10, DOVI...).</summary>
    public string? VideoRange { get; set; }

    /// <summary>Gets or sets the external subtitles.</summary>
    public List<CatalogSubtitle> Subtitles { get; set; } = [];

    /// <summary>Gets or sets the stream token (playable items).</summary>
    public string? StreamToken { get; set; }

    /// <summary>
    /// Creates a shallow copy.
    /// </summary>
    /// <returns>The copy.</returns>
    public CatalogItem ShallowCopy() => (CatalogItem)MemberwiseClone();
}

/// <summary>Sent to friends when our shared catalog changed.</summary>
public sealed class NotifyRequest
{
    /// <summary>Gets or sets the reason.</summary>
    public string Reason { get; set; } = string.Empty;
}
