namespace Translator.ChipsChallenge;

/// <summary>Authentic LESSON 1 route followed by original EndLevel and LESSON 2 initialization.</summary>
public static class ChipsCompletionProfile
{
    public static readonly string[] Functions = ["7:0CCA", "6:03C6", "1:0110", "1:00BC", "1:00C0", "1:060E", "2:18DE", "2:198E", "2:19CA", "2:1A86", "2:1ADC", "2:1C1C", "8:0308"];
    public static readonly (ushort Offset, string Name)[] Snapshot = ChipsHazardProfile.Snapshot.Concat(new (ushort, string)[] { (0x1696, "ScoreLow"), (0x1698, "ScoreHigh"), (0x4800, "Level") }).ToArray();

    public static ChipsInputPlan Create(byte[] first, byte[] second, Func<byte[], IReadOnlyList<ushort>, IReadOnlyList<ScenePixels>> pixels)
    {
        var route = ChipsTileProfile.Create(first, pixels);
        var cases = route.Cases.Where(c => c.Name.StartsWith("AuthenticLesson1", StringComparison.Ordinal)).ToList();
        if (cases.Count != 97)
        {
            throw new InvalidOperationException("Completion requires the full authenticated 97-step route.");
        }

        static InputCheck C(ushort at, int value) => new(at, BitConverter.GetBytes((ushort)value));

        var state = second.ToArray();

        void W(int at, int value) => BitConverter.GetBytes((ushort)value).CopyTo(state, at);

        W(0x92c, 1);
        W(0x92e, 0);
        W(0x930, 0x5000);
        W(0xa24, 17);
        W(0xa26, 8);
        W(0xa28, 9);
        W(0xa2a, 9);
        byte[] bugs = new byte[33];
        int[][] records = [[0x40, 14, 12, 0, -1], [0x40, 13, 11, 0, -1], [0x40, 13, 13, 0, -1]];
        for (int i = 0; i < 3; i++)
        {
            bugs[i * 11] = (byte)records[i][0];
            for (int j = 1; j < 5; j++)
            {
                BitConverter.GetBytes((ushort)records[i][j]).CopyTo(bugs, i * 11 + 1 + (j - 1) * 2);
            }
        }

        cases.Add(new("AuthenticExitOriginalScoreAndNextLevel", [], [new(0x113, 1, 2, 1, InputDriverOperation.AdvanceTicks), new(0x100, 0x26, 1), new(0x101, 0x26, 0xc0000001), new(0x100, 0x20, 1), new(0x101, 0x20, 0xc0000001)], [new(0x4000, state), C(0x64e, 194), C(0x1692, 4), C(0x1694, 100), C(0x1696, 1310), C(0x1698, 0), C(0x22, 0), C(0x20, 0), .. Enumerable.Range(0, 8).Select(i => C((ushort)(0x1682 + i * 2), 0))])
        {
            Completion = ["Yowser! First Try!", "Time Bonus:  810", "Level Bonus:  500", "Total Score:  1310", "You have established a time record for this level!", "Level Score:  1310"],
            FarChecks = [new(0x492e, bugs)],
            WindowChecks = [new(3, 0, 2), new(4, 0, 100), new(5, 0, 4)],
            Pixels = pixels(state, new ushort[8])
        });
        // A real movement in the next level proves the same process remains playable.
        state = state.ToArray();
        state[12 * 32 + 21] = 0;
        state[11 * 32 + 21] = 0x6c;
        W(0x808, 21);
        W(0x80a, 11);
        W(0xa26, 7);
        W(0xa34, 1);
        W(0xa40, 1);
        cases.Add(new("ContinuePlayingAuthenticNextLevel", [], [new(0x100, 0x26, 1), new(0x101, 0x26, 0xc0000001)], [new(0x4000, state), C(0x1696, 1310)])
        {
            FarChecks = [new(0x492e, bugs)],
            Pixels = pixels(state, new ushort[8])
        });
        return route with
        {
            Keys = [0x20, .. route.Keys],
            InitialWrites = [.. route.InitialWrites, new(0x13c6, [0, 0], "Original music-disabled configuration; no MIDI backend")],
            Cases = cases
        };
    }
}
