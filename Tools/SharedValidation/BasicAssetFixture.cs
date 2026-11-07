using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

internal static class BasicAssetFixture
{
    internal const string StateMachineId = "project://animation-state-machines/validation";
    internal const string TextureId = "project://textures/validation";
    internal const string AudioId = "project://audio/validation";
    internal const string LooseImageId = "source-file://project/media/loose.png";
    internal const string SkeletonId = "project://skeletons/validation";
    internal const string SkeletonMeshId = "project://meshes/skeleton-validation";
    internal const string StaticMeshId = "project://meshes/socket-validation";

    internal static void Prepare(string ProjectRoot)
    {
        var Folder = Path.Combine(ProjectRoot, "Content", "Media");
        Directory.CreateDirectory(Folder);
        WritePng(Path.Combine(Folder, "Preview.png"));
        File.Copy(Path.Combine(Folder, "Preview.png"), Path.Combine(Folder, "Loose.png"), true);
        WriteWave(Path.Combine(Folder, "Preview.wav"));
        File.WriteAllText(Path.Combine(Folder, "Preview.ogg"), "OGG inspection fixture");
        File.WriteAllText(Path.Combine(Folder, "Broken.png"), "invalid image");
        File.WriteAllText(Path.Combine(Folder, "Validation.sstaticmesh"), """
            {"schemaVersion":1,"kind":"static-mesh","assetId":"project://meshes/socket-validation","name":"Socket Validation Mesh","primitive":"Triangle","coordinateSystem":"Y-up","defaultMaterial":"engine://materials/default","positions":[[-0.6,0,0],[0.6,0,0],[0,1,0]],"indices":[0,1,2]}
            """);
        File.WriteAllText(Path.Combine(Folder, "Validation.sskeleton"), """
            {"schemaVersion":2,"kind":"skeleton","assetId":"project://skeletons/validation","name":"Validation Skeleton","previewMeshId":null,"bones":[
            {"boneId":"root","name":"Root","parentBoneId":null,"bindPosition":[0,0,0],"bindRotation":[0,0,0,1],"bindScale":[1,1,1]},
            {"boneId":"hand","name":"Hand","parentBoneId":"root","bindPosition":[0.8,0.8,0],"bindRotation":[0,0,0,1],"bindScale":[1,1,1]}]}
            """);
        var Skeleton = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "Validation.sskeleton")))!;
        File.WriteAllText(Path.Combine(Folder, "Validation.sskeletalmesh"), new JsonObject
        {
            ["schemaVersion"] = 2,
            ["kind"] = "skeletal-mesh",
            ["assetId"] = SkeletonMeshId,
            ["name"] = "Skeleton Preview Mesh",
            ["skeletonId"] = SkeletonId,
            ["bones"] = Skeleton["bones"]!.DeepClone(),
            ["positions"] = new JsonArray
            {
                new JsonArray(0, 0, 0), new JsonArray(0.8, 0.8, 0), new JsonArray(0, 0.8, 0),
            },
            ["indices"] = new JsonArray(0, 1, 2),
            ["influences"] = new JsonArray(Enumerable.Range(0, 3).Select(_ => new JsonObject
            {
                ["boneIndices"] = new JsonArray(0),
                ["weights"] = new JsonArray(1),
            }).ToArray()),
            ["defaultMaterial"] = "engine://materials/default",
        }.ToJsonString());
        File.WriteAllText(Path.Combine(Folder, "Controller.json"), new JsonObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = "animation-state-machine",
            ["assetId"] = StateMachineId,
            ["name"] = "Validation Controller",
            ["initialStateId"] = "Idle",
            ["parameters"] = new JsonArray(new JsonObject
            {
                ["parameterId"] = "Speed",
                ["defaultValue"] = 0,
            }),
            ["states"] = new JsonArray
            {
                State("Idle", "engine://animations/uefn-mannequin-idle"),
                State("Walk", "engine://animations/uefn-mannequin-walk"),
            },
            ["transitions"] = new JsonArray
            {
                new JsonObject
                {
                    ["transitionId"] = "StartWalking", ["fromStateId"] = "Idle", ["toStateId"] = "Walk",
                    ["exitTime"] = null,
                    ["conditions"] = new JsonArray(new JsonObject
                    {
                        ["parameterId"] = "Speed", ["comparison"] = "Greater", ["value"] = 0,
                    }),
                },
            },
        }.ToJsonString());
        var Manifest = Path.Combine(ProjectRoot, "Content", "Assets", "assets.json");
        var Root = JsonNode.Parse(File.ReadAllText(Manifest))!.AsObject();
        var Assets = Root["assets"]!.AsArray();
        Add("animation-state-machines/validation", "AnimationStateMachine", "Media/Controller.json");
        Add("textures/validation", "Texture", "Media/Preview.png");
        Add("audio/validation", "Audio", "Media/Preview.wav");
        Add("skeletons/validation", "Skeleton", "Media/Validation.sskeleton");
        Add("meshes/skeleton-validation", "SkeletalMesh", "Media/Validation.sskeletalmesh");
        Add("meshes/socket-validation", "StaticMesh", "Media/Validation.sstaticmesh");
        File.WriteAllText(Manifest, Root.ToJsonString());

        void Add(string Id, string Type, string Path)
        {
            Assets.Add(new JsonObject
            {
                ["id"] = Id,
                ["type"] = Type,
                ["path"] = Path,
            });
        }
    }

    private static JsonObject State(string Id, string Clip)
    {
        return new JsonObject
        {
            ["stateId"] = Id,
            ["clipId"] = Clip,
            ["loop"] = true,
            ["speed"] = 1,
        };
    }

    private static void WritePng(string Path)
    {
        using var Stream = File.Create(Path);
        Stream.Write(new byte[]
        {
            137, 80, 78, 71, 13, 10, 26, 10,
        });
        var Header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(Header, 64);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(Header.AsSpan(4), 32);
        Header[8] = 8;
        Header[9] = 6;
        Chunk("IHDR", Header);
        using var Compressed = new MemoryStream();
        using (var Zlib = new ZLibStream(Compressed, CompressionLevel.Fastest, true))
        {
            for (var Y = 0; Y < 32; Y++)
            {
                Zlib.WriteByte(0);
                for (var X = 0; X < 64; X++)
                {
                    Zlib.Write(new byte[]
                    {
                        X < 32 ? (byte)230 : (byte)20, Y < 16 ? (byte)180 : (byte)30, 70, 255,
                    });
                }
            }
        }
        Chunk("IDAT", Compressed.ToArray());
        Chunk("IEND", []);

        void Chunk(string Type, byte[] Data)
        {
            Span<byte> Length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(Length, Data.Length);
            Stream.Write(Length);
            var Tag = Encoding.ASCII.GetBytes(Type);
            Stream.Write(Tag);
            Stream.Write(Data);
            uint Crc = uint.MaxValue;
            foreach (var Byte in Tag.Concat(Data))
            {
                Crc ^= Byte;
                for (var Bit = 0; Bit < 8; Bit++)
                {
                    Crc = (Crc >> 1) ^ ((Crc & 1) != 0 ? 0xEDB88320u : 0);
                }
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(Length, ~Crc);
            Stream.Write(Length);
        }
    }

    private static void WriteWave(string Path)
    {
        using var Writer = new BinaryWriter(File.Create(Path));
        Writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        Writer.Write(36 + 80000);
        Writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        Writer.Write(16);
        Writer.Write((ushort)1);
        Writer.Write((ushort)1);
        Writer.Write(8000);
        Writer.Write(16000);
        Writer.Write((ushort)2);
        Writer.Write((ushort)16);
        Writer.Write(Encoding.ASCII.GetBytes("data"));
        Writer.Write(80000);
        for (var Index = 0; Index < 40000; Index++)
        {
            Writer.Write((short)(Index > 39900 ? Math.Sin(Index * 0.2) * 16000 : 0));
        }
    }
}
