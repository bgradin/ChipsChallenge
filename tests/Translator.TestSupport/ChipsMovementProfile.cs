namespace Translator.ChipsChallenge;

/// <summary>Original-entry selection and independently authored movement acceptance vectors.
/// Fixture writes are automation-only; the native host never computes movement.</summary>
public static class ChipsMovementProfile
{
    public static readonly string[] Functions = ["2:225C", "7:1184", "7:0000", "3:1A56", "3:187C", "3:1934", "3:1804", "3:0486", "2:056E", "2:0306", "2:02B2", "4:0000", "2:0CBE", "8:056C", "3:13DE"];
    public static readonly string[] FatalIndirectSites = ["8:05AD"];

    private static void Word(byte[] bytes, int offset, int value) => BitConverter.GetBytes(unchecked((ushort)value)).CopyTo(bytes, offset);

    private static InputCheck Check(ushort offset, ushort value) => new(offset, BitConverter.GetBytes(value));

    private static SceneWrite Write(ushort offset, ushort value, string why) => new(offset, BitConverter.GetBytes(value), why);
    public static ChipsInputPlan Create(byte[] originalState, Func<byte[], ushort, IReadOnlyList<ScenePixels>> pixels)
    {
        byte[] state = originalState.ToArray();
        Word(state, 0xa24, 11);
        Word(state, 0xa26, 10);
        Word(state, 0xa28, 9);
        Word(state, 0xa2a, 9);
        Word(state, 0xa40, 1);
        var cases = new List<InputCase>();
        int tick = 0;

        InputEvent Key(ushort message, ushort key) => new(message, key, message == 0x101
            ? 0xc0000001u
            : 1u);


        InputEvent Timer() => new(0x113, 1, 0, 1);


        void Add(string name, IReadOnlyList<SceneWrite> setup, IReadOnlyList<InputEvent> events, ushort yellow = 0)
        {
            var checks = new List<InputCheck>
            {
                new(0x4000, state.ToArray()),
                Check(0x64e, (ushort)tick),
                Check(0x20, 0),
                Check(0x1688, yellow),
                Check(0x1692, 11),
                Check(0x1694, 100)
            };
            cases.Add(new(name, setup, events, checks)
            {
                Pixels = pixels(state, yellow)
            });
        }


        void Buffer(string name, ushort key, short dx, short dy, IReadOnlyList<SceneWrite>? setup = null, ushort yellow = 0)
        {
            Word(state, 0x80e, 1);
            Word(state, 0x812, dx);
            Word(state, 0x814, dy);
            Add(name, setup ?? [], [Key(0x100, key), Key(0x101, key)], yellow);
        }

        // Authentic floor-only route: left/up/down/right. No coordinates or terrain
        // are seeded between these keyboard and original WM_TIMER callbacks.
        int x = 15, y = 14, steps = 0;
        foreach (var vector in new[]
        {
            ("Left", (ushort)0x25, (short)-1, (short)0, 14, 14, 0x6d, 10, 10),
            ("Up", (ushort)0x26, (short)0, (short)-1, 14, 13, 0x6c, 10, 9),
            ("Down", (ushort)0x28, (short)0, (short)1, 14, 14, 0x6e, 10, 10),
            ("Right", (ushort)0x27, (short)1, (short)0, 15, 14, 0x6f, 11, 10)
        }

        )
        {
            IReadOnlyList<SceneWrite> setup = steps == 0
                ? [Write(0x4a40, 1, "Test precondition: previous move in current turn"), Write(0x20, 0, "Clean UI dirty flag")]
                : [];
            Buffer("Buffer" + vector.Item1, vector.Item2, vector.Item3, vector.Item4, setup);
            int clocks = tick == 0
                ? 3
                : 2;
            tick += clocks;
            state[y * 32 + x] = 0;
            state[vector.Item6 * 32 + vector.Item5] = (byte)vector.Item7;
            Word(state, 0x808, vector.Item5);
            Word(state, 0x80a, vector.Item6);
            Word(state, 0x80e, 0);
            Word(state, 0xa34, ++steps);
            Word(state, 0xa24, vector.Item8);
            Word(state, 0xa26, vector.Item9);
            x = vector.Item5;
            y = vector.Item6;
            Add("Move" + vector.Item1, [], Enumerable.Range(0, clocks).Select(_ => Timer()).ToArray());
        }

        tick = 0;
        Buffer("BufferRepeatRight", 0x27, 1, 0, [Write(0x64e, 0, "Deterministic repeated-direction test tick")]);
        state[14 * 32 + 15] = 0;
        state[14 * 32 + 16] = 0x6f;
        Word(state, 0x808, 16);
        Word(state, 0x80e, 0);
        Word(state, 0xa24, 12);
        Word(state, 0xa34, 5);
        tick = 3;
        Add("RepeatRight", [], [Timer(), Timer(), Timer()]);
        // Controlled boundary/wall/door fixtures: seed state before input only.
        foreach (var v in new[]
        {
            ("WestBoundary", 0, 0, (ushort)0x25, 0x6d, 0, 0, 0, 0, 0),
            ("NorthBoundary", 0, 0, (ushort)0x26, 0x6c, 0, 0, 0, 0, 0),
            ("EastBoundary", 31, 31, (ushort)0x27, 0x6f, 23, 23, 0, 0, 0),
            ("SouthBoundary", 31, 31, (ushort)0x28, 0x6e, 23, 23, 0, 0, 0),
            ("Wall", 15, 14, (ushort)0x27, 0x6f, 11, 10, 1, 0, 0),
            ("ClosedDoor", 15, 14, (ushort)0x27, 0x6f, 11, 10, 0x19, 0, 0),
            ("OpenDoor", 15, 14, (ushort)0x27, 0x6f, 11, 10, 0x19, 1, 1)
        }

        )
        {
            state = originalState.ToArray();
            Array.Clear(state, 0, 0x800);
            state[v.Item3 * 32 + v.Item2] = 0x6e;
            if (v.Item8 != 0)
            {
                state[v.Item3 * 32 + v.Item2 + 1] = (byte)v.Item8;
            }

            Word(state, 0x808, v.Item2);
            Word(state, 0x80a, v.Item3);
            Word(state, 0xa24, v.Item6);
            Word(state, 0xa26, v.Item7);
            Word(state, 0xa28, 9);
            Word(state, 0xa2a, 9);
            Word(state, 0xa40, 1);
            tick = 0;
            var seed = state.ToArray();
            short dx = v.Item4 == 0x25
                ? (short)-1
                : v.Item4 == 0x27
                    ? (short)1
                    : (short)0;
            short dy = v.Item4 == 0x26
                ? (short)-1
                : v.Item4 == 0x28
                    ? (short)1
                    : (short)0;
            Buffer("Buffer" + v.Item1, v.Item4, dx, dy, [new(0x4000, seed, "Controlled movement fixture; never applied in interactive mode"), Write(0x64e, 0, "Tick precondition"), Write(0x1688, (ushort)v.Item9, "Conditional passability precondition"), Write(0x20, 0, "UI precondition")], (ushort)v.Item9);
            Word(state, 0x80e, 0);
            tick = 3;
            if (v.Item10 != 0)
            {
                state[v.Item3 * 32 + v.Item2] = 0;
                state[v.Item3 * 32 + v.Item2 + 1] = (byte)v.Item5;
                Word(state, 0x808, v.Item2 + 1);
                Word(state, 0xa24, 12);
                Word(state, 0xa34, 1);
            }
            else
            {
                state[v.Item3 * 32 + v.Item2] = (byte)v.Item5;
            }

            Add(v.Item1, [], [Timer(), Timer(), Timer()]);
            if (v.Item1 == "Wall")
            {
                Buffer("BufferRepeatWall", v.Item4, dx, dy);
                tick = 5;
                Word(state, 0x80e, 0);
                Add("RepeatWall", [], [Timer(), Timer()]);
            }
        }

        return new("0002:225C", 0, 0, [new(0x10, 0), new(0x12, 1)], [0x25, 0x26, 0x27, 0x28], [Write(0x22, 0, "Enable original movement/tick gate"), Write(0x13c8, 0, "Original sound-disabled configuration")], cases)
        {
            Movement = true
        };
    }
}
