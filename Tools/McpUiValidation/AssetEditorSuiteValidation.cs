using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;

internal static class AssetEditorSuiteValidation
{
    internal static void Run(string Root, Action<bool, string> Require,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call)
    {
        var Output = Path.Combine(Root, "Saved", "Validation", "AssetEditors");
        Directory.CreateDirectory(Output);
        var RequestId = 700;
        string? SharedRoot = null;

        ValidateEditor("engine://meshes/sphere", "meshPreviewViewport", true,
            "mesh", ref RequestId, KeepOpen: true);
        ValidateEditor("engine://skeletons/uefn-mannequin", "skeletonPreviewViewport",
            true, "skeleton", ref RequestId);
        ValidateEditor("engine://materials/default", "materialPreviewViewport", true,
            "material", ref RequestId);
        ValidateEditor("engine://prefabs/capsule-character", "prefabPreviewViewport", true,
            "prefab", ref RequestId);
        ValidateEditor("engine://materials/default", "source", false,
            "material-source", ref RequestId, DetailedPaneId: "source",
            ActivatePaneId: "source", MinimumDetailedRows: 20);
        ValidateEditor("engine://ui/editor-style", "assetEditorToolbar", false,
            "stylesheet", ref RequestId, DetailedPaneId: "source");
        ValidateEditor("engine://input/contexts/demo-gameplay", "assetEditorToolbar", false,
            "input-context", ref RequestId);
        ValidateEditor("engine://animation-state-machines/uefn-mannequin-locomotion", "animationStateMachineGraph", false,
            "animation-state-machine", ref RequestId);
        _ = Tool(RequestId++, "editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{SharedRoot}:window.close-requested",
        });

        void ValidateEditor(string AssetId, string SemanticId, bool InspectViewport,
            string EvidenceName, ref int Id, string? ExpectedActionPrefix = null,
            string? DetailedPaneId = null, string? ActivatePaneId = null,
            int MinimumDetailedRows = 50, bool KeepOpen = false)
        {
            var Opened = Structured(Tool(Id++, "editor.action", new JsonObject
            {
                ["action"] = "editor.open-asset:" + AssetId,
            }));
            var Editor = Opened["assetTools"]!["editors"]!.AsArray()
                .Select(Node => Node!.AsObject()).Single(Node =>
                    Node["assetId"]!.GetValue<string>() == AssetId);
            var RootId = Editor["rootId"]!.GetValue<string>();
            if (EvidenceName == "animation-state-machine")
            {
                var State = Editor["state"]!.AsObject();
                Require(Editor["openerId"]!.GetValue<string>() == "engine.animation-state-machine-editor" &&
                    State["schemaVersion"]!.GetValue<int>() == 3 && State["stateCount"]!.GetValue<int>() > 0 &&
                    State["readOnly"]!.GetValue<bool>() && !State.ContainsKey("previewWorld"),
                    "The Animation module presents a read-only state graph without allocating a placeholder World");
            }
            if (ActivatePaneId is not null)
            {
                _ = Tool(Id++, "editor.action", new JsonObject
                {
                    ["action"] = $"editor.host-action:{RootId}:editor.dock-activate:{RootId}:asset.bottom.tabs:{ActivatePaneId}",
                });
            }
            Thread.Sleep(180);
            var Snapshot = Structured(Tool(Id++, "ui.describe", new JsonObject()));
            var Host = Snapshot["hosts"]!.AsArray().Select(Node => Node!.AsObject())
                .Single(Node => Node["hostId"]!.GetValue<string>() == RootId);
            Require(Host["rootId"]!.GetValue<string>() == RootId &&
                Host["kind"]!.GetValue<string>() == "top-level-editor",
                $"{AssetId} must open in its own top-level editor root");
            var Elements = Host["elements"]!.AsArray().Select(Node => Node!.AsObject())
                .ToArray();
            File.WriteAllText(Path.Combine(Output, EvidenceName + ".json"),
                Host.ToJsonString(new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerOptions.Default)
                {
                    WriteIndented = true,
                }));
            var Target = Elements.FirstOrDefault(Element =>
                Element["semanticId"]?.GetValue<string>() == SemanticId);
            Require(Target is not null && Target["visible"]!.GetValue<bool>(),
                $"{AssetId} must expose visible editor content '{SemanticId}'");
            if (ExpectedActionPrefix is not null)
            {
                Require(Elements.Any(Element => Element["action"]?.GetValue<string>()?
                        .StartsWith(ExpectedActionPrefix, StringComparison.Ordinal) == true),
                    $"{AssetId} must expose structured authoring actions");
            }

            var FrameId = Snapshot["frameId"]!.GetValue<ulong>();
            var Frame = Call(Id++, "resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{FrameId}/{RootId}.png",
            });
            var Blob = Frame["result"]?["contents"]?[0]?["blob"]?.GetValue<string>() ??
                throw new InvalidDataException($"Frame unavailable for {AssetId}");
            var Bytes = Convert.FromBase64String(Blob);
            File.WriteAllBytes(Path.Combine(Output, EvidenceName + ".png"), Bytes);
            var Image = DecodePng(Bytes);
            if (InspectViewport)
            {
                AssetViewportUiValidation.Run(RootId, SemanticId, Require, Tool);
                var Bounds = Target!["bounds"]!.AsObject();
                var X = (int)Math.Floor(Bounds["x"]!.GetValue<double>());
                var Y = (int)Math.Floor(Bounds["y"]!.GetValue<double>());
                var Width = (int)Math.Ceiling(Bounds["width"]!.GetValue<double>());
                var Height = (int)Math.Ceiling(Bounds["height"]!.GetValue<double>());
                var Colors = new HashSet<int>();
                for (var Row = 0; Row < Height; Row += 3)
                {
                    for (var Column = 0; Column < Width; Column += 3)
                    {
                        var Pixel = Image.Pixel(X + Column, Y + Row);
                        Colors.Add(Pixel.R << 16 | Pixel.G << 8 | Pixel.B);
                    }
                }
                Require(Colors.Count > 20,
                    $"{AssetId} preview must contain rendered geometry pixels");
            }
            if (DetailedPaneId is not null)
            {
                var Pane = Elements.Single(Element =>
                    Element["role"]?.GetValue<string>() == "pane" &&
                    Element["semanticId"]?.GetValue<string>() == DetailedPaneId);
                var Bounds = Pane["bounds"]!.AsObject();
                var X = (int)Math.Floor(Bounds["x"]!.GetValue<double>());
                var Y = (int)Math.Floor(Bounds["y"]!.GetValue<double>());
                var Width = (int)Math.Ceiling(Bounds["width"]!.GetValue<double>());
                var Height = (int)Math.Ceiling(Bounds["height"]!.GetValue<double>());
                var DetailedRows = 0;
                for (var Row = 0; Row < Height; Row++)
                {
                    var Colors = new HashSet<int>();
                    for (var Column = 0; Column < Width; Column += 3)
                    {
                        var Pixel = Image.Pixel(X + Column, Y + Row);
                        Colors.Add(Pixel.R << 16 | Pixel.G << 8 | Pixel.B);
                    }
                    if (Colors.Count >= 4)
                    {
                        DetailedRows++;
                    }
                }
                Require(DetailedRows >= MinimumDetailedRows,
                    $"{AssetId} pane '{DetailedPaneId}' must render readable detailed rows");
            }

            if (KeepOpen)
            {
                SharedRoot = RootId;
            }
            else
            {
                _ = Tool(Id++, "editor.action", new JsonObject
                {
                    ["action"] = $"editor.host-action:{RootId}:window.close-requested",
                });
            }
        }
    }

    private static JsonObject Structured(JsonObject Response) =>
        Response["result"]?["structuredContent"]?.AsObject() ??
        throw new InvalidDataException(
            $"Tool response had no structured content: {Response}");

    private static DecodedPng DecodePng(byte[] Png)
    {
        var Offset = 8;
        uint Width = 0;
        uint Height = 0;
        using var Compressed = new MemoryStream();
        while (Offset + 12 <= Png.Length)
        {
            var Length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                Png.AsSpan(Offset, 4)));
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
            var Source = checked(Row * (RowBytes + 1));
            if (Scanlines[Source] != 0)
            {
                throw new InvalidDataException("Asset editor PNG used a filtered row");
            }
            Buffer.BlockCopy(Scanlines, Source + 1, Rgba, Row * RowBytes, RowBytes);
        }
        return new DecodedPng(Width, Height, Rgba);
    }

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
