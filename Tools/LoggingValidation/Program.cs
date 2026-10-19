using System.Text.Json;
using SharpEngine.Logging;

var RootArgument = args.FirstOrDefault(Argument => !Argument.StartsWith("--", StringComparison.Ordinal));
var Root = RootArgument is not null ? Path.GetFullPath(RootArgument)
    : Path.Combine(Path.GetTempPath(), "SharpEngineLoggingValidation");
var StandardOutputReserved = args.Contains("--mcp-stdio", StringComparer.Ordinal);
Directory.CreateDirectory(Root);
var LogPath = Path.Combine(Root, "Saved", "Logs", "Engine.log");
if (File.Exists(LogPath))
{
    File.Delete(LogPath);
}

var Assertions = 0;
void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new InvalidOperationException(Message);
    }
    Assertions++;
}

var Observed = new List<SharpLogEvent>();
using var Subscription = SharpLog.Subscribe(Observed.Add);
using var StandardOutput = new StringWriter();
using var StandardError = new StringWriter();
var OriginalOutput = Console.Out;
var OriginalError = Console.Error;
Console.SetOut(StandardOutput);
Console.SetError(StandardError);
try
{
    SharpLog.Initialize("LoggingValidation", Root, minimumLevel: SharpLogLevel.Trace);
    SharpLog.Trace("Validation", "trace progress");
    SharpLog.Debug("Validation", "debug progress");
    SharpLog.Info("Validation", "hello", new Dictionary<string, string>
    {
        ["case"] = "basic",
    });
    using (SharpLog.PushScope(new Dictionary<string, string>
    {
        ["session"] = "scope-1",
    }))
    {
        SharpLog.Warning("Validation", "scoped warning");
    }
    SharpLog.Error("Validation", "failed operation", new InvalidOperationException("expected"));
    SharpLog.Critical("Validation", "critical operation");
    SharpLog.Flush();
}
finally
{
    SharpLog.Shutdown();
    Console.SetOut(OriginalOutput);
    Console.SetError(OriginalError);
}

Check(File.Exists(LogPath), "Engine.log is created below Saved/Logs");
var Lines = File.ReadAllLines(LogPath);
Check(Lines.Length == 7, "Initialization and all six levels are persisted once");
var Records = Lines.Select(Line => JsonDocument.Parse(Line).RootElement).ToArray();
Check(Records.All(Record => Record.GetProperty("schemaVersion").GetInt32() == 1), "Records have schemaVersion 1");
Check(Records.Any(Record => Record.GetProperty("category").GetString() == "Validation" &&
    Record.GetProperty("level").GetString() == "info"), "Information records keep category and level");
Check(Records.Any(Record => Record.GetProperty("properties").TryGetProperty("session", out var Value) &&
    Value.GetString() == "scope-1"), "Scope properties are persisted");
Check(Records.Any(Record => Record.GetProperty("exception").GetString()?.Contains("expected", StringComparison.Ordinal) == true),
    "Exception text is persisted on error records");
Check(Observed.Count(Entry => Entry.Category == "Validation") == 6,
    "Observers receive all six structured levels");

var OutputText = StandardOutput.ToString();
var ErrorText = StandardError.ToString();
foreach (var Level in Enum.GetValues<SharpLogLevel>())
{
    var Entry = Observed.Single(Entry => Entry.Category == "Validation" && Entry.Level == Level);
    var Text = $"[{Entry.LevelName}][{Entry.Category}] {Entry.Message}";
    var ExpectedError = StandardOutputReserved || Level >= SharpLogLevel.Warning;
    Check(OutputText.Contains(Text, StringComparison.Ordinal) == !ExpectedError,
        $"{Level} stdout routing is correct");
    Check(ErrorText.Contains(Text, StringComparison.Ordinal) == ExpectedError,
        $"{Level} stderr routing is correct");
}
Check(ErrorText.Contains("InvalidOperationException: expected", StringComparison.Ordinal),
    "Error console output preserves exception details");
if (StandardOutputReserved)
{
    Check(OutputText.Length == 0, "MCP stdout contains no diagnostic output, including initialization");
}

Console.WriteLine($"PASS: {Assertions} unified logging assertions; mcpStdio={StandardOutputReserved}; log={LogPath}");
