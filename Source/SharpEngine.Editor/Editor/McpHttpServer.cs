using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SharpEngine.Logging;

namespace SharpEngine.Editor;

// Stateless Streamable HTTP. All engine access is queued onto the existing
// Editor session pump, exactly like requests from the stdio/named-pipe bridge.
internal sealed class McpHttpServer : IDisposable
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private readonly HttpListener Listener = new();
    private readonly CancellationTokenSource Cancellation = new();
    private readonly EditorMcpSessionHost Session;
    private readonly Task ServerTask;
    private readonly ConcurrentDictionary<long, Task> Requests = new();
    private long RequestSequence;

    internal int Port
    {
        get;
    }

    internal string Endpoint
    {
        get
        {
            return $"http://127.0.0.1:{Port}/mcp";
        }
    }

    internal McpHttpServer(EditorMcpSessionHost Session, int Port)
    {
        McpConfiguration.ValidatePort(Port);
        this.Session = Session;
        this.Port = Port;
        Listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try
        {
            Listener.Start();
        }
        catch
        {
            Listener.Close();
            Cancellation.Dispose();
            throw;
        }
        ServerTask = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!Cancellation.IsCancellationRequested)
            {
                var Context = await Listener.GetContextAsync().ConfigureAwait(false);
                var Id = Interlocked.Increment(ref RequestSequence);
                var Request = HandleAsync(Context);
                Requests[Id] = Request;
                _ = Request.ContinueWith(Completed => Requests.TryRemove(Id, out _), TaskScheduler.Default);
            }
        }
        catch (Exception Error) when (Error is HttpListenerException or ObjectDisposedException)
        {
            if (!Cancellation.IsCancellationRequested)
            {
                SharpLog.Error("MCP", "HTTP listener failed", Error);
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext Context)
    {
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation.Token);
        Timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var Token = Timeout.Token;
        try
        {
            var Request = Context.Request;
            var Origin = Request.Headers["Origin"];
            var OriginValid = Uri.TryCreate(Origin, UriKind.Absolute, out var OriginUri);
            if (Origin is not null && (!OriginValid || OriginUri!.Scheme != "http" ||
                OriginUri.Host != "127.0.0.1" || OriginUri.Port != Port))
            {
                Context.Response.StatusCode = 403;
                return;
            }
            if (Request.Url?.AbsolutePath.TrimEnd('/') != "/mcp")
            {
                Context.Response.StatusCode = 404;
                return;
            }
            if (Request.HttpMethod != "POST")
            {
                Context.Response.StatusCode = 405;
                Context.Response.Headers["Allow"] = "POST";
                return;
            }
            if (Request.ContentType?.Split(';')[0].Trim() != "application/json")
            {
                Context.Response.StatusCode = 415;
                return;
            }
            var Accept = Request.Headers["Accept"];
            if (Accept is not null && !Accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
                !Accept.Contains("*/*", StringComparison.Ordinal))
            {
                Context.Response.StatusCode = 406;
                return;
            }
            var Protocol = Request.Headers["MCP-Protocol-Version"];
            if (Protocol is not null && Protocol is not ("2025-06-18" or "2025-03-26" or "2024-11-05"))
            {
                Context.Response.StatusCode = 400;
                return;
            }
            if (Request.ContentLength64 > MaximumRequestBytes)
            {
                Context.Response.StatusCode = 413;
                return;
            }
            using var Body = new MemoryStream();
            var Buffer = new byte[8192];
            while (true)
            {
                var Count = await Request.InputStream.ReadAsync(Buffer, Token).ConfigureAwait(false);
                if (Count == 0)
                {
                    break;
                }
                if (Body.Length + Count > MaximumRequestBytes)
                {
                    Context.Response.StatusCode = 413;
                    return;
                }
                Body.Write(Buffer, 0, Count);
            }
            var Line = Encoding.UTF8.GetString(Body.ToArray());
            try
            {
                using var Document = JsonDocument.Parse(Line);
                var Root = Document.RootElement;
                var HasMethod = Root.ValueKind == JsonValueKind.Object &&
                    Root.TryGetProperty("method", out var Method) && Method.ValueKind == JsonValueKind.String;
                if (!HasMethod)
                {
                    Context.Response.StatusCode = 400;
                    await WriteAsync(Context, "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32600,\"message\":\"Invalid request\"}}", Token).ConfigureAwait(false);
                    return;
                }
            }
            catch (JsonException)
            {
                Context.Response.StatusCode = 400;
                await WriteAsync(Context, "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"Parse error\"}}", Token).ConfigureAwait(false);
                return;
            }
            var Response = await Session.RequestAsync(Line, Token).ConfigureAwait(false);
            if (Response is null)
            {
                Context.Response.StatusCode = 202;
                return;
            }
            await WriteAsync(Context, Response, Token).ConfigureAwait(false);
        }
        catch (Exception Error) when (Error is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException)
        {
            // Disconnects and endpoint restarts cancel pending requests.
        }
        catch (Exception Error)
        {
            SharpLog.Error("MCP", "HTTP request failed", Error);
        }
        finally
        {
            try
            {
                Context.Response.Close();
            }
            catch (Exception Error) when (Error is HttpListenerException or ObjectDisposedException)
            {
                // A restart can close the response before this handler unwinds.
            }
        }
    }

    private static async Task WriteAsync(HttpListenerContext Context, string Json, CancellationToken Token)
    {
        var Bytes = Encoding.UTF8.GetBytes(Json);
        Context.Response.ContentType = "application/json";
        Context.Response.ContentLength64 = Bytes.Length;
        await Context.Response.OutputStream.WriteAsync(Bytes, Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Cancellation.Cancel();
        Listener.Close();
        ServerTask.GetAwaiter().GetResult();
        _ = FinishShutdownAsync();
    }

    private async Task FinishShutdownAsync()
    {
        await Task.WhenAll(Requests.Values).ConfigureAwait(false);
        Cancellation.Dispose();
    }
}
