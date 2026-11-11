using WebhookRelay.Core.Breaker;

namespace WebhookRelay.Server.Persistence;

/// <summary>A delivery claimed by this relay instance, with everything needed to sign and send it.</summary>
internal sealed record ClaimedDelivery(
    long Id,
    string MessageId,
    Guid EndpointId,
    string Url,
    string EventType,
    string Payload,
    int Attempt,
    int Replay,
    DateTimeOffset MessageCreatedAt,
    IReadOnlyList<string> Secrets,
    bool IsProbe);

internal sealed record RecordedOutcome(string Status, bool LeaseLost, CircuitState? CircuitFrom, CircuitState? CircuitTo);

public sealed record EndpointView(
    Guid Id,
    string Url,
    string Description,
    IReadOnlyList<string>? EventTypes,
    bool Disabled,
    string? DisabledReason,
    string CircuitState,
    int ConsecutiveFailures,
    DateTimeOffset? OpenUntil,
    int ActiveSecrets,
    IReadOnlyDictionary<string, long> Deliveries,
    DateTimeOffset CreatedAt);

public sealed record DeliveryView(
    long Id,
    string MessageId,
    Guid EndpointId,
    string EndpointUrl,
    string EventType,
    string Status,
    int Attempts,
    int ReplayCount,
    DateTimeOffset NextAttemptAt,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? DeadAt);

public sealed record AttemptView(
    int Attempt,
    int Replay,
    DateTimeOffset StartedAt,
    int DurationMs,
    int? StatusCode,
    string Outcome,
    string? ErrorKind,
    string RelayId,
    bool LeaseLost);

public sealed record DeliveryDetail(DeliveryView Delivery, string Payload, IReadOnlyList<AttemptView> Attempts);

public sealed record CircuitEventView(DateTimeOffset At, string From, string To, int ConsecutiveFailures, DateTimeOffset? OpenUntil);

public enum PublishStatus
{
    Created,
    Duplicate,
    IdempotencyConflict,
}

public sealed record PublishResult(PublishStatus Status, string MessageId, int Deliveries);

public enum ReplayStatus
{
    Replayed,
    NotFound,
    NotReplayable,
}

internal static class StateNames
{
    public static CircuitState ParseCircuit(string value) => value switch
    {
        "open" => CircuitState.Open,
        "half_open" => CircuitState.HalfOpen,
        _ => CircuitState.Closed,
    };

    public static string Name(CircuitState state) => state switch
    {
        CircuitState.Open => "open",
        CircuitState.HalfOpen => "half_open",
        _ => "closed",
    };
}
