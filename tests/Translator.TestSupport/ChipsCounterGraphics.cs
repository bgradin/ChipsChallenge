using System.Security.Cryptography;
using Translator.Binary;

namespace Translator.ChipsChallenge;

public sealed record CounterPixelCase(ushort Value, ushort Flags, ushort ColorMode, ushort Width, ushort Height, short ClipLeft, short ClipTop, short ClipRight, short ClipBottom, uint Background, string BgrxSha256);
public sealed record CounterGraphicsPlan(int OriginalSize, string OriginalSha256, int ResourceOffset, int ResourceLength, string ResourceSha256, IReadOnlyList<CounterPixelCase> Cases)
{
    public string Startup => "0009:004E";
    public string Shutdown => "0009:00BC";
    public string Resource => "RT_BITMAP/#200";
    public string AssetPolicy => "External CHIPS.EXE required at runtime; whole-file SHA-256 validated before resource use. No resource pixels embedded.";
}

/// <summary>Game metadata and an independent pixel oracle. No painting behavior is installed in the runtime.</summary>
public static class ChipsCounterGraphics
{
    public const ushort MainInstanceOffset = 0x172a, ColorModeOffset = 0xa18, ResourceHandleOffset = 0x1720, DigitDataOffset = 0x16c4, DigitArrayOffset = 0x16f0;

    public static CounterGraphicsPlan Inspect(ReadOnlySpan<byte> bytes)
    {
        ChipsLifecycleProfile.Validate(bytes);
        using var catalog = new NeResourceCatalog(bytes);
        int index = catalog.Resources.ToList().FindIndex(r => r.Type.Id == 2 && r.Identity.Id == 200);
        if (index < 0)
        {
            throw new InvalidDataException("Original digit bitmap #200 is missing.");
        }

        var resource = catalog.Extract(index + 1);
        var bitmap = NeBitmapDecoder.Decode(resource.Bytes);
        if (bitmap.Width != 17 || bitmap.Height != 552 || bitmap.BitCount != 4 || bitmap.Compression != 0 || bitmap.PixelDataOffset != 104 || bitmap.RowStride != 12)
        {
            throw new InvalidDataException("Digit resource layout does not match original LoadDigits/DrawDigit assumptions.");
        }

        var specs = new[]
        {
            (123, 0, 4, 85, 45, 0, 0, 85, 45),
            (7, 0, 4, 85, 45, 0, 0, 85, 45),
            (907, 1, 4, 85, 45, 0, 0, 85, 45),
            (123, 0, 1, 85, 45, 0, 0, 85, 45),
            (0, 2, 4, 85, 45, 0, 0, 85, 45),
            (123, 0, 4, 85, 45, 25, 13, 52, 28),
            (123, 0, 4, 31, 17, 0, 0, 31, 17)
        };
        var cases = specs.Select(s =>
        {
            var fixture = new CounterPixelCase((ushort)s.Item1, (ushort)s.Item2, (ushort)s.Item3, (ushort)s.Item4, (ushort)s.Item5, (short)s.Item6, (short)s.Item7, (short)s.Item8, (short)s.Item9, 0x00224466, "");
            return fixture with
            {
                BgrxSha256 = Convert.ToHexString(SHA256.HashData(Expected(bitmap, fixture))).ToLowerInvariant()
            };
        }).ToArray();
        return new(bytes.Length, catalog.ExecutableSha256, resource.Metadata.FileOffset, resource.Metadata.Length, resource.PayloadSha256, cases);
    }

    // Reference composition uses decoded, top-down RGBA pixels, not native GDI or translated code.
    public static byte[] Expected(NeDecodedBitmap bitmap, CounterPixelCase fixture)
    {
        byte[] result = new byte[fixture.Width * fixture.Height * 4];
        for (int i = 0; i < result.Length; i += 4)
        {
            result[i] = (byte)fixture.Background;
            result[i + 1] = (byte)(fixture.Background >> 8);
            result[i + 2] = (byte)(fixture.Background >> 16);
        }

        int[] digits = (fixture.Flags & 2) != 0
            ? [11, 11, 11]
            : [fixture.Value >= 100
                ? fixture.Value / 100
                : 10, fixture.Value >= 10
                ? fixture.Value / 10 % 10
                : 10, fixture.Value % 10];
        int bank = (fixture.Flags & 1) != 0 || fixture.ColorMode == 1
            ? 12
            : 0;
        int left = (fixture.Width - 51) / 2, top = (fixture.Height - 23) / 2;
        for (int digit = 0; digit < 3; digit++)
        {
            for (int y = 0; y < 23; y++)
            {
                for (int x = 0; x < 17; x++)
                {
                    int dx = left + digit * 17 + x, dy = top + y;
                    if (dx < 0 || dx >= fixture.Width || dy < 0 || dy >= fixture.Height || dx < fixture.ClipLeft || dx >= fixture.ClipRight || dy < fixture.ClipTop || dy >= fixture.ClipBottom)
                    {
                        continue;
                    }

                    int src = ((bitmap.Height - (bank + digits[digit] + 1) * 23 + y) * 17 + x) * 4, dst = (dy * fixture.Width + dx) * 4;
                    result[dst] = bitmap.Rgba32[src + 2];
                    result[dst + 1] = bitmap.Rgba32[src + 1];
                    result[dst + 2] = bitmap.Rgba32[src];
                }
            }
        }

        return result;
    }
}
