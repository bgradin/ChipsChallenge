using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

[Collection("Native modal desktop")]
public sealed class NeCompletionProgressionTests
{
    internal static NeApplicationProgram Build() => NeApplicationProgram.Build(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true, KeyboardInput: true, AuthenticLevel: true, PlayerMovement: true, GameClock: true, TileInteractions: true, Hazards: true, Completion: true), levelData: File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"))!, "CHIPS.DAT")));

    [SuppliedNeFact]
    public void CompletionExportHasOriginalScoringAndProgressionAndAuthenticatedRoute()
    {
        var p = Build();
        Assert.Equal(p.Executable, Build().Executable);
        Assert.Equal(99, p.Manifest.Input!.Cases.Count);
        foreach (string address in new[]
        {
            "7:0CCA",
            "6:03C6",
            "4:0356",
            "2:1C1C",
            "1:0110"
        }

        )
        {
            Assert.Contains(p.Manifest.Translation.Functions, f => f.Address == Translator.Binary.NeBinaryAddress.Parse(address));
        }

        Assert.Contains(p.Manifest.Translation.FatalBoundaries, b => b.Contains("0007:0A74"));
        Assert.Single(p.Manifest.Input.Cases, c => c.Completion is not null);
        Assert.All(p.Manifest.Input.Cases.Skip(1), c => Assert.Empty(c.InitialWrites));
    }

    [WindowsSuppliedNeFact]
    public void OriginalExitComputesScoreLoadsNextLevelAndKeepsPlaying()
    {
        var p = Build();
        var result = Run(p.Executable);
        Assert.True(result.Exit == 0, result.Log);
        Assert.Contains("ScoreLow: 1310", result.Log);
        Assert.Contains("Level: 2", result.Log);
        Assert.Contains("PlayerY: 11", result.Log);
        Assert.Contains("ListCleanup: 1", result.Log);
    }

    [WindowsSuppliedNeFact]
    public void RemovedOriginalCompletionOrScoringCannotPass()
    {
        var p = Build();
        foreach (string address in new[]
        {
            "7:0CCA",
            "6:03C6",
            "4:0356"
        }

        )
        {
            int at = p.Manifest.Translation.Functions.Single(f => f.Address == Translator.Binary.NeBinaryAddress.Parse(address)).NativeOffset;
            var functions = p.Functions.Select(f =>
            {
                if (f.Name != "application_bodies")
                {
                    return f;
                }

                var bytes = f.Bytes.ToArray();
                bytes[at] = 0x31;
                bytes[at + 1] = 0xc0;
                bytes[at + 2] = 0xc3;
                return f with
                {
                    Bytes = bytes
                };
            }).ToArray();
            var result = Run(new Pe32PlusWriter().WriteGuiProgram(p.Entry, functions, p.Imports));
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("CleanupCode: 0", result.Log);
        }
    }

    private delegate bool Enumerate(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(Enumerate callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, Enumerate callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int length);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int length);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr key, IntPtr lparam);

    [WindowsSuppliedNeFact]
    public void LiveAuthenticRouteShowsOriginalComputedScoreAndContinuesInNextLevel() => VerifyLiveProgression(Build());
    internal static void VerifyLiveProgression(NeApplicationProgram p)
    {
        string dir = Path.Combine(Path.GetTempPath(), "completion-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Process? process = null;
        try
        {
            string exe = Path.Combine(dir, "Completion64.exe"), log = Path.Combine(dir, "native.txt"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
            File.WriteAllBytes(exe, p.Executable);
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false
            };
            foreach (string arg in new[]
            {
                "--original",
                original,
                "--levels",
                Path.Combine(Path.GetDirectoryName(original)!, "CHIPS.DAT"),
                "--diagnostics",
                log
            }

            )
            {
                start.ArgumentList.Add(arg);
            }

            process = Process.Start(start)!;
            Assert.True(process.WaitForInputIdle(10000));
            process.Refresh();
            IntPtr root = process.MainWindowHandle;
            Assert.NotEqual(IntPtr.Zero, root);

            void Key(ushort key)
            {
                string Failure() => "Cannot post route key " + key.ToString("X") + "; " + (process.HasExited
                    ? "exit " + process.ExitCode
                    : "process running") + "; " + (File.Exists(log)
                    ? File.ReadAllText(log)
                    : "no diagnostic");

                Assert.True(PostMessageW(root, 0x100, (UIntPtr)key, new IntPtr(1)), Failure());
                Assert.True(PostMessageW(root, 0x101, (UIntPtr)key, (IntPtr)unchecked((int)0xc0000001)), Failure());
                Thread.Sleep(270);
            }


            foreach (var step in Translator.ChipsChallenge.ChipsLessonOneRoute.Steps)
            {
                Key(step.Direction switch
                {
                    'L' => 0x25,
                    'U' => 0x26,
                    'D' => 0x28,
                    _ => (ushort)0x27
                });
            }

            Key(0x26);
            IntPtr dialog = IntPtr.Zero;
            var watch = Stopwatch.StartNew();
            while (dialog == IntPtr.Zero && watch.ElapsedMilliseconds < 7000 && !process.HasExited)
            {
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out uint id);
                    var name = new StringBuilder(80);
                    GetClassNameW(hwnd, name, name.Capacity);
                    if (id == process.Id && name.ToString() == "#32770")
                    {
                        dialog = hwnd;
                    }

                    return true;
                }, IntPtr.Zero);
                if (dialog == IntPtr.Zero)
                {
                    Thread.Sleep(20);
                }
            }

            Assert.True(dialog != IntPtr.Zero, File.Exists(log)
                ? File.ReadAllText(log)
                : "No completion notification");
            var text = new StringBuilder();
            EnumChildWindows(dialog, (hwnd, _) =>
            {
                var value = new StringBuilder(512);
                GetWindowTextW(hwnd, value, value.Capacity);
                text.Append(value);
                return true;
            }, IntPtr.Zero);
            Assert.Contains("Yowser! First Try!", text.ToString());
            Assert.Contains("Level Bonus:  500", text.ToString());
            Assert.Contains("Time Bonus:", text.ToString());
            Assert.Contains("Level Score:", text.ToString());
            Assert.Contains("Total Score:", text.ToString());
            Assert.True(PostMessageW(dialog, 0x10, UIntPtr.Zero, IntPtr.Zero));
            watch.Restart();
            while (IsWindow(dialog) && watch.ElapsedMilliseconds < 5000 && !process.HasExited)
            {
                Thread.Sleep(20);
            }

            Assert.False(IsWindow(dialog));
            Thread.Sleep(400);
            Key(0x20);
            Key(0x26);
            Assert.True(PostMessageW(root, 0x10, UIntPtr.Zero, IntPtr.Zero));
            Assert.True(process.WaitForExit(10000));
            string diagnostic = File.Exists(log)
                ? File.ReadAllText(log)
                : "No diagnostic";
            Assert.True(process.ExitCode == 0, diagnostic);
            Assert.Contains("CompletionDialogs: 1", diagnostic);
            Assert.Contains("Level: 2", diagnostic);
            Assert.Contains("PlayerY: 11", diagnostic);
            Assert.Contains("ListCleanup: 1", diagnostic);
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }

                process.Dispose();
            }

            Directory.Delete(dir, true);
        }
    }

    static (int Exit, string Log) Run(byte[] bytes)
    {
        string dir = Path.Combine(Path.GetTempPath(), "completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, "Completion64.exe"), log = Path.Combine(dir, "native.txt"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
            File.WriteAllBytes(exe, bytes);
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false
            };
            foreach (string arg in new[]
            {
                "--original",
                original,
                "--levels",
                Path.Combine(Path.GetDirectoryName(original)!, "CHIPS.DAT"),
                "--lifecycle-test",
                "--diagnostics",
                log
            }

            )
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            if (!process.WaitForExit(30000))
            {
                process.Kill(true);
                Assert.Fail("Completion acceptance timed out.");
            }

            return (process.ExitCode, File.Exists(log)
                ? File.ReadAllText(log)
                : "No native diagnostic");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public sealed class OriginalCompletionArithmeticTests
{
    [Fact]
    public void FinalReturnMayReadZeroFilledAllocationButPaddingCannotBecomeAnEntry()
    {
        byte[] bytes = NeDiscoveryTests.Fixture();
        bytes[0x23e] = 0xca;
        bytes[0x23f] = 10;
        bytes[0x240] = 0xff;
        NeLoaderTests.W(bytes, 0xc6, 65);
        var graph = new Translator.Analysis.NeProgramAnalyzer().Analyze(bytes, "1:003E");
        Assert.True(graph.ClosureComplete);
        var ret = Assert.Single(graph.Functions.Single().Instructions);
        Assert.Equal("CA0A00", ret.BytesHex);
        using (var p = NeNativeProgram.Create(bytes, "1:003E"))
        {
            var state = p.CreateState();
            ushort sp = state.SP;
            p.Invoke(ref state, [0, 0, 0, 0, 0]);
            Assert.Equal(sp, state.SP);
        }

        Assert.ThrowsAny<Exception>(() => new Translator.Analysis.NeProgramAnalyzer().Analyze(bytes, "1:0040"));
        NeLoaderTests.W(bytes, 0xc6, 64);
        Assert.False(new Translator.Analysis.NeProgramAnalyzer().Analyze(bytes, "1:003E").ClosureComplete);
    }

    [SuppliedNeFact]
    public void OriginalLongDivisionHandlesSignedAndMultiwordDivisors()
    {
        using var p = NeNativeProgram.Create(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!), "1:0110");
        foreach (var (dividend, divisor) in new (int, int)[]
        {
            (2000, 5),
            (-2000, 5),
            (2000, -5),
            (-2000, -5),
            (int.MaxValue, 65537),
            (123456789, 100000),
            (-123456789, 100000)
        }

        )
        {
            var s = p.CreateState();
            ushort sp = s.SP;
            p.Invoke(ref s, [(ushort)dividend, (ushort)(dividend >> 16), (ushort)divisor, (ushort)(divisor >> 16)]);
            Assert.Equal(dividend / divisor, unchecked((int)((uint)s.DX << 16 | s.AX)));
            Assert.Equal(sp, s.SP);
        }
    }
}
