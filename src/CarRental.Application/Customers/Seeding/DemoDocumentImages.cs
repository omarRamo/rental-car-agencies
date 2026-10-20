using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CarRental.Application.Customers.Seeding;

/// <summary>
/// Generated placeholder images of identity documents for the demo data (a coloured card with a photo frame, text
/// lines and "specimen" stripes): no real document is ever shipped. Minimal PNG encoder (RGB, 8 bits, no filter).
/// </summary>
internal static class DemoDocumentImages
{
    private const int Width = 480;
    private const int Height = 300;

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>A PNG card in the colour (RGB) of the document type.</summary>
    public static byte[] Card(byte red, byte green, byte blue, bool back = false)
    {
        var pixels = new byte[Height * ((Width * 3) + 1)];
        for (var y = 0; y < Height; y++)
        {
            var row = y * ((Width * 3) + 1);
            pixels[row] = 0; // filter: none
            for (var x = 0; x < Width; x++)
            {
                var (r, g, b) = PixelAt(x, y, red, green, blue, back);
                var offset = row + 1 + (x * 3);
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], Height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: RGB
        WriteChunk(png, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(pixels);
            }

            WriteChunk(png, "IDAT", compressed.ToArray());
        }

        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static (byte R, byte G, byte B) PixelAt(int x, int y, byte red, byte green, byte blue, bool back)
    {
        const byte Light = 0xF4;
        var border = x < 6 || y < 6 || x >= Width - 6 || y >= Height - 6;
        var band = y < 56;
        var photo = !back && x is >= 28 and < 148 && y is >= 84 and < 244;
        // Text lines: 10 px high, every 36 px from y = 88.
        var line = x is >= 172 and < 440 && y is >= 88 and < 260 && (y - 88) % 36 < 10;
        var stripe = ((x + y) / 14) % 9 == 0;

        if (border || band)
        {
            return (red, green, blue);
        }

        if (photo)
        {
            return (0xC9, 0xCF, 0xD6);
        }

        if (line || (back && y is >= 120 and < 132 && x is >= 28 and < 452))
        {
            return (0x9A, 0xA4, 0xAF);
        }

        return stripe ? ((byte)(Light - 18), (byte)(Light - 14), (byte)(Light - 10)) : (Light, Light, Light);
    }

    private static void WriteChunk(Stream png, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);

        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        png.Write(crcBytes);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
