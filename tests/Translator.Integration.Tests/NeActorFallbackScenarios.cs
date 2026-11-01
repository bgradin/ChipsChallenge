using System.Diagnostics;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    private static IReadOnlyList<object> ObserveActorFallbacks()
    {
        var cases = new List<(byte Family, int[] Blocked, int Expected, int PlayerX)>();
        // Direction codes are N,W,S,E. These are reviewed original preference
        // vectors, not calls to the translated AI or shared production rules.
        foreach (var (family, order) in new (byte, int[])[]
        {
            (0x40, [1, 0, 3, 2]),
            (0x44, [0, 3, 1, 2]),
            (0x50, [0, 1, 3, 2]),
            (0x60, [3, 0, 1, 2])
        })
        {
            for (int blocked = 1; blocked < 4; blocked++)
            {
                cases.Add((family, order.Take(blocked).ToArray(), order[blocked], 15));
            }
        }

        cases.Add((0x48, [0], 2, 15));
        cases.Add((0x48, [0, 2], -1, 15));
        cases.Add((0x4c, [0], -1, 15));
        cases.Add((0x54, [2], 3, 18));
        cases.Add((0x58, [0, 1, 2], 3, 15));
        cases.Add((0x5c, [0, 1, 2], 3, 15));
        (int X, int Y)[] vectors = [(0, -1), (-1, 0), (0, 1), (1, 0)];
        var results = new List<object>();
        foreach (var test in cases)
        {
            var level = new ParityScenarioLevel();
            level.Tile(15, 12, test.Family);
            level.Fields.Add((10, new byte[] { 15, 12 }));
            if (test.PlayerX != 15)
            {
                level.Tile(15, 15, 0);
                level.Tile(test.PlayerX, 15, 0x6c);
            }

            foreach (int blocked in test.Blocked)
            {
                var v = vectors[blocked];
                level.Tile(15 + v.X, 12 + v.Y, 1);
            }

            using var session = new ParitySession("actor-fallback-" + results.Count, ParityScenarioLevel.Build([level]), ParityScenarioLevel.Title(0));
            var before = Observe(session);
            PostMessageW(session.Window, 0x100, 0x20, 1);
            PostMessageW(session.Window, 0x101, 0x20, unchecked((nint)(int)0xc0000001));
            GuestSnapshot current = before;
            int at = 12 * 32 + 15;
            if (test.Expected < 0)
            {
                Until(() =>
                {
                    current = Observe(session);
                    return unchecked((ushort)(current.Global(0x64e) - before.Global(0x64e))) >= 12;
                }, session.Process, "Blocked actor timer did not advance");
                Assert.Equal(test.Family, current.Game[at]);
                Assert.Equal((ushort)1, current.Word(0x928));
            }
            else
            {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 10000)
                {
                    current = Observe(session);
                    int found = Array.FindIndex(current.Game, 0, 1024, t => t >= test.Family && t < test.Family + 4);
                    if (found >= 0 && found != at)
                    {
                        at = found;
                        break;
                    }

                    Thread.Sleep(1);
                }

                var delta = vectors[test.Expected];
                Assert.Equal(15 + delta.X, at % 32);
                Assert.Equal(12 + delta.Y, at / 32);
                Assert.Equal((byte)(test.Family + test.Expected), current.Game[at]);
            }

            results.Add(new
            {
                Family = test.Family,
                BlockedDirections = test.Blocked,
                ExpectedDirection = test.Expected,
                ActualX = at % 32,
                ActualY = at / 32,
                ActualTile = current.Game[at],
                PlayerX = test.PlayerX
            });
            session.Close();
        }

        return results;
    }
}
