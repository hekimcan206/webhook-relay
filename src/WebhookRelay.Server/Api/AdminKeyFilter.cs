using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace WebhookRelay.Server.Api;

/// <summary>
/// Shared-key authentication for the management API. Compared in constant time on SHA-256 digests, so neither the
/// key's content nor its length leaks through response timing.
/// </summary>
internal sealed class AdminKeyFilter(IOptions<RelayOptions> options) : IEndpointFilter
{
    public const string Header = "X-Admin-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var supplied = context.HttpContext.Request.Headers[Header].ToString();
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.AdminKey));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        if (supplied.Length == 0 || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Missing or invalid admin key.");
        }

        return await next(context);
    }
}
