using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;

internal static class TimelinePreviewValidation
{
    internal static void Run(
        Action<bool, string> Require,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call)
    {
        var Open = Tool(500, "editor.action", new JsonObject
        {
            ["action"] = "editor.open-asset:engine://animations/uefn-mannequin-walk",
        });
        var OpenState = Structured(Open);
        Require(OpenState["assetTools"]?["lastResult"]?["success"]?.GetValue<bool>() == true,
            "Timeline preview validation must open the UEFN mannequin walk asset");
        var Editor = OpenState["assetTools"]?["editors"]?.AsArray()
            .Select(Node => Node!.AsObject()).Single(Node =>
                Node["assetId"]!.GetValue<string>() ==
                "engine://animations/uefn-mannequin-walk") ??
            throw new InvalidDataException("Timeline asset editor state was unavailable");
        Require(Editor["editorId"]!.GetValue<string>() == "skeletal-animation-editor",
            "SkeletalAnimation assets use their dedicated editor extension");
        var RootId = Editor["rootId"]!.GetValue<string>();
        var OpenUi = Structured(Tool(5001, "ui.describe", new JsonObject()));
        var RootHost = OpenUi["hosts"]!.AsArray().Select(Node => Node!.AsObject())
            .Single(Host => Host["hostId"]!.GetValue<string>() == RootId);
        Require(RootHost["rootId"]!.GetValue<string>() == RootId &&
            RootHost["kind"]!.GetValue<string>() == "top-level-editor",
            "Timeline must open as a top-level editor root with its own native host");
        TimelineViewportUiValidation.Run(RootId, Require, Tool, Call);

        _ = Tool(501, "editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:editor.asset-command:stop",
        });
        Thread.Sleep(200);
        var Before = Capture(502, RootId, Tool, Call);
        Require(Before.Width >= 300 && Before.Height >= 250,
            "Timeline preview viewport must remain the primary asset-editor surface");
        Require(Before.Pixels.Where((Value, Index) => Index % 3 == 0 &&
                (Value < 9 || Value > 24)).Any(),
            "Timeline preview viewport must contain rendered mesh pixels");

        var Play = Tool(503, "editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:editor.asset-command:play",
        });
        Require(Structured(Play)["assetTools"]?["editors"]?.AsArray().Any(EditorNode =>
                EditorNode?["assetId"]?.GetValue<string>() ==
                    "engine://animations/uefn-mannequin-walk" &&
                EditorNode?["state"]?["playing"]?.GetValue<bool>() == true) == true,
            "Timeline play command must enter the playing state");
        Thread.Sleep(450);
        var Middle = Capture(504, RootId, Tool, Call);
        var CapturePath = Environment.GetEnvironmentVariable(
            "SHARPENGINE_TIMELINE_CAPTURE");
        if (!string.IsNullOrWhiteSpace(CapturePath))
        {
            var FullPath = Path.GetFullPath(CapturePath);
            Directory.CreateDirectory(Path.GetDirectoryName(FullPath)!);
            File.WriteAllBytes(FullPath, Middle.FramePng);
        }
        var MiddleState = Structured(Tool(505, "editor.describe", new JsonObject()));
        var CurrentTime = MiddleState["assetTools"]?["editors"]?.AsArray()
            .Select(EditorNode => EditorNode?["state"]?["currentTime"]?.GetValue<double>())
            .FirstOrDefault(Value => Value is not null) ?? 0;
        Require(CurrentTime > 0.2 && CurrentTime < 1.2,
            "Timeline play command must advance the document time");
        Require(!Before.Pixels.SequenceEqual(Middle.Pixels),
            "Timeline playback must update the rendered 3D preview geometry");
        var SecondOpen = Structured(Tool(5053, "editor.action", new JsonObject
        {
            ["action"] = "editor.open-asset:engine://animations/uefn-mannequin-idle",
        }));
        var SecondEditor = SecondOpen["assetTools"]!["editors"]!.AsArray().Single(Item =>
            Item!["assetId"]!.GetValue<string>() == "engine://animations/uefn-mannequin-idle")!;
        var SecondRoot = SecondEditor["rootId"]!.GetValue<string>();
        Require(SecondRoot != RootId && !SecondEditor["state"]!["playing"]!.GetValue<bool>() &&
            SecondEditor["state"]!["currentTime"]!.GetValue<double>() == 0,
            "Two animation editors share definitions but retain independent clocks and roots");
        var Detached = Structured(Tool(5051, "editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:editor.dock-drop:details:{RootId}:none:none:detach:-1",
        }));
        var FloatingId = Detached["assetTools"]?["editors"]?.AsArray()
            .Select(EditorNode => EditorNode!.AsObject()).Single(EditorNode =>
                EditorNode["rootId"]!.GetValue<string>() == RootId)
            ["floating"]?[0]?["hostId"]?.GetValue<string>() ??
            throw new InvalidDataException("Detached Details host was unavailable");
        var DetachedUi = Structured(Tool(5052, "ui.describe", new JsonObject()));
        var FloatingHost = DetachedUi["hosts"]!.AsArray().Select(Node => Node!.AsObject())
            .Single(Host => Host["hostId"]!.GetValue<string>() == FloatingId);
        Require(FloatingHost["rootId"]!.GetValue<string>() == RootId &&
            FloatingHost["kind"]!.GetValue<string>() == "floating-tab",
            "Detached Details must remain an owner-scoped floating tab");
        var Closed = Structured(Tool(506, "editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:window.close-requested",
        }));
        Require(Closed["assetTools"]?["editors"]?.AsArray().All(EditorNode =>
                EditorNode?["rootId"]?.GetValue<string>() != RootId) == true,
            "Closing a clean asset editor must destroy its editor root state");
        var ClosedUi = Structured(Tool(507, "ui.describe", new JsonObject()));
        Require(ClosedUi["hosts"]?.AsArray().All(HostNode =>
                HostNode?["hostId"]?.GetValue<string>() != RootId) == true,
            "Closing a clean asset editor must destroy its native host");
        var Remaining = Closed["assetTools"]!["editors"]!.AsArray().Single(Item =>
            Item!["rootId"]!.GetValue<string>() == SecondRoot)!;
        Require(Remaining["state"]!["currentTime"]!.GetValue<double>() == 0 &&
            !Remaining["state"]!["playing"]!.GetValue<bool>(),
            "Closing one animation editor does not advance or stop another instance's clock");
        _ = Tool(508, "editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{SecondRoot}:window.close-requested",
        });
    }

    private static PreviewCapture Capture(
        int RequestId,
        string RootId,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call)
    {
        var Snapshot = Structured(Tool(RequestId, "ui.describe", new JsonObject()));
        var Host = Snapshot["hosts"]!.AsArray().Select(Node => Node!.AsObject())
            .First(HostValue => HostValue["hostId"]!.GetValue<string>() == RootId);
        var Element = Host["elements"]!.AsArray().Select(Node => Node!.AsObject())
            .First(ElementValue => ElementValue["semanticId"]?.GetValue<string>() ==
                "timelinePreviewViewport");
        var FrameId = Snapshot["frameId"]!.GetValue<ulong>();
        var HostId = Host["hostId"]!.GetValue<string>();
        var Frame = Call(RequestId + 100, "resources/read", new JsonObject
        {
            ["uri"] = $"sharpengine://ui/frames/{FrameId}/{HostId}.png",
        });
        var Blob = Frame["result"]?["contents"]?[0]?["blob"]?.GetValue<string>() ??
            throw new InvalidDataException("Timeline preview frame was unavailable");
        var EncodedPng = Convert.FromBase64String(Blob);
        var Png = DecodePng(EncodedPng);
        var Bounds = Element["bounds"]!.AsObject();
        var X = (int)Math.Floor(Bounds["x"]!.GetValue<double>());
        var Y = (int)Math.Floor(Bounds["y"]!.GetValue<double>());
        var Width = (int)Math.Ceiling(Bounds["width"]!.GetValue<double>());
        var Height = (int)Math.Ceiling(Bounds["height"]!.GetValue<double>());
        var Pixels = new byte[checked(Width * Height * 3)];
        var Offset = 0;
        for (var Row = 0; Row < Height; Row++)
        {
            for (var Column = 0; Column < Width; Column++)
            {
                var Pixel = Png.Pixel(X + Column, Y + Row);
                Pixels[Offset++] = Pixel.R;
                Pixels[Offset++] = Pixel.G;
                Pixels[Offset++] = Pixel.B;
            }
        }
        return new PreviewCapture(Pixels, Width, Height, EncodedPng);
    }

    private static JsonObject Structured(JsonObject Response) =>
        Response["result"]?["structuredContent"]?.AsObject() ??
        throw new InvalidDataException($"Tool response had no structured content: {Response}");

    private static DecodedPng DecodePng(byte[] Png)
    {
        var Offset = 8;
        uint Width = 0;
        uint Height = 0;
        using var Compressed = new MemoryStream();
        while (Offset + 12 <= Png.Length)
        {
            var Length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(Png.AsSpan(Offset, 4)));
            var Type = System.Text.Encoding.ASCII.GetString(Png, Offset + 4, 4);
            var Data = Png.AsSpan(Offset + 8, Length);
            if (Type == "IHDR")
            {
                Width = BinaryPrimitives.ReadUInt32BigEndian(Data);
                Height = BinaryPrimitives.ReadUInt32BigEndian(Data[4..]);
            }
            else if (Type == "IDAT")
            {
                Compressed.Write(Data);
            }
            else if (Type == "IEND")
            {
                break;
            }
            Offset += 12 + Length;
        }
        Compressed.Position = 0;
        using var Zlib = new ZLibStream(Compressed, CompressionMode.Decompress);
        using var Raw = new MemoryStream();
        Zlib.CopyTo(Raw);
        var Scanlines = Raw.ToArray();
        var RowBytes = checked((int)Width * 4);
        var Rgba = new byte[checked(RowBytes * (int)Height)];
        for (var Row = 0; Row < Height; Row++)
        {
            var Source = checked((int)Row * (RowBytes + 1));
            if (Scanlines[Source] != 0)
            {
                throw new InvalidDataException("Timeline preview PNG used a filtered row");
            }
            Buffer.BlockCopy(Scanlines, Source + 1, Rgba, checked((int)Row * RowBytes), RowBytes);
        }
        return new DecodedPng(Width, Height, Rgba);
    }

    private readonly record struct PreviewCapture(byte[] Pixels, int Width, int Height,
        byte[] FramePng);

    private readonly record struct DecodedPng(uint Width, uint Height, byte[] Rgba)
    {
        internal (byte R, byte G, byte B) Pixel(int X, int Y)
        {
            if (X < 0 || Y < 0 || X >= Width || Y >= Height)
            {
                throw new ArgumentOutOfRangeException();
            }
            var Offset = checked(((int)Width * Y + X) * 4);
            return (Rgba[Offset], Rgba[Offset + 1], Rgba[Offset + 2]);
        }
    }
}
