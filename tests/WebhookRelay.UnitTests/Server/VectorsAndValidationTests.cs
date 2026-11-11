using System.Text.Json;
using WebhookRelay.Server;
using WebhookRelay.Server.Api;
using WebhookRelay.Signing;

namespace WebhookRelay.UnitTests.Server;

public sealed class VectorsAndValidationTests
{
    private static readonly string CommittedVectors = Path.Combine(AppContext.BaseDirectory, "vectors", "signature-vectors.json");

    [Fact]
    public void Committed_vectors_are_exactly_what_the_relay_generates()
    {
        // Golden file: the samples are tested against this copy locally and against a fresh one in CI.
        // If signing changes, this fails until the vectors are regenerated deliberately (RelayApp --emit-vectors).
        File.ReadAllText(CommittedVectors).ReplaceLineEndings("\n").Should().Be(SignatureVectors.Generate());
    }

    [Fact]
    public void The_relay_verifier_agrees_with_every_vector()
    {
        using var document = JsonDocument.Parse(SignatureVectors.Generate());
        foreach (var v in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var verifier = new WebhookVerifier(
                v.GetProperty("receiverSecrets").EnumerateArray().Select(s => WebhookSecret.Parse(s.GetString()!)),
                timeProvider: FixedClock.AtUnix(v.GetProperty("now").GetInt64()));
            var result = verifier.Verify(
                v.GetProperty("webhookId").GetString(),
                v.GetProperty("webhookTimestamp").GetString(),
                v.GetProperty("webhookSignature").GetString(),
                Convert.FromBase64String(v.GetProperty("bodyBase64").GetString()!));

            result.IsValid.Should().Be(v.GetProperty("expectValid").GetBoolean(), v.GetProperty("name").GetString());
        }
    }

    [Theory]
    [InlineData("https://hooks.example.com/in", false, false, null)]
    [InlineData("http://hooks.example.com/in", false, false, null)]
    [InlineData("http://hooks.example.com/in", true, false, "Only https URLs are accepted.")]
    [InlineData("ftp://hooks.example.com/in", false, false, "Only http and https URLs are accepted.")]
    [InlineData("/relative", false, false, "An absolute URL of at most 2048 characters is required.")]
    [InlineData("https://u:p@hooks.example.com/in", false, false, "Credentials in the URL are not accepted; they would be stored and logged in clear text.")]
    [InlineData("http://10.0.0.5/in", false, false, "The URL points at a private, loopback or link-local address.")]
    [InlineData("http://localhost:9000/in", false, false, "The URL points at a private, loopback or link-local address.")]
    [InlineData("http://10.0.0.5/in", false, true, null)]
    [InlineData("http://receiver-node:8080/webhook", false, true, null)]
    public void Endpoint_urls_are_validated(string url, bool requireHttps, bool allowPrivate, string? expected)
    {
        RelayApi.ValidateUrl(url, requireHttps, allowPrivate).Should().Be(expected);
    }

    [Fact]
    public void Overlong_urls_are_rejected()
    {
        RelayApi.ValidateUrl("https://example.com/" + new string('a', 2048), false, false).Should().NotBeNull();
    }

    [Fact]
    public void Delivery_options_refuse_a_lease_shorter_than_two_timeouts()
    {
        var options = new DeliveryOptions { RequestTimeout = TimeSpan.FromSeconds(30), LeaseDuration = TimeSpan.FromSeconds(45) };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>().WithMessage("*LeaseDuration*");
        FluentActions.Invoking(new RelayOptions { AdminKey = "short" }.Validate).Should().Throw<InvalidOperationException>();
    }
}
