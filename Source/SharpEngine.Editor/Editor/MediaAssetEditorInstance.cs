using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal sealed class MediaAssetEditorInstance : IAssetEditorInstance
{
    private readonly EditorAssetOpenContext Context;
    private SourceImagePreview? Image;
    private SourceAudioPreview? Audio;
    private string Diagnostic = string.Empty;
    private long ByteLength;
    private float PollTime;
    private bool LightBackground = true;

    public MediaAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        Reload();
    }

    public AssetDescriptor Asset
    {
        get
        {
            return Context.Asset;
        }
    }
    public string EditorId
    {
        get
        {
            return Asset.Type == "Texture" ? "texture-editor" : "audio-editor";
        }
    }
    public string Title
    {
        get
        {
            return Asset.Name;
        }
    }
    public string PrimaryPaneId
    {
        get
        {
            return "preview";
        }
    }
    public bool IsDirty
    {
        get
        {
            return false;
        }
    }
    public bool IsReadOnly
    {
        get
        {
            return true;
        }
    }
    public IReadOnlyList<AssetEditorPaneDefinition> Panes
    {
        get;
    } =
    [
        new("preview", "Preview", false),
        new("details", "Source File"),
    ];
    public IReadOnlyList<AssetEditorCommandDefinition> Commands
    {
        get;
    } =
    [
        new("reload", "Reload File", "File", 100, "reset", true),
        new("background", "Toggle Background", "View", 100, "texture", true),
        new("play", "Play From Start", "Preview", 100, "play", true),
        new("stop", "Stop", "Preview", 110, "stop", true),
    ];

    public EditorLayoutDocument CreateDefaultLayout()
    {
        return new EditorLayoutDocument
        {
            MainHost = new DockHostState
            {
                HostId = "main",
                Root = new DockNodeState
                {
                    NodeId = "asset.root",
                    Kind = DockNodeKind.Split,
                    Orientation = DockSplitOrientation.Horizontal,
                    Ratio = 0.72f,
                    First = AssetEditorInstanceUtilities.Tabs("asset.preview", "asset.preview.tabs", "preview"),
                    Second = AssetEditorInstanceUtilities.Tabs("asset.details", "asset.details.tabs", "details"),
                },
            },
            HiddenPaneIds = [],
            ContentManagers = [],
            ActiveContentManagerId = string.Empty,
        };
    }

    public bool CanExecuteCommand(string CommandId)
    {
        return CommandId switch
        {
            "reload" => true,
            "background" => Image is not null,
            "play" => Audio?.CanPlay == true,
            "stop" => Audio?.Playing == true,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        if (!CanExecuteCommand(CommandId))
        {
            return;
        }
        try
        {
            switch (CommandId)
            {
                case "reload":
                    Reload();
                    break;
                case "background":
                    LightBackground = !LightBackground;
                    break;
                case "play":
                    Audio!.Play();
                    Diagnostic = string.Empty;
                    break;
                case "stop":
                    Audio!.Stop();
                    break;
            }
        }
        catch (Exception Error) when (Error is InvalidOperationException or FormatException)
        {
            Diagnostic = Error.Message;
            Context.SetStatus(Diagnostic);
        }
    }

    public bool TryHandleAction(string Action) => false;
    public void Save() => throw new InvalidOperationException("Media source previews are read only.");
    public void Update(float DeltaSeconds)
    {
        PollTime += DeltaSeconds;
        if (PollTime < 0.25f || Audio?.Playing != true)
        {
            return;
        }
        PollTime = 0;
        try
        {
            Audio.Update();
        }
        catch (InvalidOperationException Error)
        {
            Diagnostic = Error.Message;
            Audio.Dispose();
        }
    }

    public string BuildPaneMarkup(string PaneId)
    {
        var Markup = new StringBuilder("<div class=\"asset-inspector\">");
        if (Diagnostic.Length != 0)
        {
            Markup.Append("<div id=\"mediaDiagnostic\" class=\"ui-diagnostic ui-diagnostic-error\">")
                .Append(AssetEditorInstanceUtilities.Escape(Diagnostic)).Append("</div>");
        }
        if (PaneId == "details")
        {
            Field("File", Asset.RelativePath);
            Field("Format", Path.GetExtension(Context.Path).TrimStart('.').ToUpperInvariant());
            Field("Bytes", ByteLength.ToString(CultureInfo.InvariantCulture));
            Field("Mode", Context.IsSourceFile ? "Source file preview (unregistered)" : "Registered source preview");
            if (Image is not null)
            {
                Field("Dimensions", $"{Image.Width} × {Image.Height}");
                Field("Preview", $"{Image.PreviewWidth} × {Image.PreviewHeight} (fit, at most 1024 px)");
            }
            if (Audio is not null)
            {
                Field("Duration", Audio.Duration is null ? "Not decoded" :
                    Audio.Duration.Value.ToString("0.000", CultureInfo.InvariantCulture) + " s");
                Field("Channels", Audio.Channels?.ToString() ?? "Not decoded");
                Field("Sample Rate", Audio.SampleRate?.ToString() ?? "Not decoded");
                Field("Bits / Sample", Audio.BitsPerSample?.ToString() ?? "Not decoded");
                Field("Playback", Audio.CanPlay ? "Windows media driver (WAV / MP3)" : "Inspection only");
            }
        }
        else if (Image is not null)
        {
            Markup.Append("<div id=\"texturePreview\" class=\"media-image-preview")
                .Append(LightBackground ? " light" : string.Empty).Append("\"><img id=\"texturePreviewImage\" src=\"")
                .Append(AssetEditorInstanceUtilities.EscapeAttribute(Image.PreviewPath.Replace('\\', '/')))
                .Append("\" /></div>");
        }
        else if (Audio is not null)
        {
            Markup.Append("<div id=\"audioPreview\"><h3>")
                .Append(Audio.Playing ? "Playing" : "Stopped").Append("</h3>");
            if (Audio.Peaks.Length != 0)
            {
                Markup.Append("<div id=\"audioWaveform\" class=\"media-waveform\">");
                foreach (var Peak in Audio.Peaks)
                {
                    Markup.Append("<div class=\"media-waveform-bar\" style=\"height: ")
                        .Append(Math.Max(1, Peak * 100).ToString("0.0", CultureInfo.InvariantCulture))
                        .Append("%;\"></div>");
                }
                Markup.Append("</div><div>Sampled amplitude overview</div>");
            }
            else
            {
                Markup.Append("<div>No decoded waveform. WAV PCM / float waveforms are supported.</div>");
            }
            Markup.Append("</div>");
        }
        Markup.Append("</div>");
        return Markup.ToString();

        void Field(string Label, string Value)
        {
            Markup.Append(AssetEditorInstanceUtilities.ReadOnlyField(Label, Value));
        }
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assetId"] = Asset.Id,
            ["sourceFile"] = Context.IsSourceFile,
            ["relativePath"] = Asset.RelativePath,
            ["readOnly"] = true,
            ["dirty"] = false,
            ["bytes"] = ByteLength,
            ["diagnostic"] = Diagnostic,
            ["width"] = Image?.Width,
            ["height"] = Image?.Height,
            ["previewWidth"] = Image?.PreviewWidth,
            ["previewHeight"] = Image?.PreviewHeight,
            ["background"] = LightBackground ? "light" : "dark",
            ["canPlay"] = Audio?.CanPlay == true,
            ["playing"] = Audio?.Playing == true,
            ["duration"] = Audio?.Duration,
            ["channels"] = Audio?.Channels,
            ["sampleRate"] = Audio?.SampleRate,
            ["bitsPerSample"] = Audio?.BitsPerSample,
            ["waveform"] = new JsonArray((Audio?.Peaks ?? []).Select(Peak => (JsonNode)JsonValue.Create(Peak)!).ToArray()),
        };
    }

    public void Dispose()
    {
        Image?.Dispose();
        Audio?.Dispose();
    }

    private void Reload()
    {
        Dispose();
        Image = null;
        Audio = null;
        Diagnostic = string.Empty;
        try
        {
            ByteLength = new FileInfo(Context.Path).Length;
            if (Asset.Type == "Texture")
            {
                Image = new SourceImagePreview(Context.Path);
            }
            else
            {
                Audio = new SourceAudioPreview(Context.Path);
            }
        }
        catch (Exception Error) when (Error is IOException or InvalidDataException or
            ArgumentException or OverflowException or PlatformNotSupportedException)
        {
            Diagnostic = Error is FileNotFoundException ? "Source file is missing; restore it and Reload File." :
                Error is IOException ? "Source file could not be read; restore access and Reload File." :
                Error.Message;
        }
    }
}
