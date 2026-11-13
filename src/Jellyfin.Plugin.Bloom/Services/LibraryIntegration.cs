using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Attaches the folders holding a friend's items to our Jellyfin libraries
/// (as an extra folder of an existing library, or as a dedicated library).
/// </summary>
public sealed class LibraryIntegration
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryIntegration> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryIntegration"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public LibraryIntegration(ILibraryManager libraryManager, ILogger<LibraryIntegration> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Picks the default local library for a friend's library: the first of ours with the same type
    /// that is a real (non federated) library.
    /// </summary>
    /// <param name="collectionType">"movies" or "tvshows".</param>
    /// <param name="isFederatedPath">Predicate telling federated folders apart.</param>
    /// <returns>The library name, or null.</returns>
    public string? DefaultMergeTarget(string collectionType, Func<string, bool> isFederatedPath)
    {
        var wanted = ToOptions(collectionType);
        return _libraryManager.GetVirtualFolders()
            .Where(f => f.CollectionType == wanted && f.Locations.Any(l => !isFederatedPath(l)))
            .Select(f => f.Name)
            .FirstOrDefault();
    }

    /// <summary>
    /// Lists local libraries of a given type that can receive a friend's folder.
    /// </summary>
    /// <returns>(name, collection type) pairs.</returns>
    public IReadOnlyList<(string Name, string? CollectionType, string[] Locations)> ListLibraries()
        => _libraryManager.GetVirtualFolders()
            .Select(f => (f.Name, f.CollectionType?.ToString(), f.Locations ?? []))
            .ToList();

    /// <summary>
    /// Makes sure <paramref name="directory"/> is part of the right library according to <paramref name="import"/>.
    /// </summary>
    /// <param name="import">Import settings (updated with the created library name).</param>
    /// <param name="peerName">Friend name (to name dedicated libraries).</param>
    /// <param name="directory">Folder with the friend's items.</param>
    /// <returns>Whether the library structure changed.</returns>
    public async Task<bool> EnsureAttachedAsync(ImportRecord import, string peerName, string directory)
    {
        Directory.CreateDirectory(directory);
        var folders = _libraryManager.GetVirtualFolders();
        var holders = folders.Where(f => f.Locations.Any(l => SamePath(l, directory))).ToList();
        var changed = false;

        // A dedicated library we created but whose name we lost (crash, failed sync): adopt it back.
        if (import.Mode == ImportMode.Separate && !folders.Any(f => string.Equals(f.Name, import.CreatedLibrary, StringComparison.Ordinal)))
        {
            import.CreatedLibrary = holders.FirstOrDefault(h => OnlyHolds(h, directory))?.Name;
        }

        // The admin renamed the library we merge into: it still holds our folder, under its new name.
        if (import.Mode == ImportMode.Merge && !folders.Any(f => string.Equals(f.Name, import.TargetLibrary, StringComparison.Ordinal)))
        {
            var renamed = holders.FirstOrDefault(h => h.CollectionType == ToOptions(import.CollectionType) && !OnlyHolds(h, directory));
            if (renamed is null)
            {
                // Deleted, not renamed: leave everything as it is and let the admin pick another library.
                throw new FederationException("LibraryMissing", import.TargetLibrary);
            }

            _logger.LogInformation("Bloom: library {Old} was renamed {New}", import.TargetLibrary, renamed.Name);
            import.TargetLibrary = renamed.Name;
        }

        string? wanted = import.Mode switch
        {
            ImportMode.Merge => import.TargetLibrary,
            ImportMode.Separate => import.CreatedLibrary,
            _ => null
        };

        // Detach from libraries it should no longer be part of.
        foreach (var holder in holders.Where(h => !string.Equals(h.Name, wanted, StringComparison.Ordinal)))
        {
            await DetachFromAsync(holder, directory, import).ConfigureAwait(false);
            changed = true;
        }

        if (import.Mode == ImportMode.Ignore)
        {
            return changed;
        }

        if (import.Mode == ImportMode.Merge)
        {
            var target = folders.First(f => string.Equals(f.Name, import.TargetLibrary, StringComparison.Ordinal));
            if (!target.Locations.Any(l => SamePath(l, directory)))
            {
                _logger.LogInformation("Bloom: adding {Dir} to library {Library}", directory, target.Name);
                _libraryManager.AddMediaPath(target.Name, new MediaPathInfo(directory));
                changed = true;
            }

            return changed;
        }

        // Separate library.
        var existing = folders.FirstOrDefault(f => string.Equals(f.Name, import.CreatedLibrary, StringComparison.Ordinal));
        if (existing is not null && existing.Locations.Any(l => SamePath(l, directory)))
        {
            return changed;
        }

        var name = $"{import.RemoteLibraryName} ({peerName})";
        var options = new LibraryOptions
        {
            PathInfos = [new MediaPathInfo(directory)],
            EnableRealtimeMonitor = false,
            EnableChapterImageExtraction = false,
            ExtractChapterImagesDuringLibraryScan = false,
            EnableTrickplayImageExtraction = false,
            ExtractTrickplayImagesDuringLibraryScan = false,
            SaveLocalMetadata = false
        };

        _logger.LogInformation("Bloom: creating library {Library} for {Dir}", name, directory);
        await _libraryManager.AddVirtualFolder(name, ToOptions(import.CollectionType), options, false).ConfigureAwait(false);

        // Jellyfin may have suffixed the name if it was taken: find it back by its folder.
        import.CreatedLibrary = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => f.Locations.Any(l => SamePath(l, directory)))?.Name ?? name;
        return true;
    }

    /// <summary>
    /// Removes <paramref name="directory"/> from every library (deleting libraries we created).
    /// </summary>
    /// <param name="import">Import settings.</param>
    /// <param name="directory">Folder.</param>
    /// <returns>Whether something changed.</returns>
    /// <summary>
    /// Whether a library with this name exists.
    /// </summary>
    /// <param name="name">Library name.</param>
    /// <returns>Whether it exists.</returns>
    public bool Exists(string? name)
        => name is not null && _libraryManager.GetVirtualFolders().Any(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    public async Task<bool> DetachAsync(ImportRecord import, string directory)
    {
        var changed = false;
        foreach (var holder in _libraryManager.GetVirtualFolders().Where(f => f.Locations.Any(l => SamePath(l, directory))))
        {
            await DetachFromAsync(holder, directory, import).ConfigureAwait(false);
            changed = true;
        }

        return changed;
    }

    private static bool SamePath(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            comparison);
    }

    private static bool OnlyHolds(VirtualFolderInfo folder, string directory)
        => folder.Locations.Length > 0 && folder.Locations.All(l => SamePath(l, directory));

    private static CollectionTypeOptions ToOptions(string collectionType)
        => collectionType == "tvshows" ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies;

    private async Task DetachFromAsync(VirtualFolderInfo holder, string directory, ImportRecord import)
    {
        // A library holding nothing but this friend's folder would be left empty: remove it entirely.
        if (OnlyHolds(holder, directory))
        {
            _logger.LogInformation("Bloom: removing library {Library}", holder.Name);
            await _libraryManager.RemoveVirtualFolder(holder.Name, false).ConfigureAwait(false);
            if (string.Equals(holder.Name, import.CreatedLibrary, StringComparison.Ordinal))
            {
                import.CreatedLibrary = null;
            }

            return;
        }

        _logger.LogInformation("Bloom: removing {Dir} from library {Library}", directory, holder.Name);
        _libraryManager.RemoveMediaPath(holder.Name, directory);
    }
}
