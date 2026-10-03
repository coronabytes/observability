using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Core.Observability.Tests;

public class ObservabilityMiddlewareTests : IDisposable
{
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };

    private readonly CapturingLoggerProvider _logs = new();

    // completes with the exception that escaped UseObservability, or null
    private readonly TaskCompletionSource<Exception?> _escaped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Activity? _requestActivity;

    public ObservabilityMiddlewareTests() => ActivitySource.AddActivityListener(_listener);

    public void Dispose() => _listener.Dispose();

    private async Task<WebApplication> StartAsync(RequestDelegate endpoint, Action<ObservabilityOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(_logs);
        builder.Services.Configure<ObservabilityOptions>(options => configure?.Invoke(options));

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
                _escaped.TrySetResult(null);
            }
            catch (Exception e)
            {
                _escaped.TrySetResult(e);
                throw;
            }
        });
        app.UseObservability();
        app.Run(context =>
        {
            _requestActivity = Activity.Current;
            return endpoint(context);
        });

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task Adds_trace_id_header()
    {
        await using var app = await StartAsync(_ => Task.CompletedTask);

        var response = await app.GetTestClient().GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(_requestActivity!.TraceId.ToHexString(), Assert.Single(response.Headers.GetValues("x-trace-id")));
    }

    [Fact]
    public async Task Omits_trace_id_header_when_disabled()
    {
        await using var app = await StartAsync(_ => Task.CompletedTask, o => o.TraceIdHeader = null);

        var response = await app.GetTestClient().GetAsync("/", TestContext.Current.CancellationToken);

        Assert.False(response.Headers.Contains("x-trace-id"));
    }

    [Fact]
    public async Task Augment_tags_are_added_to_trace_and_log_scope()
    {
        await using var app = await StartAsync(context =>
        {
            context.RequestServices.GetRequiredService<ILogger<ObservabilityMiddlewareTests>>().LogInformation("inside");
            return Task.CompletedTask;
        }, o => o.Augment = (_, tags) =>
        {
            tags.Add("TenantId", "tenant-1");
            return ValueTask.CompletedTask;
        });

        await app.GetTestClient().GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal("tenant-1", _requestActivity!.GetTagItem("TenantId"));
        Assert.Equal("tenant-1", _logs.Single("inside").Scope["TenantId"]);
    }

    [Fact]
    public async Task Exception_is_handled_logged_and_recorded_on_trace()
    {
        await using var app = await StartAsync(_ => throw new InvalidOperationException("boom {0}"), o => o.Augment = (_, tags) =>
        {
            tags.Add("TenantId", "tenant-1");
            return ValueTask.CompletedTask;
        });

        var response = await app.GetTestClient().PostAsync("/", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"exception":"InvalidOperationException"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(await _escaped.Task);

        var log = _logs.Single("Unhandled exception");
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.IsType<InvalidOperationException>(log.Exception);
        Assert.Equal("tenant-1", log.Scope["TenantId"]);

        var exceptionEvent = Assert.Single(_requestActivity!.Events, e => e.Name == "exception");
        Assert.Contains(exceptionEvent.Tags, t => t is { Key: "exception.type", Value: "System.InvalidOperationException" });
    }

    [Fact]
    public async Task Exception_after_response_started_is_not_handled()
    {
        var handled = false;
        await using var app = await StartAsync(async context =>
        {
            await context.Response.WriteAsync("partial");
            await context.Response.Body.FlushAsync();
            throw new InvalidOperationException("boom");
        }, o => o.ExceptionHandler = (_, _, _) =>
        {
            handled = true;
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var response = await app.GetTestClient().GetAsync("/", TestContext.Current.CancellationToken);
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        });

        Assert.IsType<InvalidOperationException>(await _escaped.Task);
        Assert.False(handled);
    }

    [Fact]
    public async Task Client_abort_is_not_handled()
    {
        var handled = false;
        var requestStarted = new TaskCompletionSource();
        await using var app = await StartAsync(async context =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }, o => o.ExceptionHandler = (_, _, _) =>
        {
            handled = true;
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource();
        var request = app.GetTestClient().GetAsync("/", cts.Token);
        await requestStarted.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Null(await _escaped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(handled);
        Assert.Empty(_logs.Entries);
    }

    [Fact]
    public async Task Exception_propagates_without_handler()
    {
        await using var app = await StartAsync(_ => throw new InvalidOperationException("boom"), o => o.ExceptionHandler = null);

        await Assert.ThrowsAnyAsync<Exception>(() => app.GetTestClient().GetAsync("/", TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(await _escaped.Task);
    }
}

internal record LogEntry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Scope);

internal class CapturingLoggerProvider : ILoggerProvider, ILogger
{
    private readonly AsyncLocal<IReadOnlyDictionary<string, object?>> _scope = new();

    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public LogEntry Single(string message) => Assert.Single(Entries, e => e.Message == message);

    public ILogger CreateLogger(string categoryName) => categoryName.StartsWith("Core.Observability") ? this : NullLogger.Instance;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var previous = _scope.Value;
        var scope = new Dictionary<string, object?>(previous ?? new Dictionary<string, object?>());

        if (state is IEnumerable<KeyValuePair<string, object>> values)
            foreach (var (key, value) in values)
                scope[key] = value;

        _scope.Value = scope;
        return new Restore(() => _scope.Value = previous!);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception, _scope.Value ?? new Dictionary<string, object?>()));

    public void Dispose()
    {
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
