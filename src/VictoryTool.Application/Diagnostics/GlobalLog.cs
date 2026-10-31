using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    private static readonly Regex WindowsAbsolutePathWithSpacesPattern = new(
        @"(?<![A-Za-z0-9>])[A-Za-z]:[\\/](?:[^\\/\r\n]+[\\/])+[^\\/\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static StreamWriter? _writer;
    private static string? _logPath;
    private static ConditionalWeakTable<Exception, object> _reportedErrors = new();
    private static long _bytesWritten;

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
        var context = new LogContext(operationName);
        Context.Value = context;
        if (IsSummaryOperation(operationName))
            Info("phase", new Dictionary<string, object?> { ["name"] = operationName, ["state"] = "started" });
        return new OperationScope(
            parent,
            context,
            Stopwatch.GetTimestamp(),
            IsSummaryOperation(operationName));
    }

    public static void StartSession(string appVersion, string? rootOverride = null)
    {
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
                _reportedErrors = new();
                _bytesWritten = 0;
                File.Delete(_logPath + ".previous");
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _writer = null;
                _logPath = null;
                global::System.Diagnostics.Debug.WriteLine(
                    $"VictoryTool log could not be initialized: {exception.Message}");
                return;
            }
        }

        Info("phase", new Dictionary<string, object?>
        {
            ["name"] = "application_started",
            ["version"] = appVersion,
        });
    }

    public static void Info(string eventName, IReadOnlyDictionary<string, object?>? data = null) =>
        Write("info", eventName, data);

    public static void Debug(string eventName, IReadOnlyDictionary<string, object?>? data = null) { }

    public static void Warn(
        string eventName,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? exception = null) { }

    public static void Error(
        string eventName,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        lock (Gate)
        {
            if (_writer is null) return;
            if (exception is not null)
            {
                if (_reportedErrors.TryGetValue(exception, out _)) return;
                _reportedErrors.Add(exception, new object());
            }
            Write("error", eventName, AddException(data, exception));
        }
    }

    public static void WriteTrace(string message) =>
        Debug("framework_trace", new Dictionary<string, object?>
        {
            ["message"] = message,
        });

    public static void Shutdown()
    {
        Info("phase", new Dictionary<string, object?> { ["name"] = "application_stopped" });

        lock (Gate)
        {
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
        operationName is "ui_initialize" or "ui_dump_load" or "ui_package_add"
            or "ui_export_preview" or "ui_export_execute" or "vrchara_draft_save"
            or "project_load" or "project_save";

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
        result["exception"] = exception.ToString();
        return result;
    }

    private static void Write(
        string level,
        string eventName,
        IReadOnlyDictionary<string, object?>? data)
    {
        if (level != "error" && eventName is not ("phase" or "character_selected" or "parameter_changed")) return;
        lock (Gate)
        {
            if (_writer is null) return;

            var record = new Dictionary<string, object?>
            {
                ["t"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["level"] = level,
                ["event"] = eventName,
            };
            if (Context.Value is { } context)
            {
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
            try
            {
                if (_bytesWritten + lineBytes > MaximumLogBytes)
                {
                    _writer.Dispose();
                    File.Move(_logPath!, _logPath + ".previous", overwrite: true);
                    _writer = new StreamWriter(new FileStream(_logPath!, FileMode.Create, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false)) { AutoFlush = true };
                    _bytesWritten = 0;
                }
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

        sanitized = WindowsAbsolutePathWithSpacesPattern.Replace(sanitized, "<path>");
        return AbsolutePathPattern.Replace(sanitized, "<path>");
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
                Info("phase", new Dictionary<string, object?>
                {
                    ["name"] = context.Operation,
                    ["state"] = "finished",
                    ["ms"] = (long)elapsedMs,
                });
            Context.Value = parent;
        }
    }

    private sealed record LogContext(string Operation);
}
