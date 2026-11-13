using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>A movie of one library, as seen by the version planner.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Federated">Whether it comes from a friend.</param>
/// <param name="Keys">Provider keys ("tmdb:27205"...).</param>
/// <param name="Width">Video width (0 if unknown).</param>
/// <param name="PrimaryVersionId">Version it is currently attached to.</param>
public sealed record VersionCandidate(Guid Id, bool Federated, IReadOnlyList<string> Keys, int Width, Guid? PrimaryVersionId);

/// <summary>
/// Decides which copies of a movie are shown as one movie with several versions: ours first (played by
/// default), then our friends'. Only friends' copies are ever attached: our own movies are never merged
/// with each other, and a friend's copy is never the one a local copy is attached to.
/// </summary>
public static class VersionPlanner
{
    /// <summary>
    /// Plans the versions of one library.
    /// </summary>
    /// <param name="movies">Every movie of the library, alternate versions included.</param>
    /// <param name="enabled">Whether versions are grouped at all (false: every friend's copy stands alone).</param>
    /// <returns>For every friend's copy, the movie it is a version of (null: stands alone).</returns>
    public static Dictionary<Guid, Guid?> Plan(IReadOnlyList<VersionCandidate> movies, bool enabled)
    {
        var plan = movies.Where(m => m.Federated).ToDictionary(m => m.Id, _ => (Guid?)null);
        if (!enabled)
        {
            return plan;
        }

        foreach (var group in Groups(movies).Where(g => g.Count > 1 && g.Any(m => m.Federated)))
        {
            var primary = PickPrimary(group);
            foreach (var copy in group.Where(m => m.Federated && m.Id != primary.Id))
            {
                plan[copy.Id] = primary.Id;
            }
        }

        return plan;
    }

    private static VersionCandidate PickPrimary(List<VersionCandidate> group)
    {
        var ids = group.Select(m => m.Id).ToHashSet();
        var locals = group.Where(m => !m.Federated).ToList();
        if (locals.Count > 0)
        {
            // Our movie (not one of its own versions), the best one if we have several.
            return locals
                .OrderBy(m => m.PrimaryVersionId is Guid p && ids.Contains(p) ? 1 : 0)
                .ThenByDescending(m => m.Width)
                .ThenBy(m => m.Id)
                .First();
        }

        // Only friends have it: keep the current choice when there is one, so nothing moves around.
        var current = group.FirstOrDefault(m => m.PrimaryVersionId is null && group.Any(o => o.PrimaryVersionId == m.Id));
        return current ?? group.OrderByDescending(m => m.Width).ThenBy(m => m.Id).First();
    }

    /// <summary>
    /// Groups movies sharing a provider id (directly or through another copy).
    /// </summary>
    private static IEnumerable<List<VersionCandidate>> Groups(IReadOnlyList<VersionCandidate> movies)
    {
        var parent = Enumerable.Range(0, movies.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < movies.Count; i++)
        {
            foreach (var key in movies[i].Keys)
            {
                if (byKey.TryGetValue(key, out var other))
                {
                    parent[Find(i)] = Find(other);
                }
                else
                {
                    byKey[key] = i;
                }
            }
        }

        return Enumerable.Range(0, movies.Count)
            .GroupBy(Find)
            .Select(g => g.Select(i => movies[i]).ToList());
    }
}
