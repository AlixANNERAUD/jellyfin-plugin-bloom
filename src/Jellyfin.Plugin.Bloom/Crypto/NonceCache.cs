using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.Bloom.Crypto;

/// <summary>
/// Remembers recently seen request nonces so a captured request cannot be replayed.
/// </summary>
public sealed class NonceCache
{
    /// <summary>Far more than friends ever send in <see cref="FedProtocol.MaxClockSkew"/> (a sync is a few hundred requests).</summary>
    internal const int Capacity = 100_000;

    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly object _purgeLock = new();
    private DateTime _lastPurge = DateTime.UtcNow;

    /// <summary>Gets a value indicating whether the cache is full (requests are refused until old nonces expire).</summary>
    public bool IsFull => _seen.Count >= Capacity;

    /// <summary>
    /// Records a nonce.
    /// </summary>
    /// <param name="sender">Sender id.</param>
    /// <param name="nonce">Nonce.</param>
    /// <returns>False if it was already used, or if too many requests arrived recently to remember another one.</returns>
    public bool TryUse(string sender, string nonce)
    {
        var now = DateTime.UtcNow;
        if (now - _lastPurge > TimeSpan.FromMinutes(1) || _seen.Count >= Capacity)
        {
            Purge(now);
        }

        // A full cache refuses rather than grows: a flood of requests cannot exhaust memory, and a request
        // refused here is retried by the friend's server later.
        return _seen.Count < Capacity && _seen.TryAdd(sender + "|" + nonce, now);
    }

    private void Purge(DateTime now)
    {
        lock (_purgeLock)
        {
            _lastPurge = now;

            // A nonce only needs remembering while its timestamp is accepted (MaxClockSkew either way).
            var limit = now - (FedProtocol.MaxClockSkew * 2);
            foreach (var key in _seen.Where(kv => kv.Value < limit).Select(kv => kv.Key).ToList())
            {
                _seen.TryRemove(key, out _);
            }
        }
    }
}
