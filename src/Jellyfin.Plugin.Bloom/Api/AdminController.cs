using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Crypto;
using Jellyfin.Plugin.Bloom.Models;
using Jellyfin.Plugin.Bloom.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Api;

/// <summary>
/// Administration API used by the dashboard page. Requires a Jellyfin administrator.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Bloom/Admin")]
[UsePageLanguage]
public class AdminController : ControllerBase
{
    private readonly FederationStore _store;
    private readonly ServerIdentity _identity;
    private readonly PairingService _pairing;
    private readonly CatalogService _catalog;
    private readonly VersionLinker _versions;
    private readonly SyncService _sync;
    private readonly LibraryIntegration _libraries;
    private readonly PeerClient _client;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<AdminController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="identity">Identity.</param>
    /// <param name="pairing">Pairing.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="sync">Sync.</param>
    /// <param name="libraries">Library integration.</param>
    /// <param name="client">Peer client.</param>
    /// <param name="versions">Version linker.</param>
    /// <param name="appHost">Application host.</param>
    /// <param name="logger">Logger.</param>
    public AdminController(
        FederationStore store,
        ServerIdentity identity,
        PairingService pairing,
        CatalogService catalog,
        SyncService sync,
        LibraryIntegration libraries,
        PeerClient client,
        VersionLinker versions,
        IServerApplicationHost appHost,
        ILogger<AdminController> logger)
    {
        _versions = versions;
        _store = store;
        _identity = identity;
        _pairing = pairing;
        _catalog = catalog;
        _sync = sync;
        _libraries = libraries;
        _client = client;
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>
    /// Everything the page needs.
    /// </summary>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    public ActionResult<object> Status()
    {
        var state = _store.Snapshot();
        string? suggested = null;
        try
        {
            suggested = _appHost.GetSmartApiUrl(Request);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "Bloom: no smart URL");
        }

        return new
        {
            Identity = new
            {
                _identity.ServerId,
                _identity.Fingerprint,
                Name = _store.GetDisplayName(),
                SuggestedUrl = suggested,
                Version = typeof(Plugin).Assembly.GetName().Version?.ToString()
            },
            state.Settings,
            MediaRoot = _store.GetMediaRoot(),
            ShareableLibraries = _catalog.GetLocalShareableLibraries(),
            MergeTargets = _libraries.ListLibraries()
                .Where(l => l.Locations.Length == 0 || l.Locations.Any(x => !_catalog.IsFederatedPath(x)))
                .Select(l => new { l.Name, l.CollectionType }),
            Invites = state.Invites.Where(i => i.ExpiresUtc > DateTime.UtcNow).Select(i => new { i.Label, i.ExpiresUtc, i.SharedLibraryIds }),
            Peers = state.Peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new
            {
                p.Id,
                p.Name,
                p.Url,
                Fingerprint = ServerIdentity.ComputeFingerprint(p.PublicKey),
                p.Enabled,
                p.RevokedByPeer,
                p.DateAdded,
                p.LastSeen,
                p.LastSync,
                p.LastError,
                p.Sleeps,
                Busy = _sync.IsBusy(p.Id),
                p.SharedLibraryIds,
                p.Imports
            })
        };
    }

    /// <summary>
    /// Saves the settings.
    /// </summary>
    /// <param name="request">Settings.</param>
    /// <returns>Nothing.</returns>
    [HttpPost("Settings")]
    public ActionResult SaveSettings([FromBody] FederationSettings request)
    {
        string? url = null;
        if (!string.IsNullOrWhiteSpace(request.PublicUrl))
        {
            url = PairingService.NormalizeUrl(request.PublicUrl);
            if (url is null)
            {
                return BadRequest(new { error = Text.Get("InvalidPublicUrl") });
            }
        }

        string? mediaRoot = null;
        if (!string.IsNullOrWhiteSpace(request.MediaRoot))
        {
            if (!Path.IsPathFullyQualified(request.MediaRoot))
            {
                return BadRequest(new { error = Text.Get("MediaRootNotAbsolute") });
            }

            mediaRoot = Path.GetFullPath(request.MediaRoot);
        }

        var previousRoot = _store.GetMediaRoot();
        var previous = _store.Snapshot().Settings;
        _store.Update(s =>
        {
            s.Settings.PublicUrl = url;
            s.Settings.ServerName = string.IsNullOrWhiteSpace(request.ServerName) ? null : request.ServerName.Trim();
            s.Settings.MediaRoot = mediaRoot;
            s.Settings.SkipOwnedItems = request.SkipOwnedItems;
            s.Settings.NotifyPeersOnChange = request.NotifyPeersOnChange;
            s.Settings.BadgePosters = request.BadgePosters;
            s.Settings.GroupVersions = request.GroupVersions;
            s.Settings.SleepsWhenIdle = request.SleepsWhenIdle;
        });

        // These change what friends' libraries look like here: apply them now rather than in 6 hours.
        if (previous.GroupVersions != request.GroupVersions)
        {
            // Groups between friends' copies do not depend on what is imported: regroup right away.
            _ = Task.Run(() => _versions.LinkAsync(CancellationToken.None));
        }

        // Friends that are asleep get the change when they wake up.
        var now = DateTime.UtcNow;
        var awake = _store.Snapshot().Peers.Where(p => p.Enabled && !p.RevokedByPeer && !p.IsAsleep(now)).ToList();
        if (previous.SkipOwnedItems != request.SkipOwnedItems || previous.BadgePosters != request.BadgePosters || previous.GroupVersions != request.GroupVersions)
        {
            foreach (var peer in awake)
            {
                _sync.QueueSync(peer.Id, TimeSpan.FromSeconds(2));
            }
        }

        if (previous.SleepsWhenIdle != request.SleepsWhenIdle)
        {
            // Tell friends now whether they may contact us on their own.
            foreach (var peer in awake)
            {
                NotifyInBackground(peer, "settings");
            }
        }

        if (!string.Equals(previousRoot, _store.GetMediaRoot(), StringComparison.Ordinal))
        {
            _logger.LogWarning("Bloom: media root changed from {Old} to {New}; remove and re-add friends' libraries to move them", previousRoot, _store.GetMediaRoot());
        }

        return NoContent();
    }

    /// <summary>
    /// Creates an invite code to send to a friend.
    /// </summary>
    /// <param name="request">Invite options.</param>
    /// <returns>The code.</returns>
    [HttpPost("Invites")]
    public ActionResult<object> CreateInvite([FromBody] InviteRequest request)
    {
        try
        {
            var (code, expires) = _pairing.CreateInvite(request.SharedLibraryIds ?? [], request.Label);
            return new { Code = code, ExpiresUtc = expires };
        }
        catch (FederationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes all pending invites.
    /// </summary>
    /// <returns>Nothing.</returns>
    [HttpDelete("Invites")]
    public ActionResult DeleteInvites()
    {
        _store.Update(s => s.Invites.Clear());
        return NoContent();
    }

    /// <summary>
    /// Decodes an invite to show who it comes from before accepting.
    /// </summary>
    /// <param name="request">Code.</param>
    /// <returns>Inviter details.</returns>
    [HttpPost("Invites/Inspect")]
    public ActionResult<object> InspectInvite([FromBody] AcceptRequest request)
    {
        try
        {
            var invite = PairingService.DecodeInvite(request.Code);
            return new
            {
                invite.Name,
                invite.Url,
                Fingerprint = ServerIdentity.ComputeFingerprint(invite.Key),
                ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(invite.Exp).UtcDateTime,
                AlreadyFriend = _store.FindPeer(invite.Id) is not null,
                IsSelf = invite.Id == _identity.ServerId
            };
        }
        catch (FederationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Accepts a friend's invite (key exchange) and starts a first sync.
    /// </summary>
    /// <param name="request">Code and libraries shared back.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The friend id.</returns>
    [HttpPost("Invites/Accept")]
    public async Task<ActionResult<object>> AcceptInvite([FromBody] AcceptRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var peer = await _pairing.AcceptInviteAsync(request.Code ?? string.Empty, request.SharedLibraryIds ?? [], cancellationToken).ConfigureAwait(false);
            _sync.QueueSync(peer.Id, TimeSpan.Zero);
            NotifyInBackground(peer, "paired");
            return new { peer.Id, peer.Name };
        }
        catch (FederationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (PeerRequestException ex)
        {
            return BadRequest(new { error = Text.Get("FriendRefused", ex.Message) });
        }
    }

    /// <summary>
    /// Updates what we share with a friend and how we import its libraries.
    /// </summary>
    /// <param name="peerId">Friend id.</param>
    /// <param name="request">Changes.</param>
    /// <returns>Nothing.</returns>
    [HttpPost("Peers/{peerId}")]
    public ActionResult UpdatePeer([FromRoute] string peerId, [FromBody] PeerUpdateRequest request)
    {
        var mergeTargets = _libraries.ListLibraries();
        string? error = null;
        var sharesChanged = false;
        var found = _store.UpdatePeer(peerId, p =>
        {
            if (request.Enabled.HasValue)
            {
                p.Enabled = request.Enabled.Value;
            }

            if (request.SharedLibraryIds is not null)
            {
                var updated = request.SharedLibraryIds.Distinct().ToList();
                sharesChanged = !updated.ToHashSet().SetEquals(p.SharedLibraryIds);
                p.SharedLibraryIds = updated;
            }

            foreach (var change in request.Imports ?? [])
            {
                var import = p.Imports.Find(i => i.RemoteLibraryId == change.RemoteLibraryId);
                if (import is null)
                {
                    continue;
                }

                if (change.Mode == ImportMode.Merge)
                {
                    var target = mergeTargets.FirstOrDefault(l => l.Name == change.TargetLibrary);
                    if (target.Name is null || !string.Equals(target.CollectionType, import.CollectionType, StringComparison.OrdinalIgnoreCase))
                    {
                        error = Text.Get("WrongLibraryType", change.TargetLibrary, Text.Get(import.CollectionType == "tvshows" ? "ShowsType" : "MoviesType"));
                        continue;
                    }

                    import.TargetLibrary = target.Name;
                }

                import.Mode = change.Mode;
            }
        });

        if (!found)
        {
            return NotFound(new { error = Text.Get("UnknownFriend") });
        }

        _sync.QueueSync(peerId, TimeSpan.FromSeconds(1));
        if (sharesChanged)
        {
            var peer = _store.FindPeer(peerId);
            if (peer is not null)
            {
                _catalog.Invalidate();
                NotifyInBackground(peer, "shares-changed");
            }
        }

        return error is null ? NoContent() : BadRequest(new { error });
    }

    /// <summary>
    /// Syncs a friend now.
    /// </summary>
    /// <param name="peerId">Friend id.</param>
    /// <returns>Result.</returns>
    [HttpPost("Peers/{peerId}/Sync")]
    public async Task<ActionResult<object>> SyncPeer([FromRoute] string peerId)
    {
        if (_store.FindPeer(peerId) is null)
        {
            return NotFound(new { error = Text.Get("UnknownFriend") });
        }

        // Not tied to the HTTP request: closing the page must not abort a sync halfway.
        var error = await _sync.SyncPeerAsync(peerId, CancellationToken.None).ConfigureAwait(false);
        return error is null ? new { ok = true } : BadRequest(new { error });
    }

    /// <summary>
    /// Ends the federation with a friend: tells them, removes their items and forgets their key.
    /// </summary>
    /// <param name="peerId">Friend id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nothing.</returns>
    [HttpDelete("Peers/{peerId}")]
    public async Task<ActionResult> DeletePeer([FromRoute] string peerId, CancellationToken cancellationToken)
    {
        var peer = _store.FindPeer(peerId);
        if (peer is null)
        {
            return NotFound(new { error = Text.Get("UnknownFriend") });
        }

        if (!peer.RevokedByPeer)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await _client.SendJsonAsync<object>(PeerEndpoint.From(peer), HttpMethod.Post, "/Bloom/Peer/Unpair", new { }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PeerRequestException or OperationCanceledException)
            {
                _logger.LogWarning("Bloom: could not tell {Peer} that we left: {Message}", peer.Name, ex.Message);
            }
        }

        await _sync.RemovePeerAsync(peerId).ConfigureAwait(false);
        return NoContent();
    }

    private void NotifyInBackground(PeerRecord peer, string reason)
    {
        if (peer.IsAsleep(DateTime.UtcNow))
        {
            // Not worth waking it up: it syncs with us when it wakes up.
            return;
        }

        var request = new NotifyRequest { Reason = reason, Sleeps = _store.Snapshot().Settings.SleepsWhenIdle };
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await _client.SendJsonAsync<object>(PeerEndpoint.From(peer), HttpMethod.Post, "/Bloom/Peer/Notify", request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (PeerRequestException ex)
            {
                _logger.LogDebug("Bloom: notify {Peer} failed: {Message}", peer.Name, ex.Message);
            }
        });
    }

    /// <summary>Invite creation request.</summary>
    public sealed class InviteRequest
    {
        /// <summary>Gets or sets the libraries shared with the new friend.</summary>
        public List<Guid>? SharedLibraryIds { get; set; }

        /// <summary>Gets or sets a label.</summary>
        public string? Label { get; set; }
    }

    /// <summary>Invite acceptance request.</summary>
    public sealed class AcceptRequest
    {
        /// <summary>Gets or sets the code.</summary>
        public string? Code { get; set; }

        /// <summary>Gets or sets our libraries shared back.</summary>
        public List<Guid>? SharedLibraryIds { get; set; }
    }

    /// <summary>Friend update request.</summary>
    public sealed class PeerUpdateRequest
    {
        /// <summary>Gets or sets whether the federation is active.</summary>
        public bool? Enabled { get; set; }

        /// <summary>Gets or sets our libraries shared with this friend.</summary>
        public List<Guid>? SharedLibraryIds { get; set; }

        /// <summary>Gets or sets import changes.</summary>
        public List<ImportChange>? Imports { get; set; }
    }

    /// <summary>Change of an import.</summary>
    public sealed class ImportChange
    {
        /// <summary>Gets or sets the friend's library id.</summary>
        public string RemoteLibraryId { get; set; } = string.Empty;

        /// <summary>Gets or sets the mode.</summary>
        public ImportMode Mode { get; set; }

        /// <summary>Gets or sets the target library (Merge).</summary>
        public string? TargetLibrary { get; set; }
    }
}
