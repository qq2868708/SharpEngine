using System.Runtime.InteropServices;

namespace SharpEngine.Editor;

/// <summary>Editor-only decoding. The derived TGA is compatible with both RmlUi renderers.</summary>
internal sealed class SourceImagePreview : IDisposable
{
    private const int MaximumPixels = 64 * 1024 * 1024;
    private const int MaximumPreviewSize = 1024;
    private const int PixelFormat = 0x26200A;
    public int Width
    {
        get;
        private set;
    }
    public int Height
    {
        get;
        private set;
    }
    public int PreviewWidth
    {
        get;
        private set;
    }
    public int PreviewHeight
    {
        get;
        private set;
    }
    public string PreviewPath
    {
        get;
        private set;
    } = string.Empty;

    public SourceImagePreview(string Path)
    {
        if (new FileInfo(Path).Length > 256L * 1024 * 1024)
        {
            throw new InvalidDataException("Image preview supports source files up to 256 MiB.");
        }
        byte[] Pixels;
        if (System.IO.Path.GetExtension(Path).Equals(".tga", StringComparison.OrdinalIgnoreCase))
        {
            Pixels = DecodeTga(Path);
        }
        else
        {
            Pixels = DecodeWindowsImage(Path);
        }
        PreviewPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "SharpEngine-SourcePreview-" + Guid.NewGuid().ToString("N") + ".tga");
        try
        {
            using var Writer = new BinaryWriter(File.Create(PreviewPath));
            Writer.Write(new byte[]
            {
                0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            });
            Writer.Write((ushort)PreviewWidth);
            Writer.Write((ushort)PreviewHeight);
            Writer.Write((byte)32);
            Writer.Write((byte)0x28);
            Writer.Write(Pixels);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (PreviewPath.Length != 0)
        {
            File.Delete(PreviewPath);
            PreviewPath = string.Empty;
        }
    }

    private void SetDimensions(int Width, int Height)
    {
        if (Width <= 0 || Height <= 0 || (long)Width * Height > MaximumPixels)
        {
            throw new InvalidDataException("Image dimensions must be positive and at most 64 megapixels.");
        }
        this.Width = Width;
        this.Height = Height;
        var Scale = Math.Min(1.0, (double)MaximumPreviewSize / Math.Max(Width, Height));
        PreviewWidth = Math.Max(1, (int)Math.Round(Width * Scale));
        PreviewHeight = Math.Max(1, (int)Math.Round(Height * Scale));
    }

    private byte[] DecodeTga(string Path)
    {
        using var Reader = new BinaryReader(File.OpenRead(Path));
        var Header = Reader.ReadBytes(18);
        if (Header.Length != 18 || Header[1] != 0 || Header[2] is not (2 or 10) ||
            Header[16] is not (24 or 32) || (Header[17] & 0xC0) != 0)
        {
            throw new InvalidDataException("TGA preview requires an uncompressed or RLE 24/32-bit image without a color map.");
        }
        SetDimensions(Header[12] | Header[13] << 8, Header[14] | Header[15] << 8);
        Reader.BaseStream.Seek(Header[0], SeekOrigin.Current);
        var BytesPerPixel = Header[16] / 8;
        var Pixels = new byte[checked(Width * Height * 4)];
        var Pixel = 0;
        while (Pixel < Width * Height)
        {
            var Packet = Header[2] == 10 ? Reader.ReadByte() : 0;
            var Count = Header[2] == 10 ? (Packet & 127) + 1 : 1;
            var Repeated = (Packet & 128) != 0;
            if (Count > Width * Height - Pixel)
            {
                throw new InvalidDataException("TGA packet exceeds the declared image dimensions.");
            }
            var Color = Repeated ? Reader.ReadBytes(BytesPerPixel) : null;
            for (var Index = 0; Index < Count; Index++, Pixel++)
            {
                var Sample = Color ?? Reader.ReadBytes(BytesPerPixel);
                if (Sample.Length != BytesPerPixel)
                {
                    throw new InvalidDataException("TGA image data is truncated.");
                }
                var X = Pixel % Width;
                var Y = Pixel / Width;
                X = (Header[17] & 16) != 0 ? Width - X - 1 : X;
                Y = (Header[17] & 32) != 0 ? Y : Height - Y - 1;
                var Offset = (Y * Width + X) * 4;
                Pixels[Offset] = Sample[0];
                Pixels[Offset + 1] = Sample[1];
                Pixels[Offset + 2] = Sample[2];
                Pixels[Offset + 3] = BytesPerPixel == 4 ? Sample[3] : (byte)255;
            }
        }
        var Thumbnail = new byte[PreviewWidth * PreviewHeight * 4];
        for (var Y = 0; Y < PreviewHeight; Y++)
        {
            for (var X = 0; X < PreviewWidth; X++)
            {
                var Source = ((long)Y * Height / PreviewHeight * Width + (long)X * Width / PreviewWidth) * 4;
                Buffer.BlockCopy(Pixels, (int)Source, Thumbnail, (Y * PreviewWidth + X) * 4, 4);
            }
        }
        return Thumbnail;
    }

    private byte[] DecodeWindowsImage(string Path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("PNG/JPEG/BMP preview currently requires Windows.");
        }
        var Startup = new StartupInput
        {
            Version = 1,
        };
        Check(GdiplusStartup(out var Token, ref Startup, IntPtr.Zero));
        var Image = IntPtr.Zero;
        var Thumbnail = IntPtr.Zero;
        try
        {
            Check(GdipCreateBitmapFromFile(Path, out Image));
            Check(GdipGetImageWidth(Image, out var ImageWidth));
            Check(GdipGetImageHeight(Image, out var ImageHeight));
            SetDimensions(checked((int)ImageWidth), checked((int)ImageHeight));
            Check(GdipGetImageThumbnail(Image, (uint)PreviewWidth, (uint)PreviewHeight,
                out Thumbnail, IntPtr.Zero, IntPtr.Zero));
            var Rect = new ImageRect
            {
                Width = PreviewWidth,
                Height = PreviewHeight,
            };
            Check(GdipBitmapLockBits(Thumbnail, ref Rect, 1, PixelFormat, out var Data));
            try
            {
                var Pixels = new byte[PreviewWidth * PreviewHeight * 4];
                for (var Row = 0; Row < PreviewHeight; Row++)
                {
                    Marshal.Copy(IntPtr.Add(Data.Scan0, Row * Data.Stride), Pixels,
                        Row * PreviewWidth * 4, PreviewWidth * 4);
                }
                return Pixels;
            }
            finally
            {
                _ = GdipBitmapUnlockBits(Thumbnail, ref Data);
            }
        }
        finally
        {
            if (Thumbnail != IntPtr.Zero)
            {
                _ = GdipDisposeImage(Thumbnail);
            }
            if (Image != IntPtr.Zero)
            {
                _ = GdipDisposeImage(Image);
            }
            GdiplusShutdown(Token);
        }
    }

    private static void Check(int Status)
    {
        if (Status != 0)
        {
            throw new InvalidDataException($"Windows image decoding failed (status {Status}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint Version;
        public IntPtr DebugCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ImageRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapData
    {
        public uint Width;
        public uint Height;
        public int Stride;
        public int PixelFormat;
        public IntPtr Scan0;
        public UIntPtr Reserved;
    }

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdiplusStartup(out UIntPtr Token, ref StartupInput Input, IntPtr Output);
    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern void GdiplusShutdown(UIntPtr Token);
    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipCreateBitmapFromFile(string Filename, out IntPtr Bitmap);
    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetImageWidth(IntPtr Image, out uint Width);
    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetImageHeight(IntPtr Image, out uint Height);
    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetImageThumbnail(IntPtr Image, uint Width, uint Height,
        out IntPtr Thumbnail, IntPtr Callback, IntPtr Data);
    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipBitmapLockBits(IntPtr Bitmap, ref ImageRect Rect, uint Flags,
        int Format, out BitmapData Data);
    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipBitmapUnlockBits(IntPtr Bitmap, ref BitmapData Data);
    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDisposeImage(IntPtr Image);
}
