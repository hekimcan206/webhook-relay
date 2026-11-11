namespace WebhookRelay.Signing;

/// <summary>Header names defined by the Standard Webhooks specification.</summary>
public static class WebhookHeaders
{
    /// <summary>Unique message identifier. It stays the same across retries, so receivers use it as an idempotency key.</summary>
    public const string Id = "webhook-id";

    /// <summary>Unix time in whole seconds at which the attempt was signed.</summary>
    public const string Timestamp = "webhook-timestamp";

    /// <summary>Space-delimited list of <c>version,base64</c> signatures (more than one while a secret rotates).</summary>
    public const string Signature = "webhook-signature";
}
