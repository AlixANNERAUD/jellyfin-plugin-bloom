using System;
using System.Buffers.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Crypto;
using Jellyfin.Plugin.Bloom.Models;
using Jellyfin.Plugin.Bloom.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Api;

/// <summary>
/// Server-to-server API. Not authenticated with Jellyfin accounts: every call is signed with the
/// calling server's key (or carries a stream token), and checked against the friend list.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Bloom/Peer")]
public class PeerController : ControllerBase
{
    private const int MaxBodyBytes = 256 * 1024;

    private readonly FederationStore _store;
    private readonly ServerIdentity _identity;
    private readonly NonceCache _nonces;
    private readonly PairingService _pairing;
    private readonly CatalogService _catalog;
    private readonly SyncService _sync;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PeerController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerController"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="identity">Identity.</param>
    /// <param name="nonces">Nonce cache.</param>
    /// <param name="pairing">Pairing.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="sync">Sync.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public PeerController(
        FederationStore store,
        ServerIdentity identity,
        NonceCache nonces,
        PairingService pairing,
        CatalogService catalog,
        SyncService sync,
        ILibraryManager libraryManager,
        ILogger<PeerController> logger)
    {
        _store = store;
        _identity = identity;
        _nonces = nonces;
        _pairing = pairing;
        _catalog = catalog;
        _sync = sync;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Public information about this server (used to check that an URL is reachable).
    /// </summary>
    /// <returns>Info.</returns>
    [HttpGet("Info")]
    public ActionResult<ServerInfoResponse> Info() => new ServerInfoResponse
    {
        Id = _identity.ServerId,
        Name = _store.GetDisplayName(),
        Fingerprint = _identity.Fingerprint,
        Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0",
        Protocol = FedProtocol.Version
    };

    /// <summary>
    /// Completes the key exchange started by an invite.
    /// </summary>
    /// <returns>Signed answer.</returns>
    [HttpPost("Pair")]
    public async Task<ActionResult> Pair()
    {
        PairRequest? pairRequest = null;
        var verified = await VerifyAsync(
            (sender, body) =>
            {
                try
                {
                    pairRequest = JsonSerializer.Deserialize<PairRequest>(body, Models.Json.Options);
                }
                catch (JsonException)
                {
                    return null;
                }

                // The new friend signs with the key it announces.
                return pairRequest is not null && pairRequest.Id == sender ? pairRequest.Key : null;
            }).ConfigureAwait(false);

        if (verified.Error is not null)
        {
            return verified.Error;
        }

        try
        {
            return Signed(_pairing.HandlePairRequest(pairRequest!), verified.Nonce!);
        }
        catch (FederationException ex)
        {
            _logger.LogWarning("Bloom: pairing refused: {Message}", ex.Message);
            return SignedError(StatusCodes.Status403Forbidden, ex.Message, verified.Nonce!);
        }
    }

    /// <summary>
    /// Lists the libraries shared with the caller (also refreshes its name and URL).
    /// </summary>
    /// <returns>Signed answer.</returns>
    [HttpPost("Libraries")]
    public async Task<ActionResult> Libraries()
    {
        var verified = await VerifyFromPeerAsync().ConfigureAwait(false);
        if (verified.Error is not null)
        {
            return verified.Error;
        }

        HelloRequest? hello = null;
        try
        {
            hello = JsonSerializer.Deserialize<HelloRequest>(verified.Body!, Models.Json.Options);
        }
        catch (JsonException)
        {
        }

        var url = PairingService.NormalizeUrl(hello?.Url);
        _store.UpdatePeer(verified.Peer!.Id, p =>
        {
            p.LastSeen = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(hello?.Name))
            {
                p.Name = hello.Name.Trim();
            }

            if (url is not null)
            {
                p.Url = url;
            }
        });

        return Signed(
            new CatalogLibrariesResponse { ServerName = _store.GetDisplayName(), Libraries = _catalog.GetSharedLibraries(verified.Peer) },
            verified.Nonce!);
    }

    /// <summary>
    /// Returns a page of a shared library.
    /// </summary>
    /// <param name="library">Library id.</param>
    /// <param name="start">Start index.</param>
    /// <param name="limit">Page size.</param>
    /// <returns>Signed answer.</returns>
    [HttpGet("Catalog")]
    public async Task<ActionResult> Catalog([FromQuery] string library, [FromQuery] int start = 0, [FromQuery] int limit = 500)
    {
        var verified = await VerifyFromPeerAsync().ConfigureAwait(false);
        if (verified.Error is not null)
        {
            return verified.Error;
        }

        if (!Guid.TryParse(library, out var libraryId))
        {
            return SignedError(StatusCodes.Status400BadRequest, "Bibliothèque invalide.", verified.Nonce!);
        }

        var page = _catalog.GetPage(verified.Peer!, libraryId, start, limit);
        return page is null
            ? SignedError(StatusCodes.Status404NotFound, "Cette bibliothèque n'est pas partagée avec vous.", verified.Nonce!)
            : Signed(page, verified.Nonce!);
    }

    /// <summary>
    /// Returns an image of a shared item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="imageType">Image type.</param>
    /// <returns>The image.</returns>
    [HttpGet("Image/{itemId}/{imageType}")]
    public async Task<ActionResult> Image([FromRoute] Guid itemId, [FromRoute] ImageType imageType)
    {
        var verified = await VerifyFromPeerAsync().ConfigureAwait(false);
        if (verified.Error is not null)
        {
            return verified.Error;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !_catalog.IsItemShared(verified.Peer!, item))
        {
            return NotFound();
        }

        var image = item.GetImageInfo(imageType, 0);
        if (image is null || !image.IsLocalFile || !System.IO.File.Exists(image.Path))
        {
            return NotFound();
        }

        return PhysicalFile(image.Path, MimeTypes.GetMimeType(image.Path));
    }

    /// <summary>
    /// Returns an external subtitle file of a shared item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="index">Stream index.</param>
    /// <returns>The subtitle file.</returns>
    [HttpGet("Subtitle/{itemId}/{index}")]
    public async Task<ActionResult> Subtitle([FromRoute] Guid itemId, [FromRoute] int index)
    {
        var verified = await VerifyFromPeerAsync().ConfigureAwait(false);
        if (verified.Error is not null)
        {
            return verified.Error;
        }

        if (_libraryManager.GetItemById(itemId) is not Video video || !_catalog.IsItemShared(verified.Peer!, video))
        {
            return NotFound();
        }

        var stream = video.GetMediaStreams().FirstOrDefault(s => s.Index == index && s.Type == MediaStreamType.Subtitle && s.IsExternal);
        if (stream is null || string.IsNullOrEmpty(stream.Path) || !System.IO.File.Exists(stream.Path))
        {
            return NotFound();
        }

        return PhysicalFile(stream.Path, MediaTypeNames.Application.Octet);
    }

    /// <summary>
    /// Streams the original file of a shared item. Authenticated by a per-friend token because
    /// it is fetched by media players and ffmpeg, which cannot sign requests. Supports HTTP ranges (seeking).
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="p">Friend's server id.</param>
    /// <param name="t">Token.</param>
    /// <returns>The file.</returns>
    [HttpGet("Stream/{itemId}")]
    [HttpHead("Stream/{itemId}")]
    public ActionResult Stream([FromRoute] Guid itemId, [FromQuery] string? p, [FromQuery] string? t)
    {
        var peer = _store.FindPeer(p);
        if (peer is null || !peer.Enabled || peer.RevokedByPeer || !StreamTokens.Verify(peer.StreamSecret, itemId, t))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (_libraryManager.GetItemById(itemId) is not Video video || !_catalog.IsItemShared(peer, video))
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(video.Path) || !System.IO.File.Exists(video.Path))
        {
            return NotFound();
        }

        if (HttpMethods.IsGet(Request.Method) && (!Request.Headers.TryGetValue("Range", out var range) || range.ToString().StartsWith("bytes=0-", StringComparison.Ordinal)))
        {
            _logger.LogInformation("Bloom: {Peer} is streaming {Item}", peer.Name, video.Name);
        }

        return PhysicalFile(video.Path, MimeTypes.GetMimeType(video.Path), enableRangeProcessing: true);
    }

    /// <summary>
    /// A friend tells us its catalog changed: sync it soon.
    /// </summary>
    /// <returns>Signed answer.</returns>
    [HttpPost("Notify")]
    public async Task<ActionResult> Notify()
    {
        var verified = await VerifyFromPeerAsync().ConfigureAwait(false);
        if (verified.Error is not null)
        {
            return verified.Error;
        }

        _store.UpdatePeer(verified.Peer!.Id, p => p.LastSeen = DateTime.UtcNow);
        _sync.QueueSync(verified.Peer.Id, TimeSpan.FromSeconds(20));
        return Signed(new { ok = true }, verified.Nonce!);
    }

    /// <summary>
    /// A friend removed us: stop sharing with it and remove its items.
    /// </summary>
    /// <returns>Signed answer.</returns>
    [HttpPost("Unpair")]
    public async Task<ActionResult> Unpair()
    {
        var verified = await VerifyFromPeerAsync(allowInactive: true).ConfigureAwait(false);
        if (verified.Error is not null)
        {
            return verified.Error;
        }

        _logger.LogInformation("Bloom: {Peer} ended the federation", verified.Peer!.Name);
        _store.UpdatePeer(verified.Peer.Id, p => p.RevokedByPeer = true);
        _sync.QueueSync(verified.Peer.Id, TimeSpan.Zero);
        return Signed(new { ok = true }, verified.Nonce!);
    }

    private static ContentResult JsonContent(int status, string json) => new()
    {
        StatusCode = status,
        ContentType = "application/json; charset=utf-8",
        Content = json
    };

    private ContentResult Signed(object payload, string nonce, int status = StatusCodes.Status200OK)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), Models.Json.Options);
        var signature = _identity.Sign(FedProtocol.ResponseSigningInput(_identity.ServerId, nonce, status, Encoding.UTF8.GetBytes(json)));
        Response.Headers[FedProtocol.HeaderSignature] = Base64Url.EncodeToString(signature);
        return JsonContent(status, json);
    }

    private ContentResult SignedError(int status, string message, string nonce) => Signed(new { error = message }, nonce, status);

    private ContentResult Unsigned(int status, string message)
        => JsonContent(status, JsonSerializer.Serialize(new { error = message }, Models.Json.Options));

    private async Task<Verified> VerifyFromPeerAsync(bool allowInactive = false)
    {
        PeerRecord? peer = null;
        var result = await VerifyAsync((sender, _) =>
        {
            peer = _store.FindPeer(sender);
            if (peer is null || (!allowInactive && (!peer.Enabled || peer.RevokedByPeer)))
            {
                return null;
            }

            return peer.PublicKey;
        }).ConfigureAwait(false);

        return result with { Peer = peer };
    }

    /// <summary>
    /// Checks the signature headers of the request.
    /// </summary>
    /// <param name="keyResolver">Returns the public key of a sender (given its id and the body), or null if unknown.</param>
    private async Task<Verified> VerifyAsync(Func<string, byte[], string?> keyResolver)
    {
        var headers = Request.Headers;
        string sender = headers[FedProtocol.HeaderSender].ToString();
        string nonce = headers[FedProtocol.HeaderNonce].ToString();
        var signature = FedProtocol.TryDecodeBase64Url(headers[FedProtocol.HeaderSignature].ToString());
        if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(nonce) || nonce.Length > 64 || signature is null
            || !long.TryParse(headers[FedProtocol.HeaderTimestamp].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            return new Verified(Unsigned(StatusCodes.Status401Unauthorized, "Requête non signée."));
        }

        var skew = TimeSpan.FromSeconds(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp));
        if (skew > FedProtocol.MaxClockSkew)
        {
            return new Verified(Unsigned(StatusCodes.Status401Unauthorized, "Horloges désynchronisées entre les deux serveurs."));
        }

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            var read = new byte[16 * 1024];
            int n;
            while ((n = await Request.Body.ReadAsync(read).ConfigureAwait(false)) > 0)
            {
                buffer.Write(read, 0, n);
                if (buffer.Length > MaxBodyBytes)
                {
                    return new Verified(Unsigned(StatusCodes.Status413PayloadTooLarge, "Requête trop grosse."));
                }
            }

            body = buffer.ToArray();
        }

        var key = keyResolver(sender, body);
        if (key is null)
        {
            return new Verified(Unsigned(StatusCodes.Status401Unauthorized, "Serveur inconnu : vous ne faites pas (ou plus) partie des amis de ce serveur."));
        }

        var input = FedProtocol.RequestSigningInput(
            Request.Method,
            FedProtocol.CanonicalPath(Request.PathBase.Add(Request.Path).Value ?? string.Empty),
            Request.QueryString.Value ?? string.Empty,
            _identity.ServerId,
            sender,
            timestamp,
            nonce,
            body);

        if (!ServerIdentity.Verify(key, input, signature))
        {
            return new Verified(Unsigned(StatusCodes.Status401Unauthorized, "Signature invalide."));
        }

        if (!_nonces.TryUse(sender, nonce))
        {
            return new Verified(Unsigned(StatusCodes.Status401Unauthorized, "Requête rejouée."));
        }

        return new Verified(null) { Nonce = nonce, Body = body };
    }

    private sealed record Verified(ActionResult? Error)
    {
        public string? Nonce { get; init; }

        public byte[]? Body { get; init; }

        public PeerRecord? Peer { get; init; }
    }
}
