using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WebhookRelay.IntegrationTests.Infrastructure;
using WebhookRelay.TestKit;

namespace WebhookRelay.IntegrationTests;

/// <summary>
/// Delivery guarantees under receiver faults. Each test asserts from the receiver's side (which webhook-ids actually
/// arrived, with valid signatures) as well as from the relay's state, so a bug in one cannot hide behind the other.
/// </summary>
public sealed class FaultInjectionTests(DatabaseFixture fixture) : RelayTestBase(fixture)
{
    [Fact]
    public async Task Server_errors_are_retried_with_backoff_until_the_receiver_recovers()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        receiver.Behavior = r => receiver.TimesSeen(r.WebhookId!) <= 2 ? new ReceiverAction.Respond(503) : ReceiverAction.Ok;
        await ConnectAsync(relay, receiver);

        var id = await relay.Client.PublishAsync("x", new { n = 1 });
        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 1, Patience);

        receiver.TimesSeen(id).Should().Be(3);
        var attempts = (await relay.Client.GetAsync($"api/v1/deliveries/{await DeliveryIdAsync()}")).GetProperty("attempts").EnumerateArray().ToList();
        attempts.Select(a => a.GetProperty("statusCode").GetInt32()).Should().Equal(503, 503, 200);

        // Base 200 ms × 2^n with ±25 % jitter: gaps of at least 150 ms and 300 ms.
        var starts = attempts.Select(a => a.GetProperty("startedAt").GetDateTimeOffset()).ToList();
        (starts[1] - starts[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140));
        (starts[2] - starts[1]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(290));
    }

    [Fact]
    public async Task A_receiver_that_times_out_gets_the_message_again_and_can_detect_the_duplicate_by_webhook_id()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        // First attempt: the receiver processes the message but answers after the relay's 1 s timeout.
        receiver.Behavior = r => r.FirstSeen ? new ReceiverAction.Delay(TimeSpan.FromSeconds(3), ReceiverAction.Ok) : ReceiverAction.Ok;
        await ConnectAsync(relay, receiver);

        var id = await relay.Client.PublishAsync("x", new { n = 1 });
        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 1, Patience);

        receiver.TimesProcessed(id).Should().Be(2, "the first attempt was processed but its response was lost");
        receiver.Requests.Where(r => r.WebhookId == id).Should().AllSatisfy(r => r.SignatureValid.Should().BeTrue());
        receiver.Duplicates.Should().Be(1, "exactly one duplicate, recognisable by its repeated webhook-id");
        (await Db.ScalarAsync<string>("SELECT error_kind FROM relay.attempts ORDER BY id LIMIT 1")).Should().Be("timeout");
    }

    [Fact]
    public async Task A_closed_connection_is_retried()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        receiver.Behavior = r => r.FirstSeen ? new ReceiverAction.Abort() : ReceiverAction.Ok;
        await ConnectAsync(relay, receiver);

        var id = await relay.Client.PublishAsync("x", new { n = 1 });
        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 1, Patience);

        receiver.TimesSeen(id).Should().Be(2);
        (await Db.ScalarAsync<string>("SELECT error_kind FROM relay.attempts ORDER BY id LIMIT 1")).Should().Be("connection_reset");
    }

    [Fact]
    public async Task A_slow_receiver_within_the_timeout_is_delivered_once()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        receiver.Behavior = _ => new ReceiverAction.Delay(TimeSpan.FromMilliseconds(600), ReceiverAction.Ok);
        await ConnectAsync(relay, receiver);

        for (var i = 0; i < 20; i++)
        {
            await relay.Client.PublishAsync("x", new { i });
        }

        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 20, Patience);
        receiver.UniqueIds.Should().Be(20);
        receiver.Duplicates.Should().Be(0);
        (await Db.ScalarAsync<long>("SELECT count(*) FROM relay.attempts")).Should().Be(20);
    }

    [Fact]
    public async Task Exhausted_messages_go_to_the_dead_letter_queue_and_replay_delivers_them_with_the_same_id()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        receiver.Behavior = _ => new ReceiverAction.Respond(500);
        await ConnectAsync(relay, receiver);

        var id = await relay.Client.PublishAsync("x", new { n = 1 });
        await relay.Client.WaitForStatsAsync(s => s["dead"] == 1, Patience);
        receiver.TimesSeen(id).Should().Be(4, "MaxAttempts = 4");

        var dead = await relay.Client.GetAsync("api/v1/deliveries?status=dead");
        var deliveryId = dead.EnumerateArray().Single().GetProperty("id").GetInt64();
        dead.EnumerateArray().Single().GetProperty("lastStatusCode").GetInt32().Should().Be(500);

        receiver.Behavior = _ => ReceiverAction.Ok;
        (await relay.Client.Http.PostAsync(new Uri($"api/v1/deliveries/{deliveryId}/replay", UriKind.Relative), null))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 1, Patience);

        receiver.TimesSeen(id).Should().Be(5);
        var detail = await relay.Client.GetAsync($"api/v1/deliveries/{deliveryId}");
        detail.GetProperty("delivery").GetProperty("replayCount").GetInt32().Should().Be(1);
        detail.GetProperty("attempts").EnumerateArray().Last().GetProperty("replay").GetInt32().Should().Be(1);

        // A pending or in-flight delivery cannot be replayed; an unknown one is 404.
        (await relay.Client.Http.PostAsync(new Uri("api/v1/deliveries/999999/replay", UriKind.Relative), null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Bulk_replay_drains_the_dead_letter_queue_of_one_endpoint()
    {
        var relay = await StartRelayAsync(s => s["Relay:Retry:MaxAttempts"] = "1");
        var broken = await StartReceiverAsync();
        broken.Behavior = _ => new ReceiverAction.Respond(500);
        var endpointId = await ConnectAsync(relay, broken);

        for (var i = 0; i < 25; i++)
        {
            await relay.Client.PublishAsync("x", new { i });
        }

        await relay.Client.WaitForStatsAsync(s => s["dead"] == 25, Patience);
        broken.Behavior = _ => ReceiverAction.Ok;

        var replay = await relay.Client.Http.PostAsJsonAsync("api/v1/deliveries/replay-dead", new { endpointId });
        (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetInt32().Should().Be(25);
        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 25 && s["dead"] == 0, Patience);
        broken.UniqueIds.Should().Be(25);
    }

    [Fact]
    public async Task Gone_disables_the_endpoint_and_dead_letters_the_message()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        receiver.Behavior = _ => new ReceiverAction.Respond(410);
        var endpointId = await ConnectAsync(relay, receiver);

        await relay.Client.PublishAsync("x", new { n = 1 });
        await relay.Client.WaitForStatsAsync(s => s["dead"] == 1, Patience);

        var endpoint = await relay.Client.GetAsync($"api/v1/endpoints/{endpointId}");
        endpoint.GetProperty("disabled").GetBoolean().Should().BeTrue();
        endpoint.GetProperty("disabledReason").GetString().Should().Contain("410");

        // New messages skip a disabled endpoint instead of piling up behind it.
        var response = await relay.Client.Http.PostAsJsonAsync("api/v1/messages", new { eventType = "x", payload = new { n = 2 } });
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deliveries").GetInt32().Should().Be(0);
        receiver.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Retry_after_from_a_rate_limited_receiver_is_honoured()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        receiver.Behavior = r => r.FirstSeen ? new ReceiverAction.Respond(429, "2") : ReceiverAction.Ok;
        await ConnectAsync(relay, receiver);

        await relay.Client.PublishAsync("x", new { n = 1 });
        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 1, Patience);

        var attempts = (await relay.Client.GetAsync($"api/v1/deliveries/{await DeliveryIdAsync()}")).GetProperty("attempts").EnumerateArray()
            .Select(a => a.GetProperty("startedAt").GetDateTimeOffset()).ToList();
        (attempts[1] - attempts[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9), "Retry-After: 2 beats the 200 ms backoff");
    }

    [Fact]
    public async Task Mixed_faults_lose_nothing_and_every_duplicate_carries_its_original_webhook_id()
    {
        var relay = await StartRelayAsync(s => s["Relay:Retry:MaxAttempts"] = "20");
        var receiver = await StartReceiverAsync();
        var random = new Random(1234);
        var gate = new object();
        receiver.Behavior = _ =>
        {
            int roll;
            lock (gate)
            {
                roll = random.Next(100);
            }

            return roll switch
            {
                < 10 => new ReceiverAction.Respond(500),
                < 18 => new ReceiverAction.Respond(503),
                < 24 => new ReceiverAction.Abort(),
                < 28 => new ReceiverAction.Delay(TimeSpan.FromMilliseconds(1500), ReceiverAction.Ok), // times out
                < 40 => new ReceiverAction.Delay(TimeSpan.FromMilliseconds(300), ReceiverAction.Ok),  // slow, fine
                _ => ReceiverAction.Ok,
            };
        };
        await ConnectAsync(relay, receiver);

        var published = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 300), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
            published.Add(await relay.Client.PublishAsync("x", new { i })));

        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 300, TimeSpan.FromSeconds(120));

        published.Should().OnlyContain(id => receiver.HasProcessed(id), "no message is lost");
        receiver.UniqueIds.Should().Be(300);
        receiver.InvalidSignatures.Should().Be(0);
        // Duplicates only come from attempts whose response was lost after the receiver saw them (timeouts).
        var timeouts = await Db.ScalarAsync<long>("SELECT count(*) FROM relay.attempts WHERE error_kind = 'timeout'");
        receiver.Duplicates.Should().BeLessThanOrEqualTo((int)timeouts);
    }

    private async Task<long> DeliveryIdAsync() => await Db.ScalarAsync<long>("SELECT id FROM relay.deliveries LIMIT 1");
}
