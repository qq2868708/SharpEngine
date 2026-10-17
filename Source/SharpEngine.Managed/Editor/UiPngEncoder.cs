using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SharpEngine.Editor;

internal static class UiPngEncoder
{
    private static readonly byte[] Signature =
        [137, 80, 78, 71, 13, 10, 26, 10];

    internal static byte[] Encode(NativeUiFrame frame)
    {
        var rowBytes = checked((int)frame.Width * 4);
        var expected = checked(rowBytes * (int)frame.Height);
        if (frame.BgraPixels.Length != expected)
            throw new InvalidDataException("Native UI frame byte count does not match its dimensions");

        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], frame.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        using var raw = new MemoryStream(checked((rowBytes + 1) * (int)frame.Height));
        for (var y = (int)frame.Height - 1; y >= 0; y--)
        {
            raw.WriteByte(0);
            var offset = y * rowBytes;
            for (var x = 0; x < rowBytes; x += 4)
            {
                raw.WriteByte(frame.BgraPixels[offset + x + 2]);
                raw.WriteByte(frame.BgraPixels[offset + x + 1]);
                raw.WriteByte(frame.BgraPixels[offset + x]);
                raw.WriteByte(frame.BgraPixels[offset + x + 3]);
            }
        }
        raw.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            raw.CopyTo(zlib);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type,
                                   ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type) crc = Update(crc, value);
        foreach (var value in data) crc = Update(crc, value);
        return ~crc;
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        return crc;
    }
}
