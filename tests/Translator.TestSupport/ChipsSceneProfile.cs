using System.Security.Cryptography;
using Translator.Binary;

namespace Translator.ChipsChallenge;

public sealed record SceneResource(string Name, int Offset, int Length, string Sha256, int Width, int Height);
public sealed record SceneWrite(ushort Offset, byte[] Bytes, string Purpose);
public sealed record SceneWindow(int Parent, string Procedure, short X, short Y, short Width, short Height, uint Style, ushort Value = 0, ushort Flags = 0);
public sealed record ScenePixels(int Window, short Width, short Height, short Left, short Top, short Right, short Bottom, string Sha256);
public sealed record ChipsScenePlan(IReadOnlyList<SceneResource> Resources, IReadOnlyList<SceneWrite> Writes, IReadOnlyList<SceneWindow> Windows, IReadOnlyList<ScenePixels> Pixels)
{
    public object DataReservation => new
    {
        Offset = 0x4000,
        Length = 0xa42,
        MinimumSegmentSize = 0x9000,
        CallbackStackOffset = 0x5000,
        CallbackStackBytes = 4096,
        CallbackStackCount = 4,
        Ownership = "Native descriptor extends automatic DS beyond original data/stack/heap; private fixture only, no LocalAlloc claim"
    };
    public bool AuthenticLevel
    {
        get; init;
    }
    public bool PlayerMovement
    {
        get; init;
    }
    public string Policy => PlayerMovement
        ? "Authentic level with original input, movement, collision, tick and painting code; bounded supported paths and fatal exclusions"
        : AuthenticLevel
            ? "Original runtime level initialization and painting; only viewport/window/graphics bootstrap is authored; gameplay timer paused"
            : "Controlled static fixture; original board/info/counter/inventory painting; no timers, level loading, original main-window initialization or gameplay.";
}

/// <summary>Hash-pinned game fixture and independent reference composition; no native game logic.</summary>
public static class ChipsSceneProfile
{
    public static readonly string[] Functions = ["0002:007A", "0002:00C4", "0002:01CA", "0002:0232", "0002:10CE", "0002:274E", "0002:2866", "0002:296A", "0002:2A9A", "0003:2A3E", "0009:0000", "0009:0028", "0009:004E", "0009:00BC", "0009:00EA"];
    public static ChipsScenePlan Inspect(ReadOnlySpan<byte> bytes)
    {
        ChipsLifecycleProfile.Validate(bytes);
        using var catalog = new NeResourceCatalog(bytes);
        var decoded = new List<NeDecodedBitmap>();
        var resources = new[]
        {
            "OBJ32_4",
            "INFOWND"
        }.Select(name =>
        {
            int index = catalog.Resources.ToList().FindIndex(r => r.Type.Id == 2 && r.Identity.Name == name);
            if (index < 0)
            {
                throw new InvalidDataException("Missing original scene resource " + name);
            }

            var raw = catalog.Extract(index + 1);
            var bitmap = NeBitmapDecoder.Decode(raw.Bytes);
            decoded.Add(bitmap);
            return new SceneResource(name, raw.Metadata.FileOffset, raw.Metadata.Length, raw.PayloadSha256, bitmap.Width, bitmap.Height);
        }).ToArray();
        if (resources[0].Width != 416 || resources[0].Height != 512 || resources[1].Width != 154 || resources[1].Height != 300)
        {
            throw new InvalidDataException("Unexpected original scene bitmap dimensions.");
        }

        var writes = new List<SceneWrite>();

        void Word(ushort offset, ushort value, string purpose) => writes.Add(new(offset, [(byte)value, (byte)(value >> 8)], purpose));

        byte[] state = new byte[0xa42];
        // A recognizable authored scene, not a claim to load an original level.
        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 9; x++)
            {
                state[y * 32 + x] = (byte)(x == 0 || y == 0 || x == 8 || y == 8
                    ? 1
                    : 0);
            }
        }

        state[2 * 32 + 2] = 2;
        state[2 * 32 + 3] = 2;
        state[3 * 32 + 6] = 0x15; // chips and exit
        state[4 * 32 + 4] = 0x6e;
        state[0x400 + 4 * 32 + 4] = 3; // original transparent Chip over water
        state[5 * 32 + 2] = 3;
        state[5 * 32 + 3] = 3;
        state[6 * 32 + 6] = 4; // water and fire
        state[0x808] = 4;
        state[0x80a] = 4;
        state[0xa28] = 9;
        state[0xa2a] = 9;
        writes.Add(new(0x4000, state, "Private fixture at DS:4000, GameState size A42: upper/lower layers, Chip=(4,4), viewport=(0,0,9,9), placard/ending/offsets zero"));
        Word(0x1680, 0x4000, "GameStatePtr");
        Word(0x1734, 0x300, "MainDC: private word token for original tile atlas DC");
        Word(0x22, 1, "Var22: disable original timer path");
        Word(0x24, 0, "GamePaused");
        Word(0xa34, 0, "DebugModeEnabled");
        foreach (ushort offset in new ushort[]
        {
            0x1682,
            0x1684,
            0x1686,
            0x1688,
            0x168a,
            0x168c,
            0x168e,
            0x1690
        }

        )
        {
            Word(offset, 1, "Inventory possession count");
        }

        Word(0xa18, 4, "ColorMode: original four-bit color atlas");
        Word(0x169e, 32, "HorizontalPadding");
        Word(0x16a0, 32, "VerticalPadding (large-screen original layout)");
        // Coordinates and parent hierarchy from CreateWindowsAndInitGame, 0002:08E8–0AF6.
        SceneWindow[] windows = [new(-1, "", 0, 0, 512, 352, 0x02c80000), new(0, "0002:274E", 32, 32, 288, 288, 0x50000000), new(0, "0002:2866", 339, 26, 154, 300, 0x52000000), new(2, "0002:296A", 45, 34, 55, 29, 0x54000000, 1), new(2, "0002:296A", 45, 96, 55, 29, 0x54000000, 999), new(2, "0002:296A", 45, 186, 55, 29, 0x54000000, 2), new(2, "0002:2A9A", 13, 221, 128, 64, 0x54000000)];
        var cases = new[]
        {
            (1, 288, 288, 0, 0, 288, 288),
            (1, 288, 288, 33, 19, 251, 207),
            (6, 128, 64, 0, 0, 128, 64),
            (2, 154, 300, 0, 0, 154, 300)
        };
        var pixels = cases.Select(c => new ScenePixels(c.Item1, (short)c.Item2, (short)c.Item3, (short)c.Item4, (short)c.Item5, (short)c.Item6, (short)c.Item7, Hash(Expected(decoded[0], decoded[1], state, c.Item1, c.Item2, c.Item3, c.Item4, c.Item5, c.Item6, c.Item7)))).ToArray();
        return new(resources, writes, windows, pixels);
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    // Independently compose the original atlas using its top-down decoded RGBA palette pixels.
    public static byte[] Expected(NeDecodedBitmap atlas, NeDecodedBitmap info, byte[] state, int window, int width, int height, int left, int top, int right, int bottom, bool authenticLevel = false, ushort yellowKeys = 0, IReadOnlyList<ushort>? inventory = null)
    {
        byte[] result = new byte[width * height * 4];
        for (int i = 0; i < result.Length; i += 4)
        {
            result[i] = 0x66;
            result[i + 1] = 0x44;
            result[i + 2] = 0x22;
        }

        uint Pixel(NeDecodedBitmap b, int x, int y)
        {
            int i = (y * b.Width + x) * 4;
            return (uint)(b.Rgba32[i + 2] | b.Rgba32[i + 1] << 8 | b.Rgba32[i] << 16);
        }

        uint Tile(int tile, int x, int y) => Pixel(atlas, (tile / 16) * 32 + x, (tile % 16) * 32 + y);

        for (int y = Math.Max(0, top); y < Math.Min(height, bottom); y++)
        {
            for (int x = Math.Max(0, left); x < Math.Min(width, right); x++)
            {
                uint pixel;
                if (window == 2)
                {
                    pixel = Pixel(info, x, y);
                }
                else
                {
                    int vx = authenticLevel
                        ? BitConverter.ToUInt16(state, 0xa24)
                        : 0, vy = authenticLevel
                        ? BitConverter.ToUInt16(state, 0xa26)
                        : 0;
                    int cell = (vy + y / 32) * 32 + vx + x / 32;
                    int tile = window == 6
                        ? (authenticLevel
                            ? (inventory is not null
                                ? (inventory[new[] { 1, 0, 3, 2, 6, 7, 5, 4 }[(y / 32) * 4 + x / 32]] > 0
                                    ? new[]
                    {
                        0x65,
                        0x64,
                        0x67,
                        0x66,
                        0x6a,
                        0x6b,
                        0x69,
                        0x68
                    }[(y / 32) * 4 + x / 32]
                                    : 0)
                                : (yellowKeys > 0 && y < 32 && x >= 64 && x < 96
                                    ? 0x67
                                    : 0))
                            : new[]
                    {
                        0x65,
                        0x64,
                        0x67,
                        0x66,
                        0x6a,
                        0x6b,
                        0x69,
                        0x68
                    }[(y / 32) * 4 + x / 32])
                        : state[cell];
                    int lower = window == 6
                        ? 0
                        : state[0x400 + cell];
                    pixel = lower != 0 && tile >= 0x40 && tile <= 0x6f
                        ? (Tile(lower, x % 32, y % 32) | Tile(tile + 0x60, x % 32, y % 32)) & Tile(tile + 0x30, x % 32, y % 32)
                        : Tile(tile, x % 32, y % 32);
                }

                int p = (y * width + x) * 4;
                result[p] = (byte)pixel;
                result[p + 1] = (byte)(pixel >> 8);
                result[p + 2] = (byte)(pixel >> 16);
            }
        }

        return result;
    }
}
