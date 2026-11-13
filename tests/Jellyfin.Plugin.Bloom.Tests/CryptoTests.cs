using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Bloom.Crypto;
using Jellyfin.Plugin.Bloom.Models;
using Jellyfin.Plugin.Bloom.Services;
using Xunit;

namespace Jellyfin.Plugin.Bloom.Tests;

public class CryptoTests
{
    [Fact]
    public void SignaturesVerifyOnlyWithTheRightKeyAndData()
    {
        using var alice = ServerIdentity.CreateEphemeral();
        using var mallory = ServerIdentity.CreateEphemeral();
        var data = Encoding.UTF8.GetBytes("hello");
        var signature = alice.Sign(data);

        Assert.True(ServerIdentity.Verify(alice.PublicKey, data, signature));
        Assert.False(ServerIdentity.Verify(mallory.PublicKey, data, signature));
        Assert.False(ServerIdentity.Verify(alice.PublicKey, Encoding.UTF8.GetBytes("hellO"), signature));
        Assert.False(ServerIdentity.Verify("not a key", data, signature));
    }

    [Fact]
    public void IdAndFingerprintDeriveFromTheKey()
    {
        using var alice = ServerIdentity.CreateEphemeral();
        Assert.Equal(alice.ServerId, ServerIdentity.ComputeServerId(alice.PublicKey));
        Assert.Equal(32, alice.ServerId.Length);
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){7}$", alice.Fingerprint);
        Assert.Equal(alice.ServerId, alice.Fingerprint.Replace("-", string.Empty).ToLowerInvariant());
    }

    [Fact]
    public void IdentityIsPersisted()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "identity.key");
            string id;
            using (var first = ServerIdentity.LoadOrCreate(path))
            {
                id = first.ServerId;
            }

            using var second = ServerIdentity.LoadOrCreate(path);
            Assert.Equal(id, second.ServerId);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Theory]
    [InlineData("/Bloom/Peer/Catalog", "/Bloom/Peer/Catalog")]
    [InlineData("/jellyfin/Bloom/Peer/Catalog", "/Bloom/Peer/Catalog")]
    [InlineData("/a/b/bloom/peer/Pair", "/bloom/peer/Pair")]
    [InlineData("/bloom/Bloom/Peer/Info", "/Bloom/Peer/Info")]
    public void CanonicalPathStripsReverseProxyPrefixes(string path, string expected)
        => Assert.Equal(expected, FedProtocol.CanonicalPath(path));

    [Fact]
    public void RequestSigningInputCoversEveryField()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var reference = FedProtocol.RequestSigningInput("GET", "/Bloom/Peer/Catalog", "?start=0", "aud", "snd", 1, "n", body);
        Assert.NotEqual(reference, FedProtocol.RequestSigningInput("POST", "/Bloom/Peer/Catalog", "?start=0", "aud", "snd", 1, "n", body));
        Assert.NotEqual(reference, FedProtocol.RequestSigningInput("GET", "/Bloom/Peer/Catalog", "?start=1", "aud", "snd", 1, "n", body));
        Assert.NotEqual(reference, FedProtocol.RequestSigningInput("GET", "/Bloom/Peer/Catalog", "?start=0", "other", "snd", 1, "n", body));
        Assert.NotEqual(reference, FedProtocol.RequestSigningInput("GET", "/Bloom/Peer/Catalog", "?start=0", "aud", "snd", 2, "n", body));
        Assert.NotEqual(reference, FedProtocol.RequestSigningInput("GET", "/Bloom/Peer/Catalog", "?start=0", "aud", "snd", 1, "n", Encoding.UTF8.GetBytes("{ }")));
    }

    [Fact]
    public void StreamTokensAreBoundToSecretAndItem()
    {
        var secret = StreamTokens.NewSecret();
        var item = Guid.NewGuid();
        var token = StreamTokens.Compute(secret, item);

        Assert.True(StreamTokens.Verify(secret, item, token));
        Assert.False(StreamTokens.Verify(secret, Guid.NewGuid(), token));
        Assert.False(StreamTokens.Verify(StreamTokens.NewSecret(), item, token));
        Assert.False(StreamTokens.Verify(secret, item, null));
        Assert.False(StreamTokens.Verify(secret, item, "%%%"));
        Assert.False(StreamTokens.Verify(secret, item, token[..^2]));
    }

    [Fact]
    public void NoncesCannotBeReused()
    {
        var cache = new NonceCache();
        Assert.True(cache.TryUse("a", "n1"));
        Assert.False(cache.TryUse("a", "n1"));
        Assert.True(cache.TryUse("b", "n1"));
    }

    private static string Encode(InviteCode invite)
        => FedProtocol.InvitePrefix + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(invite, Json.Options));

    private static InviteCode ValidInvite(ServerIdentity identity) => new()
    {
        V = FedProtocol.Version,
        Id = identity.ServerId,
        Name = "Alice",
        Url = "https://alice.example/",
        Key = identity.PublicKey,
        Token = FedProtocol.RandomToken(),
        Exp = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()
    };

    [Fact]
    public void ValidInviteDecodes()
    {
        using var alice = ServerIdentity.CreateEphemeral();
        var decoded = PairingService.DecodeInvite(Encode(ValidInvite(alice)) + "\n");
        Assert.Equal(alice.ServerId, decoded.Id);
        Assert.Equal("https://alice.example", decoded.Url);
    }

    [Fact]
    public void InviteWithSwappedKeyIsRejected()
    {
        using var alice = ServerIdentity.CreateEphemeral();
        using var mallory = ServerIdentity.CreateEphemeral();
        var invite = ValidInvite(alice);
        invite.Key = mallory.PublicKey;
        Assert.Throws<FederationException>(() => PairingService.DecodeInvite(Encode(invite)));
    }

    [Fact]
    public void ExpiredOrMalformedInvitesAreRejected()
    {
        using var alice = ServerIdentity.CreateEphemeral();
        var expired = ValidInvite(alice);
        expired.Exp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        Assert.Throws<FederationException>(() => PairingService.DecodeInvite(Encode(expired)));

        var badUrl = ValidInvite(alice);
        badUrl.Url = "file:///etc/passwd";
        Assert.Throws<FederationException>(() => PairingService.DecodeInvite(Encode(badUrl)));

        Assert.Throws<FederationException>(() => PairingService.DecodeInvite("hello"));
        Assert.Throws<FederationException>(() => PairingService.DecodeInvite(FedProtocol.InvitePrefix + "!!!"));
    }

    [Theory]
    [InlineData("https://jf.example.com/", "https://jf.example.com")]
    [InlineData(" http://10.0.0.2:8096 ", "http://10.0.0.2:8096")]
    [InlineData("https://example.com/jellyfin/", "https://example.com/jellyfin")]
    [InlineData("ftp://example.com", null)]
    [InlineData("https://example.com/?a=b", null)]
    [InlineData("example.com", null)]
    public void UrlsAreNormalized(string input, string? expected)
        => Assert.Equal(expected, PairingService.NormalizeUrl(input));
}
