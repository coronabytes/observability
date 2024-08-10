using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Core.Observability;

internal class ObservabilityMiddleware(RequestDelegate next, 
    IOptionsMonitor<ObservabilityOptions> optionsDelegate, 
    ILogger<ObservabilityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var options = optionsDelegate.CurrentValue;
        var tags = new Dictionary<string, object>();

        if (options.Augment != null)
        {
            await options.Augment(context, tags);

            foreach (var tag in tags)
                Activity.Current?.SetTag(tag.Key, tag.Value);
        }

        if (options.TraceIdHeader != null)
            context.Response.Headers.Append(options.TraceIdHeader, Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier);

        using (logger.BeginScope(tags))
        {
            if (options.ExceptionHandler != null)
            {
                try
                {
                    await next(context);
                }
                catch (Exception e)
                {
                    await options.ExceptionHandler(context, e, logger);
                }
            }
            else
                await next(context);
        }
    }
}