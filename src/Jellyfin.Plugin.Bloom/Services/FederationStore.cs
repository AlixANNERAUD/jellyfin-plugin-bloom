using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Bloom.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Where Bloom keeps its files. Lives in Jellyfin's data folder, which survives plugin upgrades.
/// </summary>
public sealed class FederationPaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FederationPaths"/> class.
    /// </summary>
    /// <param name="appPaths">Application paths.</param>
    public FederationPaths(IApplicationPaths appPaths)
        : this(Path.Combine(appPaths.DataPath, "bloom"))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationPaths"/> class.
    /// </summary>
    /// <param name="dataDir">Data directory.</param>
    public FederationPaths(string dataDir)
    {
        DataDir = dataDir;
    }

    /// <summary>Gets the data directory.</summary>
    public string DataDir { get; }

    /// <summary>Gets the private key file.</summary>
    public string IdentityFile => Path.Combine(DataDir, "identity.key");

    /// <summary>Gets the state file.</summary>
    public string StateFile => Path.Combine(DataDir, "state.json");

    /// <summary>Gets the directory with per-friend sync state.</summary>
    public string SyncStateDir => Path.Combine(DataDir, "sync");

    /// <summary>Gets the default root of imported media.</summary>
    public string DefaultMediaRoot => Path.Combine(DataDir, "media");

    /// <summary>
    /// Gets the root where friends' items are materialized.
    /// </summary>
    /// <param name="settings">Settings.</param>
    /// <returns>Absolute path.</returns>
    public string GetMediaRoot(FederationSettings settings)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(settings.MediaRoot) ? DefaultMediaRoot : settings.MediaRoot);
}

/// <summary>
/// Thread-safe JSON persistence of <see cref="FederationState"/>.
/// </summary>
public sealed class FederationStore
{
    private readonly object _lock = new();
    private readonly FederationPaths _paths;
    private readonly IServerApplicationHost? _appHost;
    private readonly ILogger<FederationStore> _logger;
    private FederationState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationStore"/> class.
    /// </summary>
    /// <param name="paths">Paths.</param>
    /// <param name="appHost">Application host (for the default server name).</param>
    /// <param name="logger">Logger.</param>
    public FederationStore(FederationPaths paths, IServerApplicationHost? appHost, ILogger<FederationStore> logger)
    {
        _paths = paths;
        _appHost = appHost;
        _logger = logger;
        _state = Load();
    }

    /// <summary>Gets the paths.</summary>
    public FederationPaths Paths => _paths;

    /// <summary>
    /// Returns a deep copy of the current state.
    /// </summary>
    /// <returns>A snapshot.</returns>
    public FederationState Snapshot()
    {
        lock (_lock)
        {
            return Clone(_state);
        }
    }

    /// <summary>
    /// Finds a friend by id (copy).
    /// </summary>
    /// <param name="peerId">Server id.</param>
    /// <returns>The friend or null.</returns>
    public PeerRecord? FindPeer(string? peerId)
    {
        if (string.IsNullOrEmpty(peerId))
        {
            return null;
        }

        lock (_lock)
        {
            var peer = _state.Peers.Find(p => string.Equals(p.Id, peerId, StringComparison.Ordinal));
            return peer is null ? null : Clone(peer);
        }
    }

    /// <summary>
    /// Mutates the state and saves it.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="mutate">Mutation.</param>
    /// <returns>The mutation result.</returns>
    public T Update<T>(Func<FederationState, T> mutate)
    {
        lock (_lock)
        {
            var result = mutate(_state);
            Save();
            return result;
        }
    }

    /// <summary>
    /// Mutates the state and saves it.
    /// </summary>
    /// <param name="mutate">Mutation.</param>
    public void Update(Action<FederationState> mutate)
    {
        Update<bool>(s =>
        {
            mutate(s);
            return true;
        });
    }

    /// <summary>
    /// Mutates the state and saves it only if the mutation reports a change.
    /// </summary>
    /// <param name="mutate">Mutation returning whether it changed anything.</param>
    /// <returns>Whether something changed.</returns>
    public bool TryUpdate(Func<FederationState, bool> mutate)
    {
        lock (_lock)
        {
            if (!mutate(_state))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>
    /// Mutates one friend if it exists.
    /// </summary>
    /// <param name="peerId">Server id.</param>
    /// <param name="mutate">Mutation.</param>
    /// <returns>Whether the friend exists.</returns>
    public bool UpdatePeer(string peerId, Action<PeerRecord> mutate)
    {
        return Update(s =>
        {
            var peer = s.Peers.Find(p => string.Equals(p.Id, peerId, StringComparison.Ordinal));
            if (peer is null)
            {
                return false;
            }

            mutate(peer);
            return true;
        });
    }

    /// <summary>
    /// Gets the name shown to friends.
    /// </summary>
    /// <returns>The display name.</returns>
    public string GetDisplayName()
    {
        lock (_lock)
        {
            var name = _state.Settings.ServerName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = _appHost?.FriendlyName;
            }

            return string.IsNullOrWhiteSpace(name) ? "Jellyfin" : name.Trim();
        }
    }

    /// <summary>
    /// Gets the configured media root.
    /// </summary>
    /// <returns>Absolute path.</returns>
    public string GetMediaRoot()
    {
        lock (_lock)
        {
            return _paths.GetMediaRoot(_state.Settings);
        }
    }

    /// <summary>
    /// Gets the root friends' items would be written to with this setting.
    /// </summary>
    /// <param name="mediaRoot">Setting value (null for the default).</param>
    /// <returns>Absolute path.</returns>
    public string MediaRootFor(string? mediaRoot) => _paths.GetMediaRoot(new FederationSettings { MediaRoot = mediaRoot });

    /// <summary>
    /// Loads the per-friend sync state.
    /// </summary>
    /// <param name="peerId">Server id.</param>
    /// <returns>The sync state.</returns>
    public PeerSyncState LoadSyncState(string peerId)
    {
        var file = Path.Combine(_paths.SyncStateDir, peerId + ".json");
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<PeerSyncState>(File.ReadAllBytes(file), Json.Options) ?? new PeerSyncState();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Bloom: sync state of {Peer} is unreadable, starting over", peerId);
        }

        return new PeerSyncState();
    }

    /// <summary>
    /// Saves the per-friend sync state.
    /// </summary>
    /// <param name="peerId">Server id.</param>
    /// <param name="state">State.</param>
    public void SaveSyncState(string peerId, PeerSyncState state)
    {
        Directory.CreateDirectory(_paths.SyncStateDir);
        WriteAtomic(Path.Combine(_paths.SyncStateDir, peerId + ".json"), JsonSerializer.SerializeToUtf8Bytes(state, Json.Options));
    }

    /// <summary>
    /// Deletes the per-friend sync state.
    /// </summary>
    /// <param name="peerId">Server id.</param>
    public void DeleteSyncState(string peerId)
    {
        var file = Path.Combine(_paths.SyncStateDir, peerId + ".json");
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Json.Options), Json.Options)!;

    private static void WriteAtomic(string path, byte[] content)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
    }

    private FederationState Load()
    {
        try
        {
            if (File.Exists(_paths.StateFile))
            {
                return JsonSerializer.Deserialize<FederationState>(File.ReadAllBytes(_paths.StateFile), Json.Options) ?? new FederationState();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "Bloom: cannot read {File}", _paths.StateFile);
            throw;
        }

        return new FederationState();
    }

    private void Save()
    {
        Directory.CreateDirectory(_paths.DataDir);
        WriteAtomic(_paths.StateFile, JsonSerializer.SerializeToUtf8Bytes(_state, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
    }
}
