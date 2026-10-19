using System.Text;

namespace SharpEngine.Runtime.Interop;

// Transient, startup-scoped bridge; older native hosts simply have no callback.
internal unsafe readonly struct NativeStartupProgress
{
    private readonly delegate* unmanaged[Cdecl]<int, byte*, byte*, void> Callback;

    internal NativeStartupProgress(nint NativeApi)
    {
        var Table = (NativeEngineApiTable*)NativeApi;
        Callback = Table != null && Table->Version >= 10 ? Table->ReportStartupProgress : null;
    }

    internal void Report(int Percent, string Module, string Detail)
    {
        if (Callback == null)
        {
            return;
        }
        var ModuleUtf8 = Encoding.UTF8.GetBytes(Module + '\0');
        var DetailUtf8 = Encoding.UTF8.GetBytes(Detail + '\0');
        fixed (byte* ModulePointer = ModuleUtf8)
        fixed (byte* DetailPointer = DetailUtf8)
        {
            Callback(Percent, ModulePointer, DetailPointer);
        }
    }
}
