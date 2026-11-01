using System.Text.Json;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [WindowsSuppliedNeFact]
    public void OriginalActorFamiliesRespectWaterFireBombGravelAndPlayerCollisions()
    {
        var observations = new List<object>();
        bool complete = false;
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath(), "actor-hazards");
        Directory.CreateDirectory(evidence);
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
                foreach (byte terrain in new byte[]
                {
                    3,
                    4,
                    42,
                    45,
                    108
                }

                )
                {
                    bool collision = terrain == 108, blocked = terrain == 45 || terrain == 4 && family is 0x40 or 0x58;
                    bool survives = terrain == 3 && family == 0x50 || terrain == 4 && family == 0x44;
                    int y = collision
                        ? 14
                        : 12;
                    var level = new ParityScenarioLevel();
                    byte actor = (byte)(family + 2);
                    level.Tile(15, y, actor);
                    level.Tile(14, y, 1);
                    level.Tile(16, y, 1);
                    level.Tile(15, y - 1, 1);
                    if (!collision)
                    {
                        level.Tile(15, y + 1, terrain);
                    }

                    level.Fields.Add((10, new byte[] { 15, (byte)y }));
                    using var session = new ParitySession($"actor-{family:X2}-terrain-{terrain:X2}", ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
                    var before = Observe(session);
                    Assert.Equal((ushort)1, before.Word(0x928));
                    Key(session.Window, 0x20);
                    GuestSnapshot current = before;
                    if (blocked)
                    {
                        Until(() =>
                        {
                            current = Observe(session);
                            return unchecked((ushort)(current.Global(0x64e) - before.Global(0x64e))) >= 12;
                        }, session.Process, "Blocked actor did not receive original timer ticks");
                        Assert.Equal(actor, current.Game[y * 32 + 15]);
                        Assert.Equal((ushort)1, current.Word(0x928));
                        Assert.Equal(terrain, current.Game[(y + 1) * 32 + 15]);
                    }
                    else if (collision)
                    {
                        Until(() =>
                        {
                            current = Observe(session);
                            return current.Death == 5;
                        }, session.Process, "Monster did not collide with Chip");
                    }
                    else if (survives)
                    {
                        Until(() =>
                        {
                            current = Observe(session);
                            return current.Game[(y + 1) * 32 + 15] >= family && current.Game[(y + 1) * 32 + 15] < family + 4;
                        }, session.Process, "Immune actor did not enter hazard");
                        Assert.Equal((ushort)1, current.Word(0x928));
                        Assert.Equal(terrain, current.Game[0x400 + (y + 1) * 32 + 15]);
                    }
                    else
                    {
                        Until(() =>
                        {
                            current = Observe(session);
                            return current.Word(0x928) == 0;
                        }, session.Process, "Actor did not die in hazard");
                        Assert.Equal(terrain == 42
                            ? (byte)0
                            : terrain, current.Game[(y + 1) * 32 + 15]);
                    }

                    observations.Add(new
                    {
                        Family = family,
                        Terrain = terrain,
                        Blocked = blocked,
                        Immune = survives,
                        Collision = collision,
                        MonsterCount = current.Word(0x928),
                        Autopsy = current.Death,
                        GuestTick = current.Global(0x64e),
                        InputDataSha256 = Hash(ParityScenarioLevel.Build([level])),
                        Oracle = "Original TileTable and MonsterCanEnterTile: gravel blocks all; water spares gliders; fire blocks bugs/walkers, spares fireballs, kills others; bombs remove all; live monsters kill Chip"
                    });
                    File.WriteAllText(Path.Combine(evidence, "partial.json"), JsonSerializer.Serialize(observations));
                    if (collision)
                    {
                        nint death = 0;
                        Until(() =>
                        {
                            death = Find(session.Process, "#32770");
                            return death != 0;
                        }, session.Process, "Original collision message missing");
                        PostMessageW(death, 0x10, 0, 0);
                        Until(() => !IsWindow(death), session.Process, "Collision message did not close");
                    }

                    session.Close();
                }
            }

            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "hazards.json"), JsonSerializer.Serialize(new
            {
                Format = "NeActorHazards/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ScenarioCount = 45,
                ExternalScenarioInput = true,
                FixtureInitialization = false,
                Observations = observations
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
