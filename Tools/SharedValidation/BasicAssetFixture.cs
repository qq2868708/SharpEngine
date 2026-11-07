using System.Text.Json.Nodes;

internal static class BasicAssetFixture
{
    internal const string SkeletonId = "project://skeletons/validation";
    internal const string SkeletonMeshId = "project://meshes/skeleton-validation";
    internal const string StaticMeshId = "project://meshes/socket-validation";

    internal static void Prepare(string ProjectRoot)
    {
        var Folder = Path.Combine(ProjectRoot, "Content", "Media");
        Directory.CreateDirectory(Folder);
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
        var Manifest = Path.Combine(ProjectRoot, "Content", "Assets", "assets.json");
        var Root = JsonNode.Parse(File.ReadAllText(Manifest))!.AsObject();
        var Assets = Root["assets"]!.AsArray();
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

}
