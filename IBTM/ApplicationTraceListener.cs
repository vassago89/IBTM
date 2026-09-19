using System.Diagnostics;
using System.Globalization;
using IBTM.Core;
using Microsoft.Extensions.Logging;

namespace IBTM;

internal sealed class ApplicationTraceListener : TraceListener
{
    private readonly ILogger<ApplicationTraceListener> _log;

    public ApplicationTraceListener(ILogger<ApplicationTraceListener> log)
    {
        _log = log;
    }

    public override void Write(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            _log.LogInformation("{Message}", message);
    }

    public override void WriteLine(string? message)
    {
        Write(message);
    }

    public override void Fail(string? message, string? detailMessage)
    {
        _log.LogError("{Message}", $"{message}\n{detailMessage}");
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message)
    {
        var level = eventType switch
        {
            TraceEventType.Critical => LogLevel.Critical,
            TraceEventType.Error => LogLevel.Error,
            TraceEventType.Warning => LogLevel.Warning,
            TraceEventType.Verbose => LogLevel.Trace,
            _ => LogLevel.Information,
        };
        _log.Log(level, new EventId(id, source), "{Message}", message ?? "");
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? format,
        params object?[]? args)
    {
        TraceEvent(
            eventCache,
            source,
            eventType,
            id,
            args is null ? format : string.Format(CultureInfo.InvariantCulture, format ?? "", args));
    }
}
