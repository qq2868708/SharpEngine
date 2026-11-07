using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpEngine.Editor;

internal sealed class SourceAudioPreview : IDisposable
{
    private readonly string Path;
    private readonly string Alias = "sharpengine_" + Guid.NewGuid().ToString("N");
    private bool Opened;
    private string? PlaybackPath;
    public bool CanPlay
    {
        get;
    }
    public bool Playing
    {
        get;
        private set;
    }
    public double? Duration
    {
        get;
        private set;
    }
    public int? Channels
    {
        get;
        private set;
    }
    public int? SampleRate
    {
        get;
        private set;
    }
    public int? BitsPerSample
    {
        get;
        private set;
    }
    public double[] Peaks
    {
        get;
        private set;
    } = [];

    public SourceAudioPreview(string Path)
    {
        this.Path = Path;
        var Extension = System.IO.Path.GetExtension(Path).ToLowerInvariant();
        CanPlay = OperatingSystem.IsWindows() && Extension is ".wav" or ".mp3";
        if (Extension == ".wav")
        {
            ReadWave();
        }
    }

    public void Play()
    {
        if (!CanPlay)
        {
            throw new InvalidOperationException("Playback is available for WAV and MP3; OGG is currently inspection only.");
        }
        if (!Opened)
        {
            var Device = System.IO.Path.GetExtension(Path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? "waveaudio" : "mpegvideo";
            try
            {
                // Legacy MCI drivers reject long file names even with the structured
                // open API. The device owns a short-lived copy in the temporary folder.
                PlaybackPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Alias + System.IO.Path.GetExtension(Path));
                try
                {
                    File.Copy(Path, PlaybackPath, false);
                }
                catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("Audio preview could not prepare its playback copy.", Error);
                }
                Send($"open \"{PlaybackPath}\" type {Device} alias {Alias}");
                Opened = true;
                Send($"set {Alias} time format milliseconds");
                var Milliseconds = Send($"status {Alias} length");
                Duration = double.Parse(Milliseconds, System.Globalization.CultureInfo.InvariantCulture) / 1000;
            }
            catch
            {
                Dispose();
                throw;
            }
        }
        Send($"play {Alias} from 0");
        Playing = true;
    }

    public void Stop()
    {
        if (Opened)
        {
            Send($"stop {Alias}");
        }
        Playing = false;
    }

    public void Update()
    {
        if (Opened && Playing)
        {
            Playing = Send($"status {Alias} mode").Equals("playing", StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        if (Opened)
        {
            _ = MciSendString("close " + Alias, null, 0, IntPtr.Zero);
            Opened = false;
        }
        Playing = false;
        if (PlaybackPath is not null)
        {
            File.Delete(PlaybackPath);
            PlaybackPath = null;
        }
    }

    private void ReadWave()
    {
        using var Stream = File.OpenRead(Path);
        using var Reader = new BinaryReader(Stream);
        if (Reader.ReadUInt32() != 0x46464952)
        {
            throw new InvalidDataException("WAV preview requires a RIFF WAVE file.");
        }
        var RiffEnd = 8L + Reader.ReadUInt32();
        if (Reader.ReadUInt32() != 0x45564157 || RiffEnd > Stream.Length)
        {
            throw new InvalidDataException("Invalid or truncated WAVE header.");
        }
        ushort Format = 0;
        ushort BlockAlign = 0;
        long DataOffset = 0;
        uint DataLength = 0;
        while (Stream.Position + 8 <= RiffEnd)
        {
            var Kind = Reader.ReadUInt32();
            var Size = Reader.ReadUInt32();
            var Start = Stream.Position;
            var End = Start + Size;
            if (End > RiffEnd)
            {
                throw new InvalidDataException("WAVE chunk exceeds the RIFF length.");
            }
            if (Kind == 0x20746D66 && Size >= 16)
            {
                Format = Reader.ReadUInt16();
                Channels = Reader.ReadUInt16();
                SampleRate = checked((int)Reader.ReadUInt32());
                _ = Reader.ReadUInt32();
                BlockAlign = Reader.ReadUInt16();
                BitsPerSample = Reader.ReadUInt16();
            }
            if (Kind == 0x61746164 && DataOffset == 0)
            {
                DataOffset = Start;
                DataLength = Size;
            }
            Stream.Position = End + (Size & 1);
        }
        if (Channels is null or <= 0 || SampleRate is null or <= 0 || BlockAlign == 0 || DataOffset == 0)
        {
            throw new InvalidDataException("WAVE requires valid fmt and data chunks.");
        }
        Duration = (double)DataLength / BlockAlign / SampleRate.Value;
        if (!(Format == 1 && BitsPerSample is 8 or 16 or 24 or 32 ||
            Format == 3 && BitsPerSample == 32) ||
            BlockAlign != Channels.Value * BitsPerSample.Value / 8)
        {
            return;
        }
        var FrameCount = DataLength / BlockAlign;
        if (FrameCount == 0)
        {
            return;
        }
        Peaks = new double[128];
        var Frame = new byte[BlockAlign];
        var BytesPerSample = BitsPerSample.Value / 8;
        // Sample evenly across the complete file, with a bounded amount of I/O.
        var ProbeCount = (int)Math.Min(FrameCount, 65536);
        for (var Probe = 0; Probe < ProbeCount; Probe++)
        {
            var FrameIndex = (long)Probe * FrameCount / ProbeCount;
            Stream.Position = DataOffset + FrameIndex * BlockAlign;
            Stream.ReadExactly(Frame);
            var Peak = 0.0;
            for (var Channel = 0; Channel < Channels; Channel++)
            {
                var Sample = Frame.AsSpan(Channel * BytesPerSample, BytesPerSample);
                var Value = Format == 3 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Sample)) :
                    BitsPerSample.Value switch
                    {
                        8 => (Sample[0] - 128) / 128.0,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(Sample) / 32768.0,
                        24 => ((Sample[0] | Sample[1] << 8 | Sample[2] << 16) << 8 >> 8) / 8388608.0,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(Sample) / 2147483648.0,
                    };
                if (double.IsFinite(Value))
                {
                    Peak = Math.Max(Peak, Math.Min(1, Math.Abs(Value)));
                }
            }
            var Bin = Math.Min(Peaks.Length - 1, (int)(FrameIndex * Peaks.Length / FrameCount));
            Peaks[Bin] = Math.Max(Peaks[Bin], Peak);
        }
    }

    private static string Send(string Command)
    {
        var Output = new StringBuilder(256);
        var Error = MciSendString(Command, Output, Output.Capacity, IntPtr.Zero);
        if (Error != 0)
        {
            // Keep machine paths and driver messages out of public snapshots.
            throw new InvalidOperationException($"Windows audio preview failed (MCI {Error}).");
        }
        return Output.ToString();
    }

    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MciSendString(string Command, StringBuilder? Output, int Length, IntPtr Callback);
}
