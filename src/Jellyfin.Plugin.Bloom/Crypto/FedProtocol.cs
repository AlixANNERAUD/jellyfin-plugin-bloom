using System;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Bloom.Crypto;

/// <summary>
/// Wire protocol constants and canonical forms for signed server-to-server messages.
/// </summary>
public static class FedProtocol
{
    /// <summary>Protocol version.</summary>
    public const int Version = 1;

    /// <summary>Prefix of invite codes.</summary>
    public const string InvitePrefix = "bloom1:";

    /// <summary>Header carrying the sender's server id.</summary>
    public const string HeaderSender = "X-Bloom-Sender";

    /// <summary>Header carrying the unix timestamp (seconds).</summary>
    public const string HeaderTimestamp = "X-Bloom-Timestamp";

    /// <summary>Header carrying a random nonce.</summary>
    public const string HeaderNonce = "X-Bloom-Nonce";

    /// <summary>Header carrying the signature (base64url).</summary>
    public const string HeaderSignature = "X-Bloom-Signature";

    /// <summary>Maximum accepted clock skew.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);

    private const string RouteMarker = "/Bloom/Peer/";

    /// <summary>
    /// Returns the part of a request path that is signed: everything from "/Bloom/Peer/" on,
    /// so that reverse proxies adding a prefix (e.g. "/jellyfin", or even "/bloom") don't break signatures.
    /// </summary>
    /// <param name="fullPath">Full request path.</param>
    /// <returns>The canonical path.</returns>
    public static string CanonicalPath(string fullPath)
    {
        var idx = fullPath.LastIndexOf(RouteMarker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? fullPath[idx..] : fullPath;
    }

    /// <summary>
    /// Builds the bytes signed for a request.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Canonical path.</param>
    /// <param name="query">Raw query string including '?', or empty.</param>
    /// <param name="audience">Server id of the recipient.</param>
    /// <param name="sender">Server id of the sender.</param>
    /// <param name="timestamp">Unix timestamp.</param>
    /// <param name="nonce">Nonce.</param>
    /// <param name="body">Request body.</param>
    /// <returns>Bytes to sign.</returns>
    public static byte[] RequestSigningInput(string method, string path, string query, string audience, string sender, long timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        var text = string.Join(
            '\n',
            "BLOOM-REQ-1",
            method.ToUpperInvariant(),
            path,
            query,
            audience,
            sender,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            Convert.ToHexStringLower(SHA256.HashData(body)));
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Builds the bytes signed for a response. Bound to the request nonce to prevent replays.
    /// </summary>
    /// <param name="responder">Server id of the responder.</param>
    /// <param name="requestNonce">Nonce of the request being answered.</param>
    /// <param name="status">HTTP status code.</param>
    /// <param name="body">Response body.</param>
    /// <returns>Bytes to sign.</returns>
    public static byte[] ResponseSigningInput(string responder, string requestNonce, int status, ReadOnlySpan<byte> body)
    {
        var text = string.Join(
            '\n',
            "BLOOM-RESP-1",
            responder,
            requestNonce,
            status.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(SHA256.HashData(body)));
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Creates a random URL-safe token.
    /// </summary>
    /// <param name="bytes">Entropy in bytes.</param>
    /// <returns>The token.</returns>
    public static string RandomToken(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>
    /// Hashes a secret for storage.
    /// </summary>
    /// <param name="secret">Secret.</param>
    /// <returns>Hex SHA-256.</returns>
    public static string HashSecret(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Decodes base64url, returning null when malformed.
    /// </summary>
    /// <param name="value">Encoded value.</param>
    /// <returns>Bytes or null.</returns>
    public static byte[]? TryDecodeBase64Url(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
