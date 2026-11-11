using System.Diagnostics;
using WebhookRelay.IntegrationTests.Infrastructure;
using WebhookRelay.TestKit;

namespace WebhookRelay.IntegrationTests;

/// <summary>
/// The relay runs as a separate OS process and is killed hard (no graceful shutdown, no lease release) in the middle
/// of delivering, twice. After restarts every message must be processed by the receiver, and the duplicates must be
/// bounded by what was in flight at each kill, each one recognisable by its webhook-id.
/// </summary>
public sealed class CrashRecoveryTests(DatabaseFixture fixture) : RelayTestBase(fixture)
{
    private const int Messages = 300;
    private const int Concurrency = 16;

    [Fact]
    public async Task Killing_the_relay_mid_delivery_loses_nothing_and_duplicates_stay_within_the_in_flight_window()
    {
        var receiver = await StartReceiverAsync();
        receiver.Behavior = _ => new ReceiverAction.Delay(TimeSpan.FromMilliseconds(150), ReceiverAction.Ok);

        // API-only relay in-process (its delivery worker is off), so the child process is the only deliverer.
        var api = await StartRelayAsync(s => s["Relay:Delivery:Enabled"] = "false");
        await ConnectAsync(api, receiver);
        for (var i = 0; i < Messages; i++)
        {
            await api.Client.PublishAsync("crash.test", new { i });
        }

        var inFlightAtKills = new List<long>();
        foreach (var killAfter in new[] { 60, 170 })
        {
            using var relay = RelayProcess.Start(Db.AppConnectionString);
            await receiver.WaitForUniqueAsync(killAfter, TimeSpan.FromSeconds(90));
            relay.Kill();
            // Rows the killed process held (leases from an earlier kill may still be outstanding, so group by holder).
            inFlightAtKills.Add(await Db.ScalarAsync<long>("""
                SELECT count(*) FROM relay.deliveries WHERE status = 'in_flight' GROUP BY leased_by ORDER BY max(lease_until) DESC LIMIT 1
                """));
        }

        using (RelayProcess.Start(Db.AppConnectionString))
        {
            await receiver.WaitForUniqueAsync(Messages, TimeSpan.FromSeconds(120));
            await api.Client.WaitForStatsAsync(s => s["delivered"] == Messages, TimeSpan.FromSeconds(60));
        }

        receiver.UniqueIds.Should().Be(Messages, "no message is lost across two hard kills");
        receiver.InvalidSignatures.Should().Be(0);
        inFlightAtKills.Should().AllSatisfy(n => n.Should().BeInRange(1, Concurrency), "each kill interrupted real in-flight work");
        receiver.Duplicates.Should().BeLessThanOrEqualTo((int)inFlightAtKills.Sum(),
            "only deliveries in flight at a kill can be sent twice; each is detectable by its repeated webhook-id");
        (await Db.ScalarAsync<long>("SELECT count(DISTINCT relay_id) FROM relay.attempts")).Should().BeGreaterThanOrEqualTo(2);
    }

    /// <summary>The relay binary (the same one the container runs), started with environment configuration.</summary>
    private sealed class RelayProcess : IDisposable
    {
        private readonly Process _process;

        private RelayProcess(Process process) => _process = process;

        public static RelayProcess Start(string connectionString)
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "WebhookRelay.Server.dll");
            var info = new ProcessStartInfo("dotnet", [dll])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            info.Environment["ConnectionStrings__Default"] = connectionString;
            info.Environment["Relay__AdminKey"] = RelayInstance.AdminKey;
            info.Environment["Relay__Delivery__AllowPrivateNetworks"] = "true";
            info.Environment["Relay__Delivery__MaxConcurrency"] = Concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture);
            info.Environment["Relay__Delivery__PollInterval"] = "00:00:00.200";
            info.Environment["Relay__Delivery__RequestTimeout"] = "00:00:01";
            info.Environment["Relay__Delivery__LeaseDuration"] = "00:00:03";
            info.Environment["Relay__Retry__BaseDelay"] = "00:00:00.200";
            info.Environment["Relay__Retry__MaxDelay"] = "00:00:01";
            info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            info.Environment["DOTNET_ENVIRONMENT"] = "Production";
            info.Environment["Serilog__MinimumLevel__Default"] = "Warning";

            var process = Process.Start(info)!;
            // Drain the pipes so a chatty child can never block on a full stdout buffer.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new RelayProcess(process);
        }

        /// <summary>TerminateProcess / SIGKILL: no shutdown hooks run, leases are left behind.</summary>
        public void Kill()
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                Kill();
            }

            _process.Dispose();
        }
    }
}
