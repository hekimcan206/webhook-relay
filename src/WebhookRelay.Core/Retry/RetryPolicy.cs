namespace WebhookRelay.Core.Retry;

public enum JitterMode
{
    /// <summary>No randomness. Only for tests and for showing why jitter matters.</summary>
    None,

    /// <summary>Uniform in [d·(1−j), d·(1+j)]: keeps the schedule recognisable while spreading a retry herd.</summary>
    Proportional,

    /// <summary>Uniform in [0, d] ("full jitter"): the least synchronised, but a retry can come almost immediately.</summary>
    Full,
}

public sealed record RetryOptions
{
    public const string Section = "Relay:Retry";

    /// <summary>Delay before the first retry (after the first failed attempt).</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(10);

    public double Multiplier { get; init; } = 3.0;

    /// <summary>Upper bound for any single delay, including jitter and a receiver's Retry-After.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Total attempts including the first one. With the defaults the last retry lands about 2.9 days in, close to the Standard Webhooks example schedule.</summary>
    public int MaxAttempts { get; init; } = 14;

    public JitterMode Jitter { get; init; } = JitterMode.Proportional;

    /// <summary>Half-width of the proportional jitter band, as a fraction of the nominal delay.</summary>
    public double JitterFraction { get; init; } = 0.25;

    public void Validate()
    {
        if (BaseDelay <= TimeSpan.Zero || MaxDelay < BaseDelay)
        {
            throw new ArgumentException("RetryOptions: BaseDelay must be positive and not larger than MaxDelay.");
        }

        if (Multiplier < 1.0 || MaxAttempts < 1 || JitterFraction is < 0 or >= 1)
        {
            throw new ArgumentException("RetryOptions: Multiplier >= 1, MaxAttempts >= 1 and 0 <= JitterFraction < 1 are required.");
        }
    }
}

/// <summary>
/// Exponential backoff with jitter. Pure: the random draw is a parameter, so the schedule is testable and the
/// distribution can be measured without waiting hours.
/// </summary>
public sealed class RetryPolicy
{
    public RetryPolicy(RetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
    }

    public RetryOptions Options { get; }

    /// <summary>True when a delivery that has used <paramref name="attemptsMade"/> attempts may be tried again.</summary>
    public bool CanRetry(int attemptsMade) => attemptsMade < Options.MaxAttempts;

    /// <summary>The delay without jitter after the <paramref name="failedAttempts"/>-th failed attempt (1-based).</summary>
    public TimeSpan NominalDelay(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);

        // Computed in double and capped before converting: 3^40 seconds would overflow TimeSpan.
        var seconds = Options.BaseDelay.TotalSeconds * Math.Pow(Options.Multiplier, failedAttempts - 1);
        return seconds >= Options.MaxDelay.TotalSeconds ? Options.MaxDelay : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The delay before the next attempt. <paramref name="random01"/> is a uniform draw in [0, 1).
    /// A receiver's Retry-After is honoured when it asks for more time than the backoff, never less, and never past
    /// <see cref="RetryOptions.MaxDelay"/>: a hostile or buggy receiver cannot park a message for a year.
    /// </summary>
    public TimeSpan NextDelay(int failedAttempts, double random01, TimeSpan? retryAfter = null)
    {
        if (random01 is < 0 or >= 1 || double.IsNaN(random01))
        {
            throw new ArgumentOutOfRangeException(nameof(random01), "Expected a value in [0, 1).");
        }

        var nominal = NominalDelay(failedAttempts).TotalSeconds;
        var jittered = Options.Jitter switch
        {
            JitterMode.None => nominal,
            JitterMode.Full => nominal * random01,
            _ => nominal * (1 - Options.JitterFraction + (2 * Options.JitterFraction * random01)),
        };

        if (retryAfter is { } hint && hint.TotalSeconds > jittered)
        {
            jittered = hint.TotalSeconds;
        }

        var capped = Math.Min(jittered, Options.MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(capped, 0));
    }

    /// <summary>Lower and upper bounds of <see cref="NextDelay"/> for an attempt, ignoring Retry-After.</summary>
    public (TimeSpan Min, TimeSpan Max) Bounds(int failedAttempts)
    {
        var nominal = NominalDelay(failedAttempts).TotalSeconds;
        var cap = Options.MaxDelay.TotalSeconds;
        var (low, high) = Options.Jitter switch
        {
            JitterMode.None => (nominal, nominal),
            JitterMode.Full => (0d, nominal),
            _ => (nominal * (1 - Options.JitterFraction), nominal * (1 + Options.JitterFraction)),
        };
        return (TimeSpan.FromSeconds(Math.Min(low, cap)), TimeSpan.FromSeconds(Math.Min(high, cap)));
    }
}
