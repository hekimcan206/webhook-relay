using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using WebhookRelay.Core.Breaker;

namespace WebhookRelay.UnitTests.Breaker;

public sealed class CircuitBreakerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly CircuitOptions Options = new()
    {
        FailureThreshold = 3,
        OpenDuration = TimeSpan.FromSeconds(30),
        OpenBackoffMultiplier = 2,
        MaxOpenDuration = TimeSpan.FromMinutes(2),
    };

    [Fact]
    public void Opens_after_the_threshold_of_consecutive_failures()
    {
        var c = Circuit.Closed;
        c = CircuitBreaker.OnFailure(c, T0, Options);
        c = CircuitBreaker.OnFailure(c, T0, Options);
        c.State.Should().Be(CircuitState.Closed);

        c = CircuitBreaker.OnFailure(c, T0, Options);

        c.State.Should().Be(CircuitState.Open);
        c.OpenUntil.Should().Be(T0.AddSeconds(30));
        c.ConsecutiveOpens.Should().Be(1);
    }

    [Fact]
    public void A_success_resets_the_failure_count()
    {
        var c = CircuitBreaker.OnFailure(CircuitBreaker.OnFailure(Circuit.Closed, T0, Options), T0, Options);
        c = CircuitBreaker.OnSuccess(c);
        c = CircuitBreaker.OnFailure(CircuitBreaker.OnFailure(c, T0, Options), T0, Options);

        c.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void Probe_is_due_only_after_the_open_period()
    {
        var open = new Circuit(CircuitState.Open, 3, 1, T0.AddSeconds(30));

        CircuitBreaker.ProbeDue(open, T0.AddSeconds(29)).Should().BeFalse();
        CircuitBreaker.ProbeDue(open, T0.AddSeconds(30)).Should().BeTrue();
        FluentActions.Invoking(() => CircuitBreaker.StartProbe(open, T0)).Should().Throw<InvalidOperationException>();
        CircuitBreaker.StartProbe(open, T0.AddSeconds(30)).State.Should().Be(CircuitState.HalfOpen);
    }

    [Fact]
    public void A_failed_probe_reopens_for_longer_up_to_the_cap()
    {
        var c = new Circuit(CircuitState.Open, 3, 1, T0);
        var durations = new List<TimeSpan>();
        var now = T0;
        for (var i = 0; i < 5; i++)
        {
            c = CircuitBreaker.StartProbe(c, now);
            c = CircuitBreaker.OnFailure(c, now, Options);
            durations.Add(c.OpenUntil!.Value - now);
            now = c.OpenUntil.Value;
        }

        durations.Select(d => d.TotalSeconds).Should().Equal(60, 120, 120, 120, 120);
    }

    [Fact]
    public void A_successful_probe_closes_and_forgets_the_history()
    {
        var c = CircuitBreaker.StartProbe(new Circuit(CircuitState.Open, 9, 4, T0), T0);

        CircuitBreaker.OnSuccess(c).Should().Be(Circuit.Closed);
    }

    [Fact]
    public void A_late_failure_while_open_keeps_the_window()
    {
        var open = new Circuit(CircuitState.Open, 3, 1, T0.AddSeconds(30));

        var after = CircuitBreaker.OnFailure(open, T0.AddSeconds(5), Options);

        after.State.Should().Be(CircuitState.Open);
        after.OpenUntil.Should().Be(T0.AddSeconds(30));
        after.ConsecutiveFailures.Should().Be(4);
    }

    [Fact]
    public void Abandoning_a_probe_makes_the_circuit_probe_again_immediately()
    {
        var halfOpen = CircuitBreaker.StartProbe(new Circuit(CircuitState.Open, 3, 1, T0), T0);

        var back = CircuitBreaker.AbandonProbe(halfOpen, T0.AddSeconds(1));

        back.State.Should().Be(CircuitState.Open);
        CircuitBreaker.ProbeDue(back, T0.AddSeconds(1)).Should().BeTrue();
    }

    private enum Step
    {
        Success,
        Failure,
        Wait,
    }

    [Property(MaxTest = 500)]
    public Property Random_histories_respect_the_breaker_invariants() =>
        Prop.ForAll(Gen.ListOf(Gen.Elements(Step.Success, Step.Failure, Step.Failure, Step.Wait)).ToArbitrary(), steps =>
        {
            var c = Circuit.Closed;
            var now = T0;
            var failuresSinceSuccess = 0;
            foreach (var step in steps)
            {
                if (CircuitBreaker.ProbeDue(c, now))
                {
                    c = CircuitBreaker.StartProbe(c, now);
                }

                // While open (not due) nothing is sent, so only Wait advances the history.
                var sendAllowed = c.State != CircuitState.Open;
                switch (step)
                {
                    case Step.Success when sendAllowed:
                        c = CircuitBreaker.OnSuccess(c);
                        failuresSinceSuccess = 0;
                        break;
                    case Step.Failure when sendAllowed:
                        c = CircuitBreaker.OnFailure(c, now, Options);
                        failuresSinceSuccess++;
                        break;
                    case Step.Wait:
                        now += TimeSpan.FromSeconds(45);
                        break;
                }

                var ok = c.ConsecutiveFailures == failuresSinceSuccess
                    && (c.State != CircuitState.Closed || c.ConsecutiveFailures < Options.FailureThreshold)
                    && (c.State != CircuitState.Open || (c.OpenUntil is { } until && until - now <= Options.MaxOpenDuration))
                    && (c.State == CircuitState.Closed || c.ConsecutiveOpens >= 1);
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        });
}
