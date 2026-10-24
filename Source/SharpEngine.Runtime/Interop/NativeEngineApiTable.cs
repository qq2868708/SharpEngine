using System.Runtime.InteropServices;
namespace SharpEngine.Interop;
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeEngineApiTable
    {
        public uint Version;
        public delegate* unmanaged[Cdecl]<nint, byte*, void>
            SetWindowTitle;
        public delegate* unmanaged[Cdecl]<nint, uint, uint, void> BeginUi;
        public delegate* unmanaged[Cdecl]<float, float, float, float,
            byte, byte, byte, byte, void> UiPanel;
        public delegate* unmanaged[Cdecl]<float, float, float, float, byte*,
            byte, byte, byte, byte, void> UiText;
        public delegate* unmanaged[Cdecl]<float, float, float, float, byte*,
            byte, byte, byte, byte, byte, void> UiButton;
        public delegate* unmanaged[Cdecl]<nint, void> EndUi;
        public delegate* unmanaged[Cdecl]<float, float, float, float, byte*,
            byte, byte, byte, byte, byte, void> UiInput;
        public delegate* unmanaged[Cdecl]<byte*, byte*, void> SetUiText;
        public delegate* unmanaged[Cdecl]<float*, float*, float*, float*, byte> GetViewportRect;
        public delegate* unmanaged[Cdecl]<float*, uint, float*, uint, void> SubmitViewportFrame;
        public delegate* unmanaged[Cdecl]<byte*, byte*, void> SetUiMarkup;
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, void> SetUiProperty;
        public delegate* unmanaged[Cdecl]<nint, int, int, uint, uint, int, void> ViewportInput;
        public delegate* unmanaged[Cdecl]<byte*, byte*, nint> CreateDockWindow;
        public delegate* unmanaged[Cdecl]<byte*, byte> DestroyDockWindow;
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte> SetHostMarkup;
        public delegate* unmanaged[Cdecl]<nint, byte*, byte*, uint, byte> ChooseWorldSavePath;
        public delegate* unmanaged[Cdecl]<ulong, ulong, byte*, uint, uint> GetUiSnapshot;
        public delegate* unmanaged[Cdecl]<byte*, uint, int, int, uint, uint, uint,
            uint, ulong, float, float, byte*, byte*, uint, uint> UiPointer;
        public delegate* unmanaged[Cdecl]<byte*, uint, uint, uint, byte*, ulong,
            byte*, uint, uint> UiKey;
        public delegate* unmanaged[Cdecl]<ulong, byte*, byte*, uint, uint*, uint*, uint>
            ReadUiFrame;
        public delegate* unmanaged[Cdecl]<int, byte*, byte*, void> ReportStartupProgress;
        public delegate* unmanaged[Cdecl]<byte*, float*, float*, float*, float*, byte> GetUiElementRect;
        public delegate* unmanaged[Cdecl]<float*, uint, float*, uint, void> SubmitTimelinePreviewFrame;
        public delegate* unmanaged[Cdecl]<byte*, byte*, nint> CreateEditorWindow;
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, nint> CreateOwnedDockWindow;
        public delegate* unmanaged[Cdecl]<byte*, byte> DestroyEditorWindow;
        public delegate* unmanaged[Cdecl]<byte*, byte*, float*, float*, float*, float*, byte>
            GetHostUiElementRect;
        public delegate* unmanaged[Cdecl]<byte*, byte*, float*, uint, float*, uint, void>
            SubmitHostViewportFrame;
        public delegate* unmanaged[Cdecl]<NativeGpuMeshUpload*, byte> EnsureGpuMesh;
        public delegate* unmanaged[Cdecl]<NativeGpuSceneFrame*, byte> SubmitGpuScene;
        public delegate* unmanaged[Cdecl]<NativeRenderStats*, byte> GetRenderStats;
        public delegate* unmanaged[Cdecl]<byte, byte> SetGameplayInputCapture;
        public delegate* unmanaged[Cdecl]<byte> GetGameplayInputCapture;
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, byte>
            SetHostUiProperty;
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte> SetUiDocument;
        public delegate* unmanaged[Cdecl]<byte> ClearUiDocument;
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, float, float, byte>
            SetHostUiDocument;
    }
