# Unified logging

SharpEngine uses `SharpEngine.Logging.SharpLog` for managed code and
`EngineLog` for the native host. Both write the same JSON-lines schema to:

```text
<project>/Saved/Logs/Engine.log
```

When no project is open, the engine root is used. Each record contains
`schemaVersion`, UTC `timestamp`, `level`, `source`, `category`, `message`,
process/thread ids, an optional exception, and string properties. The file is
append-only for a session and uses a named Windows mutex so native and managed
writers can share it safely. The live console mirror sends Trace, Debug, and Info
to stdout, and Warning, Error, and Critical to stderr, so IDE consoles do not mark
normal progress as an error. Before native host configuration and in
`--mcp-stdio` processes, all diagnostic output stays on stderr to reserve stdout
for JSON-RPC messages.

Engine and project modules receive a category-scoped `ISharpLogger` through
`ModuleContext.Logger`. Use that logger instead of writing directly to
`Console`, `std::cout`, or `std::cerr`. The editor's **Logs** pane shows the
structured unified stream, including native records read incrementally from
`Engine.log`; the **Console** pane remains a short editor-status surface.
MCP stdout remains reserved for JSON-RPC protocol messages. `editor.describe`
exposes the active file path, minimum level, recent text entries, and recent
structured events for machine-readable diagnostics.

`ProjectBuild.log` is retained as the complete compiler transcript. The unified
log records its path and build result so the session stream remains searchable
without duplicating potentially large compiler output.

Generated Target launchers use stderr for bootstrap failures, such as a missing
`Engine.exe`. They compile before the engine and require no engine assemblies.

Focused validation:

```powershell
dotnet run --project Tools/LoggingValidation/SharpEngine.LoggingValidation.csproj -- D:/SharpEngine/Saved/Validation/Logging
dotnet run --project Tools/LoggingValidation/SharpEngine.LoggingValidation.csproj -- D:/SharpEngine/Saved/Validation/LoggingMcp --mcp-stdio
```
