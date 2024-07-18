using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Core.Observability;

public class ObservabilityOptions
{
    public string? TraceIdHeader { get; set; } = "x-trace-id";
    public Func<HttpContext, Exception, ILogger, ValueTask>? ExceptionHandler { get; set; } =
        async (context, exception, logger) =>
        {
            logger.LogError(exception, exception.Message);

            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new
            {
                exception = exception.GetType().Name
            });
        };
    public Func<HttpContext, IDictionary<string, object>, ValueTask>? Augment { get; set; }
}