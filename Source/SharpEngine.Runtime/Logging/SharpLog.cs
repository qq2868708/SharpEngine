using System.Text;
using System.Text.Json;

namespace SharpEngine.Logging;

public enum SharpLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

public readonly record struct SharpLogEvent(
    DateTimeOffset Timestamp,
    SharpLogLevel Level,
    string Source,
    string Category,
    string Message,
    int ProcessId,
    int ThreadId,
    string? Exception,
    IReadOnlyDictionary<string, string> Properties)
{
    public string LevelName => Level switch
    {
        SharpLogLevel.Information => "info",
        SharpLogLevel.Warning => "warning",
        SharpLogLevel.Error => "error",
        SharpLogLevel.Critical => "critical",
        SharpLogLevel.Debug => "debug",
        _ => "trace",
    };
}

public interface ISharpLogger
{
    string Category { get; }
    void Log(SharpLogLevel level, string message, Exception? exception = null,
        IReadOnlyDictionary<string, string>? properties = null);
    void Trace(string message, IReadOnlyDictionary<string, string>? properties = null);
    void Debug(string message, IReadOnlyDictionary<string, string>? properties = null);
    void Info(string message, IReadOnlyDictionary<string, string>? properties = null);
    void Warning(string message, IReadOnlyDictionary<string, string>? properties = null);
    void Error(string message, Exception? exception = null,
        IReadOnlyDictionary<string, string>? properties = null);
    void Critical(string message, Exception? exception = null,
        IReadOnlyDictionary<string, string>? properties = null);
}

/// <summary>
/// Process-wide engine logger. The JSON-lines file is the authoritative diagnostic
/// stream; stderr remains available for native hosts and command-line runs.
/// </summary>
public static class SharpLog
{
    private const int SchemaVersion = 1;
    private const string MutexName = "Local\\SharpEngine.EngineLog";
    private static readonly object Gate = new();
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> Scope = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private static StreamWriter? _writer;
    private static Mutex? _mutex;
    private static string _source = "Managed";
    private static SharpLogLevel _minimumLevel = SharpLogLevel.Trace;
    private static bool _consoleEnabled = true;
    private static string? _filePath;

    public static event Action<SharpLogEvent>? Emitted;
    public static string? FilePath { get { lock (Gate) return _filePath; } }
    public static SharpLogLevel MinimumLevel { get { lock (Gate) return _minimumLevel; } }

    public static ISharpLogger For(string category) =>
        new CategoryLogger(string.IsNullOrWhiteSpace(category) ? "General" : category.Trim());

    public static void Initialize(string source, string? projectRoot = null,
        string? engineRoot = null, SharpLogLevel minimumLevel = SharpLogLevel.Trace,
        bool consoleEnabled = true)
    {
        var root = FirstExistingRoot(projectRoot, engineRoot, AppContext.BaseDirectory);
        var directory = Path.Combine(root, "Saved", "Logs");
        var filePath = Path.Combine(directory, "Engine.log");
        lock (Gate)
        {
            CloseFileLocked();
            _source = string.IsNullOrWhiteSpace(source) ? "Managed" : source.Trim();
            _minimumLevel = minimumLevel;
            _consoleEnabled = consoleEnabled;
            _filePath = filePath;
            try
            {
                Directory.CreateDirectory(directory);
                _mutex = new Mutex(false, MutexName);
                _writer = new StreamWriter(new FileStream(filePath, FileMode.Append,
                    FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true };
            }
            catch (Exception error)
            {
                CloseFileLocked();
                _filePath = null;
                try { Console.Error.WriteLine($"[error][Logging] file sink setup failed: {error.Message}"); }
                catch { }
            }
        }
        Info("Logging", $"Logging initialized at '{filePath}'", new Dictionary<string, string>
        {
            ["source"] = _source,
            ["minimumLevel"] = minimumLevel.ToString(),
        });
    }

    public static IDisposable PushScope(IReadOnlyDictionary<string, string> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var previous = Scope.Value;
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (previous is not null)
            foreach (var pair in previous) merged[pair.Key] = pair.Value;
        foreach (var pair in properties)
            if (!string.IsNullOrWhiteSpace(pair.Key)) merged[pair.Key] = pair.Value ?? string.Empty;
        Scope.Value = merged;
        return new ScopeLease(previous);
    }

    public static void Trace(string category, string message,
        IReadOnlyDictionary<string, string>? properties = null) =>
        Write(SharpLogLevel.Trace, category, message, null, properties);
    public static void Debug(string category, string message,
        IReadOnlyDictionary<string, string>? properties = null) =>
        Write(SharpLogLevel.Debug, category, message, null, properties);
    public static void Info(string category, string message,
        IReadOnlyDictionary<string, string>? properties = null) =>
        Write(SharpLogLevel.Information, category, message, null, properties);
    public static void Warning(string category, string message,
        IReadOnlyDictionary<string, string>? properties = null) =>
        Write(SharpLogLevel.Warning, category, message, null, properties);
    public static void Error(string category, string message, Exception? exception = null,
        IReadOnlyDictionary<string, string>? properties = null) =>
        Write(SharpLogLevel.Error, category, message, exception, properties);
    public static void Critical(string category, string message, Exception? exception = null,
        IReadOnlyDictionary<string, string>? properties = null) =>
        Write(SharpLogLevel.Critical, category, message, exception, properties);

    public static IDisposable Subscribe(Action<SharpLogEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Emitted += handler;
        return new Subscription(() => Emitted -= handler);
    }

    public static void Flush()
    {
        lock (Gate) _writer?.Flush();
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            CloseFileLocked();
            _filePath = null;
        }
    }

    internal static void Write(SharpLogLevel level, string category, string message,
        Exception? exception, IReadOnlyDictionary<string, string>? properties)
    {
        if (string.IsNullOrWhiteSpace(message)) message = exception?.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(category)) category = "General";
        SharpLogEvent entry;
        lock (Gate)
        {
            if (level < _minimumLevel) return;
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Scope.Value is not null)
                foreach (var pair in Scope.Value) merged[pair.Key] = pair.Value;
            if (properties is not null)
                foreach (var pair in properties)
                    if (!string.IsNullOrWhiteSpace(pair.Key)) merged[pair.Key] = pair.Value ?? string.Empty;
            entry = new SharpLogEvent(DateTimeOffset.UtcNow, level, _source, category.Trim(),
                message, Environment.ProcessId, Environment.CurrentManagedThreadId,
                exception?.ToString(), merged);
            WriteFileLocked(entry);
            if (_consoleEnabled)
            {
                var suffix = exception is null ? string.Empty : $" {exception}";
                Console.Error.WriteLine($"[{entry.LevelName}][{entry.Category}] {entry.Message}{suffix}");
            }
        }
        try { Emitted?.Invoke(entry); }
        catch (Exception callbackError)
        {
            // A log observer must never break engine work. The fallback goes only
            // to stderr so a broken UI observer cannot recurse into this logger.
            try { Console.Error.WriteLine($"[error][Logging] observer failed: {callbackError}"); }
            catch { }
        }
    }

    private static void WriteFileLocked(in SharpLogEvent entry)
    {
        if (_writer is null || _mutex is null) return;
        var acquired = false;
        try
        {
            acquired = _mutex.WaitOne(TimeSpan.FromSeconds(2));
            if (!acquired) return;
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = SchemaVersion,
                timestamp = entry.Timestamp,
                level = entry.LevelName,
                source = entry.Source,
                category = entry.Category,
                message = entry.Message,
                processId = entry.ProcessId,
                threadId = entry.ThreadId,
                exception = entry.Exception,
                properties = entry.Properties,
            }, JsonOptions);
            _writer.WriteLine(json);
        }
        catch (Exception error)
        {
            try { Console.Error.WriteLine($"[error][Logging] file sink failed: {error.Message}"); }
            catch { }
        }
        finally
        {
            if (acquired)
                try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    private static string FirstExistingRoot(params string?[] roots)
    {
        foreach (var value in roots)
            if (!string.IsNullOrWhiteSpace(value)) return Path.GetFullPath(value);
        return AppContext.BaseDirectory;
    }

    private static void CloseFileLocked()
    {
        try { _writer?.Flush(); } catch { }
        _writer?.Dispose();
        _writer = null;
        _mutex?.Dispose();
        _mutex = null;
    }

    private sealed class CategoryLogger(string category) : ISharpLogger
    {
        public string Category { get; } = category;
        public void Log(SharpLogLevel level, string message, Exception? exception = null,
            IReadOnlyDictionary<string, string>? properties = null) =>
            Write(level, Category, message, exception, properties);
        public void Trace(string message, IReadOnlyDictionary<string, string>? properties = null) =>
            Write(SharpLogLevel.Trace, Category, message, null, properties);
        public void Debug(string message, IReadOnlyDictionary<string, string>? properties = null) =>
            Write(SharpLogLevel.Debug, Category, message, null, properties);
        public void Info(string message, IReadOnlyDictionary<string, string>? properties = null) =>
            Write(SharpLogLevel.Information, Category, message, null, properties);
        public void Warning(string message, IReadOnlyDictionary<string, string>? properties = null) =>
            Write(SharpLogLevel.Warning, Category, message, null, properties);
        public void Error(string message, Exception? exception = null,
            IReadOnlyDictionary<string, string>? properties = null) =>
            Write(SharpLogLevel.Error, Category, message, exception, properties);
        public void Critical(string message, Exception? exception = null,
            IReadOnlyDictionary<string, string>? properties = null) =>
            Write(SharpLogLevel.Critical, Category, message, exception, properties);
    }

    private sealed class ScopeLease(IReadOnlyDictionary<string, string>? previous) : IDisposable
    {
        private IReadOnlyDictionary<string, string>? _previous = previous;
        public void Dispose() => Scope.Value = Interlocked.Exchange(ref _previous, null);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
