using System.Net;
using System.Net.Sockets;
using WebhookRelay.Core.Delivery;

namespace WebhookRelay.UnitTests.Delivery;

public sealed class OutcomeAndEgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(200, OutcomeKind.Delivered, null)]
    [InlineData(204, OutcomeKind.Delivered, null)]
    [InlineData(299, OutcomeKind.Delivered, null)]
    [InlineData(301, OutcomeKind.Retryable, "http_3xx")]
    [InlineData(400, OutcomeKind.Retryable, "http_4xx")]
    [InlineData(401, OutcomeKind.Retryable, "http_4xx")]
    [InlineData(410, OutcomeKind.Gone, "gone")]
    [InlineData(429, OutcomeKind.Retryable, "http_429")]
    [InlineData(500, OutcomeKind.Retryable, "http_5xx")]
    [InlineData(503, OutcomeKind.Retryable, "http_5xx")]
    [InlineData(101, OutcomeKind.Retryable, "http_other")]
    public void Status_codes_are_classified_per_the_specification(int status, OutcomeKind kind, string? errorKind)
    {
        var outcome = OutcomeClassifier.FromStatus(status);

        outcome.Kind.Should().Be(kind);
        outcome.ErrorKind.Should().Be(errorKind);
        outcome.StatusCode.Should().Be(status);
    }

    [Fact]
    public void Retry_after_is_read_only_for_429_and_503()
    {
        OutcomeClassifier.FromStatus(429, "120", Now).RetryAfter.Should().Be(TimeSpan.FromMinutes(2));
        OutcomeClassifier.FromStatus(503, "Sun, 01 Nov 2026 12:01:00 GMT", Now).RetryAfter.Should().Be(TimeSpan.FromMinutes(1));
        OutcomeClassifier.FromStatus(500, "120", Now).RetryAfter.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5")]
    public void Unparseable_retry_after_is_ignored(string? header)
    {
        OutcomeClassifier.ParseRetryAfter(header, Now).Should().BeNull();
    }

    [Fact]
    public void Retry_after_dates_in_the_past_mean_now()
    {
        OutcomeClassifier.ParseRetryAfter("Sun, 01 Nov 2026 11:00:00 GMT", Now).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Exceptions_map_to_stable_error_kinds()
    {
        OutcomeClassifier.FromException(new TaskCanceledException(), timedOut: true).ErrorKind.Should().Be("timeout");
        OutcomeClassifier.FromException(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)), false)
            .ErrorKind.Should().Be("connection_refused");
        OutcomeClassifier.FromException(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)), false)
            .ErrorKind.Should().Be("dns");
        OutcomeClassifier.FromException(new HttpRequestException(HttpRequestError.ResponseEnded, "ended"), false)
            .ErrorKind.Should().Be("connection_reset");
        OutcomeClassifier.FromException(new HttpRequestException("x", new IOException("reset")), false)
            .ErrorKind.Should().Be("connection_reset");
        OutcomeClassifier.FromException(new HttpRequestException("x", EgressBlockedException.For(IPAddress.Loopback)), false)
            .ErrorKind.Should().Be("egress_blocked");
        OutcomeClassifier.FromException(new InvalidOperationException(), false).Kind.Should().Be(OutcomeKind.Retryable);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:0a00:0001::1")]
    public void Internal_addresses_are_not_public(string ip)
    {
        EgressPolicy.IsPublicUnicast(IPAddress.Parse(ip)).Should().BeFalse();
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2002:0808:0808::1")]
    public void Public_addresses_are_allowed(string ip)
    {
        EgressPolicy.IsPublicUnicast(IPAddress.Parse(ip)).Should().BeTrue();
    }

    [Fact]
    public void Message_ids_are_prefixed_unique_and_validated()
    {
        var a = MessageIds.New();
        var b = MessageIds.New();

        MessageIds.IsValid(a).Should().BeTrue();
        a.Should().NotBe(b);
        MessageIds.IsValid("msg_xyz").Should().BeFalse();
        MessageIds.IsValid("evt_" + a[4..]).Should().BeFalse();
        MessageIds.IsValid(null).Should().BeFalse();
    }
}
