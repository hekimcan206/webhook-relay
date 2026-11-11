namespace WebhookRelay.Server;

public sealed class RelayOptions
{
    public const string Section = "Relay";

    /// <summary>Key required in <c>X-Admin-Key</c> for the management, publish and replay APIs. At least 16 characters.</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>Reject endpoint URLs that are not https. Off by default only so the local demo can use plain http.</summary>
    public bool RequireHttps { get; set; }

    /// <summary>Largest accepted payload. The whole body is held in memory while signing and sending.</summary>
    public int MaxPayloadBytes { get; set; } = 256 * 1024;

    public void Validate()
    {
        if (AdminKey.Length < 16)
        {
            throw new InvalidOperationException("Relay:AdminKey must be configured with at least 16 characters.");
        }
    }
}

public sealed class DeliveryOptions
{
    public const string Section = "Relay:Delivery";

    /// <summary>Run the delivery worker in this process. API-only instances set this to false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Concurrent HTTP attempts per relay instance.</summary>
    public int MaxConcurrency { get; set; } = 64;

    /// <summary>Largest number of deliveries claimed by one query.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Fallback poll interval; LISTEN/NOTIFY normally wakes the worker as soon as a message is published.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-attempt timeout covering connect, send and response headers.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a claimed delivery stays invisible to other workers. If the relay dies mid-delivery, the row becomes
    /// claimable again after this, which is what turns a crash into a retry instead of a lost message.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Deliveries older than this are dead-lettered even if attempts remain (for example behind an open circuit).</summary>
    public TimeSpan MaxMessageAge { get; set; } = TimeSpan.FromDays(4);

    /// <summary>Permit delivery to private, loopback and link-local addresses. Only for local demos and tests.</summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Send NOTIFY on publish so idle workers wake immediately instead of on the next poll.</summary>
    public bool NotifyOnPublish { get; set; } = true;

    /// <summary>Unique per process; defaults to host:pid:random.</summary>
    public string? InstanceId { get; set; }

    public void Validate()
    {
        if (MaxConcurrency < 1 || BatchSize < 1 || PollInterval <= TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Relay:Delivery: MaxConcurrency, BatchSize, PollInterval and RequestTimeout must be positive.");
        }

        // A lease shorter than the request would let a second worker claim a delivery that is still in flight.
        if (LeaseDuration < RequestTimeout + RequestTimeout)
        {
            throw new InvalidOperationException("Relay:Delivery:LeaseDuration must be at least twice RequestTimeout.");
        }
    }
}
