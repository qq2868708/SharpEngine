using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SharpEngine.ProjectSelector;

internal sealed record ProjectPreview(int SchemaVersion, string ManifestPath, string Name,
    string EngineVersion, string EngineAssociation, string? ImagePath, bool Compatible, string Diagnostic);

/// <summary>Reads saved metadata only. Never constructs an EditorRuntime or loads project assemblies.</summary>
internal sealed class ProjectBrowser
{
    private readonly string EngineRoot;
    private readonly string RecentPath;

    internal ProjectBrowser(string EngineRoot, string? RecentPath = null)
    {
        this.EngineRoot = Path.GetFullPath(EngineRoot);
        this.RecentPath = RecentPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpEngine", "projects.json");
    }

    internal IReadOnlyList<string> Recent()
    {
        if (!File.Exists(RecentPath))
        {
            return [];
        }
        using var Document = JsonDocument.Parse(File.ReadAllText(RecentPath));
        if (Document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("SEPROJECT001: Unsupported recent-project list schema");
        }
        return Document.RootElement.GetProperty("projects").EnumerateArray()
            .Select(Value => Path.GetFullPath(Value.GetString()!)).ToArray();
    }

    internal void Remember(string Manifest)
    {
        var Paths = new[]
        {
            Path.GetFullPath(Manifest),
        }.Concat(Recent())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(RecentPath)!);
        var Temporary = RecentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(Temporary, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projects = Paths,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
            }) + Environment.NewLine, new UTF8Encoding(false));
            File.Move(Temporary, RecentPath, true);
        }
        finally
        {
            File.Delete(Temporary);
        }
    }

    internal ProjectPreview Preview(string Manifest)
    {
        var PathValue = Path.GetFullPath(Manifest);
        var Name = Path.GetFileNameWithoutExtension(PathValue);
        var Version = "";
        var Association = "";
        string? Image = null;
        try
        {
            var Descriptor = ProjectContracts.ReadProject(PathValue);
            Name = Descriptor.DisplayName;
            Version = Descriptor.EngineVersion;
            Association = Descriptor.EngineAssociation;
            using var Document = JsonDocument.Parse(File.ReadAllText(PathValue));
            var Root = Document.RootElement;
            if (Root.GetProperty("schemaVersion").GetInt32() != 3)
            {
                throw new InvalidDataException("Unsupported project schemaVersion (expected 3)");
            }
            var HasName = Root.TryGetProperty("displayName", out var DisplayName);
            Name = HasName ? DisplayName.GetString()! : Root.GetProperty("projectId").GetString()!;
            var Engine = Root.GetProperty("engine");
            Version = Engine.GetProperty("version").GetString()!;
            Association = Engine.GetProperty("association").GetString()!;
            var HasImage = Root.TryGetProperty("previewImage", out var PreviewImage);
            if (HasImage)
            {
                var Relative = PreviewImage.GetString()!;
                var Prefix = Path.GetDirectoryName(PathValue)! + Path.DirectorySeparatorChar;
                Image = Path.GetFullPath(Path.Combine(Prefix, Relative));
                if (Path.IsPathRooted(Relative) || !Image.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("previewImage must remain inside the project directory");
                }
                if (!File.Exists(Image))
                {
                    throw new FileNotFoundException("Saved project preview image was not found", Image);
                }
            }
            var Registered = new EngineAssociationStore().Resolve(Association);
            var ResolvedEngine = ProjectContracts.ReadEngine(Registered ?? EngineRoot);
            var Compatible = Engine.GetProperty("id").GetString() == ResolvedEngine.Id &&
                Version == ResolvedEngine.Version;
            return new ProjectPreview(1, PathValue, Name, Version, Association, Image, Compatible,
                Compatible ? "可打开" : "引擎版本不兼容，请选择匹配的引擎安装");
        }
        catch (Exception Error) when (Error is IOException or JsonException or InvalidDataException or
            KeyNotFoundException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return new ProjectPreview(1, PathValue, Name, Version, Association, null, false, Error.Message);
        }
    }

    internal ResolvedSharpProject Resolve(string Manifest)
    {
        var Project = ProjectContracts.ReadProject(Manifest);
        var Root = new EngineAssociationStore().Resolve(Project.EngineAssociation) ?? EngineRoot;
        var Engine = ProjectContracts.ReadEngine(Root, Project.EngineManifest);
        ProjectContracts.ValidateBinding(Project, Engine);
        return new ResolvedSharpProject(Project, Engine);
    }

    internal string Create(string Name, string Parent)
    {
        var Start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = EngineRoot,
        };
        foreach (var Argument in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(EngineRoot, "Tools", "SharpEngineProject.ps1"),
                     "-Command", "New", "-Name", Name, "-Directory", Path.GetFullPath(Parent),
                     "-EngineRoot", EngineRoot,
                 })
        {
            Start.ArgumentList.Add(Argument);
        }
        using var Process = System.Diagnostics.Process.Start(Start) ?? throw new IOException("Could not create project");
        var Output = Process.StandardOutput.ReadToEndAsync();
        var Errors = Process.StandardError.ReadToEndAsync();
        Process.WaitForExit();
        Task.WaitAll(Output, Errors);
        if (Process.ExitCode != 0)
        {
            throw new IOException(Errors.Result + Output.Result);
        }
        return Path.Combine(Path.GetFullPath(Parent), Name, Name + ".sharpproject");
    }

    internal string CreateCoverTexture(string ImagePath, string CacheDirectory)
    {
        if (Path.GetExtension(ImagePath).Equals(".tga", StringComparison.OrdinalIgnoreCase))
        {
            return ImagePath;
        }
        var Bytes = File.ReadAllBytes(ImagePath);
        var Hash = Convert.ToHexString(SHA256.HashData(Bytes));
        var Output = Path.Combine(CacheDirectory, Hash + ".tga");
        if (File.Exists(Output))
        {
            return Output;
        }
        using var Stream = new MemoryStream(Bytes);
        using var Source = Image.FromStream(Stream);
        var Scale = Math.Min(1.0, 1024.0 / Math.Max(Source.Width, Source.Height));
        var Width = Math.Max(1, (int)(Source.Width * Scale));
        var Height = Math.Max(1, (int)(Source.Height * Scale));
        using var Bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var Graphics = System.Drawing.Graphics.FromImage(Bitmap))
        {
            Graphics.DrawImage(Source, 0, 0, Width, Height);
        }
        var Data = Bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var Temporary = Output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var Writer = new BinaryWriter(File.Create(Temporary)))
                {
                    var Header = new byte[18];
                    Header[2] = 2;
                    Header[12] = (byte)(Width & 255);
                    Header[13] = (byte)(Width >> 8);
                    Header[14] = (byte)(Height & 255);
                    Header[15] = (byte)(Height >> 8);
                    Header[16] = 32;
                    Header[17] = 0x28;
                    Writer.Write(Header);
                    var Row = new byte[Width * 4];
                    for (var Index = 0; Index < Height; Index++)
                    {
                        Marshal.Copy(Data.Scan0 + Index * Data.Stride, Row, 0, Row.Length);
                        Writer.Write(Row);
                    }
                }
                File.Move(Temporary, Output, true);
            }
            finally
            {
                File.Delete(Temporary);
            }
        }
        finally
        {
            Bitmap.UnlockBits(Data);
        }
        return Output;
    }
}
