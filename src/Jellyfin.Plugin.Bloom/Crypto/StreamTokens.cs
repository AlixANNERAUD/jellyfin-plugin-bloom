using System;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Bloom.Crypto;

/// <summary>
/// Capability tokens embedded in stream URLs. Media players and ffmpeg cannot sign requests,
/// so the provider hands out an HMAC per (friend, item, period) computed with a per-friend secret.
/// A token works during the 30-day period it was issued in and the next one: friends get fresh ones at each
/// sync, and a stream URL that leaked (copied by a user, written in a proxy log) stops working after 30 to 60 days.
/// Removing the friend, or pairing again, changes the secret and revokes every token at once.
/// </summary>
public static class StreamTokens
{
    /// <summary>Length of a validity period.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromDays(30);

    /// <summary>
    /// Creates a new per-friend secret.
    /// </summary>
    /// <returns>Base64 secret.</returns>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Computes the token allowing <paramref name="itemId"/> to be streamed during the current period.
    /// </summary>
    /// <param name="secret">Per-friend secret (base64).</param>
    /// <param name="itemId">Item id.</param>
    /// <returns>Base64url token.</returns>
    public static string Compute(string secret, Guid itemId) => Compute(secret, itemId, PeriodOf(DateTimeOffset.UtcNow));

    /// <summary>
    /// Verifies a token in constant time: it must have been issued in the current period or the previous one.
    /// </summary>
    /// <param name="secret">Per-friend secret (base64).</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="token">Presented token.</param>
    /// <returns>Whether the token is valid.</returns>
    public static bool Verify(string secret, Guid itemId, string? token) => Verify(secret, itemId, token, DateTimeOffset.UtcNow);

    /// <summary>
    /// Verifies a token as of a given time.
    /// </summary>
    /// <param name="secret">Per-friend secret (base64).</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="token">Presented token.</param>
    /// <param name="now">Current time.</param>
    /// <returns>Whether the token is valid.</returns>
    internal static bool Verify(string secret, Guid itemId, string? token, DateTimeOffset now)
    {
        var presented = FedProtocol.TryDecodeBase64Url(token);
        if (presented is null)
        {
            return false;
        }

        var period = PeriodOf(now);
        return Matches(presented, Compute(secret, itemId, period)) | Matches(presented, Compute(secret, itemId, period - 1));
    }

    /// <summary>
    /// Computes the token of a given period.
    /// </summary>
    /// <param name="secret">Per-friend secret (base64).</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="period">Period number (see <see cref="PeriodOf"/>).</param>
    /// <returns>Base64url token.</returns>
    internal static string Compute(string secret, Guid itemId, long period)
    {
        var input = "stream\n" + itemId.ToString("N") + "\n" + period.ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Convert.FromBase64String(secret), Encoding.UTF8.GetBytes(input));
        return Base64Url.EncodeToString(mac.AsSpan(0, 20));
    }

    /// <summary>
    /// Number of the period a time falls in.
    /// </summary>
    /// <param name="time">Time.</param>
    /// <returns>Period number.</returns>
    internal static long PeriodOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Period.TotalSeconds;

    private static bool Matches(byte[] presented, string expected)
        => CryptographicOperations.FixedTimeEquals(presented, FedProtocol.TryDecodeBase64Url(expected)!);
}
