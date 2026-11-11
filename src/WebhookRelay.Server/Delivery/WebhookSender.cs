using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using WebhookRelay.Core.Delivery;
using WebhookRelay.Server.Persistence;
using WebhookRelay.Signing;

namespace WebhookRelay.Server.Delivery;

internal readonly record struct SendResult(AttemptOutcome Outcome, DateTimeOffset StartedAt, TimeSpan Duration);

/// <summary>Signs and sends one attempt. Never throws for receiver behaviour: every failure becomes a classified outcome.</summary>
internal sealed class WebhookSender(HttpClient http, TimeProvider time, IOptions<DeliveryOptions> options)
{
    public const string HttpClientName = "webhooks";
    private static readonly MediaTypeHeaderValue Json = new("application/json") { CharSet = "utf-8" };

    public async Task<SendResult> SendAsync(ClaimedDelivery delivery, CancellationToken stopping)
    {
        var startedAt = time.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        // The timestamp is taken per attempt, not per message: a retry three days later must still fall inside the
        // receiver's tolerance window. The webhook-id stays the same, which is what makes duplicates detectable.
        var timestamp = startedAt.ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes(delivery.Payload);
        var secrets = delivery.Secrets.Select(WebhookSecret.Parse).ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = Json;
        request.Headers.TryAddWithoutValidation(WebhookHeaders.Id, delivery.MessageId);
        request.Headers.TryAddWithoutValidation(WebhookHeaders.Timestamp, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookHeaders.Signature, WebhookSigner.SignatureHeader(secrets, delivery.MessageId, timestamp, body));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(options.Value.RequestTimeout);
        AttemptOutcome outcome;
        try
        {
            // Headers only: the response body is never read, so a receiver that streams a huge or endless body cannot
            // hold a worker slot or memory.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            outcome = OutcomeClassifier.FromStatus(
                (int)response.StatusCode,
                response.Headers.RetryAfter?.Delta is { } delta
                    ? ((int)delta.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : response.Headers.RetryAfter?.Date?.ToString("r", System.Globalization.CultureInfo.InvariantCulture),
                startedAt);
        }
        catch (OperationCanceledException ex) when (!stopping.IsCancellationRequested)
        {
            outcome = OutcomeClassifier.FromException(ex, timedOut: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or EgressBlockedException)
        {
            outcome = OutcomeClassifier.FromException(ex, timedOut: false);
        }

        return new SendResult(outcome, startedAt, stopwatch.Elapsed);
    }

    /// <summary>
    /// The outbound handler: no redirects (a 302 to http://169.254.169.254 is a classic SSRF), no cookies, a bounded
    /// connect timeout, pooled connections recycled every two minutes so DNS changes are picked up, and, unless private
    /// networks are explicitly allowed, a connect callback that refuses non-public addresses after DNS resolution.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(DeliveryOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.RequestTimeout < TimeSpan.FromSeconds(10) ? options.RequestTimeout : TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = Math.Max(2, options.MaxConcurrency),
            MaxResponseHeadersLength = 64,

            // Attempts are traced locally, but traceparent/baggage are not sent: receivers are third parties and have
            // no business seeing internal trace ids.
            ActivityHeadersPropagator = System.Diagnostics.DistributedContextPropagator.CreateNoOutputPropagator(),
        };

        if (!options.AllowPrivateNetworks)
        {
            handler.ConnectCallback = ConnectPublicOnlyAsync;
        }

        return handler;
    }

    internal static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(EgressPolicy.IsPublicUnicast).ToArray();
        if (allowed.Length == 0)
        {
            throw EgressBlockedException.For(addresses.FirstOrDefault() ?? IPAddress.None);
        }

        // Connect to the vetted addresses themselves; handing the hostname back to the socket would resolve it again
        // and reopen the DNS-rebinding window.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
