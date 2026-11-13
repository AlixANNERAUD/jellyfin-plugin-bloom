using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.Bloom.Models;
using Jellyfin.Plugin.Bloom.Services;
using Xunit;

namespace Jellyfin.Plugin.Bloom.Tests;

public class WakingTests
{
    private readonly List<TimeSpan> _waits = [];

    [Fact]
    public async Task WaitsWhileTheServerIsStarting()
    {
        var answers = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
        var sent = 0;

        using var response = await Send(() => { sent++; return Answer(answers.Dequeue()); }, TimeSpan.FromMinutes(2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, sent);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)], _waits);
    }

    [Fact]
    public async Task OtherAnswersAreReturnedRightAway()
    {
        var sent = 0;

        using var response = await Send(() => { sent++; return Answer(HttpStatusCode.NotFound); }, TimeSpan.FromMinutes(2));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, sent);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task GivesUpWhenTheBudgetRunsOut()
    {
        var sent = 0;

        using var response = await Send(() => { sent++; return Answer(HttpStatusCode.ServiceUnavailable); }, TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task FollowsRetryAfterWithinLimits()
    {
        var answers = new Queue<HttpResponseMessage>([
            Answer(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(4)),
            Answer(HttpStatusCode.ServiceUnavailable, TimeSpan.FromMinutes(5)),
            Answer(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero),
            Answer(HttpStatusCode.OK)
        ]);

        using var response = await Send(answers.Dequeue, TimeSpan.FromMinutes(2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1)], _waits);
    }

    [Fact]
    public void AFriendThatSleepsIsAsleepUnlessHeardFromRecently()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(new PeerRecord { Sleeps = false }.IsAsleep(now));
        Assert.True(new PeerRecord { Sleeps = true }.IsAsleep(now));
        Assert.True(new PeerRecord { Sleeps = true, LastSeen = now.AddMinutes(-10) }.IsAsleep(now));
        Assert.False(new PeerRecord { Sleeps = true, LastSeen = now.AddSeconds(-30) }.IsAsleep(now));
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is TimeSpan delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }

    private Task<HttpResponseMessage> Send(Func<HttpResponseMessage> answer, TimeSpan budget)
        => Waking.SendAsync(
            _ => Task.FromResult(answer()),
            budget,
            CancellationToken.None,
            delay: (wait, _) =>
            {
                _waits.Add(wait);
                return Task.CompletedTask;
            });
}
