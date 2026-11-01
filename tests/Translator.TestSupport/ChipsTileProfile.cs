using System.Security.Cryptography;
using System.Text;

namespace Translator.ChipsChallenge;

/// <summary>Independent tile/inventory vectors; fixtures are applied only in automation.
/// The runtime executes original MoveChip and its original tile-table decisions.</summary>
public static class ChipsTileProfile
{
    public static readonly string[] Functions = ["3:1770", "2:0C1A", "2:0C7E", "2:2BBE", "2:0F06"];
    public const string HintText = "Hint: Collect chips to get past the chip socket. Use keys to open doors.";
    public static string HintTextHash => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(HintText))).ToLowerInvariant();

    public static readonly (ushort Offset, string Name)[] Snapshot = ChipsClockProfile.Snapshot.Concat(new (ushort, string)[] { (0x1682, "BlueKeys"), (0x1684, "RedKeys"), (0x1686, "GreenKeys"), (0x1688, "YellowKeys"), (0x168a, "Flippers"), (0x168c, "FireBoots"), (0x168e, "IceSkates"), (0x1690, "SuctionBoots"), (0x1692, "ChipsRemaining") }).ToArray();

    private static void Word(byte[] bytes, int offset, int value) => BitConverter.GetBytes(unchecked((ushort)value)).CopyTo(bytes, offset);

    private static SceneWrite Write(ushort at, ushort value) => new(at, BitConverter.GetBytes(value), "Explicit independent tile fixture; automation only");
    public static ChipsInputPlan Create(byte[] originalState, Func<byte[], IReadOnlyList<ushort>, IReadOnlyList<ScenePixels>> pixels)
    {
        var cases = new List<InputCase>();
        AuthenticRoute(originalState, cases, pixels);

        InputEvent[] Right() => [new(0x100, 0x27, 1), new(0x101, 0x27, 0xc0000001)];


        void Fixture(string name, byte tile, ushort[] before, ushort[] after, bool enters = true, int chipsBefore = 11, int chipsAfter = 11, byte lower = 0, byte facing = 0x6f, bool hint = false)
        {
            byte[] state = originalState.ToArray();
            Array.Clear(state, 0, 0x800);
            state[14 * 32 + 15] = 0x6e;
            state[14 * 32 + 16] = tile;
            Word(state, 0x808, 15);
            Word(state, 0x80a, 14);
            Word(state, 0xa24, 11);
            Word(state, 0xa26, 10);
            Word(state, 0xa28, 9);
            Word(state, 0xa2a, 9);
            Word(state, 0xa34, 0);
            Word(state, 0xa3e, 0);
            Word(state, 0xa40, 0);
            Word(state, 0x80e, 0);
            var setup = new List<SceneWrite>
            {
                new(0x4000, state.ToArray(), "Controlled isolated tile fixture; no interactive board edits"),
                Write(0x64e, 0),
                Write(0x1694, 100),
                Write(0x1692, (ushort)chipsBefore),
                Write(0x20, 0)
            };
            for (int i = 0; i < 8; i++)
            {
                setup.Add(Write((ushort)(0x1682 + i * 2), before[i]));
            }

            state[14 * 32 + 15] = enters
                ? (byte)0
                : (byte)0x6f;
            if (enters)
            {
                state[14 * 32 + 16] = facing;
                state[0x400 + 14 * 32 + 16] = lower;
                Word(state, 0x808, 16);
                Word(state, 0xa24, 12);
                Word(state, 0xa34, 1);
            }

            Word(state, 0xa40, 1);
            var checks = new List<InputCheck>
            {
                new(0x4000, state.ToArray()),
                new(0x1692, BitConverter.GetBytes((ushort)chipsAfter)),
                new(0x20, new byte[2]),
                new(0x64e, new byte[2])
            };
            for (int i = 0; i < 8; i++)
            {
                checks.Add(new((ushort)(0x1682 + i * 2), BitConverter.GetBytes(after[i])));
            }

            if (hint)
            {
                setup.Add(new(0x36, new byte[50], "Deterministic font precondition"));
                byte[] font = new byte[50];
                Word(font, 0, -16);
                Word(font, 8, 700);
                font[10] = 1;
                Encoding.ASCII.GetBytes("Helv\0").CopyTo(font, 18);
                checks.Add(new(0x36, font));
                checks.Add(new(0x1e, BitConverter.GetBytes((ushort)8)));
            }

            cases.Add(new(name, setup, [new(1, 2, 0x00020cbe, 0xffff, InputDriverOperation.CallOriginal), .. Right()], checks)
            {
                Pixels = pixels(state, after),
                WindowChecks = [new(5, 0, (ushort)chipsAfter)]
            });
            if (hint)
            {
                // Leave the original hint via a second real key. Two exact ticks
                // clear HasMoved before input; original HideHint restores the UI.
                state[14 * 32 + 16] = 0x2f;
                state[0x400 + 14 * 32 + 16] = 0;
                state[14 * 32 + 17] = 0x6f;
                Word(state, 0x808, 17);
                Word(state, 0xa24, 13);
                Word(state, 0xa34, 2);
                cases.Add(new("LeaveHintRestoresInventory", [], [new(0x113, 1, 2, 1, InputDriverOperation.AdvanceTicks), .. Right()], [new(0x4000, state.ToArray()), new(0x1e, new byte[2]), new(0x64e, BitConverter.GetBytes((ushort)2)), new(0x20, new byte[2])])
                {
                    Pixels = pixels(state, after)
                });
            }
        }


        ushort[] Empty() => new ushort[8];

        Fixture("CollectChip", 2, Empty(), Empty(), chipsAfter: 10);
        Fixture("ChipCountClampsAtZero", 2, Empty(), Empty(), chipsBefore: 0, chipsAfter: 0);
        for (int i = 0; i < 8; i++)
        {
            var after = Empty();
            after[i] = 1;
            Fixture("Collect" + new[] { "BlueKey", "RedKey", "GreenKey", "YellowKey", "Flippers", "FireBoots", "IceSkates", "SuctionBoots" }[i], (byte)(0x64 + i), Empty(), after);
        }

        var two = Empty();
        two[0] = 2;
        var one = Empty();
        one[0] = 1;
        Fixture("RepeatedBlueKeyIncrementsCount", 0x64, one, two);
        for (int i = 0; i < 4; i++)
        {
            string color = new[]
            {
                "Blue",
                "Red",
                "Green",
                "Yellow"
            }[i];
            Fixture("Blocked" + color + "Door", (byte)(0x16 + i), Empty(), Empty(), false);
            var before = Empty();
            before[i] = 1;
            var after = Empty();
            if (i == 2)
            {
                after[i] = 1;
            }

            Fixture("Open" + color + "Door", (byte)(0x16 + i), before, after);
        }

        Fixture("SocketRequiresChips", 0x22, Empty(), Empty(), false);
        Fixture("OpenSocketWhenNoChipsRemain", 0x22, Empty(), Empty(), chipsBefore: 0, chipsAfter: 0);
        foreach (var pair in new[]
        {
            (3, 4, "FlippersPreventDrowning", 0x3f),
            (4, 5, "FireBootsPreventBurning", 0x6f),
            (12, 6, "SkatesPreventSliding", 0x6f),
            (13, 7, "SuctionBootsPreventForcedSouth", 0x6f),
            (18, 7, "SuctionBootsPreventForcedNorth", 0x6f),
            (19, 7, "SuctionBootsPreventForcedEast", 0x6f),
            (20, 7, "SuctionBootsPreventForcedWest", 0x6f),
            (50, 7, "SuctionBootsPreventRandomForce", 0x6f)
        }

        )
        {
            var boots = Empty();
            boots[pair.Item2] = 1;
            Fixture(pair.Item3, (byte)pair.Item1, boots, boots, lower: (byte)pair.Item1, facing: (byte)pair.Item4);
        }

        Fixture("DirtBecomesFloor", 0xb, Empty(), Empty());
        var all = Enumerable.Repeat((ushort)1, 8).ToArray();
        var keys = new ushort[]
        {
            1,
            1,
            1,
            1,
            0,
            0,
            0,
            0
        };
        Fixture("ThiefRemovesBootsPreservesKeys", 0x21, all, keys, lower: 0x21);
        Fixture("EnterHintCreatesOriginalWindow", 0x2f, Empty(), Empty(), lower: 0x2f, hint: true);
        return new("0002:225C", 0, 0, [new(0x10, 0), new(0x12, 1)], [0x25, 0x26, 0x27, 0x28], [Write(0x22, 0), Write(0x13c8, 0)], cases)
        {
            Movement = true
        };
    }

    private static void AuthenticRoute(byte[] originalState, List<InputCase> cases, Func<byte[], IReadOnlyList<ushort>, IReadOnlyList<ScenePixels>> pixels)
    {
        byte[] state = originalState.ToArray();
        Word(state, 0xa24, 11);
        Word(state, 0xa26, 10);
        Word(state, 0xa28, 9);
        Word(state, 0xa2a, 9);
        int x = 15, y = 14, n = 0;
        bool hintSeen = false;
        foreach (var r in ChipsLessonOneRoute.Steps)
        {
            int tick = n * 2;
            n++;
            state[y * 32 + x] = state[0x400 + y * 32 + x];
            state[0x400 + y * 32 + x] = 0;
            byte facing = r.Direction switch
            {
                'U' => 0x6c,
                'L' => 0x6d,
                'D' => 0x6e,
                _ => 0x6f
            };
            state[r.Y * 32 + r.X] = facing;
            state[0x400 + r.Y * 32 + r.X] = r.Tile == 0x2f
                ? (byte)0x2f
                : (byte)0;
            Word(state, 0x808, r.X);
            Word(state, 0x80a, r.Y);
            Word(state, 0xa24, r.X - 4);
            Word(state, 0xa26, r.Y - 4);
            Word(state, 0xa34, n);
            Word(state, 0xa40, 1);
            var inventory = new ushort[]
            {
                r.Blue,
                r.Red,
                r.Green,
                r.Yellow,
                0,
                0,
                0,
                0
            };

            InputCheck W(ushort at, int v) => new(at, BitConverter.GetBytes((ushort)v));

            var checks = new List<InputCheck>
            {
                new(0x4000, state.ToArray()),
                W(0x64e, tick),
                W(0x1692, r.Chips),
                W(0x1694, 100 - tick / 10),
                W(0x20, 0),
                W(0x1e, r.Tile == 0x2f
                    ? 8
                    : 0)
            };
            for (int i = 0; i < 8; i++)
            {
                checks.Add(W((ushort)(0x1682 + i * 2), inventory[i]));
            }

            var setup = new List<SceneWrite>();
            if (n == 1)
            {
                setup.Add(Write(0x64e, 0));
                setup.Add(Write(0x20, 0));
                setup.Add(new(0x36, new byte[50], "Deterministic hint font precondition; board is untouched"));
            }

            if (r.Tile == 0x2f && !hintSeen)
            {
                byte[] font = new byte[50];
                Word(font, 0, -16);
                Word(font, 8, 700);
                font[10] = 1;
                Encoding.ASCII.GetBytes("Helv\0").CopyTo(font, 18);
                checks.Add(new(0x36, font));
                hintSeen = true;
            }

            ushort key = r.Direction switch
            {
                'L' => 0x25,
                'U' => 0x26,
                'D' => 0x28,
                _ => 0x27
            };
            var events = new List<InputEvent>();
            if (n > 1)
            {
                events.Add(new(0x113, 1, 2, 1, InputDriverOperation.AdvanceTicks));
            }

            events.Add(new(0x100, key, 1));
            events.Add(new(0x101, key, 0xc0000001));
            cases.Add(new($"AuthenticLesson1Step{n:D3}_{r.Direction}_{r.X}_{r.Y}", setup, events, checks)
            {
                Pixels = pixels(state, inventory),
                WindowChecks = [new(5, 0, r.Chips), new(4, 0, (ushort)(100 - tick / 10))]
            });
            x = r.X;
            y = r.Y;
        }
    }

    // Source-table rows and target offsets are read from the authenticated binary;
    // the support claims below come from independent acceptance vectors.
    public static object Matrix(byte[] tileTable, byte[] jumpTable, IReadOnlyList<InputCase> cases, bool hazardSubset = false, bool completion = false)
    {
        bool hazards = hazardSubset || completion;
        bool hazardsOnly = hazardSubset && !completion;
        var names = new Dictionary<int, string>
        {
            [0] = "Floor",
            [1] = "Wall",
            [2] = "ComputerChip",
            [3] = "Water",
            [4] = "Fire",
            [10] = "Block",
            [11] = "Dirt",
            [12] = "Ice",
            [13] = "ForceSouth",
            [18] = "ForceNorth",
            [19] = "ForceEast",
            [20] = "ForceWest",
            [21] = "Exit",
            [22] = "BlueDoor",
            [23] = "RedDoor",
            [24] = "GreenDoor",
            [25] = "YellowDoor",
            [33] = "Thief",
            [34] = "Socket",
            [35] = "ToggleButton",
            [36] = "CloneButton",
            [37] = "ToggleWall",
            [38] = "ToggleFloor",
            [39] = "TrapButton",
            [40] = "TankButton",
            [41] = "Teleport",
            [42] = "Bomb",
            [43] = "Trap",
            [47] = "Hint",
            [50] = "RandomForce"
        };
        for (int i = 0; i < 4; i++)
        {
            names[0x40 + i] = new[]
            {
                "BugNorth",
                "BugWest",
                "BugSouth",
                "BugEast"
            }[i];
        }

        string[] items = ["BlueKey", "RedKey", "GreenKey", "YellowKey", "Flippers", "FireBoots", "IceSkates", "SuctionBoots"];
        for (int i = 0; i < 8; i++)
        {
            names[0x64 + i] = items[i];
        }

        string Status(int id) => completion && id == 21
            ? "OriginalCompletionAndProgression"
            : hazards && (id is 3 or 4)
                ? "OriginalDeathAndRestart"
                : hazards && id >= 0x40 && id <= 0x43
                    ? "OriginalBugSubset"
                    : hazardsOnly
                        ? (id is 0 or 1
                            ? "CertifiedSubset"
                            : id >= 0x6c
                                ? "PlayerSpriteRenderingOnly"
                                : "Uncertified")
                        : id is 0 or 1 or 2 or 11 or 22 or 23 or 24 or 25 or 33 or 34 or 47 || id >= 0x64 && id <= 0x6b
                            ? "CertifiedSubset"
                            : id == 21
                                ? "EntryEligibleCompletionDeferred"
                                : id is 3 or 4 or 12 or 13 or 18 or 19 or 20 or 50
                                    ? "BootProtectedOnly"
                                    : id >= 0x6c
                                        ? "PlayerSpriteRenderingOnly"
                                        : "Uncertified";

        const string movementTest = "NePlayerMovementTests.KeyboardBufferedTicksExecuteOriginalMovementCollisionViewportAndPixels";
        const string tileTest = "NeTileInteractionTests.OriginalTilesChangeBoardInventoryCountersBootEffectsAndHintPixels";
        const string hazardTest = "NeHazardRestartTests.OriginalBugsWaterFireTimeoutDeathsRestartWithOwnedListsAndPixels";
        const string completionTest = "NeCompletionProgressionTests.OriginalExitComputesScoreLoadsNextLevelAndKeepsPlaying";

        // References may name a checkpoint test outside this descriptor. Completion
        // retains the earlier tile and hazard certification, even though its local
        // cases contain only the authentic route, exit and next-level movement.
        string[] TileTests(int id)
        {
            string status = Status(id);
            if (status == "Uncertified")
            {
                return [];
            }

            var tests = new List<string>();
            if (id is 0 or 1 || id >= 0x6c)
            {
                tests.Add(hazardsOnly
                    ? hazardTest
                    : movementTest);
            }

            if (status is "OriginalDeathAndRestart" or "OriginalBugSubset")
            {
                tests.Add(hazardTest);
            }

            if (id == 21)
            {
                tests.Add(completion
                    ? completionTest
                    : "NeTileInteractionTests.OriginalExitEligibilityAndCompletionRemainsFatal");
            }

            if (!hazardsOnly && (id is 2 or 3 or 4 or 11 or 22 or 23 or 24 or 25 or 33 or 34 or 47 || id >= 0x64 && id <= 0x6b) || status == "BootProtectedOnly")
            {
                tests.Add(tileTest);
            }

            // Associate each route case using its explicit destination tile, rather
            // than guessing from color/item names absent from the case name.
            if (!hazardsOnly)
            {
                for (int i = 0; i < ChipsLessonOneRoute.Steps.Length; i++)
                {
                    var step = ChipsLessonOneRoute.Steps[i];
                    if (step.Tile != id)
                    {
                        continue;
                    }

                    string name = $"AuthenticLesson1Step{i + 1:D3}_{step.Direction}_{step.X}_{step.Y}";
                    if (cases.Any(c => c.Name == name))
                    {
                        tests.Add(name);
                    }
                }
            }

            if (completion && id == 21)
            {
                tests.Add("AuthenticExitOriginalScoreAndNextLevel");
            }

            return tests.Distinct(StringComparer.Ordinal).ToArray();
        }


        string[] Dependencies(int id)
        {
            var dependencies = new List<string>
            {
                "MoveChip 0007:1184",
                "ChipCanEnterTile 0003:1A56",
                "Original board/inventory/counter painting"
            };
            if (hazards && (id is 3 or 4 || id >= 0x40 && id <= 0x43))
            {
                dependencies.AddRange(["MonsterLoop 0003:074E", "MoveMonster 0007:18DA", "MonsterCanEnterTile 0003:1D4A", "DeleteMonster 0003:03B4", "ShowDeathMessage 0002:0B9A", "LoadAndStartLevel 0004:0356", "FreeGameLists 0004:0240"]);
            }

            if (completion && id == 21)
            {
                dependencies.AddRange(["EndLevel 0007:0CCA", "COMPLETEMSGPROC 0006:03C6", "LoadAndStartLevel 0004:0356"]);
            }

            if (id == 47)
            {
                dependencies.AddRange(["ShowHint 0002:0C1A", "HideHint 0002:0C7E", "HINTWNDPROC 0002:2BBE", "Native USER/GDI text/font services"]);
            }

            if (id is 3 or 4 or >= 22 and <= 25 or >= 0x64 and <= 0x6b || Status(id) == "BootProtectedOnly")
            {
                dependencies.AddRange(["PickUpKeyOrBoot 0003:1770", "CanOpenDoor 0003:1804", "HaveBootsForTile 0003:187C"]);
            }

            return dependencies.ToArray();
        }

        return new
        {
            Format = "GameplayTileSupport/v1",
            AcceptanceLevel = completion
                ? "LESSON 1 -> LESSON 2"
                : hazardSubset
                    ? "LESSON 2"
                    : "LESSON 1",
            Policy = completion
                ? "Original EndLevel and COMPLETEMSGPROC own eligibility, score and progression; native notification only presents their text"
                : "Interaction-specific certification; original dispatch and fatal boundaries are retained. Completion/progression requires the completion startup mode.",
            Tiles = Enumerable.Range(0, 112).Select(id =>
            {
                int pass = tileTable[id * 6], action = tileTable[id * 6 + 1];
                string target = pass == 2 && id >= 3 && id <= 50 && id != 10
                    ? $"0003:{BitConverter.ToUInt16(jumpTable, (id - 3) * 2):X4}"
                    : "0003:1A56 (table predicate / lower-layer recursion)";
                string actionTarget = action switch
                {
                    1 => "0007:1460",
                    2 => "0007:147C",
                    4 => "0007:165F",
                    5 => "0007:1592",
                    6 => "0007:1606",
                    7 => "0007:1618",
                    _ => "0007:1672"
                };
                return new
                {
                    Id = id,
                    Hex = $"{id:X2}",
                    Name = names.GetValueOrDefault(id, $"OriginalTile{id:X2}"),
                    OriginalDispatch = new
                    {
                        TableRow = $"DS:{0x66c + id * 6:X4}",
                        Passability = pass,
                        Action = action,
                        MonsterPassability = tileTable[id * 6 + 4],
                        MonsterAction = tileTable[id * 6 + 5],
                        PassabilityTarget = target,
                        MoveChipActionTarget = actionTarget,
                        RowHex = Convert.ToHexString(tileTable.AsSpan(id * 6, 6))
                    },
                    TranslationStatus = Status(id),
                    RuntimeDependencies = Dependencies(id),
                    SupportedInteractions = Status(id) switch
                    {
                        "OriginalDeathAndRestart" => completion
                            ? "Original death reason/sprite/message, reload, inventory/list/timer restoration; corresponding boots prevent death"
                            : "Original death reason/sprite/message, reload, inventory/list/timer restoration",
                        "OriginalBugSubset" => "Original movement frequency/order/direction fallback, water removal/list compaction, collision death and restored records",
                        "CertifiedSubset" => "Named state/pixel acceptance cases below",
                        "BootProtectedOnly" => "Preserved underlying terrain, no death/sliding/forced move with the corresponding boots",
                        "OriginalCompletionAndProgression" => "Authentic LESSON 1 route, original scoring and LESSON 2 continued play",
                        "EntryEligibleCompletionDeferred" => "Original passability/action 4; MoveChip reaches explicit EndLevel fatal boundary",
                        "PlayerSpriteRenderingOnly" => "Original player sprite rendering, not passability certification",
                        _ => "No certification"
                    },
                    Tests = TileTests(id),
                    KnownLimitations = id == 21
                        ? (completion
                            ? "Last-level ending animation remains fatal; completion notification presents original computed text"
                            : "EndLevel and progression require the completion startup mode")
                        : Status(id) == "Uncertified"
                            ? "Original behavior retained; required nontranslated dependencies fail explicitly. Not treated as floor by the host."
                            : Status(id) is "OriginalDeathAndRestart" or "OriginalBugSubset"
                                ? "Only the explicit bug/death/restart cases are certified; other monster families remain uncertified"
                                : "No claim beyond the listed interactions or for nonempty dynamic lists"
                };
            }).ToArray()
        };
    }
}
