using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WebhookRelay.IntegrationTests.Infrastructure;
using WebhookRelay.Signing;

namespace WebhookRelay.IntegrationTests;

public sealed class DeliveryTests(DatabaseFixture fixture) : RelayTestBase(fixture)
{
    [Fact]
    public async Task A_published_message_reaches_a_healthy_receiver_once_signed_and_byte_identical()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        await ConnectAsync(relay, receiver);

        // Raw JSON with unusual spacing and non-ASCII text: the body must arrive exactly as published.
        const string raw = "{ \"type\" : \"order.created\",  \"data\": {\"customer\": \"Çağla Şen\", \"total\": 12.50} }";
        var response = await relay.Client.Http.PostAsync(new Uri("api/v1/messages", UriKind.Relative), new StringContent(
            $$"""{"eventType":"order.created","payload":{{raw}}}""", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var messageId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;

        await receiver.WaitForUniqueAsync(1, Patience);
        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 1, Patience);

        var request = receiver.Requests.Should().ContainSingle().Subject;
        request.WebhookId.Should().Be(messageId);
        request.SignatureValid.Should().BeTrue();
        request.Body.Should().Be(Encoding.UTF8.GetByteCount(raw));
        long.Parse(request.Timestamp!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeCloseTo(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 30);
    }

    [Fact]
    public async Task Fan_out_follows_event_type_subscriptions()
    {
        var relay = await StartRelayAsync();
        var orders = await StartReceiverAsync();
        var everything = await StartReceiverAsync();
        await ConnectAsync(relay, orders, ["order.created"]);
        await ConnectAsync(relay, everything);

        await relay.Client.PublishAsync("order.created", new { n = 1 });
        await relay.Client.PublishAsync("invoice.paid", new { n = 2 });

        await everything.WaitForUniqueAsync(2, Patience);
        await orders.WaitForUniqueAsync(1, Patience);
        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 3, Patience);
        orders.UniqueIds.Should().Be(1);
    }

    [Fact]
    public async Task The_same_idempotency_key_enqueues_once_and_a_different_body_under_it_is_a_conflict()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        await ConnectAsync(relay, receiver);

        var first = await relay.Client.Http.PostAsJsonAsync("api/v1/messages", new { eventType = "x", payload = new { a = 1 }, idempotencyKey = "k-1" });
        var again = await relay.Client.Http.PostAsJsonAsync("api/v1/messages", new { eventType = "x", payload = new { a = 1 }, idempotencyKey = "k-1" });
        var different = await relay.Client.Http.PostAsJsonAsync("api/v1/messages", new { eventType = "x", payload = new { a = 2 }, idempotencyKey = "k-1" });

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        different.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();
        againBody.GetProperty("messageId").GetString().Should().Be(firstBody.GetProperty("messageId").GetString());
        againBody.GetProperty("duplicate").GetBoolean().Should().BeTrue();

        await relay.Client.WaitForStatsAsync(s => s["delivered"] == 1, Patience);
        (await Db.ScalarAsync<long>("SELECT count(*) FROM relay.messages")).Should().Be(1);
    }

    [Fact]
    public async Task During_rotation_both_secrets_sign_and_receivers_on_either_secret_verify()
    {
        var relay = await StartRelayAsync();
        var receiver = await StartReceiverAsync();
        var endpointId = await ConnectAsync(relay, receiver);
        var oldSecret = WebhookSecret.Parse((await Db.ScalarAsync<string>("SELECT secret FROM relay.endpoint_secrets")));

        var rotate = await relay.Client.Http.PostAsJsonAsync($"api/v1/endpoints/{endpointId}/secrets/rotate", new { overlapMinutes = 60 });
        rotate.EnsureSuccessStatusCode();
        var newSecret = WebhookSecret.Parse((await rotate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!);

        // The receiver is already on the new secret; the old one must still verify too.
        receiver.UseSecrets(newSecret);
        await relay.Client.PublishAsync("x", new { n = 1 });
        await receiver.WaitForUniqueAsync(1, Patience);
        var first = receiver.Requests.Single();
        first.SignatureHeader!.Split(' ').Should().HaveCount(2);
        new WebhookVerifier(oldSecret).Verify(first.WebhookId, first.Timestamp, first.SignatureHeader, "{\"n\":1}").IsValid.Should().BeTrue();

        // Ending the overlap: only the new secret signs.
        (await relay.Client.Http.PostAsJsonAsync($"api/v1/endpoints/{endpointId}/secrets/rotate", new { overlapMinutes = 0, secret = newSecret.Encoded }))
            .EnsureSuccessStatusCode();
        await relay.Client.PublishAsync("x", new { n = 2 });
        await receiver.WaitForUniqueAsync(2, Patience);
        receiver.Requests.Last().SignatureHeader!.Split(' ').Should().ContainSingle();
        receiver.InvalidSignatures.Should().Be(0);
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("not a url")]
    [InlineData("https://user:pass@example.com/hook")]
    [InlineData("http://127.0.0.1:8080/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/hook")]
    public async Task Endpoint_registration_rejects_unsafe_urls_when_private_networks_are_not_allowed(string url)
    {
        var relay = await StartRelayAsync(s => s["Relay:Delivery:AllowPrivateNetworks"] = "false");

        var response = await relay.Client.Http.PostAsJsonAsync("api/v1/endpoints", new { url });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_connect_time_egress_guard_blocks_hostnames_that_resolve_to_loopback()
    {
        // Registration only sees "localhost"; the guard must catch it after DNS resolution, at connect time.
        var relay = await StartRelayAsync(s => s["Relay:Delivery:AllowPrivateNetworks"] = "false");
        var receiver = await StartReceiverAsync();
        var id = Guid.NewGuid();
        await Db.ExecuteAsync("INSERT INTO relay.endpoints (id, url) VALUES ($1, $2)", id, $"http://localhost:{receiver.Url.Port}/hook");
        await Db.ExecuteAsync("INSERT INTO relay.endpoint_secrets (endpoint_id, secret) VALUES ($1, $2)", id, WebhookSecret.Generate().Encoded);

        await relay.Client.PublishAsync("x", new { n = 1 });
        await relay.Client.WaitForStatsAsync(s => s["retrying"] + s["dead"] == 1, Patience);

        receiver.Requests.Should().BeEmpty();
        (await Db.ScalarAsync<string>("SELECT error_kind FROM relay.attempts LIMIT 1")).Should().Be("egress_blocked");
    }

    [Fact]
    public async Task The_api_requires_the_admin_key()
    {
        var relay = await StartRelayAsync();
        using var anonymous = new HttpClient { BaseAddress = relay.BaseAddress };
        using var wrong = new HttpClient { BaseAddress = relay.BaseAddress };
        wrong.DefaultRequestHeaders.Add("X-Admin-Key", "admin-key-for-tests-onlY");

        (await anonymous.GetAsync(new Uri("api/v1/endpoints", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrong.GetAsync(new Uri("api/v1/stats", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(new Uri("health/ready", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Publishing_validates_the_request()
    {
        var relay = await StartRelayAsync(s => s["Relay:MaxPayloadBytes"] = "1024");

        var noType = await relay.Client.Http.PostAsJsonAsync("api/v1/messages", new { eventType = "", payload = new { } });
        var tooBig = await relay.Client.Http.PostAsJsonAsync("api/v1/messages", new { eventType = "x", payload = new string('a', 2000) });
        var noPayload = await relay.Client.Http.PostAsJsonAsync("api/v1/messages", new { eventType = "x" });

        noType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        tooBig.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noPayload.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Two_relay_instances_share_the_queue_without_double_delivery()
    {
        var a = await StartRelayAsync(s => s["Relay:Delivery:MaxConcurrency"] = "8");
        var b = await StartRelayAsync(s => s["Relay:Delivery:MaxConcurrency"] = "8");
        var receiver = await StartReceiverAsync();
        receiver.Behavior = _ => new TestKit.ReceiverAction.Delay(TimeSpan.FromMilliseconds(20), TestKit.ReceiverAction.Ok);
        await ConnectAsync(a, receiver);

        for (var i = 0; i < 200; i++)
        {
            await (i % 2 == 0 ? a : b).Client.PublishAsync("x", new { i });
        }

        await receiver.WaitForUniqueAsync(200, Patience);
        await a.Client.WaitForStatsAsync(s => s["delivered"] == 200, Patience);
        receiver.Duplicates.Should().Be(0, "SKIP LOCKED claims are exclusive while their lease is valid");
        var relays = await Db.ScalarAsync<long>("SELECT count(DISTINCT relay_id) FROM relay.attempts");
        relays.Should().Be(2, "both instances took part");
    }

    [Fact]
    public async Task The_application_role_cannot_rewrite_the_attempt_log_or_change_the_schema()
    {
        await using var conn = new Npgsql.NpgsqlConnection(Db.AppConnectionString);
        await conn.OpenAsync();

        async Task<string?> TryAsync(string sql)
        {
            try
            {
                await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
                return null;
            }
            catch (Npgsql.PostgresException ex)
            {
                return ex.SqlState;
            }
        }

        (await TryAsync("UPDATE relay.attempts SET outcome = 'delivered'")).Should().Be("42501");
        (await TryAsync("DELETE FROM relay.circuit_events")).Should().Be("42501");
        (await TryAsync("CREATE TABLE relay.evil (id int)")).Should().Be("42501");
        (await TryAsync("SELECT * FROM relay_admin.schema_migrations")).Should().Be("42501");
    }
}
