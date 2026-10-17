using System.Text.Json;
using SharpEngine.Logging;

var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "SharpEngineLoggingValidation");
Directory.CreateDirectory(root);
var logPath = Path.Combine(root, "Saved", "Logs", "Engine.log");
if (File.Exists(logPath)) File.Delete(logPath);

var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
}

var observed = new List<SharpLogEvent>();
using var subscription = SharpLog.Subscribe(observed.Add);
SharpLog.Initialize("LoggingValidation", root, minimumLevel: SharpLogLevel.Trace);
SharpLog.Info("Validation", "hello", new Dictionary<string, string> { ["case"] = "basic" });
using (SharpLog.PushScope(new Dictionary<string, string> { ["session"] = "scope-1" }))
    SharpLog.Warning("Validation", "scoped warning");
SharpLog.Error("Validation", "failed operation", new InvalidOperationException("expected"));
SharpLog.Flush();
SharpLog.Shutdown();

Check(File.Exists(logPath), "Engine.log is created below Saved/Logs");
var lines = File.ReadAllLines(logPath);
Check(lines.Length >= 4, "Initialization and emitted records are persisted");
var records = lines.Select(line => JsonDocument.Parse(line).RootElement).ToArray();
Check(records.All(record => record.GetProperty("schemaVersion").GetInt32() == 1), "Records have schemaVersion 1");
Check(records.Any(record => record.GetProperty("category").GetString() == "Validation" &&
    record.GetProperty("level").GetString() == "info"), "Information records keep category and level");
Check(records.Any(record => record.GetProperty("properties").TryGetProperty("session", out var value) &&
    value.GetString() == "scope-1"), "Scope properties are persisted");
Check(records.Any(record => record.GetProperty("exception").GetString()?.Contains("expected", StringComparison.Ordinal) == true),
    "Exception text is persisted on error records");
Check(observed.Any(entry => entry.Category == "Validation" && entry.Level == SharpLogLevel.Warning),
    "Observers receive structured events");

Console.WriteLine($"PASS: {assertions} unified logging assertions; log={logPath}");
