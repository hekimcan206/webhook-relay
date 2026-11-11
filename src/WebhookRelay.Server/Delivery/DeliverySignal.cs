using System.Threading.Channels;
using Npgsql;

namespace WebhookRelay.Server.Delivery;

/// <summary>An auto-reset, coalescing wake-up: many signals before the next wait count as one.</summary>
internal sealed class DeliverySignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    public void Set() => _channel.Writer.TryWrite(true);

    /// <summary>Waits for a signal or the timeout, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_channel.Reader.TryRead(out _))
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out: the caller polls.
        }
    }
}

/// <summary>
/// Holds one connection in LISTEN on <c>relay_deliveries</c>. Publishing sends NOTIFY in the same transaction, so idle
/// workers wake within milliseconds of a commit. If the connection drops, polling still delivers; the listener reconnects.
/// </summary>
internal sealed partial class NotificationListener(
    NpgsqlDataSource dataSource, DeliverySignal signal, ILogger<NotificationListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(stoppingToken);
                conn.Notification += (_, _) => signal.Set();
                await using (var cmd = new NpgsqlCommand("LISTEN relay_deliveries", conn))
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }

                // Anything published while we were disconnected is picked up by this first wake.
                signal.Set();
                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or IOException or InvalidOperationException)
            {
                LogListenerFailed(ex);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "LISTEN connection lost; falling back to polling until it reconnects")]
    private partial void LogListenerFailed(Exception exception);
}
