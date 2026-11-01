namespace Translator.ChipsChallenge;

/// <summary>Independent original LESSON 2 bug/death/restart vectors. No runtime AI.</summary>
public static class ChipsHazardProfile
{
    public const string BoardHash = "715e4d35f107e707493e85ae9b3f33db32f3e59db43500ff4098e0069b51afe1";
    public static readonly string[] Functions = ["3:01A4", "3:0228", "3:074E", "3:00B0", "3:0116", "3:017C", "7:18DA", "3:1D4A", "2:0B9A", "2:0000", "4:0356", "4:0240", "4:0134", "4:01A0", "2:1E28", "3:03B4", "3:12BE"];
    public static readonly (ushort Offset, string Name)[] Snapshot = ChipsTileProfile.Snapshot.Concat(new (ushort, string)[] { (0x4816, "Autopsy"), (0x4a30, "Restarts"), (0x4928, "MonsterCount"), (0x492c, "MonsterHandle"), (0x4810, "Placard") }).ToArray();

    static void Word(byte[] b, int at, int v) => BitConverter.GetBytes((ushort)v).CopyTo(b, at);

    static SceneWrite W(ushort at, int v) => new(at, BitConverter.GetBytes((ushort)v), "Explicit deterministic hazard fixture; automation only");

    static InputCheck C(ushort at, int v) => new(at, BitConverter.GetBytes((ushort)v));
    public static SceneWrite[] Viewport => [W(0x4a24, 17), W(0x4a26, 8), W(0x4a28, 9), W(0x4a2a, 9)];
    public static SceneWrite[] LevelChecks => [W(0x4800, 2), W(0x4802, 149), W(0x4804, 100), W(0x4806, 4), W(0x4808, 21), W(0x480a, 12), W(0x481c, 3), W(0x4928, 3), W(0x492a, 16), W(0x492c, 1), W(0x492e, 0), W(0x4930, 0x5000), W(0x1692, 4), W(0x1694, 100), new(0x495a, "LESSON 2\0"u8.ToArray(), "Independent title"), new(0x4a1a, "JXMJ\0"u8.ToArray(), "Independent password")];

    // Explicit [tile,x,y,dx,dy] records in original initialization/movement order.
    // LESSON 2 walls at (12,11)/(13,12)/(12,13) force the
    // second bug straight and the third bug right on the first even tick.
    static readonly int[][][] _bugs = [[[0x40, 14, 12, 0, -1], [0x40, 13, 11, 0, -1], [0x40, 13, 13, 0, -1]], [[0x40, 14, 11, 0, -1], [0x40, 13, 10, 0, -1], [0x43, 14, 13, 1, 0]], [[0x41, 13, 11, -1, 0], [0x40, 13, 9, 0, -1], [0x40, 14, 12, 0, -1]], [[0x40, 13, 10, 0, -1], [0x41, 12, 9, -1, 0], [0x40, 14, 11, 0, -1]], [[0x40, 13, 9, 0, -1], [0x41, 11, 9, -1, 0], [0x41, 13, 11, -1, 0]]];

    static byte[] Records(int index)
    {
        byte[] b = new byte[33];
        for (int i = 0; i < 3; i++)
        {
            b[i * 11] = (byte)_bugs[index][i][0];
            for (int j = 1; j < 5; j++)
            {
                Word(b, i * 11 + 1 + (j - 1) * 2, _bugs[index][i][j]);
            }
        }

        return b;
    }

    static void BoardBugs(byte[] b, int from, int to)
    {
        foreach (var p in _bugs[from])
        {
            b[p[2] * 32 + p[1]] = 0;
        }

        foreach (var p in _bugs[to])
        {
            b[p[2] * 32 + p[1]] = (byte)p[0];
        }
    }

    static void Generation(byte[] state, int generation)
    {
        Word(state, 0x92c, generation);
        Word(state, 0x92e, 0);
        Word(state, 0x930, 0x5000 + generation - 1);
        Word(state, 0xa30, generation - 1);
    }

    public static ChipsInputPlan Create(byte[] originalState, Func<byte[], IReadOnlyList<ushort>, IReadOnlyList<ScenePixels>> pixels)
    {
        var cases = new List<InputCase>();
        ushort[] inventory = new ushort[8];
        int generation = 1, tick = 0, bug = 0;

        byte[] Initial()
        {
            var b = originalState.ToArray();
            Generation(b, generation);
            Word(b, 0xa24, 17);
            Word(b, 0xa26, 8);
            Word(b, 0xa28, 9);
            Word(b, 0xa2a, 9);
            return b;
        }


        byte[] state = Initial();
        Word(state, 0xa24, 9);
        Word(state, 0xa26, 7);

        InputEvent Steps(uint n) => new(0x113, 1, n, 1, InputDriverOperation.AdvanceTicks);


        InputEvent[] Key(ushort key) => [new(0x100, key, 1), new(0x101, key, 0xc0000001)];


        List<InputCheck> Checks(byte[] b, int time = 100) => [new(0x4000, b.ToArray()), C(0x64e, tick), C(0x1692, 4), C(0x1694, time), C(0x22, 0), C(0x20, 0), .. Enumerable.Range(0, 8).Select(i => C((ushort)(0x1682 + i * 2), inventory[i]))];


        void Add(string name, InputEvent[] events, SceneWrite[]? setup = null, int time = 100, InputModalPlan? modal = null)
        {
            cases.Add(new(name, setup ?? [], events, Checks(state, time))
            {
                FarChecks = [new(0x492e, Records(bug))],
                WindowChecks = [new(3, 0, 2), new(4, 0, (ushort)time), new(5, 0, 4)],
                Pixels = pixels(state, inventory),
                Modal = modal
            });
        }


        tick = 1;
        Add("OddTickDoesNotMoveBugs", [Steps(1)], [W(0x64e, 0), W(0x20, 0), W(0x4a24, 9), W(0x4a26, 7)]);
        tick = 2;
        BoardBugs(state, 0, 1);
        bug = 1;
        Word(state, 0xa3e, 1);
        Add("EvenTickPreservesBugOrderAndDirectionFallback", [Steps(1)]);
        tick = 3;
        Add("NextOddTickKeepsAllMonsterRecords", [Steps(1)]);
        tick = 4;
        BoardBugs(state, 1, 2);
        bug = 2;
        Word(state, 0xa3e, 2);
        Add("BlockedLeftTurnUsesOriginalForwardChoice", [Steps(1)]);
        tick = 8;
        BoardBugs(state, 2, 4);
        bug = 4;
        Word(state, 0xa3e, 4);
        Add("RepeatedTicksExecuteOriginalBugCollisions", [Steps(4)]);
        generation++;
        state = Initial();
        bug = 0;
        Add("OriginalManualRestartRestoresAllThreeBugs", [new(0x111, 0x71, 0), .. Key(0x20)]);
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            // Authenticated floor route from (21,12) to the real water at (17,11).
            // Inventory is a declared precondition; terrain/player are untouched.
            inventory = [1, 2, 3, 4, 0, 1, 1, 1];
            int cycleStartTick = tick;
            for (int move = 0; move < 5; move++)
            {
                var events = new List<InputEvent>();
                if (move > 0)
                {
                    events.Add(Steps(2));
                    tick += 2;
                    BoardBugs(state, bug, move);
                    bug = move;
                }

                int x = 21 - move, y = 11;
                int oldX = BitConverter.ToUInt16(state, 0x808), oldY = BitConverter.ToUInt16(state, 0x80a);
                state[oldY * 32 + oldX] = 0;
                state[y * 32 + x] = move == 0
                    ? (byte)0x6c
                    : (byte)0x6d;
                Word(state, 0x808, x);
                Word(state, 0x80a, y);
                Word(state, 0xa24, x - 4);
                Word(state, 0xa26, y - 4);
                Word(state, 0xa40, 1);
                Word(state, 0xa3e, 0);
                events.AddRange(Key(move == 0
                    ? (ushort)0x26
                    : (ushort)0x25));
                SceneWrite[] setup = move == 0
                    ? Enumerable.Range(0, 8).Select(i => W((ushort)(0x1682 + i * 2), inventory[i])).ToArray()
                    : [];
                if (move < 4)
                {
                    Word(state, 0xa34, move + 1);
                    Add($"AuthenticWaterRoute{cycle}Step{move + 1}", events.ToArray(), setup, 100 - (tick / 10 - cycleStartTick / 10));
                }
                else
                {
                    state[y * 32 + x] = 0x33;
                    state[0x400 + y * 32 + x] = 3;
                    Word(state, 0x816, 2);
                    var modalChecks = Checks(state, 100 - (tick / 10 - cycleStartTick / 10));
                    modalChecks.RemoveAll(c => c.Offset == 0x22);
                    modalChecks.Add(C(0x22, 1));
                    var modal = new InputModalPlan("Ooops! Chip can't swim without flippers!", modalChecks, [new(0x492e, Records(bug))], pixels(state, inventory));
                    generation++;
                    inventory = new ushort[8];
                    state = Initial();
                    bug = 0;
                    events.AddRange(Key(0x20));
                    Add($"OriginalDrowningAndRestart{cycle}", events.ToArray(), setup, modal: modal);
                }
            }
        }

        // Controlled collision: keep the authentic monster list/order and place
        // Chip on the second bug's next destination. No replacement AI is used.
        state[12 * 32 + 21] = 0;
        state[10 * 32 + 13] = 0x6e;
        Word(state, 0x808, 13);
        Word(state, 0x80a, 10);
        Word(state, 0xa24, 9);
        Word(state, 0xa26, 6);
        var collisionSeed = state.ToArray();
        tick += 2;
        BoardBugs(state, 0, 1);
        Word(state, 0x816, 5);
        Word(state, 0xa3e, 1);
        state[0x400 + 10 * 32 + 13] = 0x6e;
        var eatenChecks = Checks(state);
        eatenChecks.RemoveAll(c => c.Offset == 0x22);
        eatenChecks.Add(C(0x22, 1));
        var eaten = new InputModalPlan("Ooops! Look out for creatures!", eatenChecks, [new(0x492e, Records(1))], pixels(state, inventory));
        generation++;
        state = Initial();
        bug = 0;
        Add("OriginalBugCollisionDeathAndRestart", [Steps(2), .. Key(0x20)], [new(0x4000, collisionSeed, "Controlled monster-collision precondition")], modal: eaten);
        // Controlled fire next to the authentic start; original death and restart.
        state[12 * 32 + 22] = 4;
        var fireSeed = state.ToArray();
        state[12 * 32 + 21] = 0;
        state[12 * 32 + 22] = 0x34;
        state[0x400 + 12 * 32 + 22] = 4;
        Word(state, 0x808, 22);
        Word(state, 0xa24, 18);
        Word(state, 0x816, 1);
        Word(state, 0xa40, 1);
        var burnedChecks = Checks(state);
        burnedChecks.RemoveAll(c => c.Offset == 0x22);
        burnedChecks.Add(C(0x22, 1));
        var burned = new InputModalPlan("Ooops! Don't step in the fire without fire boots!", burnedChecks, [new(0x492e, Records(0))], pixels(state, inventory));
        generation++;
        state = Initial();
        Add("OriginalFireDeathAndRestart", [.. Key(0x27), .. Key(0x20)], [new(0x4000, fireSeed, "Controlled fire precondition")], modal: burned);
        tick = 30;
        BoardBugs(state, 0, 1);
        Word(state, 0xa3e, 1);
        Word(state, 0x816, 6);
        var timeoutChecks = Checks(state, 0);
        timeoutChecks.RemoveAll(c => c.Offset == 0x22);
        timeoutChecks.Add(C(0x22, 1));
        var timeout = new InputModalPlan("Ooops! Out of time!", timeoutChecks, [new(0x492e, Records(1))], pixels(state, inventory));
        generation++;
        state = Initial();
        Add("OriginalTimeoutDeathAndRestart", [Steps(1), .. Key(0x20)], [W(0x64e, 29), W(0x1694, 1)], modal: timeout);
        // The authentic monster records are freshly restored by the timeout
        // restart. A single explicit water fixture verifies deletion/compaction,
        // including the original stale tail record and unchanged list capacity.
        Word(state, 0xa24, 9);
        Word(state, 0xa26, 7);
        state[11 * 32 + 14] = 3;
        var bugWaterSeed = state.ToArray();
        BoardBugs(state, 0, 1);
        state[11 * 32 + 14] = 3;
        Word(state, 0x928, 2);
        Word(state, 0xa3e, 1);
        tick = 32;
        byte[] compacted = Records(1).Skip(11).Concat(Records(0).Skip(22)).ToArray();
        cases.Add(new("OriginalBugDrownsAndListCompacts", [new(0x4000, bugWaterSeed, "Controlled bug/water interaction with authentic list records")], [Steps(2)], Checks(state))
        {
            FarChecks = [new(0x492e, compacted)],
            Pixels = pixels(state, inventory),
            WindowChecks = [new(5, 0, 4)]
        });
        generation++;
        state = Initial();
        bug = 0;
        Add("RestartRestoresDeletedMonsterAndListCapacity", [new(0x111, 0x71, 0), .. Key(0x20)]);
        return new("0002:225C", 0, 0, [new(0x10, 0), new(0x12, 1)], [0x20, 0x25, 0x26, 0x27, 0x28], [W(0x22, 0), W(0x13c8, 0)], cases)
        {
            Movement = true
        };
    }
}
