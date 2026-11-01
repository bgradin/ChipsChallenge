using System.Diagnostics;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeGameClockTests
{
    internal static NeApplicationProgram Build() => NeApplicationProgram.Build(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true, KeyboardInput: true, AuthenticLevel: true, PlayerMovement: true, GameClock: true), levelData: File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"))!, "CHIPS.DAT")));

    [SuppliedNeFact]
    public void ClockExportIsDeterministicAndIncludesOriginalTimerLifecycle()
    {
        var program = Build();
        Assert.Equal(program.Executable, Build().Executable);
        Assert.NotNull(program.Manifest.Clock);
        foreach (string address in new[]
        {
            "2:16FA",
            "2:176E",
            "2:17A2",
            "2:17BA",
            "7:0000"
        }

        )
        {
            Assert.Contains(program.Manifest.Translation.Functions, f => f.Address == Translator.Binary.NeBinaryAddress.Parse(address));
        }

        Assert.Equal(25, program.Manifest.Input!.Cases.Count);
    }

    [WindowsSuppliedNeFact]
    public void OriginalClockSupportsExactTicksPauseResumeReplacementAndDeferredReentry()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "clock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Clock64.exe"), log = Path.Combine(directory, "native.txt");
            File.WriteAllBytes(path, program.Executable);
            string original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
            var start = new ProcessStartInfo(path)
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
                Assert.Fail("Original clock timed out.");
            }

            string diagnostic = File.Exists(log)
                ? File.ReadAllText(log)
                : "No native diagnostic";
            Assert.True(process.ExitCode == 0, diagnostic);
            foreach (string line in new[]
            {
                "ClockDelivered: 82",
                "ClockDeferred: 2",
                "ClockPosted: 1",
                "ClockElapsedMs: 10240",
                "CurrentTick: 52",
                "TimeRemaining: 95",
                "PlayerX: 14",
                "PlayerY: 14",
                "IsBuffered: 0",
                "ChipHasMoved: 0",
                "PauseDepth: 0",
                "IdleTickCount: 3"
            }

            )
            {
                Assert.Contains(line, diagnostic);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [WindowsSuppliedNeFact]
    public void ChangedTickCountAndDisabledOriginalDoTickFailIndependentStateOracles()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "clock-mutation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Clock64.exe"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;

            int Run(byte[] bytes)
            {
                File.WriteAllBytes(path, bytes);
                var start = new ProcessStartInfo(path)
                {
                    UseShellExecute = false
                };
                foreach (string arg in new[]
                {
                    "--original",
                    original,
                    "--levels",
                    Path.Combine(Path.GetDirectoryName(original)!, "CHIPS.DAT"),
                    "--lifecycle-test"
                }

                )
                {
                    start.ArgumentList.Add(arg);
                }

                using var process = Process.Start(start)!;
                if (!process.WaitForExit(30000))
                {
                    process.Kill(true);
                    Assert.Fail("Clock mutation timed out.");
                }

                return process.ExitCode;
            }


            int Find(byte[] source, byte[] pattern) => Enumerable.Range(0, source.Length - pattern.Length + 1).First(i => source.AsSpan(i, pattern.Length).SequenceEqual(pattern));

            byte[] changed = program.Executable.ToArray();
            byte[] singleStep = [1, 0, 1, 0, 0x13, 1, 1, 0, 1, 0, 0, 0];
            int at = Find(program.Executable, program.Descriptor) + Find(program.Descriptor, singleStep);
            changed[at + 8] = 2;
            Assert.Equal(40, Run(changed));
            int tick = program.Manifest.Translation.Functions.Single(f => f.Address == Translator.Binary.NeBinaryAddress.Parse("7:0000")).NativeOffset;
            var functions = program.Functions.Select(f =>
            {
                if (f.Name != "application_bodies")
                {
                    return f;
                }

                byte[] code = f.Bytes.ToArray();
                code[tick] = 0x31;
                code[tick + 1] = 0xc0;
                code[tick + 2] = 0xc3;
                return f with
                {
                    Bytes = code
                };
            }).ToArray();
            Assert.Equal(40, Run(new Pe32PlusWriter().WriteGuiProgram(program.Entry, functions, program.Imports)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr key, IntPtr lparam);
    [WindowsSuppliedNeFact]
    public void LiveWindowsTimerAdvancesOriginalClockAndStopsCleanlyOnClose()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "clock-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? process = null;
        try
        {
            string path = Path.Combine(directory, "Clock64.exe"), log = Path.Combine(directory, "native.txt"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
            File.WriteAllBytes(path, program.Executable);
            var start = new ProcessStartInfo(path)
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
            var watch = Stopwatch.StartNew();
            IntPtr window = IntPtr.Zero;
            while (window == IntPtr.Zero && watch.ElapsedMilliseconds < 10000 && !process.HasExited)
            {
                process.Refresh();
                window = process.MainWindowHandle;
                if (window == IntPtr.Zero)
                {
                    Thread.Sleep(20);
                }
            }

            Assert.NotEqual(IntPtr.Zero, window);
            Thread.Sleep(1500);
            Assert.False(process.HasExited);
            Assert.True(PostMessageW(window, 0x10, UIntPtr.Zero, IntPtr.Zero));
            Assert.True(process.WaitForExit(10000));
            string diagnostic = File.Exists(log)
                ? File.ReadAllText(log)
                : "No live diagnostic";
            Assert.True(process.ExitCode == 0, diagnostic);

            int Value(string name) => int.Parse(diagnostic.Split('\n').Single(l => l.StartsWith(name + ": ", StringComparison.Ordinal)).Split(':')[1].Trim());

            Assert.True(Value("CurrentTick") > 0, diagnostic);
            Assert.True(Value("ClockDelivered") > 0, diagnostic);
            Assert.Equal(0, Value("RuntimeError"));
            Assert.Equal(0, Value("CleanupCode"));
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

            Directory.Delete(directory, true);
        }
    }
}
