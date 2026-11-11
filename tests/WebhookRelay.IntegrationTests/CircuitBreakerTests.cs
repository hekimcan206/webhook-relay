using WebhookRelay.IntegrationTests.Infrastructure;
using WebhookRelay.TestKit;

namespace WebhookRelay.IntegrationTests;

public sealed class CircuitBreakerTests(DatabaseFixture fixture) : RelayTestBase(fixture)
{
    private static void Breaker(Dictionary<string, string?> s)
    {
        s["Relay:Circuit:FailureThreshold"] = "3";
        s["Relay:Circuit:OpenDuration"] = "00:00:01.500";
        s["Relay:Circuit:MaxOpenDuration"] = "00:00:03";
        s["Relay:Retry:MaxAttempts"] = "50";
        s["Relay:Retry:BaseDelay"] = "00:00:00.100";
        s["Relay:Retry:MaxDelay"] = "00:00:00.300";
    }

    [Fact]
    public async Task An_open_circuit_stops_traffic_probes_once_and_closes_when_the_receiver_recovers()
    {
        var relay = await StartRelayAsync(Breaker);
        var receiver = await StartReceiverAsync();
        receiver.Behavior = _ => new ReceiverAction.Respond(503);
        var endpointId = await ConnectAsync(relay, receiver);

        for (var i = 0; i < 30; i++)
        {
            await relay.Client.PublishAsync("x", new { i });
        }

        await WaitAsync(async () => (await relay.Client.GetAsync($"api/v1/endpoints/{endpointId}")).GetProperty("circuitState").GetString() == "open");

        // While open (1.5 s), at most the attempts already in flight when it opened can arrive; then silence.
        await Task.Delay(300);
        var whileOpenStart = receiver.Requests.Count;
        await Task.Delay(900);
        receiver.Requests.Count.Should().Be(whileOpenStart, "no attempts are made while the circuit is open");

        receiver.Behavior = _ => ReceiverAction.Ok;
        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 30, Patience);

        var endpoint = await relay.Client.GetAsync($"api/v1/endpoints/{endpointId}");
        endpoint.GetProperty("circuitState").GetString().Should().Be("closed");
        var events = (await relay.Client.GetAsync($"api/v1/endpoints/{endpointId}/circuit-events?limit=100")).EnumerateArray()
            .Select(e => $"{e.GetProperty("from").GetString()}->{e.GetProperty("to").GetString()}").Reverse().ToList();
        events.First().Should().Be("closed->open");
        events.Should().Contain("open->half_open");
        events.Last().Should().Be("half_open->closed");
        receiver.UniqueIds.Should().Be(30);
    }

    [Fact]
    public async Task Time_spent_behind_an_open_circuit_does_not_consume_the_attempt_budget()
    {
        var relay = await StartRelayAsync(s =>
        {
            Breaker(s);
            s["Relay:Retry:MaxAttempts"] = "3";
        });
        var receiver = await StartReceiverAsync();
        receiver.Behavior = _ => new ReceiverAction.Respond(503);
        var endpointId = await ConnectAsync(relay, receiver);

        // Trip the breaker with three messages, then queue a backlog behind it.
        for (var i = 0; i < 3; i++)
        {
            await relay.Client.PublishAsync("trip", new { i });
        }

        await WaitAsync(async () => (await relay.Client.GetAsync($"api/v1/endpoints/{endpointId}")).GetProperty("circuitState").GetString() == "open");
        for (var i = 0; i < 20; i++)
        {
            await relay.Client.PublishAsync("backlog", new { i });
        }

        // Stay down through several open/probe cycles: only probes reach the receiver.
        await Task.Delay(TimeSpan.FromSeconds(5));
        var probesWhileDown = receiver.Requests.Count - 3;
        receiver.Behavior = _ => ReceiverAction.Ok;
        await relay.Client.WaitForStatsAsync(s => s["pending"] + s["retrying"] + s["in_flight"] == 0, Patience);

        probesWhileDown.Should().BeLessThan(10, "one probe per open period, not one attempt per message");
        var backlogDead = await Db.ScalarAsync<long>(
            "SELECT count(*) FROM relay.deliveries d JOIN relay.messages m ON m.id = d.message_id WHERE m.event_type = 'backlog' AND d.status = 'dead'");
        backlogDead.Should().BeLessThanOrEqualTo(2, "only backlog messages used as failed probes can run out of attempts");
        var delivered = await Db.ScalarAsync<long>(
            "SELECT count(*) FROM relay.deliveries d JOIN relay.messages m ON m.id = d.message_id WHERE m.event_type = 'backlog' AND d.status = 'delivered'");
        delivered.Should().BeGreaterThanOrEqualTo(18);
    }

    [Fact]
    public async Task Re_enabling_an_endpoint_resets_its_circuit()
    {
        var relay = await StartRelayAsync(Breaker);
        var receiver = await StartReceiverAsync();
        receiver.Behavior = _ => new ReceiverAction.Respond(500);
        var endpointId = await ConnectAsync(relay, receiver);
        for (var i = 0; i < 3; i++)
        {
            await relay.Client.PublishAsync("x", new { i });
        }

        await WaitAsync(async () => (await relay.Client.GetAsync($"api/v1/endpoints/{endpointId}")).GetProperty("circuitState").GetString() == "open");
        (await relay.Client.Http.PostAsync(new Uri($"api/v1/endpoints/{endpointId}/enable", UriKind.Relative), null)).EnsureSuccessStatusCode();

        var endpoint = await relay.Client.GetAsync($"api/v1/endpoints/{endpointId}");
        endpoint.GetProperty("circuitState").GetString().Should().Be("closed");
        endpoint.GetProperty("consecutiveFailures").GetInt32().Should().Be(0);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached.");
            }

            await Task.Delay(50);
        }
    }
}
