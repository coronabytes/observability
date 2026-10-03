using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Core.Observability;

public static class ObservabilityExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    private static readonly string[] OtlpEndpointKeys =
    [
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
        "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT",
        "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"
    ];

    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder, 
        Action<OpenTelemetryLoggerOptions>? configureLogs = null,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<ResourceBuilder>? configureResource = null,
        Action<HttpStandardResilienceOptions>? configureHttpResilience = null, 
        bool enableHttpResilience = true)
    {
        builder.Services.AddServiceDiscovery();

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            if (enableHttpResilience)
                http.AddStandardResilienceHandler(configureHttpResilience ?? (_ => { }));

            http.AddServiceDiscovery();
        });

        if (OtlpEndpointKeys.Any(key => !string.IsNullOrWhiteSpace(builder.Configuration[key])))
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;

                configureLogs?.Invoke(logging);
            });

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(res =>
                {
                    res.AddEnvironmentVariableDetector();

                    configureResource?.Invoke(res);
                })
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddMeter("System.Runtime");

                    configureMetrics?.Invoke(metrics);
                })
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation(aspnet =>
                        {
                            // exclude health check probes from tracing
                            aspnet.Filter = context =>
                                !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                                && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath);
                        })
                        .AddHttpClientInstrumentation();

                    configureTracing?.Invoke(tracing);
                })
                .UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Register as early as possible in the pipeline, so exceptions from later middleware are enriched and handled.
    /// </summary>
    public static T UseObservability<T>(this T app) where T : IApplicationBuilder
    {
        app.UseMiddleware<ObservabilityMiddleware>();

        return app;
    }

    /// <summary>
    /// Maps /health (all checks) and /alive (checks tagged "live") without authentication.
    /// Health check results can disclose details about the service, consider mapping them only in development
    /// or on an internal port in production.
    /// </summary>
    public static T MapObservabilityHealthChecks<T>(this T app) where T : IEndpointRouteBuilder
    {
        app.MapHealthChecks(HealthEndpointPath).AllowAnonymous();
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        }).AllowAnonymous();

        return app;
    }
}