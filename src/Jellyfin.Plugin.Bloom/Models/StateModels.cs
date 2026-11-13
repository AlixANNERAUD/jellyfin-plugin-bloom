using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bloom.Models;

#pragma warning disable SA1402 // Small DTOs grouped on purpose.

/// <summary>How a library shared by a friend is shown locally.</summary>
public enum ImportMode
{
    /// <summary>Added as an extra folder of one of our existing libraries.</summary>
    Merge = 0,

    /// <summary>Gets its own library, e.g. "Films (Alice)".</summary>
    Separate = 1,

    /// <summary>Not imported.</summary>
    Ignore = 2
}

/// <summary>Persistent state of the plugin.</summary>
public sealed class FederationState
{
    /// <summary>Gets or sets the settings.</summary>
    public FederationSettings Settings { get; set; } = new();

    /// <summary>Gets or sets the friends.</summary>
    public List<PeerRecord> Peers { get; set; } = [];

    /// <summary>Gets or sets the pending invites.</summary>
    public List<PendingInvite> Invites { get; set; } = [];
}

/// <summary>User settings.</summary>
public sealed class FederationSettings
{
    /// <summary>Gets or sets the URL at which friends reach this server.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Gets or sets the name shown to friends (defaults to the Jellyfin server name).</summary>
    public string? ServerName { get; set; }

    /// <summary>Gets or sets where .strm/.nfo files are written (defaults to {data}/bloom/media).</summary>
    public string? MediaRoot { get; set; }

    /// <summary>Gets or sets a value indicating whether items we already own are skipped.</summary>
    public bool SkipOwnedItems { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether friends are told when our catalog changes.</summary>
    public bool NotifyPeersOnChange { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a friend's posters show the friend's name.</summary>
    public bool BadgePosters { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether copies of the same movie (ours in another quality, or several friends')
    /// are grouped as versions of one movie instead of being skipped or listed twice.
    /// </summary>
    public bool GroupVersions { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether this server sleeps when idle (a proxy stops it and starts it again on
    /// the next request). Friends then never wake it on their own: they sync when it says it is up.
    /// </summary>
    public bool SleepsWhenIdle { get; set; }
}

/// <summary>A friend server.</summary>
public sealed class PeerRecord
{
    /// <summary>Gets or sets the friend's server id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the friend's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the friend's public URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the friend's public key.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Gets or sets when the friendship was established.</summary>
    public DateTime DateAdded { get; set; }

    /// <summary>Gets or sets a value indicating whether federation with this friend is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the friend removed us.</summary>
    public bool RevokedByPeer { get; set; }

    /// <summary>Gets or sets the secret used to issue stream tokens to this friend.</summary>
    public string StreamSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets our libraries shared with this friend.</summary>
    public List<Guid> SharedLibraryIds { get; set; } = [];

    /// <summary>Gets or sets the friend's libraries and how we import them.</summary>
    public List<ImportRecord> Imports { get; set; } = [];

    /// <summary>Gets or sets the last successful contact.</summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>Gets or sets the last successful sync.</summary>
    public DateTime? LastSync { get; set; }

    /// <summary>Gets or sets the last error.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets a value indicating whether the friend's server sleeps when idle.</summary>
    public bool Sleeps { get; set; }

    /// <summary>
    /// Whether contacting the friend now would wake its server up: it sleeps when idle and has not been heard from
    /// recently (a server that just talked to us is up).
    /// </summary>
    /// <param name="now">Current UTC time.</param>
    /// <returns>Whether it is probably asleep.</returns>
    public bool IsAsleep(DateTime now) => Sleeps && (LastSeen is not DateTime seen || now - seen > TimeSpan.FromMinutes(2));
}

/// <summary>A library shared by a friend.</summary>
public sealed class ImportRecord
{
    /// <summary>Gets or sets the friend's library id.</summary>
    public string RemoteLibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the friend's library name.</summary>
    public string RemoteLibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection type (movies/tvshows).</summary>
    public string CollectionType { get; set; } = string.Empty;

    /// <summary>Gets or sets the import mode.</summary>
    public ImportMode Mode { get; set; }

    /// <summary>Gets or sets the local library to merge into (Merge mode).</summary>
    public string? TargetLibrary { get; set; }

    /// <summary>Gets or sets the library we created (Separate mode).</summary>
    public string? CreatedLibrary { get; set; }

    /// <summary>Gets or sets the number of items imported during the last sync.</summary>
    public int ImportedCount { get; set; }

    /// <summary>Gets or sets the number of items skipped because we already own them.</summary>
    public int SkippedCount { get; set; }

    /// <summary>Gets or sets how many imported titles are another version of a movie we have.</summary>
    public int VersionCount { get; set; }
}

/// <summary>An invite we issued and that was not used yet.</summary>
public sealed class PendingInvite
{
    /// <summary>Gets or sets the SHA-256 of the token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the expiry.</summary>
    public DateTime ExpiresUtc { get; set; }

    /// <summary>Gets or sets the libraries the new friend will see.</summary>
    public List<Guid> SharedLibraryIds { get; set; } = [];

    /// <summary>Gets or sets a free label ("Alice").</summary>
    public string? Label { get; set; }
}

/// <summary>What we wrote on disk for one friend (to update incrementally).</summary>
public sealed class PeerSyncState
{
    /// <summary>Gets or sets the hash/tag of each managed file, keyed by path relative to the friend folder.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the friend name used in file names ("Title (2010) - Alice 4K"). It is kept when the friend
    /// renames their server: new file names would make Jellyfin see every item as removed and added again.
    /// </summary>
    public string? FileLabel { get; set; }
}
