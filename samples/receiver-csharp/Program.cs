using System.Collections.Concurrent;
using System.Text.Json;
using ReceiverSample;

// `verify-vectors <file>`: check this implementation against the relay-generated vectors (used in CI), then exit.
if (args is ["verify-vectors", var vectorsPath])
{
    return VerifyVectors(vectorsPath);
}

var secrets = (Environment.GetEnvironmentVariable("WEBHOOK_SECRETS") ?? "")
    .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
if (secrets.Length == 0)
{
    Console.Error.WriteLine("Set WEBHOOK_SECRETS to one or more whsec_ secrets (space or comma separated).");
    return 2;
}

// Demo fault: answer 503 to the first N deliveries of every message, to show retries arriving with the same webhook-id.
var failFirst = int.TryParse(Environment.GetEnvironmentVariable("FAIL_FIRST_ATTEMPTS"), out var n) ? n : 0;

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();
var receipts = new ConcurrentDictionary<string, int>();
// Idempotency: in production this is a unique constraint or a key with a TTL longer than the retry window, written in
// the same transaction as the side effect. An in-memory set is enough to demonstrate the rule.
var processed = new ConcurrentDictionary<string, bool>();
var stats = new Stats();

app.MapPost("/webhook", async (HttpRequest request) =>
{
    // Verify the raw bytes exactly as received; never re-serialise parsed JSON before verifying.
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var id = request.Headers["webhook-id"].ToString();
    if (!StandardWebhooks.Verify(secrets, id, request.Headers["webhook-timestamp"], request.Headers["webhook-signature"],
            buffer.ToArray(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
    {
        Interlocked.Increment(ref stats.Rejected);
        return Results.StatusCode(401);
    }

    Interlocked.Increment(ref stats.Verified);
    if (receipts.AddOrUpdate(id, 1, (_, c) => c + 1) <= failFirst)
    {
        Interlocked.Increment(ref stats.FailedOnPurpose);
        return Results.StatusCode(503);
    }

    if (!processed.TryAdd(id, true))
    {
        Interlocked.Increment(ref stats.Duplicates);
        return Results.NoContent(); // already handled: acknowledge, do nothing
    }

    Interlocked.Increment(ref stats.Processed);
    return Results.NoContent();
});

app.MapGet("/stats", () => Results.Json(new
{
    language = "csharp",
    verified = stats.Verified,
    rejected = stats.Rejected,
    failedOnPurpose = stats.FailedOnPurpose,
    duplicates = stats.Duplicates,
    processed = stats.Processed,
}));

app.Run();
return 0;

static int VerifyVectors(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var failures = 0;
    var total = 0;
    foreach (var v in document.RootElement.GetProperty("vectors").EnumerateArray())
    {
        total++;
        var ok = StandardWebhooks.Verify(
            [.. v.GetProperty("receiverSecrets").EnumerateArray().Select(s => s.GetString()!)],
            v.GetProperty("webhookId").GetString(),
            v.GetProperty("webhookTimestamp").GetString(),
            v.GetProperty("webhookSignature").GetString(),
            Convert.FromBase64String(v.GetProperty("bodyBase64").GetString()!),
            v.GetProperty("now").GetInt64());
        if (ok != v.GetProperty("expectValid").GetBoolean())
        {
            failures++;
            Console.WriteLine($"FAIL {v.GetProperty("name").GetString()}: got {ok}");
        }
    }

    Console.WriteLine($"csharp: {total - failures}/{total} vectors as expected");
    return failures == 0 ? 0 : 1;
}

internal sealed class Stats
{
    public long Verified;
    public long Rejected;
    public long FailedOnPurpose;
    public long Duplicates;
    public long Processed;
}
