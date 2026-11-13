using System;
using System.IO;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Bloom.Crypto;

/// <summary>
/// The long-term identity of this server: an ECDSA P-256 key pair.
/// The server id and the human-readable fingerprint are both derived from the public key,
/// so an id can never be claimed without holding the matching private key.
/// </summary>
public sealed class ServerIdentity : IDisposable
{
    private readonly ECDsa _key;
    private readonly object _lock = new();

    private ServerIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        ServerId = ComputeServerId(PublicKey);
        Fingerprint = ComputeFingerprint(PublicKey);
    }

    /// <summary>Gets the base64 SubjectPublicKeyInfo of this server.</summary>
    public string PublicKey { get; }

    /// <summary>Gets the server id (hex, derived from the public key).</summary>
    public string ServerId { get; }

    /// <summary>Gets the fingerprint to compare out-of-band (e.g. by phone).</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Creates an ephemeral identity (tests).
    /// </summary>
    /// <returns>A new identity.</returns>
    public static ServerIdentity CreateEphemeral() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>
    /// Loads the identity from disk, creating it on first start.
    /// </summary>
    /// <param name="path">Path of the PKCS#8 key file.</param>
    /// <returns>The identity.</returns>
    public static ServerIdentity LoadOrCreate(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(path).Trim()), out _);
            return new ServerIdentity(key);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
        return new ServerIdentity(key);
    }

    /// <summary>
    /// Computes the server id of a public key.
    /// </summary>
    /// <param name="publicKey">Base64 SubjectPublicKeyInfo.</param>
    /// <returns>32 lowercase hex chars.</returns>
    public static string ComputeServerId(string publicKey)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(publicKey));
        return Convert.ToHexStringLower(hash, 0, 16);
    }

    /// <summary>
    /// Computes a human-friendly fingerprint ("1A2B-3C4D-...") of a public key.
    /// </summary>
    /// <param name="publicKey">Base64 SubjectPublicKeyInfo.</param>
    /// <returns>The fingerprint.</returns>
    public static string ComputeFingerprint(string publicKey)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey)), 0, 16);
        var groups = new string[8];
        for (var i = 0; i < 8; i++)
        {
            groups[i] = hex.Substring(i * 4, 4);
        }

        return string.Join('-', groups);
    }

    /// <summary>
    /// Checks that a public key is a valid P-256 SubjectPublicKeyInfo.
    /// </summary>
    /// <param name="publicKey">Base64 SubjectPublicKeyInfo.</param>
    /// <returns>Whether it can be used.</returns>
    public static bool IsValidPublicKey(string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ecdsa.KeySize == 256;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies a signature produced by <see cref="Sign"/>.
    /// </summary>
    /// <param name="publicKey">Base64 SubjectPublicKeyInfo of the signer.</param>
    /// <param name="data">Signed data.</param>
    /// <param name="signature">Signature (IEEE P1363).</param>
    /// <returns>Whether the signature is valid.</returns>
    public static bool Verify(string publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Signs data with this server's private key.
    /// </summary>
    /// <param name="data">Data to sign.</param>
    /// <returns>Signature (IEEE P1363, 64 bytes).</returns>
    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            return _key.SignData(data, HashAlgorithmName.SHA256);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}
