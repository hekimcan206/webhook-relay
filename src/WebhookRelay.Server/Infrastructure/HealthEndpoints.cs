using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WebhookRelay.Server.Infrastructure;

internal static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// <c>/health/live</c> runs no checks: it answers "can the process serve HTTP?", so a database outage
    /// never makes the orchestrator restart otherwise healthy instances. <c>/health/ready</c> runs dependency
    /// checks and takes the instance out of load balancing while they fail.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteJsonAsync,
        }).ExcludeFromDescription();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteJsonAsync,
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static Task WriteJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new HealthResponse(
            report.Status.ToString(),
            Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            report.Entries.ToDictionary(
                e => e.Key,
                // Exception details are deliberately not echoed: health endpoints are usually unauthenticated.
                e => new HealthCheckResponse(e.Value.Status.ToString(), Math.Round(e.Value.Duration.TotalMilliseconds, 1))));
        return JsonSerializer.SerializeAsync(context.Response.Body, body, JsonSerializerOptions.Web, context.RequestAborted);
    }

    internal sealed record HealthResponse(string Status, double TotalDurationMs, IReadOnlyDictionary<string, HealthCheckResponse> Checks);

    internal sealed record HealthCheckResponse(string Status, double DurationMs);
}
