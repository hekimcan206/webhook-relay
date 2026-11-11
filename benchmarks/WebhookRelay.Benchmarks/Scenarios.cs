using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using WebhookRelay.Core.Retry;
using WebhookRelay.TestKit;

namespace WebhookRelay.Benchmarks;

internal static class Scenarios
{
    // ---- throughput ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Pre-fills the queue (straight SQL, so publishing cost is excluded), starts a relay and measures how fast it drains
    /// into an instantly-answering receiver. Median of three runs after a warm-up run.
    /// </summary>
    public static async Task ThroughputAsync(Harness h)
    {
        const int deliveries = 20_000;
        var configs = new (int Endpoints, int Concurrency)[] { (1, 16), (1, 64), (10, 64), (10, 128) };
        var results = new List<object>();
        await RunThroughputAsync(h, 2_000, 1, 64); // warm-up: JIT, connection pools, PostgreSQL caches

        foreach (var (endpoints, concurrency) in configs)
        {
            var runs = new List<double>();
            for (var run = 0; run < 3; run++)
            {
                runs.Add(await RunThroughputAsync(h, deliveries, endpoints, concurrency));
            }

            var median = Harness.Median(runs);
            Console.WriteLine($"  {endpoints,2} endpoint(s), concurrency {concurrency,3}: median {median:F0} deliveries/s  (runs {string.Join(", ", runs.Select(r => r.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))})");
            results.Add(new { endpoints, concurrency, deliveries, runsPerSecond = runs, medianPerSecond = median });
        }

        await h.WriteAsync("throughput", new { scenario = "throughput", payloadBytes = 230, results });
    }

    private static async Task<double> RunThroughputAsync(Harness h, int deliveries, int endpointCount, int concurrency)
    {
        await h.Db.ResetAsync();
        await using var receiver = await Harness.StartReceiverAsync();
        await h.CreateEndpointsAsync(receiver, endpointCount);
        var messages = deliveries / endpointCount;
        await h.Db.ExecuteAsync("""
            INSERT INTO relay.messages (id, event_type, payload)
            SELECT 'msg_' || lpad(to_hex(g), 32, '0'), 'bench.event',
                   '{"sequence":' || g || ',"type":"bench.event","data":{"amount":"1250.00","currency":"TRY","note":"' || repeat('x', 160) || '"}}'
            FROM generate_series(1, $1) g;
            """, messages);
        await h.Db.ExecuteAsync("INSERT INTO relay.deliveries (message_id, endpoint_id) SELECT m.id, e.id FROM relay.messages m CROSS JOIN relay.endpoints e");
        await h.Db.ExecuteAsync("ANALYZE relay.deliveries");

        await using var relay = await h.StartRelayAsync(s =>
        {
            s["Relay:Delivery:MaxConcurrency"] = concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture);
            s["Relay:Delivery:BatchSize"] = "100";
        });
        await Harness.WaitAsync(() => Task.FromResult(receiver.Requests.Count >= deliveries), TimeSpan.FromMinutes(4), "drain");
        // First to last receipt at the receiver: excludes host start-up, includes every claim, send and record.
        var ticks = receiver.Requests.Select(r => r.ReceivedAtTicks).ToArray();
        var seconds = Harness.Ms(ticks.Max() - ticks.Min()) / 1000;
        await Harness.WaitAsync(async () => await h.Db.ScalarAsync<long>("SELECT count(*) FROM relay.deliveries WHERE status = 'delivered'") == deliveries,
            TimeSpan.FromMinutes(1), "delivered state");
        return Math.Round(deliveries / seconds, 0);
    }

    // ---- latency -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Publishes through the HTTP API at a fixed rate and measures, on one clock, the time from starting the publish
    /// request to the receiver getting the webhook.
    /// </summary>
    public static async Task LatencyAsync(Harness h)
    {
        var results = new List<object>();
        foreach (var (rate, seconds, notify) in new[] { (50, 20, true), (200, 20, true), (500, 12, true), (50, 20, false) })
        {
            await h.Db.ResetAsync();
            await using var receiver = await Harness.StartReceiverAsync();
            var endpoint = (await h.CreateEndpointsAsync(receiver, 1))[0];
            await using var relay = await h.StartRelayAsync(s =>
            {
                s["Relay:Delivery:NotifyOnPublish"] = notify ? "true" : "false";
                s["Relay:Delivery:PollInterval"] = "00:00:01"; // the production default
            });
            using var http = new HttpClient { BaseAddress = relay.BaseAddress };
            http.DefaultRequestHeaders.Add("X-Admin-Key", RelayInstance.AdminKey);

            // Warm-up outside the measurement.
            for (var i = 0; i < 50; i++)
            {
                await PublishAsync(http, i);
            }

            await Harness.WaitAsync(() => Task.FromResult(receiver.UniqueIds >= 50), TimeSpan.FromSeconds(30), "warm-up");

            var sent = new ConcurrentDictionary<string, long>();
            var apiMs = new ConcurrentBag<double>();
            var total = rate * seconds;
            var start = Stopwatch.GetTimestamp();
            var inFlight = new List<Task>();
            for (var i = 0; i < total; i++)
            {
                var due = start + (long)(i * (double)Stopwatch.Frequency / rate);
                var wait = Harness.Ms(due - Stopwatch.GetTimestamp());
                if (wait > 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait));
                }

                var sequence = 1000 + i;
                inFlight.Add(Task.Run(async () =>
                {
                    var t0 = Stopwatch.GetTimestamp();
                    var id = await PublishAsync(http, sequence);
                    apiMs.Add(Harness.Ms(Stopwatch.GetTimestamp() - t0));
                    sent[id] = t0;
                }));
            }

            await Task.WhenAll(inFlight);
            var achievedRate = total / Harness.Ms(Stopwatch.GetTimestamp() - start) * 1000;
            await Harness.WaitAsync(() => Task.FromResult(receiver.UniqueIds >= 50 + total), TimeSpan.FromMinutes(2), "delivery");

            var firstReceipt = receiver.Requests.Where(r => r.FirstSeen && r.WebhookId is not null && sent.ContainsKey(r.WebhookId))
                .ToDictionary(r => r.WebhookId!, r => r.ReceivedAtTicks);
            var latencies = sent.Select(kv => Harness.Ms(firstReceipt[kv.Key] - kv.Value)).ToList();
            var row = new
            {
                ratePerSecond = rate,
                achievedRatePerSecond = Math.Round(achievedRate, 1),
                messages = total,
                notifyOnPublish = notify,
                pollIntervalMs = 1000,
                p50Ms = Harness.Percentile(latencies, 50),
                p95Ms = Harness.Percentile(latencies, 95),
                p99Ms = Harness.Percentile(latencies, 99),
                maxMs = Math.Round(latencies.Max(), 2),
                publishApiP50Ms = Harness.Percentile([.. apiMs], 50),
                publishApiP95Ms = Harness.Percentile([.. apiMs], 95),
            };
            Console.WriteLine($"  rate {rate}/s notify={notify}: p50 {row.p50Ms} ms, p95 {row.p95Ms} ms, p99 {row.p99Ms} ms (publish API p50 {row.publishApiP50Ms} ms)");
            results.Add(row);
        }

        await h.WriteAsync("latency", new { scenario = "enqueue-to-delivery latency", results });
    }

    private static async Task<string> PublishAsync(HttpClient http, int sequence)
    {
        var response = await http.PostAsJsonAsync("api/v1/messages", new
        {
            eventType = "bench.latency",
            payload = new { sequence, amount = "1250.00", currency = "TRY" },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;
    }

    // ---- retry schedule ------------------------------------------------------------------------------------------

    public static async Task RetryAsync(Harness h)
    {
        // (a) The default policy's distribution, sampled (pure computation, no network).
        var policy = new RetryPolicy(new RetryOptions());
        var random = new Random(42);
        const int samples = 100_000;
        var perAttempt = Enumerable.Range(1, policy.Options.MaxAttempts - 1).Select(n =>
        {
            var draws = Enumerable.Range(0, samples).Select(_ => policy.NextDelay(n, random.NextDouble()).TotalSeconds).ToList();
            return new
            {
                afterFailedAttempt = n,
                nominalSeconds = policy.NominalDelay(n).TotalSeconds,
                minSeconds = Math.Round(draws.Min(), 1),
                p5Seconds = Harness.Percentile(draws, 5),
                p50Seconds = Harness.Percentile(draws, 50),
                p95Seconds = Harness.Percentile(draws, 95),
                maxSeconds = Math.Round(draws.Max(), 1),
            };
        }).ToList();
        var totals = Enumerable.Range(0, 20_000)
            .Select(_ => Enumerable.Range(1, policy.Options.MaxAttempts - 1).Sum(n => policy.NextDelay(n, random.NextDouble()).TotalHours))
            .ToList();

        // (b) Herd spreading: 10 000 deliveries that failed in the same instant (an endpoint outage), retry 3 (nominal 90 s).
        var herd = new[] { JitterMode.None, JitterMode.Proportional, JitterMode.Full }.Select(mode =>
        {
            var p = new RetryPolicy(new RetryOptions { Jitter = mode });
            var buckets = Enumerable.Range(0, 10_000).Select(_ => (int)p.NextDelay(3, random.NextDouble()).TotalSeconds)
                .GroupBy(s => s).Select(g => g.Count()).ToList();
            return new { jitter = mode.ToString(), peakRetriesInOneSecond = buckets.Max(), distinctSeconds = buckets.Count };
        }).ToList();

        // (c) The schedule the relay actually produces, scaled down: base 200 ms x2, cap 3.2 s, 6 attempts, receiver always 503.
        await h.Db.ResetAsync();
        await using var receiver = await Harness.StartReceiverAsync();
        receiver.Behavior = _ => new ReceiverAction.Respond(503);
        await h.CreateEndpointsAsync(receiver, 1);
        await using (var relay = await h.StartRelayAsync(s =>
        {
            s["Relay:Retry:BaseDelay"] = "00:00:00.200";
            s["Relay:Retry:Multiplier"] = "2";
            s["Relay:Retry:MaxDelay"] = "00:00:03.200";
            s["Relay:Retry:MaxAttempts"] = "6";
            s["Relay:Delivery:PollInterval"] = "00:00:00.100";
            s["Relay:Circuit:FailureThreshold"] = "1000000000"; // 1,200 consecutive failures here: keep the breaker out of it
        }))
        {
            for (var i = 0; i < 200; i++)
            {
                await relay.Client.PublishAsync("bench.retry", new { i });
            }

            await Harness.WaitAsync(async () => await h.Db.ScalarAsync<long>("SELECT count(*) FROM relay.deliveries WHERE status = 'dead'") == 200,
                TimeSpan.FromMinutes(2), "dead letters");
        }

        var scaled = new RetryPolicy(new RetryOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(200),
            Multiplier = 2,
            MaxDelay = TimeSpan.FromMilliseconds(3200),
            MaxAttempts = 6,
        });
        var gaps = receiver.Requests.GroupBy(r => r.WebhookId!).SelectMany(g =>
        {
            var ticks = g.Select(r => r.ReceivedAtTicks).Order().ToArray();
            return ticks.Skip(1).Select((t, i) => (Attempt: i + 1, Ms: Harness.Ms(t - ticks[i])));
        }).ToList();
        var observed = gaps.GroupBy(g => g.Attempt).OrderBy(g => g.Key).Select(g =>
        {
            var ms = g.Select(x => x.Ms).ToList();
            var (lo, hi) = scaled.Bounds(g.Key);
            return new
            {
                afterFailedAttempt = g.Key,
                count = ms.Count,
                configuredBandMs = new[] { lo.TotalMilliseconds, hi.TotalMilliseconds },
                minMs = Math.Round(ms.Min(), 1),
                p50Ms = Harness.Percentile(ms, 50),
                p95Ms = Harness.Percentile(ms, 95),
                maxMs = Math.Round(ms.Max(), 1),
                belowBand = ms.Count(x => x < lo.TotalMilliseconds - 1),
                overshootP95Ms = Harness.Percentile([.. ms.Select(x => Math.Max(0, x - hi.TotalMilliseconds))], 95),
            };
        }).ToList();

        foreach (var row in observed)
        {
            Console.WriteLine($"  gap after attempt {row.afterFailedAttempt}: band {row.configuredBandMs[0]:F0}-{row.configuredBandMs[1]:F0} ms, observed p50 {row.p50Ms} p95 {row.p95Ms} max {row.maxMs} ms, below band {row.belowBand}");
        }

        foreach (var row in herd)
        {
            Console.WriteLine($"  herd of 10k at retry 3, jitter {row.jitter}: peak {row.peakRetriesInOneSecond}/s over {row.distinctSeconds} s");
        }

        await h.WriteAsync("retry", new
        {
            scenario = "retry schedule",
            defaultPolicy = new
            {
                policy.Options.BaseDelay,
                policy.Options.Multiplier,
                policy.Options.MaxDelay,
                policy.Options.MaxAttempts,
                jitter = policy.Options.Jitter.ToString(),
                policy.Options.JitterFraction,
                perAttempt,
                lastRetryAfterHours = new { p5 = Harness.Percentile(totals, 5), p50 = Harness.Percentile(totals, 50), p95 = Harness.Percentile(totals, 95) },
            },
            herd,
            observedScaledSchedule = new { deliveries = 200, pollIntervalMs = 100, gaps = observed },
        });
    }

    // ---- circuit breaker under a flapping endpoint ---------------------------------------------------------------

    public static async Task BreakerAsync(Harness h)
    {
        var results = new List<object>();
        foreach (var breaker in new[] { true, false })
        {
            for (var run = 0; run < 2; run++)
            {
                results.Add(await RunBreakerAsync(h, breaker));
            }
        }

        await h.WriteAsync("breaker", new
        {
            scenario = "flapping endpoint: 3 s up / 3 s down (503), 40 msg/s for 30 s",
            results,
        });
    }

    private static async Task<object> RunBreakerAsync(Harness h, bool breaker)
    {
        const int rate = 40;
        const int seconds = 30;
        await h.Db.ResetAsync();
        await using var receiver = await Harness.StartReceiverAsync();
        var start = Stopwatch.GetTimestamp();
        long upRequests = 0, downRequests = 0;
        var processedAt = new ConcurrentDictionary<string, long>();
        receiver.Behavior = r =>
        {
            var phase = Harness.Ms(Stopwatch.GetTimestamp() - start) % 6000;
            if (phase < 3000)
            {
                Interlocked.Increment(ref upRequests);
                processedAt.TryAdd(r.WebhookId!, Stopwatch.GetTimestamp());
                return ReceiverAction.Ok;
            }

            Interlocked.Increment(ref downRequests);
            return new ReceiverAction.Respond(503);
        };
        var endpoint = (await h.CreateEndpointsAsync(receiver, 1))[0];
        await using var relay = await h.StartRelayAsync(s =>
        {
            s["Relay:Retry:BaseDelay"] = "00:00:00.500";
            s["Relay:Retry:Multiplier"] = "2";
            s["Relay:Retry:MaxDelay"] = "00:00:08";
            s["Relay:Retry:MaxAttempts"] = "30";
            s["Relay:Delivery:PollInterval"] = "00:00:00.200";
            s["Relay:Circuit:FailureThreshold"] = breaker ? "5" : "1000000000";
            s["Relay:Circuit:OpenDuration"] = "00:00:01";
            s["Relay:Circuit:OpenBackoffMultiplier"] = "2";
            s["Relay:Circuit:MaxOpenDuration"] = "00:00:08";
        });
        using var http = new HttpClient { BaseAddress = relay.BaseAddress };
        http.DefaultRequestHeaders.Add("X-Admin-Key", RelayInstance.AdminKey);

        var sent = new ConcurrentDictionary<string, long>();
        var tasks = new List<Task>();
        start = Stopwatch.GetTimestamp();
        for (var i = 0; i < rate * seconds; i++)
        {
            var due = start + (long)(i * (double)Stopwatch.Frequency / rate);
            var wait = Harness.Ms(due - Stopwatch.GetTimestamp());
            if (wait > 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait));
            }

            var sequence = i;
            tasks.Add(Task.Run(async () =>
            {
                var t0 = Stopwatch.GetTimestamp();
                sent[await PublishAsync(http, sequence)] = t0;
            }));
        }

        await Task.WhenAll(tasks);
        var publishEnd = Stopwatch.GetTimestamp();
        await Harness.WaitAsync(async () => await h.Db.ScalarAsync<long>(
            "SELECT count(*) FROM relay.deliveries WHERE status IN ('pending', 'retrying', 'in_flight')") == 0, TimeSpan.FromMinutes(3), "drain");
        var drainSeconds = Harness.Ms(Stopwatch.GetTimestamp() - publishEnd) / 1000;

        var latencies = sent.Where(kv => processedAt.ContainsKey(kv.Key)).Select(kv => Harness.Ms(processedAt[kv.Key] - kv.Value)).ToList();
        var transitions = await h.Db.ScalarAsync<long>("SELECT count(*) FROM relay.circuit_events");
        var opens = await h.Db.ScalarAsync<long>("SELECT count(*) FROM relay.circuit_events WHERE to_state = 'open'");
        var closes = await h.Db.ScalarAsync<long>("SELECT count(*) FROM relay.circuit_events WHERE to_state = 'closed'");
        var dead = await h.Db.ScalarAsync<long>("SELECT count(*) FROM relay.deliveries WHERE status = 'dead'");
        var attempts = await h.Db.ScalarAsync<long>("SELECT count(*) FROM relay.attempts");
        var openSeconds = await h.Db.ScalarAsync<double>("""
            SELECT COALESCE(sum(EXTRACT(EPOCH FROM (next_at - at))), 0)::float8 FROM (
                SELECT at, to_state, lead(at, 1, now()) OVER (ORDER BY id) AS next_at FROM relay.circuit_events) t
            WHERE to_state = 'open'
            """);
        var row = new
        {
            breaker,
            messages = rate * seconds,
            delivered = processedAt.Count,
            dead,
            attempts,
            attemptsAgainstDownEndpoint = Interlocked.Read(ref downRequests),
            successfulAttempts = Interlocked.Read(ref upRequests),
            attemptsPerMessage = Math.Round(attempts / (double)(rate * seconds), 2),
            circuitOpened = opens,
            circuitClosed = closes,
            circuitTransitions = transitions,
            secondsOpen = Math.Round(openSeconds, 1),
            drainAfterPublishSeconds = Math.Round(drainSeconds, 1),
            latencyP50Ms = Harness.Percentile(latencies, 50),
            latencyP95Ms = Harness.Percentile(latencies, 95),
            latencyMaxMs = Math.Round(latencies.DefaultIfEmpty(0).Max(), 0),
        };
        Console.WriteLine($"  breaker={breaker}: attempts {row.attempts} ({row.attemptsAgainstDownEndpoint} against the down endpoint), delivered {row.delivered}, dead {row.dead}, opened {row.circuitOpened}x, p95 {row.latencyP95Ms} ms, drain {row.drainAfterPublishSeconds} s");
        return row;
    }
}
