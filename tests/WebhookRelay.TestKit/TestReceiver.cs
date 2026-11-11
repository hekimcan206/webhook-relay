using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebhookRelay.Signing;

namespace WebhookRelay.TestKit;

/// <summary>What the receiver does with one request.</summary>
public abstract record ReceiverAction
{
    public static readonly ReceiverAction Ok = new Respond(200);

    /// <summary>Answer with a status code (and optionally Retry-After).</summary>
    public sealed record Respond(int StatusCode, string? RetryAfter = null) : ReceiverAction;

    /// <summary>Wait, then do something else. Longer than the relay's timeout, this is a receiver that times out.</summary>
    public sealed record Delay(TimeSpan Duration, ReceiverAction Then) : ReceiverAction;

    /// <summary>Close the connection without an HTTP response (connection reset from the relay's point of view).</summary>
    public sealed record Abort : ReceiverAction;
}

/// <summary>One request as the receiver saw it.</summary>
/// <param name="FirstSeen">False when this webhook-id was already received: the duplicate an idempotent receiver discards.</param>
public sealed record ReceivedWebhook(
    string? WebhookId, string? Timestamp, string? SignatureHeader, bool SignatureValid, VerificationFailure Failure, bool FirstSeen,
    long ReceivedAtTicks, int Body);

/// <summary>
/// A real Kestrel receiver on 127.0.0.1 with scriptable faults. It verifies every signature with the Standard Webhooks
/// verifier and records webhook-ids the way an idempotent consumer would, so tests can assert "no loss" (every id seen)
/// and "bounded, detectable duplicates" (repeat ids) independently of what the relay reports about itself.
/// </summary>
public sealed class TestReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ReceivedWebhook> _requests = new();
    private readonly ConcurrentDictionary<string, int> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _processed = new(StringComparer.Ordinal);
    private volatile WebhookVerifier _verifier;

    private TestReceiver(WebApplication app, WebhookVerifier verifier)
    {
        _app = app;
        _verifier = verifier;
    }

    public Uri Url { get; private set; } = null!;

    /// <summary>Decides the response per request; can be swapped while the receiver runs (a receiver recovering, flapping...).</summary>
    public Func<ReceivedWebhook, ReceiverAction> Behavior { get; set; } = _ => ReceiverAction.Ok;

    public IReadOnlyCollection<ReceivedWebhook> Requests => _requests;

    /// <summary>Distinct webhook-ids the receiver processed (committed and meant to answer 2xx). "No loss" means every published id is here.</summary>
    public int UniqueIds => _processed.Count;

    /// <summary>Processings of an id that was already processed: what an idempotent receiver must discard by webhook-id.</summary>
    public int Duplicates => _processed.Values.Sum(n => n - 1);

    public int InvalidSignatures => _requests.Count(r => !r.SignatureValid);

    public bool HasProcessed(string id) => _processed.ContainsKey(id);

    /// <summary>Verified requests carrying this id, whatever the receiver answered.</summary>
    public int TimesSeen(string id) => _seen.TryGetValue(id, out var n) ? n : 0;

    public int TimesProcessed(string id) => _processed.TryGetValue(id, out var n) ? n : 0;

    public void UseSecrets(params WebhookSecret[] secrets) => _verifier = new WebhookVerifier(secrets);

    public static async Task<TestReceiver> StartAsync(params WebhookSecret[] secrets)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Limits.MinRequestBodyDataRate = null);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var receiver = new TestReceiver(app, new WebhookVerifier(secrets.Length > 0 ? secrets : [WebhookSecret.Generate()]));
        app.MapPost("/hook", receiver.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        receiver.Url = new Uri(new Uri(address), "/hook");
        return receiver;
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();
        var id = context.Request.Headers[WebhookHeaders.Id].ToString();
        var ts = context.Request.Headers[WebhookHeaders.Timestamp].ToString();
        var sig = context.Request.Headers[WebhookHeaders.Signature].ToString();
        var result = _verifier.Verify(id, ts, sig, body);

        var firstSeen = false;
        if (result.IsValid)
        {
            var count = _seen.AddOrUpdate(id, 1, (_, n) => n + 1);
            firstSeen = count == 1;
        }

        var received = new ReceivedWebhook(id, ts, sig, result.IsValid, result.Failure, firstSeen, Stopwatch.GetTimestamp(), body.Length);
        _requests.Enqueue(received);

        if (!result.IsValid)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var action = Behavior(received);

        // A request whose eventual answer is 2xx counts as processed now, before any delay: the receiver committed its
        // work and the response may still be lost (relay timeout). That is exactly where at-least-once duplicates come from.
        if (Final(action) is ReceiverAction.Respond { StatusCode: >= 200 and < 300 })
        {
            _processed.AddOrUpdate(id, 1, (_, n) => n + 1);
        }

        await ApplyAsync(action, context);
    }

    private static ReceiverAction Final(ReceiverAction action) => action is ReceiverAction.Delay delay ? Final(delay.Then) : action;

    private static async Task ApplyAsync(ReceiverAction action, HttpContext context)
    {
        switch (action)
        {
            case ReceiverAction.Delay delay:
                try
                {
                    await Task.Delay(delay.Duration, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    return; // the relay gave up (timeout); nothing to answer
                }

                await ApplyAsync(delay.Then, context);
                break;
            case ReceiverAction.Abort:
                context.Abort();
                break;
            case ReceiverAction.Respond respond:
                context.Response.StatusCode = respond.StatusCode;
                if (respond.RetryAfter is not null)
                {
                    context.Response.Headers.RetryAfter = respond.RetryAfter;
                }

                break;
        }
    }

    /// <summary>Waits until <paramref name="count"/> distinct webhook-ids have been received.</summary>
    public async Task WaitForUniqueAsync(int count, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (UniqueIds < count)
        {
            if (deadline.Elapsed > timeout)
            {
                throw new TimeoutException($"Received {UniqueIds} of {count} distinct webhooks within {timeout}.");
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
