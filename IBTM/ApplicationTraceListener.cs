using System.Diagnostics;
using System.Globalization;
using IBTM.Core;

namespace IBTM;

internal sealed class ApplicationTraceListener(ApplicationLog log) : TraceListener
{
    public override void Write(string? message)
    {
        if (!string.IsNullOrEmpty(message)) log.Write(message);
    }

    public override void WriteLine(string? message) => Write(message);
    public override void Fail(string? message, string? detailMessage) =>
        log.Error($"{message}\n{detailMessage}");

    public override void TraceEvent(TraceEventCache? eventCache, string source,
        TraceEventType eventType, int id, string? message)
    {
        if (eventType is TraceEventType.Error or TraceEventType.Critical)
            log.Error(message ?? "");
        else Write(message);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source,
        TraceEventType eventType, int id, string? format, params object?[]? args) =>
        TraceEvent(eventCache, source, eventType, id, args is null ? format
            : string.Format(CultureInfo.InvariantCulture, format ?? "", args));
}
