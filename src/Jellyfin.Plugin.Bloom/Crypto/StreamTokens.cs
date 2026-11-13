using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Bloom.Crypto;

/// <summary>
/// Capability tokens embedded in stream URLs. Media players and ffmpeg cannot sign requests,
/// so the provider hands out an HMAC per (friend, item) computed with a per-friend secret.
/// Removing the friend (or rotating the secret) revokes every token at once.
/// </summary>
public static class StreamTokens
{
    /// <summary>
    /// Creates a new per-friend secret.
    /// </summary>
    /// <returns>Base64 secret.</returns>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Computes the token allowing <paramref name="itemId"/> to be streamed.
    /// </summary>
    /// <param name="secret">Per-friend secret (base64).</param>
    /// <param name="itemId">Item id.</param>
    /// <returns>Base64url token.</returns>
    public static string Compute(string secret, Guid itemId)
    {
        var mac = HMACSHA256.HashData(Convert.FromBase64String(secret), Encoding.UTF8.GetBytes("stream\n" + itemId.ToString("N")));
        return Base64Url.EncodeToString(mac.AsSpan(0, 20));
    }

    /// <summary>
    /// Verifies a token in constant time.
    /// </summary>
    /// <param name="secret">Per-friend secret (base64).</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="token">Presented token.</param>
    /// <returns>Whether the token is valid.</returns>
    public static bool Verify(string secret, Guid itemId, string? token)
    {
        var presented = FedProtocol.TryDecodeBase64Url(token);
        if (presented is null)
        {
            return false;
        }

        var expected = FedProtocol.TryDecodeBase64Url(Compute(secret, itemId))!;
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
