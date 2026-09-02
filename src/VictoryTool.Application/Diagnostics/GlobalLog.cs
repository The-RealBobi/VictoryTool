using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace VictoryTool.Application.Diagnostics;

public static class GlobalLog
{
    private const long MaximumLogBytes = 8 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AsyncLocal<LogContext?> Context = new();
    private static readonly Regex AbsolutePathPattern = new(
        @"(?<![A-Za-z0-9>])(?:(?:[A-Za-z]:[\\/])|/)(?:[^\\/\r\n\s]+[\\/])+[^\\/\r\n\s]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static StreamWriter? _writer;
    private static TraceListener? _traceListener;
    private static string? _logPath;
    private static string _sessionId = "uninitialized";
    private static long _bytesWritten;
    private static bool _limitReported;

    private static bool DebugLoggingEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("VICTORYTOOL_LOG_LEVEL"),
            "debug",
            StringComparison.OrdinalIgnoreCase);

    public static string? FilePath
    {
        get
        {
            lock (Gate) return _logPath;
        }
    }

    public static IDisposable BeginOperation(
        string operationName,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        var parent = Context.Value;
        var context = new LogContext(
            parent?.CorrelationId ?? CreateIdentifier(),
            CreateIdentifier(),
            operationName);
        Context.Value = context;
        Debug("operation_started", Merge(data, new Dictionary<string, object?>
        {
            ["operation"] = operationName,
        }));
        return new OperationScope(
            parent,
            context,
            Stopwatch.GetTimestamp(),
            IsSummaryOperation(operationName));
    }

    public static void StartSession(string appVersion, string? rootOverride = null)
    {
        string? logPath;
        lock (Gate)
        {
            if (_writer is not null) return;

            try
            {
                var root = string.IsNullOrWhiteSpace(rootOverride)
                    ? GetDefaultDirectory()
                    : rootOverride;
                Directory.CreateDirectory(root);
                _logPath = Path.Combine(root, "VictoryTool.log");
                _writer = new StreamWriter(
                    new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
                _sessionId = Guid.NewGuid().ToString("N")[..12];
                _bytesWritten = 0;
                _limitReported = false;
                var listener = new GlobalTraceListener();
                _traceListener = listener;
                Trace.Listeners.Add(listener);
                logPath = _logPath;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _writer = null;
                _traceListener = null;
                _logPath = null;
                global::System.Diagnostics.Debug.WriteLine(
                    $"VictoryTool log could not be initialized: {exception.Message}");
                return;
            }
        }

        Info("log_initialized", new Dictionary<string, object?>
        {
            ["path"] = logPath,
            ["storage"] = OperatingSystem.IsWindows()
                ? "local_application_data"
                : OperatingSystem.IsMacOS()
                    ? "macos_application_support"
                    : "application_data",
        });
        Info("application_started", new Dictionary<string, object?>
        {
            ["appVersion"] = appVersion,
            ["osDescription"] = RuntimeInformation.OSDescription,
            ["osVersion"] = Environment.OSVersion.VersionString,
            ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["culture"] = CultureInfo.CurrentCulture.Name,
            ["uiCulture"] = CultureInfo.CurrentUICulture.Name,
            ["is64BitProcess"] = Environment.Is64BitProcess,
            ["is64BitOperatingSystem"] = Environment.Is64BitOperatingSystem,
        });
    }

    public static void Info(string eventName, IReadOnlyDictionary<string, object?>? data = null) =>
        Write("info", eventName, data);

    public static void Debug(string eventName, IReadOnlyDictionary<string, object?>? data = null)
    {
        if (DebugLoggingEnabled) Write("debug", eventName, data);
    }

    public static void Warn(
        string eventName,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? exception = null) =>
        Write("warn", eventName, AddException(data, exception));

    public static void Error(
        string eventName,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        Write("error", eventName, AddException(data, exception));

    public static void WriteTrace(string message) =>
        Debug("framework_trace", new Dictionary<string, object?>
        {
            ["message"] = message,
        });

    public static void Shutdown()
    {
        Info("application_stopped");

        lock (Gate)
        {
            if (_traceListener is not null)
            {
                Trace.Listeners.Remove(_traceListener);
                _traceListener.Dispose();
                _traceListener = null;
            }

            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _writer = null;
            }
        }
    }

    public static string GetDefaultDirectory()
    {
        var applicationData = OperatingSystem.IsMacOS()
            ? GetMacApplicationSupportDirectory()
            : Environment.GetFolderPath(
                OperatingSystem.IsWindows()
                    ? Environment.SpecialFolder.LocalApplicationData
                    : Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(applicationData))
        {
            applicationData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".victorytool");
        }

        return Path.Combine(applicationData, "VictoryTool");
    }

    private static bool IsSummaryOperation(string operationName) =>
        operationName is not "asset_preview_load"
            and not "character_portrait_load"
            and not "texture_decode"
            and not "portrait_png_convert";

    private static string GetMacApplicationSupportDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        return string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : Path.Combine(userProfile, "Library", "Application Support");
    }

    private static IReadOnlyDictionary<string, object?>? AddException(
        IReadOnlyDictionary<string, object?>? data,
        Exception? exception)
    {
        if (exception is null) return data;

        var result = data is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(data);
        result["exceptionType"] = exception.GetType().FullName;
        result["exceptionMessage"] = exception.Message;
        result["stackTrace"] = exception.ToString();
        return result;
    }

    private static void Write(
        string level,
        string eventName,
        IReadOnlyDictionary<string, object?>? data)
    {
        lock (Gate)
        {
            if (_writer is null) return;

            var record = new Dictionary<string, object?>
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["level"] = level,
                ["event"] = eventName,
                ["sessionId"] = _sessionId,
                ["correlationId"] = Context.Value?.CorrelationId ?? _sessionId,
            };
            if (Context.Value is { } context)
            {
                record["operationId"] = context.OperationId;
                record["operation"] = context.Operation;
            }
            if (data is not null)
            {
                record["data"] = data.ToDictionary(
                    pair => pair.Key,
                    pair => SanitizeValue(pair.Value),
                    StringComparer.Ordinal);
            }

            var line = JsonSerializer.Serialize(record, JsonOptions);
            var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (_bytesWritten + lineBytes > MaximumLogBytes)
            {
                if (_limitReported) return;
                _limitReported = true;
                line = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["level"] = "warn",
                    ["event"] = "log_limit_reached",
                    ["sessionId"] = _sessionId,
                    ["data"] = new Dictionary<string, object?>
                    {
                        ["maximumBytes"] = MaximumLogBytes,
                    },
                }, JsonOptions);
            }

            try
            {
                _writer.WriteLine(line);
                _bytesWritten += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _writer = null;
                _logPath = null;
                global::System.Diagnostics.Debug.WriteLine(
                    $"VictoryTool log write failed: {exception.Message}");
            }
        }
    }

    private static object? SanitizeValue(object? value) => value switch
    {
        null => null,
        string text => Sanitize(text),
        bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal => value,
        Enum enumValue => enumValue.ToString(),
        _ => Sanitize(value.ToString() ?? string.Empty),
    };

    private static IReadOnlyDictionary<string, object?>? Merge(
        IReadOnlyDictionary<string, object?>? first,
        IReadOnlyDictionary<string, object?> second)
    {
        var result = first is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(first);
        foreach (var pair in second) result[pair.Key] = pair.Value;
        return result;
    }

    private static string CreateIdentifier() => Guid.NewGuid().ToString("N")[..12];

    private static string Sanitize(string value)
    {
        var sanitized = value;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var temporary = Path.GetTempPath();
        var application = AppContext.BaseDirectory;

        foreach (var (path, replacement) in new[]
        {
            (home, "<user-home>"),
            (temporary, "<temp>"),
            (application, "<app>"),
        })
        {
            if (!string.IsNullOrWhiteSpace(path))
                sanitized = sanitized.Replace(path, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return AbsolutePathPattern.Replace(sanitized, "<path>");
    }

    private sealed class GlobalTraceListener : TraceListener
    {
        public override void Write(string? message) => WriteTrace(message ?? string.Empty);

        public override void WriteLine(string? message) => WriteTrace(message ?? string.Empty);
    }

    private sealed class OperationScope(
        LogContext? parent,
        LogContext context,
        long startedAt,
        bool writeSummary) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            if (writeSummary)
            {
                Info("operation_finished", new Dictionary<string, object?>
                {
                    ["operation"] = context.Operation,
                    ["elapsedMs"] = Math.Round(elapsedMs, 2),
                });
            }
            else
            {
                Debug("operation_finished", new Dictionary<string, object?>
                {
                    ["operation"] = context.Operation,
                    ["elapsedMs"] = Math.Round(elapsedMs, 2),
                });
            }
            Context.Value = parent;
        }
    }

    private sealed record LogContext(
        string CorrelationId,
        string OperationId,
        string Operation);
}
