using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace Translator.Binary;

public sealed record NeDecodedBitmap(int Width, int Height, int BitCount, uint Compression, bool TopDown, int RowStride, int PixelDataOffset, string RgbaSha256, [property: JsonIgnore] byte[] Rgba32);
/// <summary>Focused packed DIB decoder: BITMAPINFOHEADER, indexed BI_RGB and BI_RLE4 found in the supplied NE.</summary>
public static class NeBitmapDecoder
{
    public static NeDecodedBitmap Decode(ReadOnlySpan<byte> bytes, int maximumPixels = 16 * 1024 * 1024)
    {
        if (maximumPixels < 1 || maximumPixels > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPixels));
        }

        byte[] data = bytes.ToArray();

        void Bounds(int offset, long length) => NeResourceCatalog.ResourceBounds(data.Length, offset, length);


        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));


        int I32(int p) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));


        ushort W(int p) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));

        Bounds(0, 4);
        uint header = U32(0);
        Bounds(0, header);
        if (header != 40)
        {
            throw new NotSupportedException($"DIB header size {header} is unsupported; expected BITMAPINFOHEADER (40).");
        }

        int width = I32(4), signedHeight = I32(8), bits = W(14);
        uint compression = U32(16), declaredSize = U32(20), used = U32(32);
        if (width < 1 || signedHeight == 0 || signedHeight == int.MinValue || W(12) != 1)
        {
            throw new InvalidDataException("Invalid DIB dimensions or planes.");
        }

        int height = Math.Abs(signedHeight);
        bool topDown = signedHeight < 0;
        long pixels = (long)width * height;
        if (pixels > maximumPixels)
        {
            throw new InvalidDataException("DIB decoded pixel budget exceeded.");
        }

        if (bits is not (1 or 4 or 8 or 24 or 32) || compression != 0 && !(bits == 4 && compression == 2))
        {
            throw new NotSupportedException($"DIB bit depth {bits}, compression {compression} is unsupported.");
        }

        if (topDown && compression != 0)
        {
            throw new InvalidDataException("Compressed DIB cannot use top-down rows.");
        }

        long palette = used != 0
            ? used
            : bits <= 8
                ? 1L << bits
                : 0;
        if (palette > 256 || bits <= 8 && palette > 1L << bits)
        {
            throw new InvalidDataException("Invalid DIB palette size.");
        }

        Bounds(40, palette * 4);
        int offset = 40 + (int)palette * 4;
        int stride = checked((int)(((long)width * bits + 31) / 32 * 4));
        if (declaredSize != 0)
        {
            Bounds(offset, declaredSize);
        }

        var rgba = new byte[(int)pixels * 4];
        var indexes = bits <= 8
            ? new byte[(int)pixels]
            : Array.Empty<byte>();
        if (compression == 2)
        {
            if (declaredSize == 0)
            {
                throw new InvalidDataException("RLE4 image requires a bounded declared image size.");
            }

            Rle4(data.AsSpan(offset, (int)declaredSize).ToArray(), indexes, width, height);
        }
        else
        {
            long required = (long)stride * height;
            Bounds(offset, required);
            if (declaredSize != 0 && declaredSize < required)
            {
                throw new InvalidDataException("DIB declared image size is smaller than its rows.");
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int row = offset + y * stride, index = y * width + x;
                    if (bits == 1)
                    {
                        indexes[index] = (byte)((data[row + x / 8] >> (7 - x % 8)) & 1);
                    }
                    else if (bits == 4)
                    {
                        indexes[index] = (byte)((data[row + x / 2] >> (x % 2 == 0
                            ? 4
                            : 0)) & 15);
                    }
                    else if (bits == 8)
                    {
                        indexes[index] = data[row + x];
                    }
                    else
                    {
                        int source = row + x * (bits / 8), target = ((topDown
                            ? y
                            : height - y - 1) * width + x) * 4;
                        rgba[target] = data[source + 2];
                        rgba[target + 1] = data[source + 1];
                        rgba[target + 2] = data[source];
                        rgba[target + 3] = 255;
                    }
                }
            }
        }

        if (bits <= 8)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int value = indexes[y * width + x];
                    if (value >= palette)
                    {
                        throw new InvalidDataException("DIB pixel references a missing palette entry.");
                    }

                    int source = 40 + value * 4, target = ((topDown
                        ? y
                        : height - y - 1) * width + x) * 4;
                    rgba[target] = data[source + 2];
                    rgba[target + 1] = data[source + 1];
                    rgba[target + 2] = data[source];
                    rgba[target + 3] = 255;
                }
            }
        }

        return new(width, height, bits, compression, topDown, stride, offset, NeResourceCatalog.Hash(rgba), rgba);
    }

    private static void Rle4(byte[] data, byte[] pixels, int width, int height)
    {
        int p = 0, x = 0, y = 0;

        byte Read()
        {
            NeResourceCatalog.ResourceBounds(data.Length, p, 1);
            return data[p++];
        }


        void Pixel(int value)
        {
            if (x >= width || y >= height)
            {
                throw new InvalidDataException("RLE4 run exceeds bitmap bounds.");
            }

            pixels[y * width + x++] = (byte)value;
        }

        while (p < data.Length)
        {
            int count = Read(), value = Read();
            if (count != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    Pixel((value >> (i % 2 == 0
                        ? 4
                        : 0)) & 15);
                }
            }
            else if (value == 0)
            {
                x = 0;
                if (++y > height)
                {
                    throw new InvalidDataException("RLE4 row exceeds height.");
                }
            }
            else if (value == 1)
            {
                return;
            }
            else if (value == 2)
            {
                x += Read();
                y += Read();
                if (x > width || y >= height)
                {
                    throw new InvalidDataException("RLE4 delta exceeds bitmap bounds.");
                }
            }
            else
            {
                int bytes = (value + 1) / 2;
                for (int i = 0; i < bytes; i++)
                {
                    int pair = Read();
                    Pixel(pair >> 4);
                    if (i * 2 + 1 < value)
                    {
                        Pixel(pair & 15);
                    }
                }

                if ((bytes & 1) != 0)
                {
                    Read();
                }
            }
        }

        throw new InvalidDataException("Unterminated RLE4 stream.");
    }
}
