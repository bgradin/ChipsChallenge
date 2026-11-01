using System.Text.Json;
using Translator.Binary;
using Translator.ChipsChallenge;
using Xunit;
using Translator.TestSupport;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [WindowsSuppliedNeFact]
    public void OriginalNativeRendererPaintsEveryMatrixSpriteAndMaskInBothDisplayModes()
    {
        byte[] tiles = Enumerable.Range(0, 112).Where(t => t is not (32 or 54 or 55 or 56) && t < 108).Select(t => (byte)t).ToArray();
        var batches = tiles.Chunk(80).Concat(new[] { Array.Empty<byte>(), Array.Empty<byte>() }).ToArray();
        var observations = new List<object>();
        bool complete = false;
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath(), "sprite-catalog");
        Directory.CreateDirectory(evidence);
        using var catalog = new NeResourceCatalog(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!));
        try
        {
            for (int i = 0; i < batches.Length; i++)
            {
                var level = new ParityScenarioLevel();
                int at = 0;
                for (int y = 11; y <= 19; y++)
                {
                    for (int x = 11; x <= 19; x++)
                    {
                        if (x == 15 && y == 15 || at == batches[i].Length)
                        {
                            continue;
                        }

                        byte tile = batches[i][at++];
                        level.Tile(x, y, tile, tile is >= 60 and <= 63
                            ? (byte)3
                            : tile >= 64
                                ? (byte)45
                                : (byte)0);
                    }
                }

                level.Tile(15, 15, (byte)(108 + i));
                using var session = new ParitySession("sprites-" + i, ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
                Key(session.Window, 0x20);
                nint board = 0;
                EnumChildWindows(session.Window, (w, _) =>
                {
                    if (Class(w) == "BoardClass")
                    {
                        board = w;
                    }

                    return true;
                }, 0);
                foreach (bool mono in new[]
                {
                    true,
                    false
                }

                )
                {
                    if ((Checked(session.Window, 0x7a) == 0) != mono)
                    {
                        Command(session.Window, 0x7a);
                        Until(() => (Checked(session.Window, 0x7a) == 0) == mono, session.Process, "Sprite display mode did not change");
                    }

                    string name = mono
                        ? "OBJ32_1"
                        : "OBJ32_4";
                    int index = catalog.Resources.ToList().FindIndex(r => r.Type.Id == 2 && r.Identity.Name == name) + 1;
                    var atlas = NeBitmapDecoder.Decode(catalog.Extract(index).Bytes);
                    var state = Observe(session);
                    string expected = Hash(ChipsSceneProfile.Expected(atlas, atlas, state.Game, 1, 288, 288, 0, 0, 288, 288, authenticLevel: true)), actual = "";
                    Until(() =>
                    {
                        actual = Hash(CaptureBoard(board));
                        return expected == actual;
                    }, session.Process, "Original sprite/mask catalog differs: " + name);
                    observations.Add(new
                    {
                        Atlas = name,
                        TileValues = batches[i].Append((byte)(108 + i)).Select(t => (int)t).ToArray(),
                        ExpectedPixelsSha256 = expected,
                        ActualPixelsSha256 = actual,
                        MonsterInitialListEmpty = true
                    });
                }

                session.Close();
            }

            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "sprites.json"), JsonSerializer.Serialize(new
            {
                Format = "NeSpriteCatalog/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ExpectedTileValues = 108,
                ExternalScenarioInput = true,
                FixtureInitialization = false,
                Observations = observations,
                Policy = "Native static painting/masking only. Gameplay semantics require the separate terrain, actor, inventory and ending scenarios."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [WindowsSuppliedNeFact]
    public void OriginalBoardPaintMatchesBothResourceAtlasesAndPreservesPartialClipBoundaries()
    {
        using var session = new ParitySession("rendering");
        bool complete = false;
        var observations = new List<object>();
        using var resources = new NeResourceCatalog(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!));
        try
        {
            Key(session.Window, 0x20);
            nint board = 0;
            EnumChildWindows(session.Window, (w, _) =>
            {
                if (Class(w) == "BoardClass")
                {
                    board = w;
                }

                return true;
            }, 0);
            Assert.NotEqual(0, board);
            foreach (bool monochrome in new[]
            {
                true,
                false,
                true
            }

            )
            {
                if ((Checked(session.Window, 0x7a) == 0) != monochrome)
                {
                    Command(session.Window, 0x7a);
                    Until(() => (Checked(session.Window, 0x7a) == 0) == monochrome, session.Process, "Original display-mode command failed");
                }

                string name = monochrome
                    ? "OBJ32_1"
                    : "OBJ32_4";
                int index = resources.Resources.ToList().FindIndex(r => r.Type.Id == 2 && r.Identity.Name == name) + 1;
                var atlas = NeBitmapDecoder.Decode(resources.Extract(index).Bytes);
                var state = Observe(session);
                foreach (var clip in new[]
                {
                    new Rect
                    {
                        Left = 0,
                        Top = 0,
                        Right = 288,
                        Bottom = 288
                    },
                    new Rect
                    {
                        Left = 7,
                        Top = 11,
                        Right = 147,
                        Bottom = 189
                    },
                    new Rect
                    {
                        Left = 245,
                        Top = 251,
                        Right = 288,
                        Bottom = 288
                    }
                }

                )
                {
                    string expected = Hash(ChipsSceneProfile.Expected(atlas, atlas, state.Game, 1, 288, 288, clip.Left, clip.Top, clip.Right, clip.Bottom, authenticLevel: true));
                    string actual = "";
                    Until(() =>
                    {
                        actual = Hash(CaptureBoard(board, clip));
                        return actual == expected;
                    }, session.Process, "Native original board render/clip differs: " + name);
                    observations.Add(new
                    {
                        Atlas = name,
                        clip.Left,
                        clip.Top,
                        clip.Right,
                        clip.Bottom,
                        ExpectedPixelsSha256 = expected,
                        ActualPixelsSha256 = actual
                    });
                }
            }

            session.Close();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "rendering.json"), JsonSerializer.Serialize(new
            {
                Format = "NeRendering/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                OriginalCapabilityPolicyPreserved = true,
                FixtureInitialization = false,
                Observations = observations
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
