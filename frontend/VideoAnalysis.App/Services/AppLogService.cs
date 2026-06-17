using System.Text;
using Avalonia.Threading;

namespace VideoAnalysis.App.Services;

public static class AppLogService
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;
    private static bool _globalHandlersInstalled;
    private static bool _uiHandlerInstalled;

    public static string LogsDirectory { get; private set; } = BuildDefaultLogsDirectory();

    public static string CurrentLogPath
    {
        get
        {
            InitializeDefault();
            return Path.Combine(LogsDirectory, $"videoanalytics-{DateTimeOffset.Now:yyyyMMdd}.log");
        }
    }

    public static void InitializeDefault()
    {
        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            LogsDirectory = BuildDefaultLogsDirectory();
            Directory.CreateDirectory(LogsDirectory);
            _initialized = true;
        }

        Info("Application log initialized.");
        Info($"Logs directory: {LogsDirectory}");
        Info($".NET: {Environment.Version}; OS: {Environment.OSVersion}; 64-bit process: {Environment.Is64BitProcess}");
    }

    public static void InstallGlobalExceptionHandlers()
    {
        lock (SyncRoot)
        {
            if (_globalHandlersInstalled)
            {
                return;
            }

            _globalHandlersInstalled = true;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            WriteCrashReport(exception, "Unhandled AppDomain exception", args.IsTerminating, args.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashReport(args.Exception, "Unobserved task exception", isTerminating: false, args.Exception);
            args.SetObserved();
        };
    }

    public static void InstallUiExceptionHandler()
    {
        lock (SyncRoot)
        {
            if (_uiHandlerInstalled)
            {
                return;
            }

            _uiHandlerInstalled = true;
        }

        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            WriteCrashReport(args.Exception, "Unhandled Avalonia UI exception", isTerminating: false, args.Exception);
        };
    }

    public static void Info(string message, string? context = null)
    {
        Write("INFO", message, context, exception: null);
    }

    public static void Warning(string message, string? context = null)
    {
        Write("WARN", message, context, exception: null);
    }

    public static void Warning(Exception exception, string context)
    {
        Write("WARN", exception.Message, context, exception);
    }

    public static void Error(string message, string? context = null)
    {
        Write("ERROR", message, context, exception: null);
    }

    public static void Error(Exception exception, string context)
    {
        Write("ERROR", exception.Message, context, exception);
    }

    public static void Fatal(Exception exception, string context)
    {
        Write("FATAL", exception.Message, context, exception);
        WriteCrashReport(exception, context, isTerminating: true, exception);
    }

    public static void Status(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (LooksLikeError(message))
        {
            Error(message, "StatusMessage");
            return;
        }

        Info(message, "StatusMessage");
    }

    public static void Trace(string message, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Write("TRACE", message, source, exception: null);
    }

    public static string? WriteCrashReport(Exception? exception, string context, bool isTerminating, object? rawExceptionObject)
    {
        try
        {
            InitializeDefault();
            var reportPath = Path.Combine(LogsDirectory, $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.log");
            var builder = new StringBuilder();
            builder.AppendLine("VideoAnalytics crash report");
            builder.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
            builder.AppendLine($"Context: {context}");
            builder.AppendLine($"Is terminating: {isTerminating}");
            builder.AppendLine($"Process: {Environment.ProcessPath}");
            builder.AppendLine($"Current directory: {Environment.CurrentDirectory}");
            builder.AppendLine($".NET: {Environment.Version}");
            builder.AppendLine($"OS: {Environment.OSVersion}");
            builder.AppendLine();

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }
            else
            {
                builder.AppendLine(rawExceptionObject?.ToString() ?? "No exception object was provided.");
            }

            File.WriteAllText(reportPath, builder.ToString(), Encoding.UTF8);
            Write("FATAL", $"Crash report saved: {reportPath}", context, exception);
            return reportPath;
        }
        catch
        {
            return null;
        }
    }

    private static void Write(string level, string message, string? context, Exception? exception)
    {
        try
        {
            InitializeDefault();
            var line = new StringBuilder();
            line.Append(DateTimeOffset.Now.ToString("O"));
            line.Append(" [");
            line.Append(level);
            line.Append(']');

            if (!string.IsNullOrWhiteSpace(context))
            {
                line.Append(" [");
                line.Append(context);
                line.Append(']');
            }

            line.Append(' ');
            line.AppendLine(message);

            if (exception is not null)
            {
                line.AppendLine(exception.ToString());
            }

            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogsDirectory);
                File.AppendAllText(CurrentLogPath, line.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static bool LooksLikeError(string message)
    {
        var value = message.Trim();
        return Contains(value, "error")
            || Contains(value, "failed")
            || Contains(value, "exception")
            || Contains(value, "missing")
            || Contains(value, "not found")
            || Contains(value, "could not")
            || Contains(value, "unable")
            || Contains(value, "ошиб")
            || Contains(value, "не удалось")
            || Contains(value, "не найден")
            || Contains(value, "недоступ")
            || Contains(value, "не хватает");
    }

    private static bool Contains(string value, string candidate)
    {
        return value.Contains(candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDefaultLogsDirectory()
    {
        var documentsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Video Analytics");
        return Path.Combine(documentsRoot, "Logs");
    }
}
