using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.Bloom.Crypto;

/// <summary>
/// Remembers recently seen request nonces so a captured request cannot be replayed.
/// </summary>
public sealed class NonceCache
{
    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private DateTime _lastPurge = DateTime.UtcNow;

    /// <summary>
    /// Records a nonce.
    /// </summary>
    /// <param name="sender">Sender id.</param>
    /// <param name="nonce">Nonce.</param>
    /// <returns>False if it was already used.</returns>
    public bool TryUse(string sender, string nonce)
    {
        var now = DateTime.UtcNow;
        if (now - _lastPurge > TimeSpan.FromMinutes(5))
        {
            _lastPurge = now;
            var limit = now - (FedProtocol.MaxClockSkew * 2);
            foreach (var key in _seen.Where(kv => kv.Value < limit).Select(kv => kv.Key).ToList())
            {
                _seen.TryRemove(key, out _);
            }
        }

        return _seen.TryAdd(sender + "|" + nonce, now);
    }
}
