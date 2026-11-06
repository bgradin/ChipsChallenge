namespace Translator.ChipsChallenge;

/// <summary>Original timer roots and explicit independent state oracles. The driver
/// delivers time/messages and calls original entries; it implements no gameplay.</summary>
public static class ChipsClockProfile
{
    public static readonly string[] Functions = ["2:176E", "2:17A2", "2:17BA"];
    public static readonly (ushort Offset, string Name)[] Snapshot = [(0x64e, "CurrentTick"), (0x1694, "TimeRemaining"), (0x4808, "PlayerX"), (0x480a, "PlayerY"), (0x480e, "IsBuffered"), (0x4a40, "ChipHasMoved"), (0x22, "PauseDepth"), (0x4a3e, "IdleTickCount")];

    private static void Word(byte[] b, int at, int value) => BitConverter.GetBytes(unchecked((ushort)value)).CopyTo(b, at);

    private static SceneWrite Write(ushort at, ushort value) => new(at, BitConverter.GetBytes(value), "Deterministic clock precondition; automation only");
    public static ChipsInputPlan Create(byte[] originalState, Func<byte[], ushort, IReadOnlyList<ScenePixels>> pixels, bool includeGlitches = false)
    {
        var state = originalState.ToArray();
        Word(state, 0xa24, 11);
        Word(state, 0xa26, 10);
        Word(state, 0xa28, 9);
        Word(state, 0xa2a, 9);
        var cases = new List<InputCase>();

        InputEvent Steps(uint n, ushort id = 1) => new(0x113, id, n, 1, InputDriverOperation.AdvanceTicks);


        InputEvent Time(uint ms) => new(0x113, 1, ms, 1, InputDriverOperation.AdvanceMilliseconds);


        InputEvent Call(ushort offset, ushort? argument = null, ushort expected = 0xffff) => new(argument.HasValue
            ? (ushort)1
            : (ushort)0, argument ?? 0, 0x00020000u | offset, expected, InputDriverOperation.CallOriginal);


        void Add(string name, int tick, int idle, int time, ushort pause, InputEvent[] events, SceneWrite[]? setup = null)
        {
            Word(state, 0xa3e, idle);

            InputCheck W(ushort at, ushort value) => new(at, BitConverter.GetBytes(value));

            cases.Add(new(name, setup ?? [], events, [new(0x4000, state.ToArray()), W(0x64e, (ushort)tick), W(0x1694, (ushort)time), W(0x1692, 11), W(0x22, pause), W(0x20, 0)])
            {
                Pixels = pixels(state, 0)
            });
        }


        Add("SingleStep", 1, 0, 100, 0, [Steps(1)], [new(0x4000, state.ToArray(), "Original level idle-state fixture"), Write(0x64e, 0), Write(0x1694, 100), Write(0x20, 0)]);
        Add("Elapsed109msNoTick", 1, 0, 100, 0, [Time(109)]);
        Add("Elapsed110msOneTick", 2, 1, 100, 0, [Time(1)]);
        Add("CountdownAtTenTicks", 10, 5, 99, 0, [Steps(8)]);
        Add("AdvanceTwentyTicks", 30, 15, 97, 0, [Steps(20)]);
        Add("OriginalPause", 30, 15, 97, 1, [Call(0x17a2)]);
        Add("PausedTwentyTicks", 30, 15, 97, 1, [Steps(20)]);
        Add("NestedPauseAndPartialResume", 30, 15, 97, 1, [Call(0x17a2), Call(0x17ba), Steps(3)]);
        Add("OriginalResume", 31, 15, 97, 0, [Call(0x17ba), Steps(1)]);
        Add("ResumeClampsAtZero", 32, 16, 97, 0, [Call(0x17ba), Steps(1)]);
        Add("ReplaceTimerResetsInterval", 32, 16, 97, 0, [Call(0x16fa, 1, 1), Time(109)]);
        Add("ReplacedTimerFirstDeadline", 33, 16, 97, 0, [Time(1)]);
        Add("OriginalKillTimer", 33, 16, 97, 0, [Call(0x176e, 1, 1)]);
        Add("StoppedTimerElapsed", 33, 16, 97, 0, [Time(1000)]);
        Add("OriginalRestartTimer", 40, 20, 96, 0, [Call(0x16fa, 1, 1), Steps(7)]);
        Add("NestedTimerCoalescesWithoutReentry", 42, 21, 96, 0, [new(0x113, 1, 2, 1, InputDriverOperation.ProbeNestedTimer)]);
        Add("SecondTimerDoesNotAdvanceGame", 42, 21, 96, 0, [Call(0x16fa, 2, 1), Steps(3, 2), Call(0x176e, 2, 1)]);
        Add("KillMissingTimerReturnsFalse", 42, 21, 96, 0, [Call(0x176e, 2, 0)]);
        Add("RejectInvalidTimerContracts", 42, 21, 96, 0, [new(0, 0, 0, 0, InputDriverOperation.ProbeTimerContracts)]);
        Word(state, 0xa40, 1);
        Word(state, 0x80e, 1);
        Word(state, 0x812, -1);
        Add("BufferInputBeforePause", 42, 21, 96, 0, [new(0x100, 0x25, 1), new(0x101, 0x25, 0xc0000001)], [Write(0x4a40, 1)]);
        Add("PauseRetainsBufferedInput", 42, 21, 96, 1, [Call(0x17a2), Steps(4)]);
        Add("ResumedOddTickRetainsAlreadyMoved", 43, 21, 96, 0, [Call(0x17ba), Steps(1)]);
        Word(state, 0xa40, 0);
        Add("ResumedEvenTickClearsAlreadyMoved", 44, 21, 96, 0, [Steps(1)]);
        state[14 * 32 + 15] = 0;
        state[14 * 32 + 14] = 0x6d;
        Word(state, 0x808, 14);
        Word(state, 0xa24, 10);
        Word(state, 0xa34, 1);
        Word(state, 0x80e, 0);
        Word(state, 0xa40, 1);
        Add("ResumedTickConsumesOriginalBuffer", 45, 0, 96, 0, [Steps(1)]);
        state[14 * 32 + 14] = 0x6e;
        Word(state, 0xa40, 0);
        Add("OriginalIdleFacingAndCountdown", 52, 3, 95, 0, [Steps(7)]);
        if (includeGlitches)
        {
            // Long First Second: seed the session counter near ushort rollover.
            // The original timer body must skip decrementing at tick zero.
            // Check the entire game state as well as the countdown boundary.
            byte[] rolloverState = state.ToArray();
            void Rollover(string name, int ticks, ushort counter, ushort remaining, int idle, SceneWrite[]? setup = null)
            {
                Word(rolloverState, 0xa3e, idle);
                cases.Add(new(name, setup ?? [], [Steps((uint)ticks)],
                    [new(0x4000, rolloverState.ToArray()), new(0x64e, BitConverter.GetBytes(counter)), new(0x1694, BitConverter.GetBytes(remaining))])
                { Pixels = pixels(state, 0).Where(p => p.Window == 6).ToArray() });
            }
            Rollover("GlitchLongFirstSecondBeforeRollover", 5, 65535, 95, 2,
                [Write(0x64e, 65530), Write(0x1694, 95), Write(0x4a3e, 0)]);
            Rollover("GlitchLongFirstSecondZeroDoesNotDecrement", 1, 0, 95, 3);
            Rollover("GlitchLongFirstSecondNineStillDoesNotDecrement", 9, 9, 95, 7);
            Rollover("GlitchLongFirstSecondTenFinallyDecrements", 1, 10, 94, 8);
            // Leave the established final diagnostic state unchanged.
            cases.Add(new("GlitchLongFirstSecondRestore", [new(0x4000, state.ToArray(), "Restore clock fixture"),
                Write(0x64e, 52), Write(0x1694, 95)], [new(0, 0, 0, 0, InputDriverOperation.ProbeTimerContracts)],
                [new(0x4000, state.ToArray()), new(0x64e, BitConverter.GetBytes((ushort)52)),
                new(0x1694, BitConverter.GetBytes((ushort)95))]) { Pixels = pixels(state, 0) });
        }
        return new("0002:225C", 0, 0, [new(0x10, 0), new(0x12, 1)], [0x25, 0x26, 0x27, 0x28], [Write(0x22, 0), Write(0x13c8, 0)], cases)
        {
            Movement = true
        };
    }
}
