using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.UI;

namespace SharpEngine.EditorValidation;

/// <summary>Runtime screen selection is code-driven: a project Module names the asset,
/// the runtime resolves it and hands RML text plus the authoritative path to the
/// presenting host. These checks cover the contract without a native window.</summary>
internal static class UiScreenValidation
{
    internal sealed class RecordingHost : IUiScreenHost
    {
        public string? Rml
        {
            get;
            private set;
        }

        public string? SourcePath
        {
            get;
            private set;
        }

        public int ClearCount
        {
            get;
            private set;
        }

        public bool Reject;

        public bool SetDocument(string Rml, string SourcePath, out string Message)
        {
            Message = string.Empty;
            if (Reject)
            {
                Message = "host refused";
                return false;
            }
            this.Rml = Rml;
            this.SourcePath = SourcePath;
            return true;
        }

        public bool ClearDocument()
        {
            ClearCount++;
            return true;
        }
    }

    internal static void Run(string Root, AssetRegistry Assets, IUiRuntime EditorUi,
        Action<bool, string> Check)
    {
        // The Editor does not present runtime game screens, so it must report the
        // capability gap instead of silently accepting the call.
        var EditorResult = EditorUi.ShowScreen("project://ui/main");
        Check(!EditorResult.Success && EditorResult.Code == "UI_SCREEN_UNAVAILABLE",
            $"An editor-only host reports runtime screen unavailability: {EditorResult.Code}");
        Check(EditorUi.ActiveScreenId is null,
            "A rejected screen is never recorded as active");

        var Project = Path.Combine(Root, "UiScreenValidation");
        var UiDirectory = Directory.CreateDirectory(Path.Combine(Project, "Content", "UI"));
        const string PackageRml = """
            <rml><head><link type="text/rcss" href="Screen.rcss" /></head>
            <body><div id="root"><span id="label">Runtime screen</span></div></body></rml>
            """;
        File.WriteAllText(Path.Combine(UiDirectory.FullName, "Screen.rcss"),
            ".root { display: flex; }");
        var PackagePath = Path.Combine(UiDirectory.FullName, "Screen.sui");
        File.WriteAllText(PackagePath,
            UiDocumentAsset.Create("Runtime Screen", PackageRml).Serialize());
        var RmlPath = Path.Combine(UiDirectory.FullName, "Plain.rml");
        File.WriteAllText(RmlPath, PackageRml);

        var Service = new UiRuntimeService();
        var Host = new RecordingHost();
        Service.AttachScreenHost(Host, Assets);

        var Shown = Service.ShowScreen("project://ui/screen");
        Check(Shown.Success && Shown.Code == "UI_SCREEN_SHOWN" &&
            Shown.AssetId == "project://ui/screen" &&
            Host.Rml is not null && Host.Rml.Contains("id=\"label\"", StringComparison.Ordinal),
            $"A package screen is presented as its inline RML: {Shown.Code}");
        Check(Host.SourcePath is not null &&
            Host.SourcePath.EndsWith("Screen.sui", StringComparison.OrdinalIgnoreCase) &&
            Path.IsPathRooted(Host.SourcePath),
            "The presented screen carries its resolved absolute source path");
        Check(Service.ActiveScreenId == "project://ui/screen",
            "The active screen id is exposed as transient runtime state");

        var Plain = Service.ShowScreen("project://ui/plain");
        Check(Plain.Success && Host.SourcePath!.EndsWith("Plain.rml",
                StringComparison.OrdinalIgnoreCase),
            "A bare RML document is presented from its file text");

        var Unsupported = Service.ShowScreen("project://worlds/main");
        Check(!Unsupported.Success && Unsupported.Code == "UI_SCREEN_TYPE_UNSUPPORTED" &&
            Service.ActiveScreenId == "project://ui/plain",
            "An unsupported asset type is rejected and keeps the current screen");
        var Missing = Service.ShowScreen("project://ui/does-not-exist");
        Check(!Missing.Success && Missing.Code == "UI_SCREEN_ASSET_MISSING",
            "An unknown asset id is reported as missing");
        var Empty = Service.ShowScreen("  ");
        Check(!Empty.Success && Empty.Code == "UI_SCREEN_ASSET_INVALID",
            "A blank asset id is rejected before any lookup");

        Host.Reject = true;
        var Refused = Service.ShowScreen("project://ui/screen");
        Check(!Refused.Success && Refused.Code == "UI_SCREEN_PRESENT_FAILED" &&
            Refused.Message.Contains("host refused", StringComparison.Ordinal) &&
            Service.ActiveScreenId == "project://ui/plain",
            "A host refusal is surfaced and does not replace the active screen");
        Host.Reject = false;

        Service.ClearScreen();
        Check(Service.ActiveScreenId is null && Host.ClearCount == 1,
            "Clearing the screen releases the host document");
        Service.ClearScreen();
        Check(Host.ClearCount == 1,
            "Clearing twice does not unload an already cleared screen");

        var ThreadError = string.Empty;
        try
        {
            Task.Run(() => Service.ShowScreen("project://ui/screen")).GetAwaiter().GetResult();
        }
        catch (Exception Error)
        {
            ThreadError = Error.GetBaseException().Message;
        }
        Check(ThreadError.Contains("engine thread", StringComparison.Ordinal),
            $"Runtime screen selection must stay on the engine thread: {ThreadError}");
    }
}
