using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;
using WebhookRelay.Core.Breaker;
using WebhookRelay.Core.Retry;
using WebhookRelay.Server.Api;
using WebhookRelay.Server.Delivery;
using WebhookRelay.Server.Infrastructure;
using WebhookRelay.Server.Persistence;

namespace WebhookRelay.Server;

/// <summary>
/// Builds the relay host. Kept separate from Program so integration tests and the benchmark harness start the real
/// app on Kestrel, with configuration overrides, in-process.
/// </summary>
public static class RelayApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.AddObservability();
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1024 * 1024);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseLower)));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddPersistence(builder.Configuration);

        builder.Services.AddOptions<RelayOptions>().Bind(builder.Configuration.GetSection(RelayOptions.Section));
        builder.Services.AddOptions<DeliveryOptions>().Bind(builder.Configuration.GetSection(DeliveryOptions.Section));
        builder.Services.AddOptions<RetryOptions>().BindConfiguration(RetryOptions.Section);
        builder.Services.AddOptions<CircuitOptions>().BindConfiguration(CircuitOptions.Section);
        builder.Services.AddSingleton(sp => new RetryPolicy(sp.GetRequiredService<IOptions<RetryOptions>>().Value));
        builder.Services.AddSingleton(sp => RelayIdentity.Create(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value.InstanceId));
        builder.Services.AddSingleton<AdminKeyFilter>();

        builder.Services.AddSingleton<DeliverySignal>();
        builder.Services.AddHttpClient(WebhookSender.HttpClientName, c =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan; // the per-attempt timeout is enforced by WebhookSender
                c.DefaultRequestHeaders.UserAgent.ParseAdd("webhook-relay/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(sp => WebhookSender.CreateHandler(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan); // connection recycling is done by PooledConnectionLifetime
        builder.Services.AddSingleton(sp => new WebhookSender(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebhookSender.HttpClientName),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<DeliveryOptions>>()));

        if (builder.Configuration.GetValue(DeliveryOptions.Section + ":Enabled", true))
        {
            builder.Services.AddSingleton<DeliveryWorker>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DeliveryWorker>());
            builder.Services.AddHostedService<NotificationListener>();
        }

        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));

        builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "webhook-relay";
            document.Info.Version = "v1";
            document.Info.Description = "Webhook delivery with Standard Webhooks signatures, a PostgreSQL queue, backoff with jitter, a per-endpoint circuit breaker and a dead-letter queue with replay.";
            return Task.CompletedTask;
        }));

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSerilogRequestLogging(options =>
            options.GetLevel = (context, _, exception) =>
                exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
                    ? Serilog.Events.LogEventLevel.Verbose
                    : Serilog.Events.LogEventLevel.Information);

        // The replay panel is static HTML + fetch; it holds no secrets and calls the same admin-key-protected API.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
        {
            app.MapOpenApi();
            app.MapScalarApiReference(options => options.WithTitle("webhook-relay"));
        }

        app.MapHealthEndpoints();
        app.MapGroup("/api/v1").WithGroupName("v1").MapRelayApi();
        return app;
    }

    /// <summary>
    /// One image, several roles: <c>--migrate-only</c> applies migrations as the schema owner and exits;
    /// <c>--emit-vectors path</c> writes signature test vectors for the receiver samples and exits; otherwise serve.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var vectorsIndex = Array.IndexOf(args, "--emit-vectors");
        if (vectorsIndex >= 0)
        {
            var path = vectorsIndex + 1 < args.Length ? args[vectorsIndex + 1] : "signature-vectors.json";
            await File.WriteAllTextAsync(path, SignatureVectors.Generate());
            Console.WriteLine($"wrote {path}");
            return 0;
        }

        var app = Build(args);
        app.Services.GetRequiredService<IOptions<RelayOptions>>().Value.Validate();
        app.Services.GetRequiredService<IOptions<DeliveryOptions>>().Value.Validate();
        app.Services.GetRequiredService<IOptions<CircuitOptions>>().Value.Validate();

        if (args.Contains("--migrate-only"))
        {
            await app.Services.MigrateDatabaseAsync();
            return 0;
        }

        if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            await app.Services.MigrateDatabaseAsync();
        }

        await app.RunAsync();
        return 0;
    }
}
