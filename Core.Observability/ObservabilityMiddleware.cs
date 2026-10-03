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
        Dictionary<string, object>? tags = null;

        if (options.Augment != null)
        {
            tags = new Dictionary<string, object>();
            await options.Augment(context, tags);

            foreach (var tag in tags)
                Activity.Current?.SetTag(tag.Key, tag.Value);
        }

        if (options.TraceIdHeader != null)
            context.Response.Headers.Append(options.TraceIdHeader, Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier);

        using (tags is { Count: > 0 } ? logger.BeginScope(tags) : null)
        {
            if (options.ExceptionHandler == null)
            {
                await next(context);
                return;
            }

            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // client disconnected - nothing to respond to
            }
            catch (Exception e) when (!context.Response.HasStarted)
            {
                // response not started yet: record on trace and let the handler write the error response
                // otherwise the exception propagates to the server, which aborts the connection
                Activity.Current?.AddException(e);
                await options.ExceptionHandler(context, e, logger);
            }
        }
    }
}
