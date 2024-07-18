using Core.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthorization();

app.MapObservabilityHealthChecks();
app.UseObservability();

app.MapControllers();

app.Run();

