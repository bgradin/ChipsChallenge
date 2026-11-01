using System.Diagnostics;
using System.Text.Json;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [WindowsSuppliedNeFact]
    public void OriginalMonsterLoopMovesEveryActorFamilyAndInitialDirectionUsingExternalLevels()
    {
        var observations = new List<object>();
        IReadOnlyList<object> fallbacks = [];
        bool complete = false;
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath(), "actors");
        Directory.CreateDirectory(evidence);
        (int X, int Y)[] vectors = [(0, -1), (-1, 0), (0, 1), (1, 0)];
        try
        {
            foreach (byte family in new byte[]
            {
                0x40,
                0x44,
                0x48,
                0x4c,
                0x50,
                0x54,
                0x58,
                0x5c,
                0x60
            }

            )
            {
                for (int direction = 0; direction < 4; direction++)
                {
                    var level = new ParityScenarioLevel();
                    byte initial = (byte)(family + direction);
                    level.Tile(15, 12, initial);
                    level.Fields.Add((10, new byte[] { 15, 12 }));
                    int expectedDirection = family == 0x40
                        ? (direction + 1) % 4
                        : family == 0x60
                            ? (direction + 3) % 4
                            : family == 0x54
                                ? 2
                                : direction;
                    if (family == 0x5c)
                    {
                        foreach (var v in vectors)
                        {
                            level.Tile(15 + v.X, 12 + v.Y, 1);
                        }
                    }

                    if (family == 0x5c)
                    {
                        var v = vectors[expectedDirection];
                        level.Tile(15 + v.X, 12 + v.Y, 0);
                    }

                    var movement = vectors[expectedDirection];
                    int expectedX = 15 + movement.X, expectedY = 12 + movement.Y;
                    using var session = new ParitySession("actor-" + initial.ToString("X2"), ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
                    var before = Observe(session);
                    Assert.Equal((ushort)1, before.Word(0x928));
                    Assert.Equal(initial, before.Game[12 * 32 + 15]);
                    // Observe the first transition rather than assuming a wall-clock
                    // number of guest ticks. No timer/global/RNG value is written.
                    Assert.True(PostMessageW(session.Window, 0x100, 0x20, 1));
                    Assert.True(PostMessageW(session.Window, 0x101, 0x20, unchecked((nint)(int)0xc0000001)));
                    GuestSnapshot current = before;
                    int at = 12 * 32 + 15;
                    var watch = Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < 10000)
                    {
                        current = Observe(session);
                        int found = Array.FindIndex(current.Game, 0, 1024, t => t >= family && t < family + 4);
                        if (found >= 0 && found != at)
                        {
                            at = found;
                            break;
                        }

                        Thread.Sleep(1);
                    }

                    Assert.True(at != 12 * 32 + 15, "Actor did not move: " + initial.ToString("X2"));
                    Assert.Equal(expectedX, at % 32);
                    Assert.Equal(expectedY, at / 32);
                    Assert.Equal((byte)(family + expectedDirection), current.Game[at]);
                    Assert.Equal(0, current.Death);
                    Command(session.Window, 0x74);
                    Until(() => Checked(session.Window, 0x74) != 0, session.Process, "Pause after actor observation failed");
                    observations.Add(new
                    {
                        TileValue = initial,
                        Family = family,
                        Direction = direction,
                        ActualX = at % 32,
                        ActualY = at / 32,
                        ActualTile = current.Game[at],
                        ExpectedX = expectedX,
                        ExpectedY = expectedY,
                        ExpectedDirection = expectedDirection,
                        GuestTick = current.Global(0x64e),
                        MonsterCount = current.Word(0x928),
                        Oracle = family == 0x5c
                        ? "A single legal exit constrains random blob movement; all other exits are walls"
                        : "Independent original movement-preference vector"
                    });
                    session.Close();
                    File.WriteAllText(Path.Combine(evidence, "partial.json"), JsonSerializer.Serialize(observations));
                }
            }

            fallbacks = ObserveActorFallbacks();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "actors.json"), JsonSerializer.Serialize(new
            {
                Format = "NeActorScenarios/v2",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ScenarioCount = 36,
                FallbackScenarioCount = fallbacks.Count,
                FallbackObservations = fallbacks,
                ExternalScenarioInput = true,
                FixtureInitialization = false,
                Observations = observations
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
