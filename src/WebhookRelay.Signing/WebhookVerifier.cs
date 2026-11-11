using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebhookRelay.Signing;

/// <summary>Why a delivery was rejected. Receivers should answer any failure with 4xx and log the reason, not echo it.</summary>
public enum VerificationFailure
{
    None = 0,
    MissingHeaders,
    InvalidTimestamp,
    TimestampTooOld,
    TimestampTooNew,
    NoValidSignature,
}

public readonly record struct VerificationResult(VerificationFailure Failure)
{
    public bool IsValid => Failure == VerificationFailure.None;

    public static VerificationResult Valid => new(VerificationFailure.None);
}

/// <summary>
/// Verifies Standard Webhooks <c>v1</c> signatures. Accepts the message if any <c>v1</c> entry matches any configured
/// secret (rotation on either side), compares in constant time, and rejects timestamps outside the tolerance window to
/// limit replay of captured requests. Unknown versions (for example <c>v1a</c>, Ed25519) are skipped, not failed.
/// </summary>
public sealed class WebhookVerifier
{
    /// <summary>Five minutes, the window used by the reference Standard Webhooks libraries.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    // A bound on work per request: an attacker cannot make the verifier run thousands of HMAC comparisons.
    private const int MaxSignatureEntries = 16;

    private readonly WebhookSecret[] _secrets;
    private readonly long _toleranceSeconds;
    private readonly TimeProvider _time;

    public WebhookVerifier(IEnumerable<WebhookSecret> secrets, TimeSpan? tolerance = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = [.. secrets];
        if (_secrets.Length == 0)
        {
            throw new ArgumentException("At least one secret is required.", nameof(secrets));
        }

        _toleranceSeconds = (long)(tolerance ?? DefaultTolerance).TotalSeconds;
        _time = timeProvider ?? TimeProvider.System;
    }

    public WebhookVerifier(WebhookSecret secret, TimeSpan? tolerance = null, TimeProvider? timeProvider = null)
        : this([secret], tolerance, timeProvider)
    {
    }

    public VerificationResult Verify(string? messageId, string? timestamp, string? signatureHeader, ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signatureHeader))
        {
            return new(VerificationFailure.MissingHeaders);
        }

        // Digits only: "+123", " 123" or "1e9" are rejected rather than normalised, because the signed string uses the
        // header text verbatim and a lenient parse would accept a timestamp different from the one that was signed.
        if (timestamp.Length > 15 || !timestamp.All(char.IsAsciiDigit) ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            timestamp.Length != seconds.ToString(CultureInfo.InvariantCulture).Length)
        {
            return new(VerificationFailure.InvalidTimestamp);
        }

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (seconds < now - _toleranceSeconds)
        {
            return new(VerificationFailure.TimestampTooOld);
        }

        if (seconds > now + _toleranceSeconds)
        {
            return new(VerificationFailure.TimestampTooNew);
        }

        Span<byte> expected = stackalloc byte[WebhookSigner.HashSize];
        Span<byte> candidate = stackalloc byte[WebhookSigner.HashSize + 3];
        var entries = signatureHeader.Split(' ', MaxSignatureEntries + 1, StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Min(entries.Length, MaxSignatureEntries);

        foreach (var secret in _secrets)
        {
            WebhookSigner.ComputeHash(secret, messageId, seconds, body, expected);
            for (var i = 0; i < count; i++)
            {
                var entry = entries[i].AsSpan();
                var comma = entry.IndexOf(',');
                if (comma <= 0 || !entry[..comma].SequenceEqual(WebhookSigner.Version))
                {
                    continue;
                }

                if (Convert.TryFromBase64Chars(entry[(comma + 1)..], candidate, out var written) &&
                    written == WebhookSigner.HashSize &&
                    CryptographicOperations.FixedTimeEquals(candidate[..written], expected))
                {
                    return VerificationResult.Valid;
                }
            }
        }

        return new(VerificationFailure.NoValidSignature);
    }

    public VerificationResult Verify(string? messageId, string? timestamp, string? signatureHeader, string body) =>
        Verify(messageId, timestamp, signatureHeader, Encoding.UTF8.GetBytes(body));
}
