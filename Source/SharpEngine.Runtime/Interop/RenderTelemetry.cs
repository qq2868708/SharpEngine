using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Diagnostics;
using SharpEngine.Assets;
using SharpEngine.World;

namespace SharpEngine.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRenderStats
{
    public ulong FramesBegun;
    public ulong FramesSubmitted;
    public ulong TriangleVertices;
    public ulong LineVertices;
    public uint Width;
    public uint Height;
    public byte HasFramebuffer;
    public uint Backend;
    public byte SupportsGpuScene;
    public byte VSyncEnabled;
    public byte ValidationEnabled;
}

public sealed unsafe class RenderTelemetry
{
    private const int SampleCapacity = 240;
    private readonly delegate* unmanaged[Cdecl]<NativeRenderStats*, byte>
        GetRenderStatsFunction;
    private readonly double[] FrameSamples = new double[SampleCapacity];
    private readonly double[] SceneSamples = new double[SampleCapacity];
    private int SampleCount;
    private int SampleCursor;
    private long PreviousFrameTimestamp;
    private ulong GpuSubmissionAttempts;
    private ulong GpuSubmittedFrames;
    private ulong CpuFallbackFrames;

    public RenderTelemetry(nint NativeApi)
    {
        var Table = (NativeEngineApiTable*)NativeApi;
        if (Table is not null && Table->Version >= 14)
        {
            GetRenderStatsFunction = Table->GetRenderStats;
        }
    }

    public void Record(float DeltaSeconds, double SceneMilliseconds,
        bool GpuAttempted, bool GpuSubmitted)
    {
        var Timestamp = Stopwatch.GetTimestamp();
        var FrameMilliseconds = PreviousFrameTimestamp == 0 ?
            (float.IsFinite(DeltaSeconds) && DeltaSeconds >= 0 ?
                DeltaSeconds * 1000.0 : 0) :
            Stopwatch.GetElapsedTime(PreviousFrameTimestamp, Timestamp)
                .TotalMilliseconds;
        PreviousFrameTimestamp = Timestamp;
        FrameSamples[SampleCursor] = FrameMilliseconds;
        SceneSamples[SampleCursor] = double.IsFinite(SceneMilliseconds) &&
            SceneMilliseconds >= 0 ? SceneMilliseconds : 0;
        SampleCursor = (SampleCursor + 1) % SampleCapacity;
        SampleCount = Math.Min(SampleCount + 1, SampleCapacity);
        if (GpuAttempted)
        {
            GpuSubmissionAttempts++;
        }
        if (GpuSubmitted)
        {
            GpuSubmittedFrames++;
        }
        else
        {
            CpuFallbackFrames++;
        }
    }

    public JsonObject Describe(AssetRegistry? Assets)
    {
        NativeRenderStats Native = default;
        var NativeAvailable = false;
        if (GetRenderStatsFunction != null)
        {
            NativeAvailable = GetRenderStatsFunction(&Native) != 0;
        }
        var Cache = Assets is null ? null : GpuSceneBuilder.DescribeCache(Assets);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["state"] = "transient",
            ["backend"] = NativeAvailable ? BackendName(Native.Backend) :
                "unavailable",
            ["vsync"] = NativeAvailable && Native.VSyncEnabled != 0,
            ["validation"] = NativeAvailable && Native.ValidationEnabled != 0,
            ["gpuScene"] = new JsonObject
            {
                ["supported"] = NativeAvailable && Native.SupportsGpuScene != 0,
                ["submissionAttempts"] = GpuSubmissionAttempts,
                ["submittedFrames"] = GpuSubmittedFrames,
                ["cpuFallbackFrames"] = CpuFallbackFrames,
            },
            ["cache"] = Cache is null ? null : new JsonObject
            {
                ["kind"] = "derived",
                ["hits"] = Cache.Hits,
                ["misses"] = Cache.Misses,
                ["rebuilds"] = Cache.Rebuilds,
                ["residentPayloads"] = Cache.ResidentPayloads,
                ["residentBytes"] = Cache.ResidentBytes,
            },
            ["timingMilliseconds"] = new JsonObject
            {
                ["sampleCount"] = SampleCount,
                ["frameAverage"] = Average(FrameSamples),
                ["frameP95"] = Percentile95(FrameSamples),
                ["sceneAverage"] = Average(SceneSamples),
                ["sceneP95"] = Percentile95(SceneSamples),
            },
            ["native"] = NativeAvailable ? new JsonObject
            {
                ["framesBegun"] = Native.FramesBegun,
                ["framesSubmitted"] = Native.FramesSubmitted,
                ["triangleVertices"] = Native.TriangleVertices,
                ["lineVertices"] = Native.LineVertices,
                ["width"] = Native.Width,
                ["height"] = Native.Height,
                ["hasFramebuffer"] = Native.HasFramebuffer != 0,
            } : null,
            ["diagnostics"] = NativeAvailable ? new JsonArray() :
                new JsonArray(new JsonObject
                {
                    ["code"] = "RENDER_TELEMETRY_UNAVAILABLE",
                    ["severity"] = "warning",
                    ["message"] = "The native host does not expose render telemetry",
                    ["retryable"] = false,
                }),
        };
    }

    private double Average(IReadOnlyList<double> Samples)
    {
        if (SampleCount == 0)
        {
            return 0;
        }
        var Total = 0.0;
        for (var Index = 0; Index < SampleCount; Index++)
        {
            Total += Samples[Index];
        }
        return Total / SampleCount;
    }

    private double Percentile95(IReadOnlyList<double> Samples)
    {
        if (SampleCount == 0)
        {
            return 0;
        }
        var Ordered = Samples.Take(SampleCount).Order().ToArray();
        var Index = (int)Math.Ceiling(Ordered.Length * 0.95) - 1;
        return Ordered[Math.Clamp(Index, 0, Ordered.Length - 1)];
    }

    private static string BackendName(uint Backend) => Backend switch
    {
        0 => "opengl",
        1 => "vulkan",
        2 => "offscreen",
        3 => "null",
        _ => "unknown",
    };
}
