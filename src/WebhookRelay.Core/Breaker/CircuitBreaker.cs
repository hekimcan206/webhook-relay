namespace WebhookRelay.Core.Breaker;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

public sealed record CircuitOptions
{
    public const string Section = "Relay:Circuit";

    /// <summary>Consecutive failed attempts that open the circuit.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>How long the first opening lasts before a single probe is allowed.</summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Each re-opening without an intervening success multiplies the open time (a flapping endpoint cools off longer).</summary>
    public double OpenBackoffMultiplier { get; init; } = 2.0;

    public TimeSpan MaxOpenDuration { get; init; } = TimeSpan.FromMinutes(10);

    public void Validate()
    {
        if (FailureThreshold < 1 || OpenDuration <= TimeSpan.Zero || MaxOpenDuration < OpenDuration || OpenBackoffMultiplier < 1)
        {
            throw new ArgumentException("CircuitOptions: FailureThreshold >= 1, OpenDuration > 0, MaxOpenDuration >= OpenDuration and OpenBackoffMultiplier >= 1 are required.");
        }
    }
}

/// <summary>Persisted breaker state of one endpoint.</summary>
/// <param name="ConsecutiveFailures">Failures since the last success.</param>
/// <param name="ConsecutiveOpens">Openings since the last success; drives the growing open time.</param>
/// <param name="OpenUntil">While <see cref="CircuitState.Open"/>: when the next probe is allowed.</param>
public readonly record struct Circuit(CircuitState State, int ConsecutiveFailures, int ConsecutiveOpens, DateTimeOffset? OpenUntil)
{
    public static Circuit Closed => new(CircuitState.Closed, 0, 0, null);
}

/// <summary>
/// Per-endpoint circuit breaker as a pure state machine. The relay stores <see cref="Circuit"/> on the endpoint row
/// and applies these transitions under that row's lock, so every relay instance sees one breaker per endpoint.
///
/// Why a breaker in a relay that already backs off per message: backoff is per message, but when an endpoint is down
/// every one of its thousands of pending messages would still be tried on its own schedule. The breaker turns that into
/// one probe per open period, and the time spent open does not consume the messages' attempt budget.
/// </summary>
public static class CircuitBreaker
{
    public static Circuit OnSuccess(Circuit _) => Circuit.Closed;

    public static Circuit OnFailure(Circuit circuit, DateTimeOffset now, CircuitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = circuit.ConsecutiveFailures + 1;
        return circuit.State switch
        {
            // The probe failed: open again, for longer.
            CircuitState.HalfOpen => Open(failures, circuit.ConsecutiveOpens + 1, now, options),

            // A request that was already in flight when the circuit opened came back failed: count it, keep the window.
            CircuitState.Open => circuit with { ConsecutiveFailures = failures },

            _ when failures >= options.FailureThreshold => Open(failures, circuit.ConsecutiveOpens + 1, now, options),
            _ => circuit with { ConsecutiveFailures = failures },
        };
    }

    /// <summary>True when the circuit is open and its open period has elapsed, so one probe may be sent.</summary>
    public static bool ProbeDue(Circuit circuit, DateTimeOffset now) =>
        circuit.State == CircuitState.Open && circuit.OpenUntil is { } until && now >= until;

    /// <summary>Moves an open circuit whose period elapsed to half-open; exactly one delivery is then sent as the probe.</summary>
    public static Circuit StartProbe(Circuit circuit, DateTimeOffset now) =>
        ProbeDue(circuit, now)
            ? circuit with { State = CircuitState.HalfOpen, OpenUntil = null }
            : throw new InvalidOperationException($"No probe is due for a circuit in state {circuit.State}.");

    /// <summary>A probe found nothing to deliver: return to open, immediately eligible for the next probe.</summary>
    public static Circuit AbandonProbe(Circuit circuit, DateTimeOffset now) =>
        circuit.State == CircuitState.HalfOpen ? circuit with { State = CircuitState.Open, OpenUntil = now } : circuit;

    public static TimeSpan OpenDurationFor(int consecutiveOpens, CircuitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var seconds = options.OpenDuration.TotalSeconds * Math.Pow(options.OpenBackoffMultiplier, Math.Max(0, consecutiveOpens - 1));
        return seconds >= options.MaxOpenDuration.TotalSeconds ? options.MaxOpenDuration : TimeSpan.FromSeconds(seconds);
    }

    private static Circuit Open(int failures, int opens, DateTimeOffset now, CircuitOptions options) =>
        new(CircuitState.Open, failures, opens, now + OpenDurationFor(opens, options));
}
