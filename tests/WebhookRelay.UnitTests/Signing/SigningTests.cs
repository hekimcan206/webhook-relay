using System.Text;
using WebhookRelay.Signing;

namespace WebhookRelay.UnitTests.Signing;

public sealed class SigningTests
{
    // The test vector used across the Standard Webhooks reference libraries (secret, id, timestamp, body, signature).
    private const string VectorSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string VectorId = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const long VectorTimestamp = 1614265330;
    private const string VectorBody = """{"test": 2432232314}""";
    private const string VectorSignature = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";

    private static readonly WebhookSecret Secret = WebhookSecret.Parse(VectorSecret);

    [Fact]
    public void Sign_reproduces_the_reference_test_vector()
    {
        WebhookSigner.Sign(Secret, VectorId, VectorTimestamp, Encoding.UTF8.GetBytes(VectorBody))
            .Should().Be(VectorSignature);
    }

    [Fact]
    public void Verify_accepts_the_reference_test_vector_inside_the_tolerance()
    {
        var verifier = new WebhookVerifier(Secret, timeProvider: FixedClock.AtUnix(VectorTimestamp + 299));

        verifier.Verify(VectorId, "1614265330", VectorSignature, VectorBody).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(-301, VerificationFailure.TimestampTooNew)]
    [InlineData(301, VerificationFailure.TimestampTooOld)]
    [InlineData(300, VerificationFailure.None)]
    [InlineData(-300, VerificationFailure.None)]
    public void Verify_enforces_the_timestamp_tolerance_at_its_edges(long clockOffset, VerificationFailure expected)
    {
        var verifier = new WebhookVerifier(Secret, timeProvider: FixedClock.AtUnix(VectorTimestamp + clockOffset));

        verifier.Verify(VectorId, "1614265330", VectorSignature, VectorBody).Failure.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+1614265330")]
    [InlineData(" 1614265330")]
    [InlineData("01614265330")]
    [InlineData("1614265330.0")]
    [InlineData("1.6e9")]
    [InlineData("99999999999999999999")]
    public void Verify_rejects_timestamps_that_are_not_canonical_digits(string timestamp)
    {
        var verifier = new WebhookVerifier(Secret, timeProvider: FixedClock.AtUnix(VectorTimestamp));

        verifier.Verify(VectorId, timestamp, VectorSignature, VectorBody).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "1614265330", VectorSignature)]
    [InlineData(VectorId, null, VectorSignature)]
    [InlineData(VectorId, "1614265330", null)]
    public void Verify_reports_missing_headers(string? id, string? timestamp, string? signature)
    {
        var verifier = new WebhookVerifier(Secret, timeProvider: FixedClock.AtUnix(VectorTimestamp));

        verifier.Verify(id, timestamp, signature, VectorBody).Failure.Should().Be(VerificationFailure.MissingHeaders);
    }

    [Theory]
    [InlineData("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE")] // truncated base64
    [InlineData("v2,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=")] // unknown version with the right bytes
    [InlineData("g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=")] // no version
    [InlineData("v1,not base64!")]
    [InlineData("v1,")]
    [InlineData(",g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=")]
    public void Verify_rejects_malformed_signature_entries(string header)
    {
        var verifier = new WebhookVerifier(Secret, timeProvider: FixedClock.AtUnix(VectorTimestamp));

        verifier.Verify(VectorId, "1614265330", header, VectorBody).Failure.Should().Be(VerificationFailure.NoValidSignature);
    }

    [Fact]
    public void Verify_skips_unknown_versions_and_accepts_a_later_v1_entry()
    {
        var verifier = new WebhookVerifier(Secret, timeProvider: FixedClock.AtUnix(VectorTimestamp));
        var header = "v1a,hnO3f9T8Ytu9HwrXslvumlUpqtNVqkhqw/enGzPCXe5BdqzCInXqYXFymVJaA7AZdpXwVLPo3mNl8EM+m7TBAg== v1,AAAA " + VectorSignature;

        verifier.Verify(VectorId, "1614265330", header, VectorBody).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Verify_stops_looking_after_sixteen_entries()
    {
        var verifier = new WebhookVerifier(Secret, timeProvider: FixedClock.AtUnix(VectorTimestamp));
        var padding = string.Join(' ', Enumerable.Repeat("v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", 16));

        verifier.Verify(VectorId, "1614265330", padding + " " + VectorSignature, VectorBody).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rotation_header_carries_one_signature_per_secret_and_each_secret_verifies_alone()
    {
        var oldSecret = WebhookSecret.Generate();
        var newSecret = WebhookSecret.Generate();
        var header = WebhookSigner.SignatureHeader([oldSecret, newSecret], "msg_1", VectorTimestamp, VectorBody);
        var clock = FixedClock.AtUnix(VectorTimestamp);

        header.Split(' ').Should().HaveCount(2).And.AllSatisfy(e => e.Should().StartWith("v1,"));
        new WebhookVerifier(oldSecret, timeProvider: clock).Verify("msg_1", "1614265330", header, VectorBody).IsValid.Should().BeTrue();
        new WebhookVerifier(newSecret, timeProvider: clock).Verify("msg_1", "1614265330", header, VectorBody).IsValid.Should().BeTrue();
        new WebhookVerifier(WebhookSecret.Generate(), timeProvider: clock)
            .Verify("msg_1", "1614265330", header, VectorBody).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_receiver_holding_both_secrets_accepts_a_sender_that_has_only_the_new_one()
    {
        var oldSecret = WebhookSecret.Generate();
        var newSecret = WebhookSecret.Generate();
        var header = WebhookSigner.SignatureHeader([newSecret], "msg_2", VectorTimestamp, VectorBody);

        new WebhookVerifier([oldSecret, newSecret], timeProvider: FixedClock.AtUnix(VectorTimestamp))
            .Verify("msg_2", "1614265330", header, VectorBody).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")] // missing prefix
    [InlineData("whsec_")]
    [InlineData("whsec_c2hvcnQ=")] // 5 bytes
    [InlineData("whsec_!!!!")]
    [InlineData("WHSEC_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")]
    public void Secret_parsing_rejects_invalid_values(string value)
    {
        WebhookSecret.TryParse(value, out _).Should().BeFalse();
        FluentActions.Invoking(() => WebhookSecret.Parse(value)).Should().Throw<FormatException>();
    }

    [Fact]
    public void Secret_round_trips_and_never_prints_key_material()
    {
        var secret = WebhookSecret.Generate(48);

        WebhookSecret.Parse(secret.Encoded).Should().Be(secret);
        secret.Key.Length.Should().Be(48);
        secret.ToString().Should().Be("whsec_*** (48 bytes)");
        FluentActions.Invoking(() => WebhookSecret.Generate(16)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => WebhookSecret.FromBytes(new byte[65])).Should().Throw<ArgumentException>();
    }
}
