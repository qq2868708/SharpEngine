using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SharpEngine.Editor;

namespace SharpEngine.Game;

/// <summary>Compiles off-thread; all project-code activation runs on the editor thread.</summary>
internal sealed class ProjectCodeService : IDisposable
{
    private sealed record Update(string State, string Message, string Fingerprint = "");
    private readonly ConcurrentQueue<Update> _updates = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly string _manifest, _engine, _configuration, _platform, _root;
    private readonly EditorRuntime _runtime;
    private readonly InputRuntimeService _input;
    private ProjectModuleHost? _host;
    private string? _pending;
    private long _request;
    private bool _disposed;
    private readonly Task _worker;

    internal ProjectCodeService(EditorRuntime runtime, InputRuntimeService input, ProjectModuleHost? host)
    {
        _runtime = runtime; _input = input; _host = host;
        _manifest = Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST") ?? "";
        _engine = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT") ?? "";
        _root = Path.GetDirectoryName(_manifest) ?? "";
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(ProjectCodeService).Assembly.Location)!);
        _configuration = directory.Parent!.Name;
        _platform = directory.Parent.Parent!.Name;
        _runtime.RequestProjectBuild = () => Interlocked.Increment(ref _request);
        _worker = _manifest.Length == 0 ? Task.CompletedTask : Task.Run(Watch);
    }

    private async Task Watch()
    {
        var token = _cancel.Token;
        string observed = "", attempted = "";
        long requested = -1;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(750, token);
                string fingerprint;
                try { fingerprint = Fingerprint(_root); }
                catch (IOException) { continue; } // An editor may be replacing a file.
                if (fingerprint != observed)
                {
                    observed = fingerprint;
                    _updates.Enqueue(new("Changed", "Project code changed; waiting for file writes to settle"));
                    continue;
                }
                var request = Interlocked.Read(ref _request);
                if (fingerprint == attempted && request == requested) continue;
                attempted = fingerprint; requested = request;
                _updates.Enqueue(new("Building", "Building project code…"));
                var (success, output) = await Build(token);
                if (Fingerprint(_root) != fingerprint)
                {
                    observed = "";
                    _updates.Enqueue(new("Changed", "Build superseded by newer source edits"));
                    continue;
                }
                _updates.Enqueue(new(success ? "Ready" : "BuildFailed", output, fingerprint));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error)
            {
                _updates.Enqueue(new("BuildFailed", error.Message));
            }
        }
    }

    private async Task<(bool Success, string Output)> Build(CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root,
        };
        foreach (var argument in new[] { "run", "--project", Path.Combine(_engine, "Tools", "ProjectBuild", "SharpEngine.ProjectBuild.csproj"),
            "--", "build", _manifest, "--target", "Editor", "--configuration", _configuration,
            "--platform", _platform, "--engine-root", _engine }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the project builder");
        using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await stdout + Environment.NewLine + await stderr;
        var logDirectory = Path.Combine(_root, "Saved", "Logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(Path.Combine(logDirectory, "ProjectBuild.log"), output);
        return (process.ExitCode == 0, process.ExitCode == 0 ? "Project build complete; ready to reload" :
            output.Length <= 12000 ? output : output[^12000..]);
    }

    internal void Tick()
    {
        ProjectModuleHost.CollectRetiredCopies();
        while (_updates.TryDequeue(out var update))
        {
            _pending = update.State == "Ready" ? update.Fingerprint : null;
            _runtime.SetProjectBuildStatus(update.State, update.Message);
        }
        if (_pending is null || !_runtime.CanApplyProjectCode) return;
        try { if (Fingerprint(_root) != _pending) { _pending = null; return; } }
        catch (IOException) { return; }
        _pending = null;
        ProjectModuleHost? candidate = null;
        EditorRuntime.ModuleReloadPreparation? preparation = null;
        var switching = false;
        try
        {
            candidate = ProjectModuleHost.Prepare() ?? throw new InvalidOperationException("Project manifest unavailable");
            preparation = _runtime.PrepareModuleReload(candidate.Assemblies);
            switching = true;
            _runtime.SetProjectCodeAvailable(false, "Applying project code…");
            _host?.Dispose();
            var shutdownError = _host?.ShutdownError;
            _host = null;
            if (shutdownError is not null) throw new InvalidOperationException(shutdownError);
            preparation.Commit();
            candidate.Start(_runtime, _input);
            _runtime.SetProjectCodeAvailable(true, "Project code reloaded");
            preparation.Accept();
            _host = candidate; candidate = null;
            _runtime.SetProjectBuildStatus("Idle", "Project code reloaded");
        }
        catch (Exception error)
        {
            candidate?.Dispose();
            preparation?.Dispose();
            if (switching) _runtime.SetProjectCodeAvailable(false, "Project reload failed. Edit code and rebuild to recover; Editor remains open.");
            _runtime.SetProjectBuildStatus("ReloadFailed", error.ToString());
        }
        finally { preparation?.Dispose(); }
    }

    internal static string Fingerprint(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var source = Path.Combine(root, "Source");
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var files = Directory.Exists(source) ? Directory.EnumerateFiles(source, "*", options)
            .Where(path => !Path.GetRelativePath(source, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj") &&
                Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" or ".props" or ".targets") : [];
        files = files.Concat(Directory.EnumerateFiles(root).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".sharpproject" or ".props" or ".targets"));
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file) + "\0"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.RequestProjectBuild = null;
        _cancel.Cancel();
        _host?.Dispose(); _host = null;
        _ = _worker.ContinueWith(_ => _cancel.Dispose(), TaskScheduler.Default);
    }
}
