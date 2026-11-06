using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    // Independently authored regressions from https://chipschallenge.fandom.com/wiki/Glitch.
    // These exercise the delivered executable and original loader, not a replacement rules engine.
    private static byte[] GlitchConnection(params ushort[] words) => words.SelectMany(BitConverter.GetBytes).ToArray();

    [WindowsSuppliedNeFact]
    public void Glitch00TileActsAsWallDespiteItsBugArtwork()
    {
        var level = new ParityScenarioLevel();
        level.Tile(16, 15, 112);
        RunTileCases([new("00Tile-blocks-chip", 112, level, [0x27], 15, 15,
            Extra: s => s.Game[15 * 32 + 16] == 112 && s.Word(0x928) == 0)], "glitch-00tile");
    }

    [WindowsSuppliedNeFact]
    public void GlitchButtonSmashFlickMovesBlockWithoutTogglingWalls()
    {
        var cases = new List<TileCase>();
        foreach (bool flick in new[] { false, true })
        {
            var level = new ParityScenarioLevel();
            // Entry from west is forbidden by the west edge of the thin wall,
            // but the push still moves the block. Floor is the positive control.
            level.Tile(16, 15, 10, flick ? (byte)7 : (byte)0);
            level.Tile(17, 15, 35);
            level.Tile(18, 18, 37);
            level.Tile(19, 18, 38);
            cases.Add(new("button-smash-green-" + flick, 35, level, [0x27], flick ? 15 : 16, 15,
                Extra: s => s.Game[15 * 32 + 17] == 10 && s.Game[1024 + 15 * 32 + 17] == 35
                    && s.Game[18 * 32 + 18] == (flick ? 37 : 38)
                    && s.Game[18 * 32 + 19] == (flick ? 38 : 37)));
        }
        RunTileCases(cases, "glitch-button-smash");
    }

    [WindowsSuppliedNeFact]
    public void GlitchControllerDirectsBugsParameciaAndTeethOutOfTraps()
    {
        var cases = new List<TileCase>();
        foreach (byte family in new byte[] { 0x40, 0x54, 0x60 })
        {
            var level = new ParityScenarioLevel();
            level.Tile(16, 15, 39);
            // A trapped east-facing ball precedes the affected north-facing
            // monster. Its own trap is never released, so its direction is stable.
            level.Tile(11, 11, 0x5b, 43);
            level.Tile(18, 12, family, 43);
            level.Tile(18, 11, 1);
            level.Tile(17, 12, 1);
            level.Tile(18, 13, 1);
            level.Fields.Add((4, GlitchConnection(16, 15, 18, 12, 0)));
            level.Fields.Add((10, new byte[] { 11, 11, 18, 12 }));
            cases.Add(new("controller-east-" + family.ToString("X2"), family, level, [0x27], 16, 15,
                Extra: s => s.Game[12 * 32 + 18] == 43 && s.Word(0x928) == 2));
        }
        RunTileCases(cases, "glitch-controller");
    }

    [WindowsSuppliedNeFact]
    public void GlitchBossClonesNorthFacingBugInPredecessorsEastDirection()
    {
        var level = new ParityScenarioLevel();
        level.Tile(16, 15, 36);
        level.Tile(18, 12, 0x40, 49);
        level.Tile(19, 12, 43); // Capture the east-going clone for stable observation.
        level.Tile(11, 11, 0x5b, 43);
        level.Fields.Add((10, new byte[] { 11, 11 }));
        level.Fields.Add((5, GlitchConnection(16, 15, 18, 12)));
        RunTileCases([new("boss-east-overrides-north", 49, level, [0x27], 16, 15,
            Extra: s => s.Game[12 * 32 + 18] == 0x40
                && s.Game[12 * 32 + 19] == 0x43
                && s.Word(0x928) == 2)], "glitch-boss");
    }

    [WindowsSuppliedNeFact]
    public void GlitchDataResettingClonesThroughVirtualRow32AndCopiesOldDataIntoMap()
    {
        var cases = new List<TileCase>();
        // Both bytes of time/chip counts are tested independently. The real
        // remaining-chip counter is deliberately distinct from the header copy.
        foreach (int column in new[] { 0, 2, 4, 5, 6, 7, 8, 10 })
        {
            var level = new ParityScenarioLevel { TimeLimit = 0x0101, Chips = 0x0101 };
            level.Tile(16, 15, 36);
            level.Tile(column, 0, 0, 14); // Buried north clone block.
            level.Tile(column, 31, 0);
            level.Fields.Add((5, GlitchConnection(16, 15, (ushort)column, 32)));
            int expectedX = column == 8 ? 0 : 16;
            int expectedY = column == 10 ? 0 : 15;
            int former = column switch { 0 or 2 or 4 or 5 or 6 or 7 => 1, 8 => 16, _ => 15 };
            cases.Add(new("data-reset-row32-" + column, 36, level, [0x27], expectedX, expectedY,
                Chips: 0x0101,
                Extra: s => s.Game[0x800 + column] == 0
                    && s.Game[31 * 32 + column] == 10
                    && s.Game[1024 + column] == former));
        }
        // RunTileCases batches eight cases into one DAT; row32[2] must instead
        // be tested alone so that its old value is exactly one.
        foreach (var test in cases)
        {
            RunTileCases([test], test.Id);
        }
    }

    [WindowsSuppliedNeFact]
    public void GlitchMultipleTankDuplicateListMovesBuriedForceFloorAsAnActor()
    {
        foreach (byte buried in new byte[] { 19, 20 })
        {
            var level = new ParityScenarioLevel();
            level.Tile(14, 12, 0x4f, buried);
            level.Tile(15, 12, 19);
            level.Fields.Add((10, new byte[] { 14, 12, 14, 12 }));
            using var session = new ParitySession("glitch-multiple-tank-" + buried,
                ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
            Assert.Equal((ushort)2, Observe(session).Word(0x928));
            // A W force floor belongs to the 20..23 quartet, and becomes
            // red lock (23) when treated as an east-moving actor. E stays 19.
            byte morphed = buried == 20 ? (byte)23 : (byte)19;
            Assert.True(PostMessageW(session.Window, 0x100, 0x20, 1));
            Assert.True(PostMessageW(session.Window, 0x101, 0x20, unchecked((nint)(int)0xc0000001)));
            Until(() =>
            {
                var state = Observe(session);
                return state.Game[12 * 32 + 16] == morphed
                    && state.Game[12 * 32 + 14] == 0;
            }, session.Process, "Duplicate tank did not move/morph its buried tile: " + buried);
            Assert.Equal(0, Observe(session).Death);
            session.Close();
        }
    }
    [WindowsSuppliedNeFact]
    public void GlitchMousePanelFlicksBlockAndMovesInSecondaryDirection()
    {
        var level = new ParityScenarioLevel();
        level.Tile(16, 15, 10, 7);
        level.Tile(16, 14, 1); // Stop continued mouse walking after the secondary move.
        using var session = new ParitySession("glitch-mouse-panel",
            ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
        nint board = GlitchBoard(session);
        Key(session.Window, 0x20);
        // Chip is centered in the 9x9 viewport. The target is 2R U, so the
        // primary east push is rejected at the panel and north is tried next.
        Assert.True(GetClientRect(board, out var bounds));
        int tile = bounds.Right / 9;
        Assert.Equal(bounds.Right, tile * 9);
        nint point = (nint)(((3 * tile + tile / 2) << 16) | (6 * tile + tile / 2));
        Assert.True(PostMessageW(board, 0x201, 1, point));
        Assert.True(PostMessageW(board, 0x202, 0, point));
        Until(() =>
        {
            var state = Observe(session);
            return state.X == 15 && state.Y == 14 && state.Game[15 * 32 + 17] == 10;
        }, session.Process, "Mouse panel did not combine a flick and secondary movement");
        Assert.Equal(0, Observe(session).Death);
        session.Close();
    }

    private static nint GlitchBoard(ParitySession session)
    {
        nint board = 0;
        EnumChildWindows(session.Window, (window, _) =>
        {
            if (Class(window) == "BoardClass") board = window;
            return true;
        }, 0);
        Assert.NotEqual(0, board);
        return board;
    }

    [WindowsSuppliedNeFact]
    public void GlitchFrankensteinBlockedCloneReversesAndChangesItsTemplate()
    {
        var level = new ParityScenarioLevel();
        level.Tile(11, 10, 0x5a);
        level.Tile(11, 11, 36);
        level.Tile(11, 12, 35);
        level.Tile(11, 13, 40);
        level.Tile(18, 12, 0x4e, 49);
        level.Tile(18, 13, 38);
        level.Fields.Add((10, new byte[] { 11, 10 }));
        level.Fields.Add((5, GlitchConnection(11, 11, 18, 12)));
        using var session = new ParitySession("glitch-frankenstein",
            ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
        Key(session.Window, 0x20);
        Until(() =>
        {
            var s = Observe(session);
            return s.Game[12 * 32 + 18] == 0x4c && s.Game[1024 + 12 * 32 + 18] == 49
                && s.Word(0x928) == 2;
        }, session.Process, "Blocked live tank clone did not reverse its stationary replacement");
        session.Close();
    }

    [WindowsSuppliedNeFact]
    public void GlitchTankTopReversesPreSlideDirectionOnSingleTurningForceFloor()
    {
        var level = new ParityScenarioLevel();
        level.Tile(12, 11, 0x4e);
        level.Tile(12, 12, 19); // South becomes east on this single force floor.
        level.Tile(13, 12, 40);
        level.Tile(13, 11, 1); // The erroneous north-facing tank remains observable.
        level.Fields.Add((10, new byte[] { 12, 11 }));
        RunTileCases([new("tank-top-south-east-north", 40, level, [], 15, 15,
            Extra: s => s.Game[12 * 32 + 13] == 0x4c)], "glitch-tank-top");
    }

    [WindowsSuppliedNeFact]
    public void GlitchTerminationCompletingShortSetReportsMissingNextLevel()
    {
        var level = new ParityScenarioLevel();
        level.Tile(16, 15, 21);
        using var session = new ParitySession("glitch-termination",
            ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
        Key(session.Window, 0x20);
        Key(session.Window, 0x27);
        nint score = 0;
        Until(() => (score = DialogContaining(session.Process, "Level Bonus:")) != 0,
            session.Process, "Short-set completion did not show score");
        Command(score, 106);
        nint error = 0;
        Until(() => (error = DialogContaining(session.Process, "Corrupt or inaccessible CHIPS.DAT file.")) != 0,
            session.Process, "Missing level 2 did not trigger Termination Glitch");
        Assert.True(PostMessageW(error, 0x10, 0, 0));
        Assert.True(session.Process.WaitForExit(15000), "Termination error did not close the game");
        Assert.Equal(0, session.Process.ExitCode);
    }

    [WindowsSuppliedNeFact]
    public void GlitchNonExistenceCreatesChipAtOriginAndErasesTheOriginalTile()
    {
        var level = new ParityScenarioLevel();
        level.Tile(15, 15, 0);
        level.Tile(0, 0, 1); // The absent Chip eventually overwrites even a wall.
        using var session = new ParitySession("glitch-non-existence",
            ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
        var before = Observe(session);
        Assert.Equal(0, before.X);
        Assert.Equal(0, before.Y);
        Assert.Equal((byte)1, before.Game[0]);
        Key(session.Window, 0x20);
        Until(() =>
        {
            var s = Observe(session);
            return s.X == 0 && s.Y == 0 && s.Game[0] is >= 108 and <= 111;
        }, session.Process, "Absent Chip did not materialize at the origin");
        Assert.Equal(0, Observe(session).Death);
        session.Close();
    }

    [WindowsSuppliedNeFact]
    public void GlitchTimeDilationMouseDeathAdvancesRestartedMonstersButKeyboardDeathDoesNot()
    {
        foreach (bool mouse in new[] { false, true })
        {
            var level = new ParityScenarioLevel();
            level.Tile(16, 15, 4);
            level.Tile(11, 11, 0x4f);
            level.Tile(13, 11, 1);
            level.Fields.Add((10, new byte[] { 11, 11 }));
            using var session = new ParitySession("glitch-time-dilation-" + mouse,
                ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
            Key(session.Window, 0x20);
            if (mouse)
            {
                nint board = GlitchBoard(session);
                Assert.True(GetClientRect(board, out var bounds));
                int tile = bounds.Right / 9;
                nint point = (nint)(((4 * tile + tile / 2) << 16) | (5 * tile + tile / 2));
                Assert.True(PostMessageW(board, 0x201, 1, point));
                Assert.True(PostMessageW(board, 0x202, 0, point));
            }
            else Key(session.Window, 0x27);
            nint death = 0;
            Until(() => (death = DialogContaining(session.Process, "Don't step in the fire without fire boots!")) != 0,
                session.Process, "Dilation setup did not reach the fire death");
            Assert.True(PostMessageW(death, 0x10, 0, 0));
            Until(() => !IsWindow(death) && Observe(session).Word(0x810) != 0,
                session.Process, "Death did not restart the level");
            var restarted = Observe(session);
            Assert.Equal(15, restarted.X);
            Assert.Equal(15, restarted.Y);
            Assert.Equal(0, restarted.Death);
            Assert.Equal((byte)0x4f, restarted.Game[11 * 32 + (mouse ? 12 : 11)]);
            Assert.Equal((byte)0, restarted.Game[11 * 32 + (mouse ? 11 : 12)]);
            session.Close();
        }
    }

    [WindowsSuppliedNeFact]
    public void GlitchDataSizeCompletionLoadsBothSidesOfLegacyNonMapLimit()
    {
        foreach (bool overflow in new[] { false, true })
        {
            var level = new ParityScenarioLevel();
            level.Fields.Add((5, Enumerable.Range(0, 31).SelectMany(_ => GlitchConnection(16, 15, 18, 12)).ToArray()));
            level.Fields.Add((4, Enumerable.Range(0, 24).SelectMany(_ => GlitchConnection(16, 15, 18, 12, 0)).ToArray()));
            var monsters = new List<byte>();
            int monsterCount = overflow ? 15 : 14;
            for (byte x = 10; x < 10 + monsterCount; x++)
            {
                level.Tile(x, 10, 0x4c, 43);
                monsters.Add(x);
                monsters.Add(10);
            }
            level.Fields.Add((10, monsters.ToArray()));
            // Default title/password fields occupy 22 bytes; the other fields
            // occupy 250 + 242 + 30/32 bytes, independently of map compression.
            Assert.Equal(overflow ? 546 : 544, 22 + level.Fields.Sum(f => 2 + f.Bytes.Length));
            var first = new ParityScenarioLevel();
            first.Tile(16, 15, 21);
            using var session = new ParitySession("glitch-data-size-" + overflow,
                ParityScenarioLevel.Build([first, level]), ParityScenarioLevel.Title(0));
            Key(session.Window, 0x20);
            Key(session.Window, 0x27);
            nint score = 0;
            Until(() => (score = DialogContaining(session.Process, "Level Bonus:")) != 0,
                session.Process, "Metadata setup did not finish the previous level");
            Command(score, 106);
            Until(() => !IsWindow(score) && Text(session.Window).Contains(ParityScenarioLevel.Title(1), StringComparison.Ordinal),
                session.Process, "Completion did not bypass the legacy metadata size restriction");
            Assert.Equal((ushort)monsterCount, Observe(session).Word(0x928));
            Assert.Equal(15, Observe(session).X);
            Assert.Equal(15, Observe(session).Y);
            session.Close();
        }
    }

    [WindowsSuppliedNeFact]
    public void GlitchTransparencyDoubleTransparentPickupTerminatesInsteadOfCollectingBoth()
    {
        var control = new ParityScenarioLevel();
        control.Tile(16, 15, 100);
        RunTileCases([new("single-transparent-key-control", 100, control, [0x27], 16, 15,
            InventoryIndex: 0, InventoryCount: 1)], "glitch-transparency-control");
        var level = new ParityScenarioLevel();
        level.Tile(16, 15, 100, 104);
        using var session = new ParitySession("glitch-transparency",
            ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
        var before = Observe(session);
        Assert.Equal((byte)100, before.Game[15 * 32 + 16]);
        Assert.Equal((byte)104, before.Game[1024 + 15 * 32 + 16]);
        Key(session.Window, 0x20);
        Assert.True(PostMessageW(session.Window, 0x100, 0x27, 1));
        Assert.True(PostMessageW(session.Window, 0x101, 0x27, unchecked((nint)(int)0xc0000001)));
        Assert.True(session.Process.WaitForExit(15000), "Double-transparent recursion did not terminate");
        Assert.Equal(30, session.Process.ExitCode);
        using var trace = new BinaryReader(File.OpenRead(Path.Combine(session.Directory, "execution.bin")));
        uint[] header = Enumerable.Range(0, 24).Select(_ => trace.ReadUInt32()).ToArray();
        Assert.Equal(0x38525457u, header[0]);
        Assert.Equal(8u, header[10]); // Guest control/stack boundary fault, not an arbitrary process failure.
        Assert.Equal(30u, header[11]);
        Assert.All(header.Skip(20), value => Assert.Equal(0u, value));
    }

    [WindowsSuppliedNeFact]
    public void GlitchTeleportSkipIgnoresTeleportBuriedUnderAnOccupyingMonster()
    {
        var cases = new List<TileCase>();
        foreach (bool occupied in new[] { false, true })
        {
            var level = new ParityScenarioLevel();
            level.Tile(16, 15, 41);
            level.Tile(12, 12, 41);
            level.Tile(18, 12, occupied ? (byte)0x4c : (byte)41, occupied ? (byte)41 : (byte)0);
            level.Tile(18, 11, 1);
            if (occupied) level.Fields.Add((10, new byte[] { 18, 12 }));
            cases.Add(new("teleport-skip-occupied-" + occupied, 41, level, [0x27], occupied ? 13 : 19, 12,
                Extra: s => s.Game[1024 + 12 * 32 + 18] == (occupied ? 41 : 0)));
        }
        // This isolates the lower-layer omission that makes a mouse boost skip
        // a portal. It does not assert the timing of two boosts in one move.
        RunTileCases(cases, "glitch-teleport-skip");
    }

    [WindowsSuppliedNeFact]
    public void GlitchConvergenceClonedBlockAndChipShareTeleportAndCreateAnExtraChip()
    {
        var level = new ParityScenarioLevel();
        level.Tile(16, 15, 41);
        level.Tile(12, 12, 41);
        level.Tile(13, 12, 10);
        level.Tile(14, 12, 36);
        level.Tile(16, 16, 14, 49);
        level.Fields.Add((5, GlitchConnection(14, 12, 16, 16)));
        using var session = new ParitySession("glitch-convergence",
            ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
        Key(session.Window, 0x20);
        Assert.True(PostMessageW(session.Window, 0x100, 0x27, 1));
        Assert.True(PostMessageW(session.Window, 0x101, 0x27, unchecked((nint)(int)0xc0000001)));
        GuestSnapshot? actual = null;
        try
        {
            Until(() =>
            {
                actual = Observe(session);
                return actual.Death == 0 && actual.Game.Take(1024).Count(t => t is >= 108 and <= 111) == 2;
            }, session.Process, "Simultaneous teleport exit did not create a block-Chip");
        }
        finally
        {
            if (actual is not null)
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "glitch-convergence-state.json"),
                    System.Text.Json.JsonSerializer.Serialize(actual));
        }
        Assert.Equal(12, actual!.X);
        Assert.Equal(12, actual.Y);
        Assert.Equal((byte)108, actual.Game[11 * 32 + 12]);
        Assert.Equal((byte)108, actual.Game[12 * 32 + 12]);
        Assert.Equal((byte)0, actual.Game[1024 + 12 * 32 + 12]);
        session.Close();
    }

}
