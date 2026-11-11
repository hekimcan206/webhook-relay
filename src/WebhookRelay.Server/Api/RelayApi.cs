using System.Net;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using WebhookRelay.Core.Delivery;
using WebhookRelay.Server.Delivery;
using WebhookRelay.Server.Infrastructure;
using WebhookRelay.Server.Persistence;
using WebhookRelay.Signing;

namespace WebhookRelay.Server.Api;

internal static class RelayApi
{
    private static readonly string[] Statuses = ["pending", "in_flight", "retrying", "delivered", "dead"];

    public static RouteGroupBuilder MapRelayApi(this RouteGroupBuilder api)
    {
        api.AddEndpointFilter<AdminKeyFilter>();

        var endpoints = api.MapGroup("/endpoints").WithTags("Endpoints");
        endpoints.MapPost("/", CreateEndpointAsync).WithSummary("Register a receiver endpoint and get its signing secret.");
        endpoints.MapGet("/", async (RelayStore store, CancellationToken ct) => TypedResults.Ok(await store.ListEndpointsAsync(null, ct)))
            .WithSummary("List endpoints with breaker state and delivery counts.");
        endpoints.MapGet("/{id:guid}", GetEndpointAsync).WithSummary("One endpoint.");
        endpoints.MapPost("/{id:guid}/secrets/rotate", RotateSecretAsync)
            .WithSummary("Add a new secret; existing ones keep signing until the overlap ends.");
        endpoints.MapPost("/{id:guid}/enable", async Task<Results<NoContent, NotFound>> (Guid id, RelayStore store, CancellationToken ct) =>
            await store.SetEnabledAsync(id, true, null, ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithSummary("Re-enable an endpoint and reset its circuit breaker.");
        endpoints.MapPost("/{id:guid}/disable", async Task<Results<NoContent, NotFound>> (Guid id, RelayStore store, CancellationToken ct) =>
            await store.SetEnabledAsync(id, false, "disabled by operator", ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithSummary("Stop delivering to an endpoint; its pending deliveries wait.");
        endpoints.MapGet("/{id:guid}/circuit-events", async (Guid id, RelayStore store, CancellationToken ct, int limit = 50) =>
            TypedResults.Ok(await store.CircuitEventsAsync(id, Math.Clamp(limit, 1, 500), ct)))
            .WithSummary("Recent circuit breaker transitions of an endpoint.");

        api.MapPost("/messages", PublishAsync).WithTags("Messages").WithSummary("Publish an event; it fans out to subscribed endpoints.");

        var deliveries = api.MapGroup("/deliveries").WithTags("Replay");
        deliveries.MapGet("/", ListDeliveriesAsync).WithSummary("List deliveries, newest first; filter by status (e.g. dead) and endpoint.");
        deliveries.MapGet("/{id:long}", async Task<Results<Ok<DeliveryDetail>, NotFound>> (long id, RelayStore store, CancellationToken ct) =>
            await store.GetDeliveryAsync(id, ct) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound())
            .WithSummary("A delivery with its payload and every attempt.");
        deliveries.MapPost("/{id:long}/replay", ReplayAsync).WithSummary("Re-queue one delivery with a fresh attempt budget.");
        deliveries.MapPost("/replay-dead", async (ReplayDeadRequest? request, RelayStore store, CancellationToken ct) =>
            TypedResults.Ok(new ReplayResponse(await store.ReplayDeadAsync(request?.EndpointId, ct))))
            .WithSummary("Re-queue the whole dead-letter queue, or one endpoint's part of it.");

        api.MapGet("/stats", async (RelayStore store, IServiceProvider services, RelayIdentity identity, CancellationToken ct) =>
            TypedResults.Ok(new StatsResponse(
                await store.StatusCountsAsync(ct),
                services.GetServices<IHostedService>().OfType<DeliveryWorker>().FirstOrDefault()?.InFlight ?? 0,
                identity.Id)))
            .WithTags("Replay").WithSummary("Delivery counts by status.");

        return api;
    }

    private static async Task<Results<Created<CreateEndpointResponse>, ValidationProblem>> CreateEndpointAsync(
        CreateEndpointRequest request, RelayStore store, IOptions<RelayOptions> relay, IOptions<DeliveryOptions> delivery, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (ValidateUrl(request.Url, relay.Value.RequireHttps, delivery.Value.AllowPrivateNetworks) is { } urlError)
        {
            errors["url"] = [urlError];
        }

        if (request.Description is { Length: > 500 })
        {
            errors["description"] = ["At most 500 characters."];
        }

        if (request.EventTypes is { } types && (types.Count == 0 || types.Count > 50 || types.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 200)))
        {
            errors["eventTypes"] = ["Between 1 and 50 non-empty event types of at most 200 characters, or omit for all."];
        }

        WebhookSecret? secret = null;
        if (request.Secret is not null && !WebhookSecret.TryParse(request.Secret, out secret))
        {
            errors["secret"] = ["Expected 'whsec_' followed by 24 to 64 base64-encoded bytes."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        secret ??= WebhookSecret.Generate();
        var id = Guid.CreateVersion7();
        await store.CreateEndpointAsync(id, request.Url, request.Description ?? "", request.EventTypes, secret.Encoded, ct);
        return TypedResults.Created($"/api/v1/endpoints/{id}", new CreateEndpointResponse(id, request.Url, secret.Encoded));
    }

    private static async Task<Results<Ok<EndpointView>, NotFound>> GetEndpointAsync(Guid id, RelayStore store, CancellationToken ct) =>
        await store.ListEndpointsAsync(id, ct) is [var endpoint, ..] ? TypedResults.Ok(endpoint) : TypedResults.NotFound();

    private static async Task<Results<Ok<RotateSecretResponse>, NotFound, ValidationProblem>> RotateSecretAsync(
        Guid id, RotateSecretRequest? request, RelayStore store, CancellationToken ct)
    {
        var overlap = TimeSpan.FromMinutes(request?.OverlapMinutes ?? 24 * 60);
        WebhookSecret? secret = null;
        if (overlap < TimeSpan.Zero || overlap > TimeSpan.FromDays(30) ||
            (request?.Secret is not null && !WebhookSecret.TryParse(request.Secret, out secret)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["overlapMinutes must be between 0 and 43200; secret, if given, must be a valid whsec_ value."],
            });
        }

        secret ??= WebhookSecret.Generate();
        return await store.RotateSecretAsync(id, secret.Encoded, overlap, ct) is { } expires
            ? TypedResults.Ok(new RotateSecretResponse(secret.Encoded, expires))
            : TypedResults.NotFound();
    }

    private static async Task<Results<Accepted<PublishResponse>, Ok<PublishResponse>, ValidationProblem, Conflict<ProblemDetailsBody>>> PublishAsync(
        PublishRequest request, RelayStore store, IOptions<RelayOptions> relay, IOptions<DeliveryOptions> delivery, RelayMetrics metrics,
        CancellationToken ct)
    {
        var payload = request.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? null : request.Payload.GetRawText();
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.EventType) || request.EventType.Length > 200)
        {
            errors["eventType"] = ["Required, at most 200 characters."];
        }

        if (payload is null)
        {
            errors["payload"] = ["Required: any JSON value."];
        }
        else if (System.Text.Encoding.UTF8.GetByteCount(payload) > relay.Value.MaxPayloadBytes)
        {
            errors["payload"] = [$"At most {relay.Value.MaxPayloadBytes} bytes."];
        }

        if (request.IdempotencyKey is { Length: 0 or > 200 })
        {
            errors["idempotencyKey"] = ["Between 1 and 200 characters."];
        }

        if (request.EndpointIds is { Count: 0 or > 100 })
        {
            errors["endpointIds"] = ["Between 1 and 100 ids, or omit to use subscriptions."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await store.PublishAsync(
            MessageIds.New(), request.EventType, payload!, request.IdempotencyKey, request.EndpointIds, delivery.Value.NotifyOnPublish, ct);
        switch (result.Status)
        {
            case PublishStatus.IdempotencyConflict:
                return TypedResults.Conflict(new ProblemDetailsBody(
                    "Idempotency key reused with a different request.", $"The key was first used for {result.MessageId}."));
            case PublishStatus.Duplicate:
                return TypedResults.Ok(new PublishResponse(result.MessageId, result.Deliveries, Duplicate: true));
            default:
                metrics.Published(result.Deliveries);
                return TypedResults.Accepted($"/api/v1/deliveries?messageId={result.MessageId}", new PublishResponse(result.MessageId, result.Deliveries, false));
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryView>>, ValidationProblem>> ListDeliveriesAsync(
        RelayStore store, CancellationToken ct, string? status = null, Guid? endpointId = null, long? before = null, int limit = 50)
    {
        if (status is not null && !Statuses.Contains(status))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [$"One of: {string.Join(", ", Statuses)}."] });
        }

        return TypedResults.Ok(await store.ListDeliveriesAsync(status, endpointId, before, Math.Clamp(limit, 1, 500), ct));
    }

    private static async Task<Results<Accepted, NotFound, Conflict<ProblemDetailsBody>>> ReplayAsync(long id, RelayStore store, CancellationToken ct) =>
        await store.ReplayAsync(id, ct) switch
        {
            ReplayStatus.Replayed => TypedResults.Accepted($"/api/v1/deliveries/{id}"),
            ReplayStatus.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Conflict(new ProblemDetailsBody("Not replayable.", "Only dead, retrying or delivered deliveries can be replayed.")),
        };

    /// <summary>Validates an endpoint URL at registration. The connect-time egress check is the real guard; this gives early, clear errors.</summary>
    internal static string? ValidateUrl(string? url, bool requireHttps, bool allowPrivate)
    {
        // On Linux, Uri.TryCreate("/relative", Absolute) succeeds as an implicit file:///relative URI, so a path would be
        // reported as a wrong scheme instead of a relative URL. Implicit file URIs (no explicit "file:") count as relative.
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.IsFile && !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
        {
            return "An absolute URL of at most 2048 characters is required.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps && (requireHttps || uri.Scheme != Uri.UriSchemeHttp))
        {
            return requireHttps ? "Only https URLs are accepted." : "Only http and https URLs are accepted.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "Credentials in the URL are not accepted; they would be stored and logged in clear text.";
        }

        if (!allowPrivate && (uri.IsLoopback || (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && !EgressPolicy.IsPublicUnicast(ip))))
        {
            return "The URL points at a private, loopback or link-local address.";
        }

        return null;
    }
}

public sealed record ProblemDetailsBody(string Title, string Detail);
