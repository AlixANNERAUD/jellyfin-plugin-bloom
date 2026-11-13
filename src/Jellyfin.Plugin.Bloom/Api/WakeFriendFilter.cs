using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Bloom.Api;

/// <summary>
/// Before playing, apps ask Jellyfin how to play the item (<c>/Items/{id}/PlaybackInfo</c>), and Jellyfin reads a
/// friend's .strm stream right then. When the item is a friend's, this filter first makes sure the friend's server is
/// up, waking it if it sleeps when idle: the app shows its loading spinner meanwhile, instead of a playback error.
/// </summary>
public sealed class WakeFriendFilter : IAsyncActionFilter
{
    private readonly CatalogService _catalog;
    private readonly FederationStore _store;
    private readonly PeerWaker _waker;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="WakeFriendFilter"/> class.
    /// </summary>
    /// <param name="catalog">Catalog service.</param>
    /// <param name="store">Store.</param>
    /// <param name="waker">Waker.</param>
    /// <param name="libraryManager">Library manager.</param>
    public WakeFriendFilter(CatalogService catalog, FederationStore store, PeerWaker waker, ILibraryManager libraryManager)
    {
        _catalog = catalog;
        _store = store;
        _waker = waker;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "MediaInfo", ActionName: "GetPlaybackInfo" or "GetPostedPlaybackInfo" }
            && _store.FindPeer(_catalog.PeerIdOf(PlayedPath(context))) is { Enabled: true, RevokedByPeer: false } peer)
        {
            await _waker.WaitUpAsync(peer, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Path of the version about to be played: the media source the app picked (a friend's version of one of our
    /// movies), or else the item itself.
    /// </summary>
    private string? PlayedPath(ActionExecutingContext context)
    {
        var args = context.ActionArguments;
        var sourceId = args.TryGetValue("mediaSourceId", out var query) ? query as string : null;
        if (sourceId is null && args.TryGetValue("playbackInfoDto", out var dto) && dto is not null)
        {
            sourceId = dto.GetType().GetProperty("MediaSourceId")?.GetValue(dto) as string;
        }

        if (Guid.TryParse(sourceId, out var id) && _libraryManager.GetItemById(id) is { } source)
        {
            return source.Path;
        }

        return args.TryGetValue("itemId", out var item) && item is Guid itemId ? _libraryManager.GetItemById(itemId)?.Path : null;
    }
}
