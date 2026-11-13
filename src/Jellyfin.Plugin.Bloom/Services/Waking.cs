using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Waits for a friend's server that is starting. Jellyfin answers 503 until it has started, and so do proxies that
/// stop idle servers and start them again on the next request (the first request wakes the server up and gets a 503).
/// </summary>
internal static class Waking
{
    /// <summary>How long a request waits for a starting server before giving up.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether an answer means "not ready yet": the server is starting, or the proxy in front of it cannot reach it.
    /// </summary>
    /// <param name="status">HTTP status.</param>
    /// <returns>Whether to try again.</returns>
    public static bool IsWaking(HttpStatusCode status)
        => status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Delay before the next try: the server's Retry-After if it gives one, else 1, 2, 3, 4 then 5 seconds.
    /// </summary>
    /// <param name="response">"Not ready" answer.</param>
    /// <param name="attempt">Number of tries so far (from 1).</param>
    /// <returns>The delay.</returns>
    public static TimeSpan NextDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(Math.Min(attempt, 5)));
        return delay < MinDelay ? MinDelay : delay > MaxDelay ? MaxDelay : delay;
    }

    /// <summary>
    /// Sends a request, and sends it again while the server answers that it is not ready, for up to <paramref name="budget"/>.
    /// Connection errors are not retried: a server that cannot be reached at all is off, not starting.
    /// </summary>
    /// <param name="send">Sends one try (builds a new request each time).</param>
    /// <param name="budget">How long to keep trying.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onRetry">Called before each new try, with the last status and the delay.</param>
    /// <param name="delay">Waits between tries (replaced in tests).</param>
    /// <returns>The first answer that is not "not ready", or the last one when the budget runs out.</returns>
    public static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        TimeSpan budget,
        CancellationToken cancellationToken,
        Action<HttpStatusCode, TimeSpan>? onRetry = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var started = Stopwatch.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            var response = await send(cancellationToken).ConfigureAwait(false);
            if (!IsWaking(response.StatusCode))
            {
                return response;
            }

            var wait = NextDelay(response, attempt);
            if (Stopwatch.GetElapsedTime(started) + wait > budget)
            {
                return response;
            }

            onRetry?.Invoke(response.StatusCode, wait);
            response.Dispose();
            await delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
