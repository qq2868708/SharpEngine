using System.Runtime.InteropServices;
using SharpEngine.UI;

namespace SharpEngine.Rendering;

/// <summary>Native vertex ABI: viewport-local pixels, 0..1 depth and RGBA.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ViewportVertex(float X, float Y, float Z, float R, float G, float B, float A);

/// <summary>Presentation data independent of scenes, assets and editor state.</summary>
public record ViewportFrame(UiRect Bounds, ViewportVertex[] Triangles, ViewportVertex[] Lines)
{
    public int TriangleCount
    {
        get
        {
            return Triangles.Length / 3;
        }
    }
    public int LineCount
    {
        get
        {
            return Lines.Length / 2;
        }
    }
    public IReadOnlyList<string> Diagnostics
    {
        get;
        init;
    } = Array.Empty<string>();
}
