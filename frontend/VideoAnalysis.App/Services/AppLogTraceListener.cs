using System.Diagnostics;
using System.Text;

namespace VideoAnalysis.App.Services;

public sealed class AppLogTraceListener : TraceListener
{
    private readonly StringBuilder _pendingLine = new();

    public override void Write(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            _pendingLine.Append(message);
        }
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            _pendingLine.Append(message);
        }

        FlushPendingLine();
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message)
    {
        WriteTraceEvent(source, eventType, message);
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? format,
        params object?[]? args)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return;
        }

        WriteTraceEvent(source, eventType, args is null ? format : string.Format(format, args));
    }

    public override void Flush()
    {
        FlushPendingLine();
    }

    private void WriteTraceEvent(string source, TraceEventType eventType, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var context = string.IsNullOrWhiteSpace(source)
            ? $"Trace:{eventType}"
            : $"Trace:{source}:{eventType}";

        switch (eventType)
        {
            case TraceEventType.Critical:
            case TraceEventType.Error:
                AppLogService.Error(message, context);
                break;
            case TraceEventType.Warning:
                AppLogService.Warning(message, context);
                break;
            default:
                AppLogService.Trace(message, context);
                break;
        }
    }

    private void FlushPendingLine()
    {
        if (_pendingLine.Length == 0)
        {
            return;
        }

        AppLogService.Trace(_pendingLine.ToString(), "Trace");
        _pendingLine.Clear();
    }
}
