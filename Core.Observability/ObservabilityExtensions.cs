using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Core.Observability;

public static class ObservabilityExtensions
{
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder, 
        Action<OpenTelemetryLoggerOptions>? configureLogs = null,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<ResourceBuilder>? configureResource = null)
    {
        builder.Services.AddServiceDiscovery();

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
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
                        .AddRuntimeInstrumentation();

                    configureMetrics?.Invoke(metrics);
                })
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation();

                    configureTracing?.Invoke(tracing);
                })
                .UseOtlpExporter();
        }

        return builder;
    }

    public static WebApplication UseObservability(this WebApplication app)
    {
        app.UseMiddleware<ObservabilityMiddleware>();

        return app;
    }
    public static WebApplication MapObservabilityHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        }).AllowAnonymous();

        return app;
    }
}