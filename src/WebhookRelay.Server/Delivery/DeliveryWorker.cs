using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Npgsql;
using WebhookRelay.Core.Breaker;
using WebhookRelay.Core.Retry;
using WebhookRelay.Server.Infrastructure;
using WebhookRelay.Server.Persistence;

namespace WebhookRelay.Server.Delivery;

/// <summary>
/// The delivery loop. It keeps up to <see cref="DeliveryOptions.MaxConcurrency"/> attempts in flight as a continuous
/// pipeline rather than in lock-step batches, so one slow receiver occupies one slot instead of stalling a whole batch.
///
/// Guarantee: at-least-once. A delivery is marked delivered only after a 2xx came back, in a transaction after the HTTP
/// call. A crash between the receiver's 2xx and that commit leaves the row in_flight; when its lease expires another
/// attempt is made with the same webhook-id, which is the duplicate receivers are told to discard.
/// </summary>
internal sealed partial class DeliveryWorker(
    RelayStore store,
    WebhookSender sender,
    DeliverySignal signal,
    RelayMetrics metrics,
    RetryPolicy retryPolicy,
    IOptions<CircuitOptions> circuitOptions,
    IOptions<DeliveryOptions> deliveryOptions,
    RelayIdentity identity,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ExpirySweepInterval = TimeSpan.FromSeconds(30);

    private readonly DeliverySignal _slotFreed = new();
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();

    // Cancelled only when the graceful drain period is over: attempts still running are then abandoned and released.
    private readonly CancellationTokenSource _hardStop = new();

    private DeliveryOptions Options => deliveryOptions.Value;

    public int InFlight => _inFlight.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(identity.Id, Options.MaxConcurrency);
        var lastProbe = DateTimeOffset.MinValue;
        var lastSweep = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastSweep >= ExpirySweepInterval)
                {
                    lastSweep = now;
                    var expired = await store.ExpireAsync(Options.MaxMessageAge, stoppingToken);
                    if (expired > 0)
                    {
                        LogExpired(expired);
                    }
                }

                // Probes are cheap to look for but not free; checking once per poll interval bounds the query rate while
                // keeping probe latency within one interval of the open period ending.
                if (now - lastProbe >= Options.PollInterval && FreeSlots > 0)
                {
                    lastProbe = now;
                    foreach (var probe in await store.ClaimProbesAsync(Options.LeaseDuration, identity.Id, stoppingToken))
                    {
                        Start(probe);
                    }
                }

                var free = FreeSlots;
                if (free < Math.Max(1, Math.Min(Options.BatchSize, Options.MaxConcurrency) / 4))
                {
                    // Nearly full: wait for attempts to finish before paying for another claim round trip.
                    await _slotFreed.WaitAsync(Options.PollInterval, stoppingToken);
                    continue;
                }

                var want = Math.Min(free, Options.BatchSize);
                var claimed = await store.ClaimAsync(want, Options.LeaseDuration, identity.Id, stoppingToken);
                foreach (var delivery in claimed)
                {
                    Start(delivery);
                }

                if (claimed.Count < want)
                {
                    // The queue is drained for now: sleep until a NOTIFY, or the poll interval for retries coming due.
                    await signal.WaitAsync(Options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                // Database unavailable: claimed rows stay leased and are retried after the lease; keep the loop alive.
                LogLoopFailed(ex);
                await Task.Delay(Options.PollInterval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private int FreeSlots => Options.MaxConcurrency - _inFlight.Count;

    private void Start(ClaimedDelivery delivery)
    {
        // Registered before it may run, so a very fast attempt cannot remove itself before it was added.
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlight[delivery.Id] = Task.Run(async () =>
        {
            await registered.Task;
            await ProcessAsync(delivery);
        });
        registered.SetResult();
    }

    private async Task ProcessAsync(ClaimedDelivery delivery)
    {
        try
        {
            var result = await sender.SendAsync(delivery, _hardStop.Token);
            metrics.Attempt(result.Outcome, result.Duration, delivery.IsProbe);

            // Recording uses its own token: an attempt that finished during shutdown must still be written, otherwise
            // a delivered message would be sent again after the restart.
            var recorded = await store.RecordAsync(
                delivery, result.Outcome, result.StartedAt, result.Duration, identity.Id, retryPolicy, circuitOptions.Value,
                Random.Shared.NextDouble(), CancellationToken.None);

            metrics.Finished(recorded.Status, result.StartedAt + result.Duration - delivery.MessageCreatedAt);
            if (recorded.CircuitFrom is { } from && recorded.CircuitTo is { } to)
            {
                metrics.CircuitTransition(from, to);
                LogCircuit(delivery.EndpointId, from, to);
            }

            if (recorded.LeaseLost)
            {
                LogLeaseLost(delivery.Id, delivery.MessageId);
            }
        }
        catch (OperationCanceledException) when (_hardStop.IsCancellationRequested)
        {
            // Abandoned at shutdown; StopAsync releases the lease.
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or FormatException)
        {
            // The attempt may have reached the receiver but its result could not be stored. The lease expires and the
            // delivery is attempted again: a duplicate, never a loss.
            LogRecordFailed(ex, delivery.Id);
        }
        finally
        {
            _inFlight.TryRemove(delivery.Id, out _);
            _slotFreed.Set();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop claiming, then give running attempts until the host's shutdown deadline to finish and record.
        await base.StopAsync(cancellationToken);
        var pending = _inFlight.Values.ToArray();
        if (pending.Length > 0)
        {
            LogDraining(pending.Length);
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (!_inFlight.IsEmpty)
        {
            await _hardStop.CancelAsync();
            var released = await store.ReleaseAsync([.. _inFlight.Keys], identity.Id, CancellationToken.None);
            LogReleased(released);
        }
    }

    public override void Dispose()
    {
        _hardStop.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivery worker {RelayId} started with {MaxConcurrency} slots")]
    private partial void LogStarted(string relayId, int maxConcurrency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-lettered {Count} deliveries older than the maximum message age")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Circuit for endpoint {EndpointId} moved {From} -> {To}")]
    private partial void LogCircuit(Guid endpointId, CircuitState from, CircuitState to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease lost for delivery {DeliveryId} ({MessageId}); another instance owns it now")]
    private partial void LogLeaseLost(long deliveryId, string messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery loop failed; retrying after the poll interval")]
    private partial void LogLoopFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record the outcome of delivery {DeliveryId}; it will be retried after its lease")]
    private partial void LogRecordFailed(Exception exception, long deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Draining {Count} in-flight attempts before shutdown")]
    private partial void LogDraining(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Released {Count} unfinished deliveries at shutdown")]
    private partial void LogReleased(int count);
}

/// <summary>Identifies this process in leases and the attempt log.</summary>
internal sealed record RelayIdentity(string Id)
{
    public static RelayIdentity Create(string? configured) =>
        new(string.IsNullOrWhiteSpace(configured)
            ? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..6]}"
            : configured);
}
