using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SharpEngine.Interop;

internal static class UiPngEncoder
{
    private static readonly byte[] Signature =
        [137, 80, 78, 71, 13, 10, 26, 10];

    internal static byte[] Encode(NativeUiFrame Frame)
    {
        var RowBytes = checked((int)Frame.Width * 4);
        var Expected = checked(RowBytes * (int)Frame.Height);
        if (Frame.BgraPixels.Length != Expected)
        {
            throw new InvalidDataException("Native UI frame byte count does not match its dimensions");
        }

        using var Output = new MemoryStream();
        Output.Write(Signature);
        Span<byte> Header = stackalloc byte[13];
        Header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(Header, Frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(Header[4..], Frame.Height);
        Header[8] = 8;
        Header[9] = 6;
        WriteChunk(Output, "IHDR", Header);

        using var Raw = new MemoryStream(checked((RowBytes + 1) * (int)Frame.Height));
        for (var Y = (int)Frame.Height - 1; Y >= 0; Y--)
        {
            Raw.WriteByte(0);
            var Offset = Y * RowBytes;
            for (var X = 0; X < RowBytes; X += 4)
            {
                Raw.WriteByte(Frame.BgraPixels[Offset + X + 2]);
                Raw.WriteByte(Frame.BgraPixels[Offset + X + 1]);
                Raw.WriteByte(Frame.BgraPixels[Offset + X]);
                Raw.WriteByte(Frame.BgraPixels[Offset + X + 3]);
            }
        }
        Raw.Position = 0;
        using var Compressed = new MemoryStream();
        using (var Zlib = new ZLibStream(Compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            Raw.CopyTo(Zlib);
        }
        WriteChunk(Output, "IDAT", Compressed.ToArray());
        WriteChunk(Output, "IEND", ReadOnlySpan<byte>.Empty);
        return Output.ToArray();
    }

    private static void WriteChunk(Stream Output, string Type,
                                   ReadOnlySpan<byte> Data)
    {
        Span<byte> Length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(Length, checked((uint)Data.Length));
        Output.Write(Length);
        var TypeBytes = Encoding.ASCII.GetBytes(Type);
        Output.Write(TypeBytes);
        Output.Write(Data);
        var Crc = Crc32(TypeBytes, Data);
        Span<byte> CrcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(CrcBytes, Crc);
        Output.Write(CrcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> Type, ReadOnlySpan<byte> Data)
    {
        var Crc = uint.MaxValue;
        foreach (var Value in Type)
        {
            Crc = Update(Crc, Value);
        }
        foreach (var Value in Data)
        {
            Crc = Update(Crc, Value);
        }
        return ~Crc;
    }

    private static uint Update(uint Crc, byte Value)
    {
        Crc ^= Value;
        for (var Bit = 0; Bit < 8; Bit++)
        {
            Crc = (Crc >> 1) ^ ((Crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        return Crc;
    }
}
