using System.Text;
using SharpEngine.Interop;
using SharpEngine.UI;

namespace SharpEngine.Host;

/// <summary>Presents runtime UI screens through the native game shell. The managed
/// host owns asset resolution; native owns RmlUi. This keeps one authoritative source
/// (the project asset) and a transient presented document.</summary>
internal sealed unsafe class NativeUiScreenHost : IUiScreenHost
{
    private readonly NativeEngineApiTable* Api;

    internal NativeUiScreenHost(nint Api)
    {
        this.Api = (NativeEngineApiTable*)Api;
    }

    public bool SetDocument(string Rml, string SourcePath, out string Message)
    {
        Message = string.Empty;
        if (Api is null || Api->Version < 19 || Api->SetUiDocument is null)
        {
            Message = "The native host does not expose runtime UI screens";
            return false;
        }

        var RmlUtf8 = Encoding.UTF8.GetBytes(Rml + "\0");
        var PathUtf8 = Encoding.UTF8.GetBytes(SourcePath + "\0");
        fixed (byte* RmlPointer = RmlUtf8)
        fixed (byte* PathPointer = PathUtf8)
        {
            if (Api->SetUiDocument(RmlPointer, PathPointer) == 0)
            {
                Message = "The native UI shell rejected the document";
                return false;
            }
        }
        return true;
    }

    public bool ClearDocument()
    {
        return Api is not null && Api->Version >= 19 && Api->ClearUiDocument is not null &&
            Api->ClearUiDocument() != 0;
    }
}
