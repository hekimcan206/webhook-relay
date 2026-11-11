using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using WebhookRelay.Core.Retry;

namespace WebhookRelay.UnitTests.Retry;

public sealed class RetryPolicyTests
{
    private static readonly RetryPolicy Default = new(new RetryOptions());

    [Fact]
    public void Default_schedule_is_golden()
    {
        // 10 s × 3^n capped at 12 h. Changing the defaults changes what receivers experience; this test makes it deliberate.
        var nominal = Enumerable.Range(1, 13).Select(n => Default.NominalDelay(n).TotalSeconds).ToArray();

        nominal.Should().Equal(10, 30, 90, 270, 810, 2430, 7290, 21870, 43200, 43200, 43200, 43200, 43200);
        TimeSpan.FromSeconds(nominal.Sum()).TotalDays.Should().BeApproximately(2.88, 0.01);
    }

    [Fact]
    public void Max_attempts_counts_the_first_attempt()
    {
        Default.CanRetry(13).Should().BeTrue();
        Default.CanRetry(14).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.0, 7.5)]
    [InlineData(0.5, 10.0)]
    [InlineData(0.999999, 12.5)]
    public void Proportional_jitter_spans_a_quarter_either_side(double draw, double expectedSeconds)
    {
        Default.NextDelay(1, draw).TotalSeconds.Should().BeApproximately(expectedSeconds, 0.001);
    }

    [Fact]
    public void Full_jitter_draws_between_zero_and_the_nominal_delay()
    {
        var policy = new RetryPolicy(new RetryOptions { Jitter = JitterMode.Full });

        policy.NextDelay(3, 0).Should().Be(TimeSpan.Zero);
        policy.NextDelay(3, 0.5).TotalSeconds.Should().BeApproximately(45, 0.001);
    }

    [Fact]
    public void Retry_after_extends_but_never_shortens_and_is_capped()
    {
        Default.NextDelay(1, 0.5, TimeSpan.FromSeconds(1)).TotalSeconds.Should().BeApproximately(10, 0.001);
        Default.NextDelay(1, 0.5, TimeSpan.FromMinutes(2)).Should().Be(TimeSpan.FromMinutes(2));
        Default.NextDelay(1, 0.5, TimeSpan.FromDays(365)).Should().Be(TimeSpan.FromHours(12));
    }

    [Fact]
    public void Huge_attempt_numbers_do_not_overflow()
    {
        Default.NominalDelay(500).Should().Be(TimeSpan.FromHours(12));
        Default.NextDelay(500, 0.999).Should().Be(TimeSpan.FromHours(12));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void Draws_outside_the_unit_interval_are_rejected(double draw)
    {
        FluentActions.Invoking(() => Default.NextDelay(1, draw)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        FluentActions.Invoking(() => new RetryPolicy(new RetryOptions { Multiplier = 0.5 })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new RetryPolicy(new RetryOptions { MaxDelay = TimeSpan.FromSeconds(1) })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new RetryPolicy(new RetryOptions { JitterFraction = 1 })).Should().Throw<ArgumentException>();
    }

    private static Arbitrary<RetryOptions> Options() =>
        (from baseMs in Gen.Choose(1, 60_000)
         from multiplier in Gen.Choose(10, 40).Select(x => x / 10.0)
         from capFactor in Gen.Choose(1, 10_000)
         from jitter in Gen.Elements(JitterMode.None, JitterMode.Proportional, JitterMode.Full)
         from fraction in Gen.Choose(0, 90).Select(x => x / 100.0)
         select new RetryOptions
         {
             BaseDelay = TimeSpan.FromMilliseconds(baseMs),
             Multiplier = multiplier,
             MaxDelay = TimeSpan.FromMilliseconds((long)baseMs * capFactor),
             Jitter = jitter,
             JitterFraction = fraction,
         }).ToArbitrary();

    [Property(MaxTest = 500)]
    public Property Every_delay_is_within_its_bounds_and_never_exceeds_the_cap() =>
        Prop.ForAll(Options(), Gen.Choose(1, 40).ToArbitrary(), Gen.Choose(0, 999_999).ToArbitrary(), (options, attempt, draw) =>
        {
            var policy = new RetryPolicy(options);
            var delay = policy.NextDelay(attempt, draw / 1_000_000.0);
            var (min, max) = policy.Bounds(attempt);
            var tolerance = TimeSpan.FromTicks(1);
            return delay >= min - tolerance && delay <= max + tolerance && delay <= options.MaxDelay && delay >= TimeSpan.Zero;
        });

    [Property(MaxTest = 300)]
    public Property Nominal_schedule_is_non_decreasing() =>
        Prop.ForAll(Options(), Gen.Choose(1, 40).ToArbitrary(), (options, attempt) =>
        {
            var policy = new RetryPolicy(options);
            return policy.NominalDelay(attempt + 1) >= policy.NominalDelay(attempt);
        });
}
