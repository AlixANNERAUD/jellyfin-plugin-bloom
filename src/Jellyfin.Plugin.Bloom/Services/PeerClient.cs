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
    /// <summary>
    /// Name of the HTTP client used for everything sent to friends. Unlike Jellyfin's default client it does not
    /// follow redirects: a friend's server cannot bounce our requests to another address (our own network).
    /// </summary>
    public const string HttpClientName = "Bloom";

    private static readonly TimeSpan IdentityTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A catalog page of 500 items is a few MB: anything much bigger is not a real answer.</summary>
    private const long MaxJsonBytes = 32L * 1024 * 1024;

    private static readonly TimeSpan JsonTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(30);

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
        var bodyBytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json.Options);
        var nonce = string.Empty;
        var response = await Waking.SendAsync(
            async token =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(JsonTimeout);

                // A fresh nonce on each try: the friend refuses one it has already seen.
                nonce = FedProtocol.RandomToken(18);
                try
                {
                    var answer = await SendSignedAsync(peer, method, pathAndQuery, bodyBytes, nonce, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    await BufferAsync(peer, answer, timeout.Token).ConfigureAwait(false);
                    return answer;
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    throw new PeerRequestException(Text.Get("Timeout", peer.Url), null, ex);
                }
            },
            Waking.Budget,
            cancellationToken,
            (status, wait) => LogWaking(peer, status, wait)).ConfigureAwait(false);

        using var disposeResponse = response;

        // The answer is already buffered (BufferAsync).
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

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
        => Waking.SendAsync(
            token => SendSignedAsync(peer, HttpMethod.Get, pathAndQuery, [], FedProtocol.RandomToken(18), HttpCompletionOption.ResponseHeadersRead, token),
            Waking.Budget,
            cancellationToken,
            (status, wait) => LogWaking(peer, status, wait));

    /// <summary>
    /// Whether an address serves the friend's Bloom (same id and key). Checked before following a friend to a new
    /// address, so a friend cannot point our requests at an arbitrary server (for example one on our network).
    /// </summary>
    /// <param name="url">Announced address.</param>
    /// <param name="peer">Friend.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the address answers with the friend's identity.</returns>
    public async Task<bool> ServesPeerAsync(string url, PeerRecord peer, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(IdentityTimeout);
            using var response = await client.GetAsync(Combine(url, "/Bloom/Peer/Info"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 64 * 1024)
            {
                return false;
            }

            await response.Content.LoadIntoBufferAsync(64 * 1024, timeout.Token).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<ServerInfoResponse>(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false), Json.Options);
            return info is not null
                && info.Id == peer.Id
                && info.Fingerprint == ServerIdentity.ComputeFingerprint(peer.PublicKey);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Bloom: {Url} does not answer as {Peer}", url, peer.Name);
            return false;
        }
    }

    /// <summary>
    /// Wakes a friend's server up if it sleeps (any request starts it again), and waits until it answers.
    /// Unsigned: the endpoint does nothing, and friends with an older Bloom answer 404, which also means "up".
    /// </summary>
    /// <param name="peer">Friend.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the friend's server is up.</returns>
    public async Task<bool> WakeAsync(PeerEndpoint peer, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await Waking.SendAsync(
                async token =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(WakeTimeout);
                    using var request = new HttpRequestMessage(HttpMethod.Post, Combine(peer.Url, "/Bloom/Peer/Wake")) { Content = new ByteArrayContent([]) };
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                },
                Waking.Budget,
                cancellationToken,
                (status, wait) => LogWaking(peer, status, wait)).ConfigureAwait(false);
            return !Waking.IsWaking(response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Bloom: {Url} unreachable", peer.Url);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    internal static string DescribeError(HttpStatusCode status, byte[] body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // A known code is shown in our own language; otherwise, the friend's text as is. Anything else
                // (another server's error format: numbers, objects) is ignored rather than trusted.
                var code = StringProperty(doc.RootElement, "code");
                message = Text.HasPlain(code) ? Text.Get(code!) : StringProperty(doc.RootElement, "error");
            }
        }
        catch (JsonException)
        {
        }

        if (message is null && status == HttpStatusCode.ServiceUnavailable)
        {
            // Jellyfin starting, or a proxy starting it: still not up after Waking.Budget.
            return Text.Get("NotAwake");
        }

        return message is null ? Text.Get("HttpError", (int)status) : Text.Get("HttpErrorWithMessage", (int)status, message);
    }

    /// <summary>
    /// Reads the whole answer into memory, up to <see cref="MaxJsonBytes"/>.
    /// </summary>
    private static async Task BufferAsync(PeerEndpoint peer, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength > MaxJsonBytes)
            {
                throw new PeerRequestException(Text.Get("ResponseTooLarge"), response.StatusCode);
            }

            await response.Content.LoadIntoBufferAsync(MaxJsonBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Over the limit without a Content-Length, or the connection dropped while reading.
            response.Dispose();
            throw new PeerRequestException(Text.Get("Unreachable", ex.Message), null, ex);
        }
        catch (Exception ex) when (ex is PeerRequestException or OperationCanceledException)
        {
            response.Dispose();
            throw;
        }
    }

    private static string? StringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void LogWaking(PeerEndpoint peer, HttpStatusCode status, TimeSpan wait)
        => _logger.LogDebug("Bloom: {Url} is not ready (HTTP {Status}), trying again in {Delay}s", peer.Url, (int)status, wait.TotalSeconds);

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

        var client = _httpClientFactory.CreateClient(HttpClientName);
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
