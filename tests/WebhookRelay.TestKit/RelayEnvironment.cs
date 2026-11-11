using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using WebhookRelay.Server;

namespace WebhookRelay.TestKit;

/// <summary>
/// PostgreSQL 17 in a container, initialised with the same least-privilege role script as docker compose and migrated
/// as <c>relay_migrator</c>. Relays connect as <c>relay_app</c>.
/// </summary>
public sealed class PostgresDatabase : IAsyncDisposable
{
    public const string MigratorPassword = "migrator-test";
    public const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _container;

    private PostgresDatabase(PostgreSqlContainer container) => _container = container;

    public string SuperuserConnectionString => _container.GetConnectionString();

    public string MigratorConnectionString => WithCredentials("relay_migrator", MigratorPassword);

    public string AppConnectionString => WithCredentials("relay_app", AppPassword);

    /// <param name="tuning">Extra <c>postgres -c</c> settings (the benchmark harness uses this; tests use defaults).</param>
    public static async Task<PostgresDatabase> StartAsync(params string[] tuning)
    {
        var builder = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("relay")
            .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("APP_PASSWORD", AppPassword)
            .WithResourceMapping(
                await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
                "/docker-entrypoint-initdb.d/01-roles.sh");
        if (tuning.Length > 0)
        {
            builder = builder.WithCommand([.. tuning.SelectMany(t => new[] { "-c", t })]);
        }

        var container = builder.Build();
        await container.StartAsync();
        var database = new PostgresDatabase(container);
        await database.MigrateAsync();
        return database;
    }

    public async Task MigrateAsync()
    {
        // The real migrate path: the relay binary's --migrate-only role, in-process.
        await using var app = RelayApp.Build([], b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = MigratorConnectionString,
            ["Relay:Delivery:Enabled"] = "false",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        }));
        await app.Services.GetRequiredService<WebhookRelay.Server.Persistence.MigrationRunner>().MigrateAsync();
    }

    /// <summary>Removes all relay data between tests (as the schema owner; the application role may not truncate).</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE relay.attempts, relay.circuit_events, relay.deliveries, relay.messages, relay.endpoint_secrets, relay.endpoints", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private string WithCredentials(string user, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = user,
            Password = password,
            Timeout = 30,
            CommandTimeout = 60,
        }.ConnectionString;
}

/// <summary>A real relay (Kestrel, delivery worker, LISTEN/NOTIFY) started in-process with configuration overrides.</summary>
public sealed class RelayInstance : IAsyncDisposable
{
    public const string AdminKey = "admin-key-for-tests-only";

    private readonly WebApplication _app;

    private RelayInstance(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
        Client = CreateClient(baseAddress);
    }

    public Uri BaseAddress { get; }

    public RelayClient Client { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>Settings tuned for tests: sub-second backoff, short timeouts and leases.</summary>
    public static Dictionary<string, string?> FastSettings(string connectionString) => new()
    {
        ["ConnectionStrings:Default"] = connectionString,
        ["Relay:AdminKey"] = AdminKey,
        ["Relay:Delivery:AllowPrivateNetworks"] = "true",
        ["Relay:Delivery:MaxConcurrency"] = "32",
        ["Relay:Delivery:PollInterval"] = "00:00:00.200",
        ["Relay:Delivery:RequestTimeout"] = "00:00:01",
        ["Relay:Delivery:LeaseDuration"] = "00:00:03",
        ["Relay:Retry:BaseDelay"] = "00:00:00.200",
        ["Relay:Retry:Multiplier"] = "2",
        ["Relay:Retry:MaxDelay"] = "00:00:02",
        ["Relay:Retry:MaxAttempts"] = "4",
        ["Relay:Circuit:FailureThreshold"] = "1000",
        ["Relay:Circuit:OpenDuration"] = "00:00:01",
        ["Relay:Circuit:MaxOpenDuration"] = "00:00:04",
        ["Serilog:MinimumLevel:Default"] = "Warning",
        ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
    };

    public static async Task<RelayInstance> StartAsync(string connectionString, Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = FastSettings(connectionString);
        configure?.Invoke(settings);
        var app = RelayApp.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new RelayInstance(app, new Uri(address + "/"));
    }

    public static RelayClient CreateClient(Uri baseAddress)
    {
        var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        return new RelayClient(http);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Thin typed client over the relay's HTTP API.</summary>
public sealed class RelayClient(HttpClient http) : IDisposable
{
    public HttpClient Http => http;

    public async Task<(Guid Id, string Secret)> CreateEndpointAsync(Uri url, string[]? eventTypes = null, string? secret = null)
    {
        var response = await http.PostAsJsonAsync("api/v1/endpoints", new { url = url.ToString(), eventTypes, secret });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("secret").GetString()!);
    }

    public async Task<string> PublishAsync(string eventType, object payload, string? idempotencyKey = null)
    {
        var response = await http.PostAsJsonAsync("api/v1/messages", new { eventType, payload, idempotencyKey });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;
    }

    public async Task<JsonElement> GetAsync(string path)
    {
        var response = await http.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<IReadOnlyDictionary<string, long>> StatsAsync()
    {
        var stats = await GetAsync("api/v1/stats");
        return stats.GetProperty("deliveries").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
    }

    /// <summary>Polls until <paramref name="predicate"/> holds for the delivery counts.</summary>
    public async Task<IReadOnlyDictionary<string, long>> WaitForStatsAsync(Func<IReadOnlyDictionary<string, long>, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var stats = await StatsAsync();
            if (predicate(stats))
            {
                return stats;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Delivery counts did not reach the expected state: " + string.Join(", ", stats.Select(kv => $"{kv.Key}={kv.Value}")));
            }

            await Task.Delay(100);
        }
    }

    public void Dispose() => http.Dispose();
}
