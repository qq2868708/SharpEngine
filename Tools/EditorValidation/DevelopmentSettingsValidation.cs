using System.Text.Json.Nodes;
using SharpEngine.Editor;
using SharpEngine.Modules;

internal static class DevelopmentSettingsValidation
{
    public static void Run(string root, Action<bool, string> require)
    {
        var project = Path.Combine(root, "SettingsProject");
        var engine = Path.Combine(root, "SettingsEngine");
        var defaultsPath = Path.Combine(engine, "Config", "EngineSettings",
            "sharpengine.editor-navigation.json");
        Directory.CreateDirectory(Path.GetDirectoryName(defaultsPath)!);
        File.WriteAllText(defaultsPath,
            """{"schemaVersion":1,"settingsId":"sharpengine.editor-navigation","values":{"invertYAxis":true}}""");
        string savedJson;
        var path = Path.Combine(project, "Config", "ProjectSettings", "validation.gameplay.json");
        using (var registry = new DevelopmentSettingsRegistry(project, engine))
        {
            registry.RegisterAssembly(typeof(EngineSettings).Assembly, SettingsScope.Engine);
            registry.RegisterAssembly(typeof(ValidationProjectSettings).Assembly, SettingsScope.Project);
            var page = registry.Find(SettingsScope.Project, "validation.gameplay")!;
            require(DevelopmentSettings.Get<EngineSettings>().InvertYAxis,
                "Engine config defaults must load before project overrides");
            require(registry.Describe()["pages"]!.AsArray().All(node =>
                node!["schema"]!["$schema"]!.GetValue<string>().EndsWith("2020-12/schema")),
                "Every settings page must expose a JSON Schema");
            require(registry.TrySet(SettingsScope.Project, page.Attribute.Id, "weights",
                    """{"z":2,"a":1}""", out _) &&
                DevelopmentSettings.Get<ValidationProjectSettings>().Weights["z"] == 2,
                "String-key dictionaries must round-trip without JSON node ownership errors");
            var revision = page.Revision;
            require(!registry.TrySet(SettingsScope.Project, page.Attribute.Id, "moveSpeed", "-1", out _) &&
                    page.Revision == revision,
                "Failed range validation must leave value, revision and history unchanged");
            require(!registry.TrySet(SettingsScope.Project, page.Attribute.Id, "profiles",
                    """[{"title":"Invalid","strength":-1}]""", out _) &&
                    page.Revision == revision,
                "Constraints on objects inside collections must be validated");
            require(registry.TrySet(SettingsScope.Project, page.Attribute.Id, "moveSpeed", "900", out _) &&
                registry.Undo(SettingsScope.Project, page.Attribute.Id) &&
                DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 600 &&
                registry.Redo(SettingsScope.Project, page.Attribute.Id) &&
                DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 900,
                "Undo and Redo must publish coherent typed snapshots");
            require(!File.Exists(path), "Editing settings must never save implicitly");
            require(registry.Save(SettingsScope.Project, page.Attribute.Id, out _) && !page.IsDirty,
                "Explicit Save must advance the settings savepoint");
            savedJson = File.ReadAllText(path);
            require(savedJson.IndexOf("\"a\"", StringComparison.Ordinal) <
                    savedJson.IndexOf("\"z\"", StringComparison.Ordinal),
                "Dictionary serialization must sort keys deterministically");
            require(registry.Save(SettingsScope.Project, page.Attribute.Id, out _) &&
                    File.ReadAllText(path) == savedJson,
                "Repeated settings saves must produce identical bytes");
            registry.TrySet(SettingsScope.Project, page.Attribute.Id, "moveSpeed", "1200", out _);
            require(registry.Revert(SettingsScope.Project, page.Attribute.Id) &&
                    DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 900 && !page.IsDirty,
                "Revert must restore the saved state rather than constructor defaults");
            require(registry.Undo(SettingsScope.Project, page.Attribute.Id) &&
                    DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 1200 && page.IsDirty,
                "Revert must be undoable");
            var callerCopy = DevelopmentSettings.Get<ValidationProjectSettings>();
            callerCopy.Weights.Clear();
            require(DevelopmentSettings.Get<ValidationProjectSettings>().Weights.Count == 2,
                "Mutating a returned dictionary must not mutate engine-owned authority");
        }
        using (var reloaded = new DevelopmentSettingsRegistry(project, engine))
        {
            reloaded.RegisterAssembly(typeof(ValidationProjectSettings).Assembly, SettingsScope.Project);
            require(DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 900,
                "A fresh settings service must load saved values");
            var updated = JsonNode.Parse(savedJson)!;
            updated["values"]!["moveSpeed"] = 777;
            File.WriteAllText(path, updated.ToJsonString());
            require(reloaded.ReloadChangedFiles() &&
                    DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 777,
                "A valid external settings edit must reload atomically");
            File.WriteAllText(path, "{ invalid");
            require(reloaded.ReloadChangedFiles() &&
                    DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 777,
                "An invalid external edit must retain the last valid live state");
            File.WriteAllText(path, savedJson);
            reloaded.ReloadChangedFiles();
            reloaded.TrySet(SettingsScope.Project, "validation.gameplay", "moveSpeed", "1000", out _);
            File.WriteAllText(path, updated.ToJsonString());
            require(!reloaded.Save(SettingsScope.Project, "validation.gameplay", out _) &&
                    DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 1000,
                "Save must reject external file conflicts without losing local changes");
            require(reloaded.Revert(SettingsScope.Project, "validation.gameplay") &&
                    DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 777,
                "Explicit Revert must resolve the conflict by loading the validated external state");
        }
        var future = JsonNode.Parse(savedJson)!;
        future["schemaVersion"] = 999;
        File.WriteAllText(path, future.ToJsonString());
        using (var invalid = new DevelopmentSettingsRegistry(project, engine))
        {
            var rejected = false;
            try { invalid.RegisterAssembly(typeof(ValidationProjectSettings).Assembly, SettingsScope.Project); }
            catch (InvalidDataException) { rejected = true; }
            require(rejected && invalid.Descriptors.Count == 0,
                "Unsupported versions must reject the entire registration without partial state");
            File.WriteAllText(path, savedJson);
            invalid.RegisterAssembly(typeof(ValidationProjectSettings).Assembly, SettingsScope.Project);
            require(invalid.Descriptors.Count == 1,
                "A failed assembly scan must be retryable after repairing its configuration");
        }
    }
}
