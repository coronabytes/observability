[![Nuget](https://img.shields.io/nuget/v/Core.Observability)](https://www.nuget.org/packages/Core.Observability)
[![Nuget](https://img.shields.io/nuget/dt/Core.Observability)](https://www.nuget.org/packages/Core.Observability)

```
dotnet add package Core.Observability
```
# 3-Line OpenTelemetry ASP.NET Core Integration
- trace id in response header
- sends logs, metrics and traces to opentelemetry collector
- enrichment/augmentation of logs and traces even when exceptions occur
- exceptions recorded on traces, client disconnects ignored
- inject trace id to http requests
- health checks (excluded from tracing)
- best practices from aspire
  - servicediscovery
  - http resilience handler

## Initialization in ASP.NET Core

```csharp
builder.AddObservability();

builder.Services.Configure<ObservabilityOptions>(options =>
{
    options.TraceIdHeader = "x-trace-id";
    options.Augment = (context, tags) =>
    {
        tags.Add("TenantId", "extract from http context");
        return ValueTask.CompletedTask;
    };
});
```

```csharp
// required - register early, so exceptions from later middleware are enriched and handled
app.UseObservability();

// optional - /health and /alive are anonymous, consider mapping them only in development or on an internal port
app.MapObservabilityHealthChecks();

app.UseAuthorization();

app.MapControllers();
app.Run();
```

## More Instrumentation

```csharp
builder.AddObservability(configureTracing: tracer =>
{
    tracer.AddEntityFrameworkCoreInstrumentation();
    tracer.AddRedisInstrumentation();
});
```

## Environment Variables

```
"OTEL_SERVICE_NAME": "my-project",
"OTEL_RESOURCE_ATTRIBUTES": "service.namespace=core,environment.name=dev",

"OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317",
or
"OTEL_EXPORTER_OTLP_ENDPOINT": "http://collector:4317",

"OTEL_EXPORTER_OTLP_PROTOCOL": "grpc"
```
