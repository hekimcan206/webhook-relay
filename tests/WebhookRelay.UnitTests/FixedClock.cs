namespace WebhookRelay.UnitTests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public static FixedClock AtUnix(long seconds) => new(DateTimeOffset.FromUnixTimeSeconds(seconds));

    public override DateTimeOffset GetUtcNow() => Now;
}
