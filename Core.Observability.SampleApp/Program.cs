using Core.Observability;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

builder.AddObservability();
builder.Services.Configure<ObservabilityOptions>(options =>
{
    options.TraceIdHeader = "x-trace-id";
    options.Augment = (context, tags) =>
    {
        tags.Add("TenantId", "xxx");
        return ValueTask.CompletedTask;
    };
});

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.UseAuthorization();

app.MapObservabilityHealthChecks();
app.UseObservability();

app.MapControllers();

app.Run();

