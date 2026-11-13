using System;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Crypto;
using Jellyfin.Plugin.Bloom.Models;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Error returned by a friend server.
/// </summary>
public sealed class PeerRequestException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerRequestException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="status">HTTP status, if any.</param>
    /// <param name="inner">Inner exception.</param>
    public PeerRequestException(string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
    }

    /// <summary>Gets the HTTP status.</summary>
    public HttpStatusCode? Status { get; }
}

/// <summary>
/// The address book entry needed to talk to a friend.
/// </summary>
/// <param name="Id">Server id.</param>
/// <param name="Url">Base URL.</param>
/// <param name="PublicKey">Public key.</param>
public sealed record PeerEndpoint(string Id, string Url, string PublicKey)
{
    /// <summary>
    /// Builds the endpoint of a friend.
    /// </summary>
    /// <param name="peer">Friend.</param>
    /// <returns>The endpoint.</returns>
    public static PeerEndpoint From(PeerRecord peer) => new(peer.Id, peer.Url, peer.PublicKey);
}

/// <summary>
/// Sends signed requests to friend servers and checks their signed answers.
/// </summary>
public sealed class PeerClient
{
    private static readonly TimeSpan JsonTimeout = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ServerIdentity _identity;
    private readonly ILogger<PeerClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="identity">Our identity.</param>
    /// <param name="logger">Logger.</param>
    public PeerClient(IHttpClientFactory httpClientFactory, ServerIdentity identity, ILogger<PeerClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _identity = identity;
        _logger = logger;
    }

    /// <summary>
    /// Joins a base URL and an absolute path.
    /// </summary>
    /// <param name="baseUrl">Base URL.</param>
    /// <param name="pathAndQuery">Path starting with '/'.</param>
    /// <returns>The URL.</returns>
    public static string Combine(string baseUrl, string pathAndQuery) => baseUrl.TrimEnd('/') + pathAndQuery;

    /// <summary>
    /// Sends a signed JSON request and returns the verified JSON answer.
    /// </summary>
    /// <typeparam name="T">Answer type.</typeparam>
    /// <param name="peer">Friend.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="pathAndQuery">Path (from "/Bloom/") and query.</param>
    /// <param name="body">Body to serialize, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer.</returns>
    public async Task<T> SendJsonAsync<T>(PeerEndpoint peer, HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(JsonTimeout);

        var bodyBytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json.Options);
        var nonce = FedProtocol.RandomToken(18);
        HttpResponseMessage response;
        byte[] responseBytes;
        try
        {
            response = await SendSignedAsync(peer, method, pathAndQuery, bodyBytes, nonce, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            responseBytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PeerRequestException(Text.Get("Timeout", peer.Url), null, ex);
        }

        using var disposeResponse = response;

        if (!response.IsSuccessStatusCode)
        {
            throw new PeerRequestException(DescribeError(response.StatusCode, responseBytes), response.StatusCode);
        }

        var signature = response.Headers.TryGetValues(FedProtocol.HeaderSignature, out var values) ? FedProtocol.TryDecodeBase64Url(string.Join(string.Empty, values)) : null;
        if (signature is null
            || !ServerIdentity.Verify(peer.PublicKey, FedProtocol.ResponseSigningInput(peer.Id, nonce, (int)response.StatusCode, responseBytes), signature))
        {
            throw new PeerRequestException(Text.Get("UnsignedResponse"));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(responseBytes, Json.Options) ?? throw new PeerRequestException(Text.Get("EmptyResponse"));
        }
        catch (JsonException ex)
        {
            throw new PeerRequestException(Text.Get("UnreadableResponse"), null, ex);
        }
    }

    /// <summary>
    /// Sends a signed GET and returns the raw response (images, subtitles). The caller disposes it.
    /// </summary>
    /// <param name="peer">Friend.</param>
    /// <param name="pathAndQuery">Path and query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    public Task<HttpResponseMessage> GetRawAsync(PeerEndpoint peer, string pathAndQuery, CancellationToken cancellationToken)
        => SendSignedAsync(peer, HttpMethod.Get, pathAndQuery, [], FedProtocol.RandomToken(18), HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private static string DescribeError(HttpStatusCode status, byte[] body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // A known code is shown in our own language; otherwise, the friend's text as is.
                message = doc.RootElement.TryGetProperty("code", out var code) && Text.Has(code.GetString())
                    ? Text.Get(code.GetString()!)
                    : doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : null;
            }
        }
        catch (JsonException)
        {
        }

        return message is null ? Text.Get("HttpError", (int)status) : Text.Get("HttpErrorWithMessage", (int)status, message);
    }

    private async Task<HttpResponseMessage> SendSignedAsync(PeerEndpoint peer, HttpMethod method, string pathAndQuery, byte[] body, string nonce, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var queryIndex = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
        var query = queryIndex >= 0 ? pathAndQuery[queryIndex..] : string.Empty;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = _identity.Sign(FedProtocol.RequestSigningInput(method.Method, FedProtocol.CanonicalPath(path), query, peer.Id, _identity.ServerId, timestamp, nonce, body));

        using var request = new HttpRequestMessage(method, Combine(peer.Url, pathAndQuery));
        request.Headers.Add(FedProtocol.HeaderSender, _identity.ServerId);
        request.Headers.Add(FedProtocol.HeaderTimestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(FedProtocol.HeaderNonce, nonce);
        request.Headers.Add(FedProtocol.HeaderSignature, Base64Url.EncodeToString(signature));
        if (body.Length > 0 || method != HttpMethod.Get)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        try
        {
            return await client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Bloom: {Url} unreachable", peer.Url);
            throw new PeerRequestException(Text.Get("Unreachable", ex.Message), null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PeerRequestException(Text.Get("Timeout", peer.Url), null, ex);
        }
    }
}
