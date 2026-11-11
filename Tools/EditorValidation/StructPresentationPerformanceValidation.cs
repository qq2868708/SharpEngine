using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Editor;

internal static class StructPresentationPerformanceValidation
{
    internal static void Run(EditorRuntime Runtime, string Folder, Action<bool, string> Check)
    {
        Runtime.RegisteredDataAssetTypes.Register<PresentationPerformanceAsset>();
        var Asset = new AssetDescriptor
        {
            Id = "project://validation/struct-performance",
            Type = "Validation.StructPresentationPerformance",
            Name = "Struct presentation performance",
            Source = AssetSource.Project,
        };
        var PathValue = Path.Combine(Folder, "StructPerformance.sdataasset");
        File.WriteAllText(PathValue, DataAssetSerializer.Serialize(new PresentationPerformanceAsset(), Asset.Id, Asset.Name));
        using var Editor = new DataAssetEditorInstance(new EditorAssetOpenContext(Runtime, Asset,
            PathValue, "data-asset-editor", "struct-performance"));
        var Before = GC.GetAllocatedBytesForCurrentThread();
        var Started = Stopwatch.GetTimestamp();
        var Markup = Editor.BuildPaneMarkup("details");
        var ColdMilliseconds = Stopwatch.GetElapsedTime(Started).TotalMilliseconds;
        var ColdAllocated = GC.GetAllocatedBytesForCurrentThread() - Before;
        var Xml = XElement.Parse(Markup);
        Check(Xml.Descendants("input").Count() == 128 * 6,
            "The large-list workload actually expands all 128 nested structs and their 768 inputs");
        Check(ColdAllocated < 180L * 1024 * 1024,
            $"First expanded-list rendering allocates less than 180 MiB, observed {ColdAllocated} bytes");
        var Toggle = Xml.Descendants("button").First(Element =>
            Element.Attribute("class")?.Value.Split(' ').Contains("se-struct-toggle") == true)
            .Attribute("data-action")!.Value;
        var Samples = new List<double>();
        var Allocations = new List<long>();
        for (var Index = 0; Index < 6; Index++)
        {
            Editor.TryHandleAction(Toggle);
            _ = Editor.BuildPaneMarkup("details");
            Before = GC.GetAllocatedBytesForCurrentThread();
            Started = Stopwatch.GetTimestamp();
            Editor.TryHandleAction(Toggle);
            Markup = Editor.BuildPaneMarkup("details");
            Samples.Add(Stopwatch.GetElapsedTime(Started).TotalMilliseconds);
            Allocations.Add(GC.GetAllocatedBytesForCurrentThread() - Before);
        }
        Check(Allocations.Max() < 16L * 1024 * 1024,
            $"Expanding one of 128 structs allocates less than 16 MiB on every sample, observed {Allocations.Max()} bytes");
        var Ordered = Samples.Order().ToArray();
        var Median = (Ordered[2] + Ordered[3]) / 2;
        Check(Median <= 18 && Samples.Max() <= 25,
            $"128-item warmed fold rebuild stays within median 18 ms / P95 25 ms: {Median:0.###}/{Samples.Max():0.###} ms");
        for (var Index = 0; Index < 100; Index++)
        {
            _ = Editor.BuildPaneMarkup("details");
        }
        Before = GC.GetAllocatedBytesForCurrentThread();
        var Stable = true;
        for (var Index = 0; Index < 10000; Index++)
        {
            Stable &= ReferenceEquals(Markup, Editor.BuildPaneMarkup("details"));
        }
        var CachedAllocated = GC.GetAllocatedBytesForCurrentThread() - Before;
        Check(Stable && CachedAllocated == 0,
            $"10,000 unchanged large-list reads reuse the same markup with zero allocations: stable={Stable}, bytes={CachedAllocated}");
        Check(!Editor.IsDirty && !Editor.CanExecuteCommand("undo"),
            "Repeated large-list folding leaves authoritative state, dirty state and history untouched");
        var Input = XElement.Parse(Markup).Descendants("input").First(Element =>
            Element.Attribute("id")?.Value.EndsWith("-power", StringComparison.Ordinal) == true);
        var Action = Input.Attribute("data-value-commit")!.Value;
        Editor.TryHandleAction(Action + "999");
        string DisplayedPower()
        {
            return XElement.Parse(Editor.BuildPaneMarkup("details")).Descendants("input").Single(Element =>
                Element.Attribute("id")?.Value == Input.Attribute("id")!.Value).Attribute("value")!.Value;
        }
        Check(DisplayedPower() == "999", "An edit invalidates cached rows and publishes the latest committed value");
        Editor.TryHandleAction(Action + "888");
        Check(DisplayedPower() == "999", "Fingerprint reuse during rendering never permits stale actions after a data edit");
        Editor.ExecuteCommand("undo");
        Check(DisplayedPower() == "0" && !Editor.IsDirty, "Undo rebuilds the original rows and clean savepoint");
        Editor.ExecuteCommand("redo");
        Check(DisplayedPower() == "999", "Redo rebuilds the edited rows");
        Editor.Save();
        Check(!Editor.IsDirty && DisplayedPower() == "999" &&
            JsonNode.Parse(File.ReadAllText(PathValue))!["entries"]![0]!["power"]!.GetValue<int>() == 999,
            "Saving retains the current presentation and writes the committed value");
        Console.WriteLine($"Struct presentation (128 items/768 inputs): cold {ColdMilliseconds:0.###} ms/{ColdAllocated} bytes; " +
            $"expand median {Median:0.###} ms, max {Samples.Max():0.###} ms/{Allocations.Max()} bytes; cached reads {CachedAllocated} bytes");
    }
}

[DataAssetType("Validation.StructPresentationPerformance")]
public sealed class PresentationPerformanceAsset : DataAsset
{
    public List<PresentationPerformanceValue> Entries
    {
        get;
        set;
    } = Enumerable.Range(0, 128).Select(Index => new PresentationPerformanceValue
    {
        Power = Index,
        Offset = new Vector3(1, 2, 3),
        Child = new PresentationPerformanceChild
        {
            Title = "Nested " + Index,
            Enabled = true,
        },
    }).ToList();
}

public struct PresentationPerformanceValue
{
    public int Power
    {
        get;
        set;
    }

    public Vector3 Offset
    {
        get;
        set;
    }

    public PresentationPerformanceChild Child
    {
        get;
        set;
    }
}

public struct PresentationPerformanceChild
{
    public string Title
    {
        get;
        set;
    }

    public bool Enabled
    {
        get;
        set;
    }
}
