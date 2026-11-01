using System.Text.Json;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    private sealed record TileCase(string Id, byte Tile, ParityScenarioLevel Level, uint[] Keys, int X, int Y, int Death = 0, int InventoryIndex = -1, int InventoryCount = 0, int? Chips = null, Func<GuestSnapshot, bool>? Extra = null);
    private static IReadOnlyList<TileCase> BasicTileCases()
    {
        var cases = new List<TileCase>();

        void Adjacent(string id, byte tile, bool blocked = false, int death = 0, int inventory = -1)
        {
            var level = new ParityScenarioLevel();
            level.Tile(16, 15, tile);
            if (tile == 2)
            {
                level.Chips = 1;
            }

            cases.Add(new(id, tile, level, [0x27], blocked
                ? 15
                : 16, 15, death, inventory, inventory < 0
                ? 0
                : 1, tile == 2
                ? 0
                : null));
        }


        Adjacent("floor", 0);
        Adjacent("wall", 1, true);
        Adjacent("chip", 2);
        Adjacent("dirt", 11);
        Adjacent("gravel", 45);
        Adjacent("water-death", 3, death: 2);
        Adjacent("fire-death", 4, death: 1);
        Adjacent("bomb-death", 42, death: 3);
        Adjacent("invisible-wall", 5, true);
        Adjacent("fake-floor", 30);
        Adjacent("fake-wall", 31, true);
        Adjacent("hidden-wall", 44, true);
        Adjacent("hint", 47);
        Adjacent("socket-open", 34);
        Adjacent("toggle-wall-blocks", 37, true);
        Adjacent("toggle-floor-passes", 38);
        Adjacent("clone-machine-blocks-chip", 49, true);
        Adjacent("unused-bombed-tile-blocks", 53, true);
        for (byte i = 0; i < 8; i++)
        {
            Adjacent("pickup-" + (i + 100).ToString("X2"), (byte)(i + 100), inventory: i);
        }

        for (byte i = 0; i < 4; i++)
        {
            Adjacent("locked-door-" + i, (byte)(22 + i), true);
            var level = new ParityScenarioLevel();
            level.Tile(16, 15, (byte)(100 + i));
            level.Tile(17, 15, (byte)(22 + i));
            cases.Add(new("key-door-" + i, (byte)(22 + i), level, [0x27, 0x27], 17, 15, InventoryIndex: i, InventoryCount: i == 2
                ? 1
                : 0));
        }

        var socket = new ParityScenarioLevel
        {
            Chips = 1
        };
        socket.Tile(16, 15, 34);
        cases.Add(new("socket-needs-chips", 34, socket, [0x27], 15, 15));
        var popup = new ParityScenarioLevel();
        popup.Tile(16, 15, 46);
        cases.Add(new("popup-closes-after-leaving", 46, popup, [0x27, 0x25], 15, 15, Extra: s => s.Game[15 * 32 + 16] == 1));
        var blockWater = new ParityScenarioLevel();
        blockWater.Tile(16, 15, 10);
        blockWater.Tile(17, 15, 3);
        cases.Add(new("block-fills-water", 10, blockWater, [0x27], 16, 15, Extra: s => s.Game[15 * 32 + 17] == 11));
        var blockBomb = new ParityScenarioLevel();
        blockBomb.Tile(16, 15, 10);
        blockBomb.Tile(17, 15, 42);
        cases.Add(new("block-removes-bomb", 42, blockBomb, [0x27], 16, 15, Extra: s => s.Game[15 * 32 + 17] == 0));
        var teleport = new ParityScenarioLevel();
        teleport.Tile(16, 15, 41);
        teleport.Tile(18, 15, 41);
        cases.Add(new("teleport-and-exit", 41, teleport, [0x27], 19, 15));
        return cases;
    }

    [WindowsSuppliedNeFact]
    public void OriginalStartupAndExternalScenarioLevelsVerifyTerrainItemsDoorsAndDeaths() => RunTileCases(BasicTileCases(), "tile-scenarios");
    private static IReadOnlyList<TileCase> TimedTileCases()
    {
        var cases = new List<TileCase>();
        (int X, int Y, uint Key)[] directions = [(0, -1, 0x26), (-1, 0, 0x25), (0, 1, 0x28), (1, 0, 0x27)];
        for (int edge = 0; edge < 4; edge++)
        {
            for (int movement = 0; movement < 4; movement++)
            {
                foreach (bool leaving in new[]
                {
                    false,
                    true
                }

                )
                {
                    var level = new ParityScenarioLevel();
                    var d = directions[movement];
                    byte panel = (byte)(6 + edge);
                    if (leaving)
                    {
                        level.Tile(15, 15, 0x6c, panel);
                    }
                    else
                    {
                        level.Tile(15 + d.X, 15 + d.Y, panel);
                    }

                    bool blocked = movement == (leaving
                        ? edge
                        : (edge + 2) % 4);
                    cases.Add(new($"panel-{edge}-move-{movement}-leaving-{leaving}", panel, level, [d.Key], 15 + (blocked
                        ? 0
                        : d.X), 15 + (blocked
                        ? 0
                        : d.Y)));
                }
            }
        }

        for (int movement = 0; movement < 4; movement++)
        {
            foreach (bool leaving in new[]
            {
                false,
                true
            }

            )
            {
                var level = new ParityScenarioLevel();
                var d = directions[movement];
                if (leaving)
                {
                    level.Tile(15, 15, 0x6c, 48);
                }
                else
                {
                    level.Tile(15 + d.X, 15 + d.Y, 48);
                }

                bool blocked = leaving
                    ? movement is 2 or 3
                    : movement is 0 or 1;
                cases.Add(new($"panel-SE-move-{movement}-leaving-{leaving}", 48, level, [d.Key], 15 + (blocked
                    ? 0
                    : d.X), 15 + (blocked
                    ? 0
                    : d.Y)));
            }
        }

        foreach (var (tile, x, y) in new[]
        {
            (12, 17, 15),
            (13, 16, 16),
            (18, 16, 14),
            (19, 17, 15),
            (20, 15, 15),
            (27, 16, 16),
            (28, 16, 14)
        }

        )
        {
            var level = new ParityScenarioLevel();
            level.Tile(16, 15, (byte)tile);
            cases.Add(new("slide-" + tile.ToString("X2"), (byte)tile, level, [0x27], x, y, Extra: s => s.Word(0x80c) == 0));
        }

        var northWest = new ParityScenarioLevel();
        northWest.Tile(15, 14, 26);
        cases.Add(new("slide-ice-NW-turn-east", 26, northWest, [0x26], 16, 14, Extra: s => s.Word(0x80c) == 0));
        var southWest = new ParityScenarioLevel();
        southWest.Tile(15, 16, 29);
        cases.Add(new("slide-ice-SW-turn-east", 29, southWest, [0x28], 16, 16, Extra: s => s.Word(0x80c) == 0));
        for (int direction = 0; direction < 4; direction++)
        {
            var level = new ParityScenarioLevel();
            var d = directions[direction];
            byte swim = (byte)(60 + direction);
            level.Tile(15 + d.X, 15 + d.Y, 104);
            level.Tile(15 + 2 * d.X, 15 + 2 * d.Y, 3);
            cases.Add(new("flippers-swim-" + direction, swim, level, [d.Key, d.Key], 15 + 2 * d.X, 15 + 2 * d.Y, InventoryIndex: 4, InventoryCount: 1, Extra: s => s.Game[(15 + 2 * d.Y) * 32 + 15 + 2 * d.X] == swim));
        }

        for (int i = 0; i < 4; i++)
        {
            var level = new ParityScenarioLevel();
            level.Tile(16, 15, (byte)(104 + i));
            level.Tile(17, 15, 33);
            cases.Add(new("thief-removes-boot-" + i, 33, level, [0x27, 0x27], 17, 15, InventoryIndex: 4 + i, InventoryCount: 0));
        }

        var toggle = new ParityScenarioLevel();
        toggle.Tile(16, 15, 35);
        toggle.Tile(18, 15, 37);
        toggle.Tile(19, 15, 38);
        cases.Add(new("toggle-button-exchanges-wall-floor", 35, toggle, [0x27], 16, 15, Extra: s => s.Game[15 * 32 + 18] == 38 && s.Game[15 * 32 + 19] == 37));
        var closed = new ParityScenarioLevel();
        closed.Tile(16, 15, 43);
        cases.Add(new("trap-prevents-leaving", 43, closed, [0x27, 0x27], 16, 15));

        static byte[] Connection(params ushort[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

        var release = new ParityScenarioLevel();
        release.Tile(16, 15, 39);
        release.Tile(18, 12, 0x4e, 43);
        release.Fields.Add((4, Connection(16, 15, 18, 12, 0)));
        release.Fields.Add((10, new byte[] { 18, 12 }));
        cases.Add(new("brown-button-releases-trapped-tank", 39, release, [0x27], 16, 15, Extra: s => s.Game[12 * 32 + 18] == 43 && s.Word(0x928) == 1));
        var tank = new ParityScenarioLevel();
        tank.Tile(16, 15, 40);
        tank.Tile(18, 12, 0x4e);
        tank.Fields.Add((10, new byte[] { 18, 12 }));
        cases.Add(new("blue-button-reverses-tank", 40, tank, [0x27], 16, 15, Extra: s => s.Game.Take(1024).Contains((byte)0x4c)));
        var clone = new ParityScenarioLevel();
        clone.Tile(16, 15, 36);
        clone.Tile(18, 12, 0x4f, 49);
        clone.Fields.Add((5, Connection(16, 15, 18, 12)));
        clone.Fields.Add((10, new byte[] { 18, 12 }));
        cases.Add(new("red-button-clones-tank", 49, clone, [0x27], 16, 15, Extra: s => s.Game[12 * 32 + 18] == 0x4f && s.Game.Take(1024).Count(t => t == 0x4f) >= 2 && s.Word(0x928) >= 1));
        for (int direction = 0; direction < 4; direction++)
        {
            var level = new ParityScenarioLevel();
            byte tile = (byte)(14 + direction);
            var d = directions[direction];
            level.Tile(16, 15, 36);
            level.Tile(18, 12, tile, 49);
            level.Fields.Add((5, Connection(16, 15, 18, 12)));
            cases.Add(new("clone-directed-block-" + direction, tile, level, [0x27], 16, 15, Extra: s => s.Game[12 * 32 + 18] == tile && s.Game[(12 + d.Y) * 32 + 18 + d.X] == 10));
        }

        for (int i = 0; i < 4; i++)
        {
            var level = new ParityScenarioLevel();
            var d = directions[i];
            byte facing = (byte)(108 + i);
            cases.Add(new("chip-faces-" + i, facing, level, [d.Key], 15 + d.X, 15 + d.Y, Extra: s => s.Game[(15 + d.Y) * 32 + 15 + d.X] == facing));
        }

        return cases;
    }

    [WindowsSuppliedNeFact]
    public void OriginalDirectionalPanelsSlidingThiefToggleTrapAndFacingUseNormalInput() => RunTileCases(TimedTileCases(), "timed-tile-scenarios");
    private static void RunTileCases(IReadOnlyList<TileCase> cases, string evidenceName)
    {
        var observations = new List<object>();
        bool complete = false;
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath(), evidenceName);
        Directory.CreateDirectory(evidence);
        try
        {
            // Fresh sessions keep diagnostic segment-map observations bounded.
            foreach (var batch in cases.Chunk(8))
            {
                byte[] data = ParityScenarioLevel.Build(batch.Select(c => c.Level).ToArray());
                using var session = new ParitySession("tiles-" + observations.Count, data, ParityScenarioLevel.Title(0));
                for (int i = 0; i < batch.Length; i++)
                {
                    var test = batch[i];
                    if (i != 0)
                    {
                        nint go = Dialog(session.Process, session.Window, 0x77);
                        SetField(go, 100, (i + 1).ToString());
                        SetField(go, 101, ParityScenarioLevel.Password(i));
                        Command(go, 1);
                        Until(() => !IsWindow(go) && Text(session.Window).Contains(ParityScenarioLevel.Title(i), StringComparison.Ordinal), session.Process, "Scenario selection failed");
                    }

                    Key(session.Window, 0x20);
                    foreach (uint key in test.Keys)
                    {
                        Key(session.Window, key);
                        Thread.Sleep(300);
                    }

                    GuestSnapshot? actual = null;
                    Until(() =>
                    {
                        actual = Observe(session);
                        return actual.X == test.X && actual.Y == test.Y && actual.Death == test.Death && (test.InventoryIndex < 0 || actual.Inventory[test.InventoryIndex] == test.InventoryCount) && (test.Chips is null || actual.Global(0x1692) == test.Chips) && (test.Extra is null || test.Extra(actual));
                    }, session.Process, "Original scenario differs: " + test.Id);
                    byte actualTile = actual!.Game[actual.Y * 32 + actual.X];
                    if (test.Death is 1 or 2)
                    {
                        Assert.Equal(test.Death == 1
                            ? (byte)52
                            : (byte)51, actualTile);
                    }

                    observations.Add(new
                    {
                        test.Id,
                        TileValue = test.Tile,
                        ExpectedX = test.X,
                        ExpectedY = test.Y,
                        ExpectedDeath = test.Death,
                        ActualX = actual.X,
                        ActualY = actual.Y,
                        ActualDeath = actual.Death,
                        ActualTile = actualTile,
                        Inventory = actual.Inventory,
                        ChipsRemaining = actual.Global(0x1692),
                        InputDataSha256 = Hash(data),
                        ObservationPolicy = "ReadProcessMemory only; no guest-state writes or direct game-function calls"
                    });
                    File.WriteAllText(Path.Combine(evidence, "partial.json"), JsonSerializer.Serialize(observations));
                    if (test.Death != 0)
                    {
                        nint death = 0;
                        Until(() =>
                        {
                            death = Find(session.Process, "#32770");
                            return death != 0;
                        }, session.Process, "Original death message did not appear");
                        Assert.True(PostMessageW(death, 0x10, 0, 0));
                        Until(() => !IsWindow(death), session.Process, "Original death message did not close");
                    }
                }

                session.Close();
            }

            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "terrain.json"), JsonSerializer.Serialize(new
            {
                Format = "NeTerrainScenarios/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ScenarioCount = cases.Count,
                ExternalScenarioInput = true,
                FixtureInitialization = false,
                Observations = observations
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
