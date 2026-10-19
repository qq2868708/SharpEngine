using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Logging;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private const string McpSettingsHost = "settings.mcp";
    private readonly int McpOwnerThread = Environment.CurrentManagedThreadId;
    private McpHttpServer? McpHttp;
    private McpConfiguration McpConfigurationValue = new();
    private string McpConfigurationPath = string.Empty;
    private string? McpObservedFile;
    private string? McpDiagnostic;
    private string McpPortDraft = string.Empty;
    private bool McpSettingsOpen;
    private ulong McpRevision;
    private ulong McpDraftRevision;
    private long McpNextPoll;

    private void InitializeMcp()
    {
        var OverridePath = Environment.GetEnvironmentVariable("SHARPENGINE_MCP_CONFIG");
        McpConfigurationPath = Path.GetFullPath(string.IsNullOrWhiteSpace(OverridePath)
            ? Path.Combine(_projectRoot ?? _engineRoot, "Config", "Mcp.json") : OverridePath);
        if (!HasLiveUi)
        {
            return;
        }
        _mcpSession = new EditorMcpSessionHost(this);
        try
        {
            if (!File.Exists(McpConfigurationPath))
            {
                McpConfigurationValue.Save(McpConfigurationPath);
            }
            McpObservedFile = File.ReadAllText(McpConfigurationPath);
            var Configuration = McpConfiguration.Parse(McpObservedFile);
            McpConfigurationValue = Configuration;
            McpHttp = new McpHttpServer(_mcpSession, Configuration.Port);
            McpRevision++;
            SharpLog.Info("MCP", $"HTTP endpoint started: {McpHttp.Endpoint}");
        }
        catch (Exception Error) when (IsMcpConfigurationError(Error))
        {
            SetMcpDiagnostic(Error.Message);
        }
        RefreshExtensionUi();
    }

    public string DescribeMcpJson() { return DescribeMcpState().ToJsonString(); }

    private JsonObject DescribeMcpState()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["revision"] = McpRevision,
            ["sessionId"] = McpSessionDescriptor?.SessionId,
            ["configurationPath"] = McpConfigurationPath,
            ["configuredPort"] = McpConfigurationValue.Port,
            ["activePort"] = McpHttp?.Port,
            ["endpoint"] = McpHttp?.Endpoint,
            ["running"] = McpHttp is not null,
            ["diagnostic"] = McpDiagnostic,
            ["settingsOpen"] = McpSettingsOpen,
        };
    }

    // Editor preference + external effect: explicit confirmation saves and
    // restarts the transport; it does not create a World Undo transaction.
    public JsonObject ConfigureMcp(int Port, ulong ExpectedRevision)
    {
        if (Environment.CurrentManagedThreadId != McpOwnerThread)
        {
            return McpFailure("MCP_WRONG_THREAD", "MCP configuration must run on the editor thread");
        }
        if (ExpectedRevision != McpRevision)
        {
            return McpFailure("MCP_REVISION_CONFLICT", "MCP configuration changed; reopen settings and retry");
        }
        if (_mcpSession is null)
        {
            return McpFailure("MCP_SESSION_UNAVAILABLE", "No live Editor MCP session is available");
        }
        try
        {
            var Configuration = new McpConfiguration
            {
                Port = Port,
            };
            ApplyMcpConfiguration(Configuration, Save: true);
            SetStatus($"MCP restarted · {McpHttp!.Endpoint}");
            return new JsonObject
            {
                ["schemaVersion"] = 1,
                ["success"] = true,
                ["mcp"] = DescribeMcpState(),
            };
        }
        catch (Exception Error) when (IsMcpConfigurationError(Error))
        {
            SetMcpDiagnostic(Error.Message);
            return McpFailure("MCP_CONFIGURATION_FAILED", Error.Message);
        }
    }

    private void ApplyMcpConfiguration(McpConfiguration Configuration, bool Save)
    {
        McpConfiguration.ValidatePort(Configuration.Port);
        var Previous = McpHttp;
        var RestartSamePort = Previous?.Port == Configuration.Port;
        McpHttpServer? Replacement = null;
        try
        {
            if (RestartSamePort)
            {
                Previous!.Dispose();
                McpHttp = null;
            }
            // Acquire the requested port before touching the savepoint or the
            // previous endpoint, so a busy port leaves the running service intact.
            Replacement = new McpHttpServer(_mcpSession!, Configuration.Port);
            if (Save)
            {
                Configuration.Save(McpConfigurationPath);
            }
        }
        catch
        {
            Replacement?.Dispose();
            if (RestartSamePort)
            {
                McpHttp = new McpHttpServer(_mcpSession!, McpConfigurationValue.Port);
            }
            throw;
        }
        McpHttp = Replacement;
        if (!RestartSamePort)
        {
            Previous?.Dispose();
        }
        McpConfigurationValue = Configuration;
        if (Save)
        {
            McpObservedFile = Configuration.ToJson();
        }
        McpDiagnostic = null;
        McpRevision++;
        RefreshExtensionUi();
    }

    private JsonObject McpFailure(string Code, string Message)
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["success"] = false,
            ["diagnostics"] = new JsonArray
            {
                new JsonObject
                {
                    ["code"] = Code,
                    ["severity"] = "error",
                    ["objectId"] = "editor.mcp",
                    ["fieldPath"] = "port",
                    ["expected"] = "an available local port from 1 to 65535 and the current revision",
                    ["actual"] = Message,
                    ["stateRevision"] = McpRevision,
                    ["retryable"] = true,
                    ["message"] = Message,
                },
            },
            ["mcp"] = DescribeMcpState(),
        };
    }

    private void PollMcpConfiguration()
    {
        if (_mcpSession is null || Environment.TickCount64 < McpNextPoll)
        {
            return;
        }
        McpNextPoll = Environment.TickCount64 + 1000;
        try
        {
            var Text = File.ReadAllText(McpConfigurationPath);
            if (Text == McpObservedFile)
            {
                return;
            }
            McpObservedFile = Text;
            var Configuration = McpConfiguration.Parse(Text);
            ApplyMcpConfiguration(Configuration, Save: false);
            SetStatus($"MCP configuration reloaded · {McpHttp!.Endpoint}");
        }
        catch (Exception Error) when (IsMcpConfigurationError(Error))
        {
            if (McpDiagnostic != Error.Message)
            {
                SetMcpDiagnostic(Error.Message);
            }
        }
    }

    private void SetMcpDiagnostic(string Message)
    {
        McpDiagnostic = Message;
        SharpLog.Error("MCP", Message);
        SetStatus("MCP: " + Message);
        RefreshExtensionUi();
    }

    private static bool IsMcpConfigurationError(Exception Error)
    {
        return Error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            ArgumentException or HttpListenerException or InvalidOperationException;
    }

    private string McpToolbarLabel()
    {
        var Port = McpHttp?.Port ?? McpConfigurationValue.Port;
        return $"MCP : {Port}" + (McpHttp is null ? " · Offline" : string.Empty);
    }

    private void OpenMcpSettings()
    {
        if (McpSettingsOpen)
        {
            _nativeApi.CreateDockWindow(McpSettingsHost, "MCP Settings");
            return;
        }
        if (_nativeApi.CreateDockWindow(McpSettingsHost, "MCP Settings") == 0)
        {
            SetStatus("MCP Settings requires the document UI host");
            return;
        }
        McpSettingsOpen = true;
        McpDraftRevision = McpRevision;
        McpPortDraft = McpConfigurationValue.Port.ToString(CultureInfo.InvariantCulture);
        _nativeApi.SetHostMarkup(McpSettingsHost, "dockWindowTitle", "MCP Settings");
        _nativeApi.SetHostMarkup(McpSettingsHost, "workspace", $"""
            <div class="settings-window"><div class="settings-content">
            <div class="settings-header"><div class="settings-heading">MCP Settings</div><div class="grow" />
            <button id="mcp-cancel" class="settings-action" action="editor.mcp-cancel">Cancel</button>
            <button id="mcp-confirm" class="settings-action primary" action="editor.mcp-confirm">Confirm &amp; Restart</button></div>
            <div class="settings-fields"><div class="settings-field"><div class="settings-field-copy">
            <label>Port</label><span>Confirm to save and restart MCP immediately.</span></div>
            <div class="settings-control"><input id="mcp-port" type="text" value="{McpPortDraft}" data-change-action="editor.mcp-port:" /></div></div>
            <div class="settings-field"><div class="settings-field-copy"><label>Endpoint</label></div>
            <div id="mcp-endpoint" class="settings-control">{Escape(McpHttp?.Endpoint ?? "Offline")}</div></div>
            <div class="settings-field mcp-configuration-row"><div class="settings-field-copy"><label>Configuration file</label></div>
            <div class="settings-control mcp-configuration-path">{Escape(McpConfigurationPath)}</div></div></div>
            <div id="mcp-feedback" class="settings-footer"><span>{Escape(McpDiagnostic ?? "Ready")}</span></div>
            </div></div>
            """);
    }

    private bool HandleMcpAction(string HostId, string Action)
    {
        if (Action == "editor.mcp-open")
        {
            OpenMcpSettings();
            return true;
        }
        if ((HostId == McpSettingsHost && Action == "window.closed") || Action == "editor.mcp-cancel")
        {
            CloseMcpSettings();
            return true;
        }
        if (!Action.StartsWith("editor.mcp-", StringComparison.Ordinal))
        {
            return false;
        }
        if (!McpSettingsOpen)
        {
            return true;
        }
        if (Action.StartsWith("editor.mcp-port:", StringComparison.Ordinal))
        {
            // Keep the input alive throughout its Change event to retain focus.
            McpPortDraft = Action["editor.mcp-port:".Length..];
        }
        else if (Action == "editor.mcp-confirm")
        {
            var Valid = int.TryParse(McpPortDraft, NumberStyles.None, CultureInfo.InvariantCulture, out var Port);
            var Result = Valid ? ConfigureMcp(Port, McpDraftRevision) :
                McpFailure("MCP_PORT_INVALID", "Enter a port between 1 and 65535");
            if (Result["success"]?.GetValue<bool>() == true)
            {
                CloseMcpSettings();
            }
            else
            {
                var Message = Result["diagnostics"]?[0]?["message"]?.GetValue<string>() ?? "MCP configuration failed";
                _nativeApi.SetHostMarkup(McpSettingsHost, "mcp-feedback", $"<span>{Escape(Message)}</span>");
                SetStatus("MCP: " + Message);
            }
        }
        return true;
    }

    private void CloseMcpSettings()
    {
        McpSettingsOpen = false;
        _nativeApi.DestroyDockWindow(McpSettingsHost);
    }

    private void ShutdownMcp()
    {
        McpHttp?.Dispose();
        McpHttp = null;
        CloseMcpSettings();
        _mcpSession?.Dispose();
        _mcpSession = null;
    }
}
