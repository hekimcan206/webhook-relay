using System.Buffers;

namespace WebhookRelay.Core.Delivery;

public static class MessageIds
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>
    /// <c>msg_</c> + a UUIDv7 in hex. Time-ordered, so the primary-key index stays append-mostly under insert load,
    /// and unguessable enough (74 random bits) that ids do not enumerate other tenants' messages.
    /// </summary>
    public static string New() => "msg_" + Guid.CreateVersion7().ToString("N");

    public static bool IsValid(string? id) =>
        id is { Length: 36 } && id.StartsWith("msg_", StringComparison.Ordinal) && !id.AsSpan(4).ContainsAnyExcept(LowerHex);
}
