using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReceiverSample;

/// <summary>
/// A from-scratch Standard Webhooks v1 verifier: HMAC-SHA256 over "{webhook-id}.{webhook-timestamp}.{raw body}", keyed
/// with the base64-decoded part of the whsec_ secret, compared in constant time against every v1 entry.
/// </summary>
public static class StandardWebhooks
{
    public static bool Verify(
        IReadOnlyList<string> secrets, string? id, string? timestamp, string? signatureHeader, ReadOnlySpan<byte> rawBody,
        long nowUnixSeconds, long toleranceSeconds = 300)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        // Canonical digits only; the signed string uses the header text as sent.
        if (timestamp.Length > 15 || !timestamp.All(char.IsAsciiDigit) || (timestamp.Length > 1 && timestamp[0] == '0') ||
            Math.Abs(nowUnixSeconds - long.Parse(timestamp, CultureInfo.InvariantCulture)) > toleranceSeconds)
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        var signed = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(signed, 0);
        rawBody.CopyTo(signed.AsSpan(prefix.Length));

        foreach (var secret in secrets)
        {
            var key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret);
            var expected = Encoding.ASCII.GetBytes(Convert.ToBase64String(HMACSHA256.HashData(key, signed)));
            foreach (var entry in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(',', 2);
                if (parts is ["v1", var signature] &&
                    CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), expected))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
