using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Services;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Bloom.Api;

/// <summary>
/// Jellyfin answers "download this item" (<c>/Items/{id}/Download</c>, used by apps for offline copies, and by some
/// players such as Infuse to play) with the item's file. For a friend's movie that file is our small .strm: this filter
/// swaps it for the actual video, relayed from the friend's server. Jellyfin has already checked permissions by then.
/// </summary>
public sealed class FederatedDownloadFilter : IAsyncResultFilter
{
    private readonly CatalogService _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederatedDownloadFilter"/> class.
    /// </summary>
    /// <param name="catalog">Catalog service.</param>
    public FederatedDownloadFilter(CatalogService catalog)
    {
        _catalog = catalog;
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is PhysicalFileResult file
            && file.FileName.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            && _catalog.IsFederatedPath(file.FileName))
        {
            var url = await ReadUrlAsync(file.FileName).ConfigureAwait(false);
            if (url is not null)
            {
                context.Result = new RelayedFileResult(url, Path.GetFileNameWithoutExtension(file.FileName));
            }
        }

        await next().ConfigureAwait(false);
    }

    private static async Task<Uri?> ReadUrlAsync(string strm)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(strm).ConfigureAwait(false);
            return Uri.TryCreate(lines.FirstOrDefault()?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Streams a friend's file through this server, with seeking (HTTP Range).
    /// </summary>
    private sealed class RelayedFileResult : IActionResult
    {
        private static readonly string[] CopiedHeaders = ["Accept-Ranges", "Content-Range", "Last-Modified", "ETag"];

        private readonly Uri _url;
        private readonly string _name;

        public RelayedFileResult(Uri url, string name)
        {
            _url = url;
            _name = name;
        }

        public async Task ExecuteResultAsync(ActionContext context)
        {
            var http = context.HttpContext;
            var services = http.RequestServices;
            var logger = services.GetRequiredService<ILogger<FederatedDownloadFilter>>();
            var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(PeerClient.HttpClientName);

            HttpResponseMessage response;
            try
            {
                // If the friend's server sleeps when idle, this wakes it up: wait for it rather than fail.
                response = await Waking.SendAsync(
                    async token =>
                    {
                        using var request = new HttpRequestMessage(HttpMethods.IsHead(http.Request.Method) ? HttpMethod.Head : HttpMethod.Get, _url);
                        foreach (var header in new[] { "Range", "If-Range" })
                        {
                            if (http.Request.Headers.TryGetValue(header, out var value))
                            {
                                request.Headers.TryAddWithoutValidation(header, value.ToArray());
                            }
                        }

                        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    },
                    Waking.Budget,
                    http.RequestAborted).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Bloom: friend unreachable for a download: {Message}", ex.Message);
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            using (response)
            {
                http.Response.StatusCode = (int)response.StatusCode;
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                http.Response.ContentType = contentType;
                http.Response.ContentLength = response.Content.Headers.ContentLength;
                foreach (var header in CopiedHeaders)
                {
                    if (response.Headers.TryGetValues(header, out var values) || response.Content.Headers.TryGetValues(header, out values))
                    {
                        http.Response.Headers[header] = values.ToArray();
                    }
                }

                if (response.IsSuccessStatusCode)
                {
                    var disposition = new ContentDispositionHeaderValue("attachment");
                    disposition.SetHttpFileName(_name + (MimeTypes.ToExtension(contentType) ?? ".mkv"));
                    http.Response.Headers.ContentDisposition = disposition.ToString();
                }

                if (HttpMethods.IsHead(http.Request.Method))
                {
                    return;
                }

                var body = await response.Content.ReadAsStreamAsync(http.RequestAborted).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    try
                    {
                        await body.CopyToAsync(http.Response.Body, 81920, http.RequestAborted).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
                    {
                        // The client stopped (seek, pause, closed the app).
                    }
                }
            }
        }
    }
}
