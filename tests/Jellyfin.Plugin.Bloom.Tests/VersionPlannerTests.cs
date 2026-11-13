using Jellyfin.Plugin.Bloom.Services;
using Xunit;

namespace Jellyfin.Plugin.Bloom.Tests;

public sealed class VersionPlannerTests
{
    private static readonly Guid Mine = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid MineToo = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Claire = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static VersionCandidate Movie(Guid id, bool federated, int width, Guid? primary = null, params string[] keys)
        => new(id, federated, keys.Length == 0 ? ["tmdb:1"] : keys, width, primary);

    [Fact]
    public void AFriendsCopyBecomesAVersionOfOurs()
    {
        // Even when the friend's copy is better: ours stays the movie (and plays by default).
        var plan = VersionPlanner.Plan([Movie(Mine, false, 1920), Movie(Alice, true, 3840)], enabled: true);

        Assert.Equal(Mine, plan[Alice]);
        Assert.False(plan.ContainsKey(Mine));
    }

    [Fact]
    public void CopiesOfSeveralFriendsAreGroupedBehindTheBestOne()
    {
        var plan = VersionPlanner.Plan([Movie(Alice, true, 1920), Movie(Claire, true, 3840)], enabled: true);

        Assert.Equal(Claire, plan[Alice]);
        Assert.Null(plan[Claire]);
    }

    [Fact]
    public void AnExistingGroupOfFriendsCopiesDoesNotMoveAround()
    {
        var plan = VersionPlanner.Plan([Movie(Alice, true, 1920), Movie(Claire, true, 3840, primary: Alice)], enabled: true);

        Assert.Null(plan[Alice]);
        Assert.Equal(Alice, plan[Claire]);
    }

    [Fact]
    public void WhenWeGetTheMovieOurCopyTakesOver()
    {
        var plan = VersionPlanner.Plan([Movie(Alice, true, 1920), Movie(Claire, true, 3840, primary: Alice), Movie(Mine, false, 1280)], enabled: true);

        Assert.Equal(Mine, plan[Alice]);
        Assert.Equal(Mine, plan[Claire]);
    }

    [Fact]
    public void OurOwnVersionsAreLeftAsTheyAre()
    {
        // MineToo is one of our versions of Mine: the friend's copy joins Mine, nothing else changes.
        var plan = VersionPlanner.Plan([Movie(MineToo, false, 3840, primary: Mine), Movie(Mine, false, 1920), Movie(Alice, true, 1280)], enabled: true);

        Assert.Equal(Mine, plan[Alice]);
        Assert.Single(plan);
    }

    [Fact]
    public void CopiesAreMatchedThroughAnyProviderId()
    {
        var plan = VersionPlanner.Plan(
            [Movie(Mine, false, 1920, null, "imdb:tt1"), Movie(Alice, true, 1920, null, "tmdb:1", "imdb:tt1"), Movie(Claire, true, 1920, null, "tmdb:1")],
            enabled: true);

        Assert.Equal(Mine, plan[Alice]);
        Assert.Equal(Mine, plan[Claire]);
    }

    [Fact]
    public void DifferentMoviesAreNeverGrouped()
    {
        var plan = VersionPlanner.Plan([Movie(Mine, false, 1920, null, "tmdb:1"), Movie(Alice, true, 1920, null, "tmdb:2"), Movie(Claire, true, 1920, null)], enabled: true);

        Assert.Null(plan[Alice]);
        Assert.Equal(Mine, plan[Claire]);
    }

    [Fact]
    public void TurningItOffDetachesEveryFriendsCopy()
    {
        var plan = VersionPlanner.Plan([Movie(Mine, false, 1920), Movie(Alice, true, 3840, primary: Mine)], enabled: false);

        Assert.Null(plan[Alice]);
    }

    [Fact]
    public void EpisodesAreMatchedBySeriesAndNumbers()
    {
        // Keys as the linker builds them: "<series provider id>/s<season>e<episode>".
        var plan = VersionPlanner.Plan(
            [
                Movie(Mine, false, 1280, null, "tvdb:79175/s1e2"),
                Movie(Alice, true, 1920, null, "tvdb:79175/s1e2", "tmdb:1234/s1e2"),
                Movie(Claire, true, 1920, null, "tvdb:79175/s1e3")
            ],
            enabled: true);

        Assert.Equal(Mine, plan[Alice]);
        Assert.Null(plan[Claire]);
    }
}
