using System.Diagnostics.Metrics;
using WebhookRelay.Core.Breaker;
using WebhookRelay.Core.Delivery;

namespace WebhookRelay.Server.Infrastructure;

internal sealed class RelayMetrics : IDisposable
{
    public const string MeterName = "WebhookRelay";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _published;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _finished;
    private readonly Counter<long> _circuitTransitions;
    private readonly Histogram<double> _attemptDuration;
    private readonly Histogram<double> _endToEnd;

    public RelayMetrics()
    {
        _published = _meter.CreateCounter<long>("relay.messages.published", description: "Messages accepted by the publish API.");
        _attempts = _meter.CreateCounter<long>("relay.attempts", description: "HTTP delivery attempts by outcome and error kind.");
        _finished = _meter.CreateCounter<long>("relay.deliveries.finished", description: "Deliveries reaching delivered or dead.");
        _circuitTransitions = _meter.CreateCounter<long>("relay.circuit.transitions", description: "Breaker state changes.");
        _attemptDuration = _meter.CreateHistogram<double>("relay.attempt.duration", unit: "ms", description: "Duration of one HTTP attempt.");
        _endToEnd = _meter.CreateHistogram<double>("relay.delivery.lag", unit: "ms", description: "Message creation to successful delivery.");
    }

    public void Published(int deliveries) => _published.Add(1, new KeyValuePair<string, object?>("fanout", deliveries));

    public void Attempt(AttemptOutcome outcome, TimeSpan duration, bool probe)
    {
        _attempts.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome.Kind.ToString()),
            new KeyValuePair<string, object?>("error", outcome.ErrorKind ?? "none"),
            new KeyValuePair<string, object?>("probe", probe));
        _attemptDuration.Record(duration.TotalMilliseconds);
    }

    public void Finished(string status, TimeSpan? lag)
    {
        if (status is "delivered" or "dead")
        {
            _finished.Add(1, new KeyValuePair<string, object?>("status", status));
        }

        if (status == "delivered" && lag is { } value)
        {
            _endToEnd.Record(value.TotalMilliseconds);
        }
    }

    public void CircuitTransition(CircuitState from, CircuitState to) =>
        _circuitTransitions.Add(1, new KeyValuePair<string, object?>("from", from.ToString()), new KeyValuePair<string, object?>("to", to.ToString()));

    public void Dispose() => _meter.Dispose();
}
