using WebhookRelay.TestKit;

[assembly: AssemblyFixture(typeof(WebhookRelay.IntegrationTests.Infrastructure.DatabaseFixture))]

namespace WebhookRelay.IntegrationTests.Infrastructure;

/// <summary>One PostgreSQL container for the whole run; every test starts from empty tables.</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public PostgresDatabase Database { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Database = await PostgresDatabase.StartAsync();

    public async ValueTask DisposeAsync() => await Database.DisposeAsync();
}

/// <summary>Base class: resets the database, and disposes relays and receivers a test created.</summary>
public abstract class RelayTestBase(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _owned = [];

    protected PostgresDatabase Db => fixture.Database;

    public async ValueTask InitializeAsync() => await Db.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        // Relays first, so no attempt is in flight against a receiver that is already gone.
        foreach (var owned in _owned.OfType<RelayInstance>().Cast<IAsyncDisposable>().Concat(_owned.Where(o => o is not RelayInstance)))
        {
            await owned.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    protected async Task<RelayInstance> StartRelayAsync(Action<Dictionary<string, string?>>? configure = null)
    {
        var relay = await RelayInstance.StartAsync(Db.AppConnectionString, configure);
        _owned.Add(relay);
        return relay;
    }

    protected async Task<TestReceiver> StartReceiverAsync()
    {
        var receiver = await TestReceiver.StartAsync();
        _owned.Add(receiver);
        return receiver;
    }

    /// <summary>Registers the receiver at the relay and makes the receiver verify with the endpoint's secret.</summary>
    protected static async Task<Guid> ConnectAsync(RelayInstance relay, TestReceiver receiver, string[]? eventTypes = null)
    {
        var (id, secret) = await relay.Client.CreateEndpointAsync(receiver.Url, eventTypes);
        receiver.UseSecrets(Signing.WebhookSecret.Parse(secret));
        return id;
    }

    protected static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
}
