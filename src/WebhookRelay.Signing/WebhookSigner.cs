using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebhookRelay.Signing;

/// <summary>
/// Produces Standard Webhooks <c>v1</c> signatures: base64(HMAC-SHA256(key, "{id}.{timestamp}.{body}")).
/// </summary>
public static class WebhookSigner
{
    public const string Version = "v1";
    internal const int HashSize = 32;

    /// <summary>Signs one message with one secret and returns a single <c>v1,&lt;base64&gt;</c> entry.</summary>
    public static string Sign(WebhookSecret secret, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> hash = stackalloc byte[HashSize];
        ComputeHash(secret, messageId, timestamp, body, hash);
        return Version + "," + Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Builds the full <c>webhook-signature</c> header value, one entry per active secret. During a rotation the relay
    /// signs with both the old and the new secret, so a receiver configured with either one keeps verifying.
    /// </summary>
    public static string SignatureHeader(IEnumerable<WebhookSecret> secrets, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var builder = new StringBuilder();
        foreach (var secret in secrets)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Sign(secret, messageId, timestamp, body));
        }

        if (builder.Length == 0)
        {
            throw new ArgumentException("At least one secret is required.", nameof(secrets));
        }

        return builder.ToString();
    }

    /// <summary>Signs a UTF-8 string body. The receiver must verify the raw bytes it received, never re-serialised JSON.</summary>
    public static string SignatureHeader(IEnumerable<WebhookSecret> secrets, string messageId, long timestamp, string body) =>
        SignatureHeader(secrets, messageId, timestamp, Encoding.UTF8.GetBytes(body));

    internal static void ComputeHash(WebhookSecret secret, string messageId, long timestamp, ReadOnlySpan<byte> body, Span<byte> destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentOutOfRangeException.ThrowIfNegative(timestamp);

        // Incremental HMAC: the body is hashed in place instead of being copied into one "{id}.{ts}.{body}" buffer.
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret.Key);
        hmac.AppendData(Encoding.UTF8.GetBytes(messageId));
        Span<byte> separatorAndTimestamp = stackalloc byte[24];
        separatorAndTimestamp[0] = (byte)'.';
        timestamp.TryFormat(separatorAndTimestamp[1..], out var digits, provider: CultureInfo.InvariantCulture);
        separatorAndTimestamp[digits + 1] = (byte)'.';
        hmac.AppendData(separatorAndTimestamp[..(digits + 2)]);
        hmac.AppendData(body);
        hmac.GetHashAndReset(destination);
    }
}
