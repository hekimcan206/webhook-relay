using BenchmarkDotNet.Running;
using WebhookRelay.Benchmarks;

// dotnet run -c Release --project benchmarks/WebhookRelay.Benchmarks -- <scenario>
//   micro       BenchmarkDotNet: sign / verify cost
//   throughput  deliveries per second from a pre-filled queue
//   latency     enqueue -> delivery p50/p95/p99 at a steady publish rate, NOTIFY vs polling
//   retry       retry schedule: policy distribution and the schedule observed against a failing receiver
//   breaker     a flapping endpoint with and without the circuit breaker
//   all         every scenario except micro
var scenario = args.FirstOrDefault() ?? "all";
var output = Path.GetFullPath(Environment.GetEnvironmentVariable("BENCH_OUT") ?? "benchmark-results");
Directory.CreateDirectory(output);

if (scenario == "micro")
{
    BenchmarkRunner.Run<SigningBenchmarks>(args: [.. args.Skip(1)]);
    return 0;
}

await using var harness = await Harness.StartAsync(output);
foreach (var name in scenario == "all" ? ["throughput", "latency", "retry", "breaker"] : new[] { scenario })
{
    Console.WriteLine($"== {name}");
    switch (name)
    {
        case "throughput":
            await Scenarios.ThroughputAsync(harness);
            break;
        case "latency":
            await Scenarios.LatencyAsync(harness);
            break;
        case "retry":
            await Scenarios.RetryAsync(harness);
            break;
        case "breaker":
            await Scenarios.BreakerAsync(harness);
            break;
        default:
            Console.Error.WriteLine($"unknown scenario {name}");
            return 2;
    }
}

return 0;
