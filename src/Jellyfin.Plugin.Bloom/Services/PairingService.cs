using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Crypto;
using Jellyfin.Plugin.Bloom.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Error shown to the admin (or returned to a friend) as is.
/// </summary>
public sealed class FederationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FederationException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    public FederationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Friendship establishment: invite codes and the key exchange.
/// <para>
/// 1. Alice creates an invite: her URL, public key and a one-time token, sent to Bob through a trusted channel.<br/>
/// 2. Bob accepts it: his server sends its own URL and public key to Alice's server, signed with his key, with the token.<br/>
/// 3. Alice's server checks the token, stores Bob's key and answers with a response signed by her key,
///    which Bob's server checks against the key from the invite (no man in the middle possible).<br/>
/// From then on, every request between the two servers is signed and verified.
/// </para>
/// </summary>
public sealed class PairingService
{
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    private readonly FederationStore _store;
    private readonly ServerIdentity _identity;
    private readonly PeerClient _client;
    private readonly ILogger<PairingService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingService"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="identity">Our identity.</param>
    /// <param name="client">Peer client.</param>
    /// <param name="logger">Logger.</param>
    public PairingService(FederationStore store, ServerIdentity identity, PeerClient client, ILogger<PairingService> logger)
    {
        _store = store;
        _identity = identity;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Normalizes and validates a public URL.
    /// </summary>
    /// <param name="url">URL.</param>
    /// <returns>Normalized URL, or null if invalid.</returns>
    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>
    /// Decodes and validates an invite code.
    /// </summary>
    /// <param name="code">Code.</param>
    /// <returns>The invite.</returns>
    public static InviteCode DecodeInvite(string? code)
    {
        code = code?.Trim();
        if (string.IsNullOrEmpty(code) || !code.StartsWith(FedProtocol.InvitePrefix, StringComparison.Ordinal))
        {
            throw new FederationException("Ce n'est pas un code d'invitation Bloom.");
        }

        var bytes = FedProtocol.TryDecodeBase64Url(code[FedProtocol.InvitePrefix.Length..]) ?? throw new FederationException("Code d'invitation corrompu.");
        InviteCode? invite;
        try
        {
            invite = JsonSerializer.Deserialize<InviteCode>(bytes, Json.Options);
        }
        catch (JsonException)
        {
            invite = null;
        }

        if (invite is null || invite.V != FedProtocol.Version || string.IsNullOrEmpty(invite.Token))
        {
            throw new FederationException("Code d'invitation corrompu ou d'une version incompatible.");
        }

        if (!ServerIdentity.IsValidPublicKey(invite.Key) || ServerIdentity.ComputeServerId(invite.Key) != invite.Id)
        {
            throw new FederationException("La clé contenue dans l'invitation est invalide.");
        }

        invite.Url = NormalizeUrl(invite.Url) ?? throw new FederationException("L'URL contenue dans l'invitation est invalide.");
        if (DateTimeOffset.FromUnixTimeSeconds(invite.Exp) < DateTimeOffset.UtcNow)
        {
            throw new FederationException("Cette invitation a expiré, demandez-en une nouvelle.");
        }

        return invite;
    }

    /// <summary>
    /// Creates an invite code.
    /// </summary>
    /// <param name="sharedLibraryIds">Libraries the new friend will see.</param>
    /// <param name="label">Free label.</param>
    /// <returns>The code and its expiry.</returns>
    public (string Code, DateTime ExpiresUtc) CreateInvite(List<Guid> sharedLibraryIds, string? label)
    {
        var settings = _store.Snapshot().Settings;
        var url = NormalizeUrl(settings.PublicUrl) ?? throw new FederationException("Renseignez d'abord l'URL publique de votre serveur (celle que vos amis peuvent joindre).");
        var token = FedProtocol.RandomToken(32);
        var expires = DateTime.UtcNow.Add(InviteLifetime);

        _store.Update(s =>
        {
            s.Invites.RemoveAll(i => i.ExpiresUtc < DateTime.UtcNow);
            s.Invites.Add(new PendingInvite
            {
                TokenHash = FedProtocol.HashSecret(token),
                ExpiresUtc = expires,
                SharedLibraryIds = sharedLibraryIds.Distinct().ToList(),
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()
            });
        });

        var invite = new InviteCode
        {
            V = FedProtocol.Version,
            Id = _identity.ServerId,
            Name = _store.GetDisplayName(),
            Url = url,
            Key = _identity.PublicKey,
            Token = token,
            Exp = new DateTimeOffset(expires).ToUnixTimeSeconds()
        };

        return (FedProtocol.InvitePrefix + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(invite, Json.Options)), expires);
    }

    /// <summary>
    /// Accepts a friend's invite: performs the key exchange with their server.
    /// </summary>
    /// <param name="code">Invite code.</param>
    /// <param name="sharedLibraryIds">Our libraries shared back with them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new friend.</returns>
    public async Task<PeerRecord> AcceptInviteAsync(string code, List<Guid> sharedLibraryIds, CancellationToken cancellationToken)
    {
        var invite = DecodeInvite(code);
        if (invite.Id == _identity.ServerId)
        {
            throw new FederationException("C'est votre propre invitation : envoyez-la à votre ami.");
        }

        if (_store.FindPeer(invite.Id) is not null)
        {
            throw new FederationException("Ce serveur fait déjà partie de vos amis.");
        }

        var settings = _store.Snapshot().Settings;
        var myUrl = NormalizeUrl(settings.PublicUrl) ?? throw new FederationException("Renseignez d'abord l'URL publique de votre serveur : votre ami en a besoin pour lire vos films.");

        var request = new PairRequest
        {
            V = FedProtocol.Version,
            Token = invite.Token,
            Id = _identity.ServerId,
            Name = _store.GetDisplayName(),
            Url = myUrl,
            Key = _identity.PublicKey
        };

        // The answer must be signed with the key from the invite: proves we reached the real inviter.
        var endpoint = new PeerEndpoint(invite.Id, invite.Url, invite.Key);
        var response = await _client.SendJsonAsync<PairResponse>(endpoint, HttpMethod.Post, "/Bloom/Peer/Pair", request, cancellationToken).ConfigureAwait(false);
        if (response.Id != invite.Id)
        {
            throw new FederationException("Le serveur a répondu avec une autre identité que celle de l'invitation.");
        }

        var peer = new PeerRecord
        {
            Id = invite.Id,
            Name = string.IsNullOrWhiteSpace(response.Name) ? invite.Name : response.Name.Trim(),
            Url = NormalizeUrl(response.Url) ?? invite.Url,
            PublicKey = invite.Key,
            DateAdded = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            StreamSecret = StreamTokens.NewSecret(),
            SharedLibraryIds = sharedLibraryIds.Distinct().ToList()
        };

        _store.Update(s =>
        {
            s.Peers.RemoveAll(p => p.Id == peer.Id);
            s.Peers.Add(peer);
        });

        _logger.LogInformation("Bloom: now federated with {Name} ({Url})", peer.Name, peer.Url);
        return peer;
    }

    /// <summary>
    /// Handles a pairing request from a friend who received our invite.
    /// The caller has already verified that <paramref name="request"/> is signed by <see cref="PairRequest.Key"/>.
    /// </summary>
    /// <param name="request">Request.</param>
    /// <returns>Our answer.</returns>
    public PairResponse HandlePairRequest(PairRequest request)
    {
        if (request.V != FedProtocol.Version)
        {
            throw new FederationException("Version de protocole incompatible.");
        }

        if (!ServerIdentity.IsValidPublicKey(request.Key) || ServerIdentity.ComputeServerId(request.Key) != request.Id)
        {
            throw new FederationException("Clé invalide.");
        }

        if (request.Id == _identity.ServerId)
        {
            throw new FederationException("Un serveur ne peut pas être ami avec lui-même.");
        }

        var url = NormalizeUrl(request.Url) ?? throw new FederationException("URL invalide.");
        var hash = FedProtocol.HashSecret(request.Token);
        var name = string.IsNullOrWhiteSpace(request.Name) ? url : request.Name.Trim();

        var accepted = _store.TryUpdate(s =>
        {
            var invite = s.Invites.Find(i => i.TokenHash == hash && i.ExpiresUtc > DateTime.UtcNow);
            if (invite is null)
            {
                return false;
            }

            s.Invites.Remove(invite);
            var existing = s.Peers.Find(p => p.Id == request.Id);
            if (existing is not null)
            {
                existing.Url = url;
                existing.Name = name;
                existing.RevokedByPeer = false;
                existing.SharedLibraryIds = existing.SharedLibraryIds.Union(invite.SharedLibraryIds).ToList();
                return true;
            }

            s.Peers.Add(new PeerRecord
            {
                Id = request.Id,
                Name = name,
                Url = url,
                PublicKey = request.Key,
                DateAdded = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                StreamSecret = StreamTokens.NewSecret(),
                SharedLibraryIds = invite.SharedLibraryIds
            });
            return true;
        });

        if (!accepted)
        {
            throw new FederationException("Invitation inconnue, déjà utilisée ou expirée.");
        }

        _logger.LogInformation("Bloom: {Name} ({Url}) accepted our invite", name, url);
        return new PairResponse
        {
            Id = _identity.ServerId,
            Name = _store.GetDisplayName(),
            Url = NormalizeUrl(_store.Snapshot().Settings.PublicUrl) ?? string.Empty
        };
    }
}
