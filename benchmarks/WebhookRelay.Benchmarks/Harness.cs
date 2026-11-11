using System.Diagnostics;
using System.Text.Json;
using WebhookRelay.Signing;
using WebhookRelay.TestKit;

namespace WebhookRelay.Benchmarks;

/// <summary>One PostgreSQL container for all scenarios; each scenario resets the tables and starts its own relay.</summary>
internal sealed class Harness : IAsyncDisposable
{
    public static readonly WebhookSecret Secret = WebhookSecret.Generate();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private Harness(PostgresDatabase db, string output)
    {
        Db = db;
        Output = output;
    }

    public PostgresDatabase Db { get; }

    public string Output { get; }

    public static async Task<Harness> StartAsync(string output)
    {
        var db = await PostgresDatabase.StartAsync();
        return new Harness(db, output);
    }

    public Task<RelayInstance> StartRelayAsync(Action<Dictionary<string, string?>> configure) =>
        RelayInstance.StartAsync(Db.AppConnectionString, s =>
        {
            s["Serilog:MinimumLevel:Default"] = "Error";
            configure(s);
        });

    public static async Task<TestReceiver> StartReceiverAsync() => await TestReceiver.StartAsync(Secret);

    /// <summary>Registers <paramref name="count"/> endpoints pointing at the receiver, all with the shared secret.</summary>
    public async Task<Guid[]> CreateEndpointsAsync(TestReceiver receiver, int count)
    {
        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = Guid.CreateVersion7();
            await Db.ExecuteAsync("INSERT INTO relay.endpoints (id, url) VALUES ($1, $2)", ids[i], receiver.Url.ToString());
            await Db.ExecuteAsync("INSERT INTO relay.endpoint_secrets (endpoint_id, secret) VALUES ($1, $2)", ids[i], Secret.Encoded);
        }

        return ids;
    }

    public async Task WriteAsync(string name, object result)
    {
        var path = Path.Combine(Output, name + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"  -> {path}");
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.Order().ToArray();
        var rank = p / 100.0 * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return Math.Round(sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo)), 2);
    }

    public static double Median(IEnumerable<double> values) => Percentile([.. values], 50);

    public static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(25);
        }
    }
}
