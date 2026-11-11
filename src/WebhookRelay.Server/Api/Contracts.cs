using System.Text.Json;

namespace WebhookRelay.Server.Api;

/// <param name="Url">Absolute http(s) URL that receives POSTs.</param>
/// <param name="EventTypes">Event types to subscribe to; omit for all.</param>
/// <param name="Secret">Optional <c>whsec_</c> secret to import; one is generated when omitted.</param>
public sealed record CreateEndpointRequest(string Url, string? Description, IReadOnlyList<string>? EventTypes, string? Secret);

/// <summary>The secret is returned only here and on rotation; it is never listed again.</summary>
public sealed record CreateEndpointResponse(Guid Id, string Url, string Secret);

/// <param name="OverlapMinutes">How long the old secret keeps signing alongside the new one (default 24 h).</param>
public sealed record RotateSecretRequest(int? OverlapMinutes, string? Secret);

public sealed record RotateSecretResponse(string Secret, DateTimeOffset PreviousSecretsExpireAt);

/// <param name="Payload">Any JSON value; it is delivered byte for byte as the request body.</param>
/// <param name="IdempotencyKey">Producer-side de-duplication: publishing the same key twice enqueues once.</param>
/// <param name="EndpointIds">Restrict the fan-out to these endpoints; omit to use subscriptions.</param>
public sealed record PublishRequest(string EventType, JsonElement Payload, string? IdempotencyKey, IReadOnlyList<Guid>? EndpointIds);

public sealed record PublishResponse(string MessageId, int Deliveries, bool Duplicate);

public sealed record ReplayDeadRequest(Guid? EndpointId);

public sealed record ReplayResponse(int Replayed);

public sealed record StatsResponse(IReadOnlyDictionary<string, long> Deliveries, int InFlightHere, string RelayId);
