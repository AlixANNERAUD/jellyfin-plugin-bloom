using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Makes sure a friend's server is up before we play one of its movies: a server that sleeps when idle is started by
/// the first request and answers 503 meanwhile, which players show as an error instead of waiting.
/// </summary>
public sealed class PeerWaker
{
    private readonly PeerClient _client;
    private readonly ILogger<PeerWaker> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _waking = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerWaker"/> class.
    /// </summary>
    /// <param name="client">Peer client.</param>
    /// <param name="logger">Logger.</param>
    public PeerWaker(PeerClient client, ILogger<PeerWaker> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Wakes the friend's server up if needed and waits until it answers (at most <see cref="Waking.Budget"/>).
    /// A server that is up answers right away, so this costs one small request per play. Several plays at once
    /// share the same wait.
    /// </summary>
    /// <param name="peer">Friend.</param>
    /// <param name="cancellationToken">Stops waiting (the wake-up itself goes on).</param>
    /// <returns>Whether the friend's server is up.</returns>
    public async Task<bool> WaitUpAsync(PeerRecord peer, CancellationToken cancellationToken)
    {
        var endpoint = PeerEndpoint.From(peer);
        var lazy = _waking.GetOrAdd(peer.Id, _ => new Lazy<Task<bool>>(() => WakeAsync(endpoint)));
        var task = lazy.Value;
        _ = task.ContinueWith(_ => _waking.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(peer.Id, lazy)), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WakeAsync(PeerEndpoint peer)
    {
        var started = Stopwatch.GetTimestamp();
        var up = await _client.WakeAsync(peer, CancellationToken.None).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (up && elapsed > TimeSpan.FromSeconds(3))
        {
            _logger.LogInformation("Bloom: {Url} was asleep, it woke up in {Seconds:0}s", peer.Url, elapsed.TotalSeconds);
        }
        else if (!up)
        {
            _logger.LogWarning("Bloom: {Url} is not answering, playback will probably fail", peer.Url);
        }

        return up;
    }
}
