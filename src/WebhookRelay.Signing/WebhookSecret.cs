using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace WebhookRelay.Signing;

/// <summary>
/// A symmetric Standard Webhooks signing secret: 24 to 64 random bytes, written as <c>whsec_</c> + base64.
/// The HMAC key is the decoded bytes, not the text, which is the most common interoperability mistake.
/// </summary>
public sealed class WebhookSecret : IEquatable<WebhookSecret>
{
    public const string Prefix = "whsec_";
    public const int MinBytes = 24;
    public const int MaxBytes = 64;

    private readonly byte[] _key;

    private WebhookSecret(byte[] key) => _key = key;

    /// <summary>The raw key bytes used as the HMAC-SHA256 key.</summary>
    public ReadOnlySpan<byte> Key => _key;

    /// <summary>The secret in its shareable <c>whsec_</c> form.</summary>
    public string Encoded => Prefix + Convert.ToBase64String(_key);

    /// <summary>Creates a new secret from a cryptographically secure random source.</summary>
    public static WebhookSecret Generate(int sizeInBytes = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sizeInBytes, MinBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeInBytes, MaxBytes);
        return new WebhookSecret(RandomNumberGenerator.GetBytes(sizeInBytes));
    }

    /// <summary>Wraps existing key bytes (for example a key loaded from a secret store).</summary>
    public static WebhookSecret FromBytes(ReadOnlySpan<byte> key)
    {
        if (key.Length is < MinBytes or > MaxBytes)
        {
            throw new ArgumentException($"A webhook secret must be between {MinBytes} and {MaxBytes} bytes.", nameof(key));
        }

        return new WebhookSecret(key.ToArray());
    }

    /// <summary>Parses <c>whsec_&lt;base64&gt;</c>. The prefix is required so a secret is never confused with another credential.</summary>
    public static WebhookSecret Parse(string value) =>
        TryParse(value, out var secret) ? secret : throw new FormatException("Expected 'whsec_' followed by 24 to 64 base64-encoded bytes.");

    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out WebhookSecret? secret)
    {
        secret = null;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = value.AsSpan(Prefix.Length);
        Span<byte> buffer = stackalloc byte[MaxBytes + 3];
        if (!Convert.TryFromBase64Chars(encoded, buffer, out var written) || written is < MinBytes or > MaxBytes)
        {
            return false;
        }

        secret = new WebhookSecret(buffer[..written].ToArray());
        return true;
    }

    public bool Equals(WebhookSecret? other) =>
        other is not null && CryptographicOperations.FixedTimeEquals(_key, other._key);

    public override bool Equals(object? obj) => Equals(obj as WebhookSecret);

    public override int GetHashCode() => _key.Length;

    /// <summary>Never prints key material, so logs and exception messages stay safe.</summary>
    public override string ToString() => $"{Prefix}*** ({_key.Length} bytes)";
}
