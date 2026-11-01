using System.Diagnostics;
using Translator.Linker;
using Xunit;
using System.Runtime.InteropServices;
using System.Text;

namespace Translator.Integration.Tests;

[CollectionDefinition("Native modal desktop", DisableParallelization = true)]
public sealed class NativeModalDesktopCollection
{
}

[Collection("Native modal desktop")]
public sealed class NeHazardRestartTests
{
    internal static NeApplicationProgram Build() => NeApplicationProgram.Build(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true, KeyboardInput: true, AuthenticLevel: true, PlayerMovement: true, GameClock: true, TileInteractions: true, Hazards: true), levelData: File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"))!, "CHIPS.DAT")));

    [SuppliedNeFact]
    public void HazardExportIncludesOriginalMonsterAndDeathRestartBodiesAndIsDeterministic()
    {
        var program = Build();
        Assert.Equal(program.Executable, Build().Executable);
        Assert.NotNull(program.Manifest.Hazards);
        foreach (string address in new[]
        {
            "3:074E",
            "3:0228",
            "7:18DA",
            "2:0B9A",
            "4:0356",
            "4:0240"
        }

        )
        {
            Assert.Contains(program.Manifest.Translation.Functions, f => f.Address == Translator.Binary.NeBinaryAddress.Parse(address));
        }

        Assert.Contains(program.Manifest.Translation.FatalBoundaries, b => b.Contains("0007:0A74"));
        Assert.Equal(21, program.Manifest.Input!.Cases.Count);
        Assert.Equal(5, program.Manifest.Input.Cases.Count(c => c.Modal is not null));
        Assert.All(program.Manifest.Input.Cases, c => Assert.NotEmpty(c.FarChecks));
        using var matrix = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(program.Manifest.Tiles));
        Assert.Equal("LESSON 2", matrix.RootElement.GetProperty("AcceptanceLevel").GetString());
        Assert.Equal("OriginalBugSubset", matrix.RootElement.GetProperty("Tiles")[64].GetProperty("TranslationStatus").GetString());
        var free = Win16_Runtime.Win16WindowContracts.HazardByImportIdentity["KERNEL.#17"];
        Assert.Equal(2, free.CleanupBytes);
    }

    [WindowsSuppliedNeFact]
    public void OriginalBugsWaterFireTimeoutDeathsRestartWithOwnedListsAndPixels()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "hazard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Hazards64.exe"), log = Path.Combine(directory, "native.txt"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
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
                Assert.Fail("Original hazard/restart acceptance timed out.");
            }

            string diagnostic = File.Exists(log)
                ? File.ReadAllText(log)
                : "No native diagnostic";
            Assert.True(process.ExitCode == 0, diagnostic);
            Assert.Contains("DeathDialogs: 5", diagnostic);
            Assert.Contains("ListCleanup: 1", diagnostic);
            Assert.Contains("Restarts: 7", diagnostic);
        }
        finally
        {
            NeApplicationTests.DeleteProcessDirectory(directory);
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
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr key, IntPtr lparam);
    [WindowsSuppliedNeFact]
    public void LiveAuthenticDrowningShowsRealModalDialogAndRestartPlacardAndMenuWork()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "hazard-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? process = null;
        try
        {
            string path = Path.Combine(directory, "Hazards64.exe"), log = Path.Combine(directory, "native.txt"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
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

            void Key(ushort key)
            {
                Assert.True(PostMessageW(window, 0x100, (UIntPtr)key, new IntPtr(1)));
                Assert.True(PostMessageW(window, 0x101, (UIntPtr)key, (IntPtr)unchecked((int)0xc0000001)));
                Thread.Sleep(270);
            }


            Key(0x26);
            for (int i = 0; i < 4; i++)
            {
                Key(0x25);
            }

            IntPtr dialog = IntPtr.Zero;
            watch.Restart();
            while (dialog == IntPtr.Zero && watch.ElapsedMilliseconds < 7000 && !process.HasExited)
            {
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out uint id);
                    var text = new StringBuilder(80);
                    GetClassNameW(hwnd, text, text.Capacity);
                    if (id == process.Id && text.ToString() == "#32770")
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
                : process.HasExited
                    ? $"Process exited {process.ExitCode:X8} without diagnostics"
                    : "No native dialog");
            var messages = new StringBuilder();
            EnumChildWindows(dialog, (hwnd, _) =>
            {
                var text = new StringBuilder(256);
                GetWindowTextW(hwnd, text, text.Capacity);
                messages.Append(text);
                return true;
            }, IntPtr.Zero);
            Assert.Contains("Chip can't swim without flippers!", messages.ToString());
            Assert.True(PostMessageW(dialog, 0x10, UIntPtr.Zero, IntPtr.Zero));
            watch.Restart();
            while (IsWindow(dialog) && watch.ElapsedMilliseconds < 5000 && !process.HasExited)
            {
                Thread.Sleep(20);
            }

            Assert.False(IsWindow(dialog));
            Thread.Sleep(400);
            Assert.False(process.HasExited);
            Key(0x20);
            Assert.True(PostMessageW(window, 0x111, (UIntPtr)0x71, IntPtr.Zero));
            Thread.Sleep(400);
            Assert.False(process.HasExited);
            Key(0x20);
            Assert.True(PostMessageW(window, 0x10, UIntPtr.Zero, IntPtr.Zero), File.Exists(log)
                ? File.ReadAllText(log)
                : "Live main window disappeared before close");
            Assert.True(process.WaitForExit(10000), "Live close timed out");
            string diagnostic = File.Exists(log)
                ? File.ReadAllText(log)
                : "No live diagnostic";
            Assert.True(process.ExitCode == 0, diagnostic);
            Assert.Contains("DeathDialogs: 1", diagnostic);
            Assert.Contains("Restarts: 2", diagnostic);
            Assert.Contains("ListCleanup: 1", diagnostic);
            Assert.DoesNotContain("PlacardDraws: 0", diagnostic);
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

            NeApplicationTests.DeleteProcessDirectory(directory);
        }
    }

    [WindowsSuppliedNeFact]
    public void ChangedDeathOracleRemovedOriginalAIAndRestartAndListFreeCannotPass()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "hazard-mutation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Hazards64.exe"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;

            (int Exit, string Log) Run(byte[] bytes)
            {
                string log = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
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
                    Assert.Fail("Hazard mutation timed out.");
                }

                return (process.ExitCode, File.ReadAllText(log));
            }


            byte[] Disable(string address)
            {
                int at = program.Manifest.Translation.Functions.Single(f => f.Address == Translator.Binary.NeBinaryAddress.Parse(address)).NativeOffset;
                var functions = program.Functions.Select(f =>
                {
                    if (f.Name != "application_bodies")
                    {
                        return f;
                    }

                    byte[] code = f.Bytes.ToArray();
                    code[at] = 0x31;
                    code[at + 1] = 0xc0;
                    code[at + 2] = 0xc3;
                    return f with
                    {
                        Bytes = code
                    };
                }).ToArray();
                return new Pe32PlusWriter().WriteGuiProgram(program.Entry, functions, program.Imports);
            }

            foreach (string address in new[]
            {
                "3:074E",
                "4:0356"
            }

            )
            {
                var result = Run(Disable(address));
                Assert.True(result.Exit == 40, result.Log);
                Assert.Contains("CleanupCode: 0", result.Log);
            }

            var leaked = Run(Disable("4:0240"));
            Assert.True(leaked.Exit == 50, leaked.Log);
            Assert.Contains("ListCleanup: 0", leaked.Log);

            int Find(byte[] source, byte[] pattern) => Enumerable.Range(0, source.Length - pattern.Length + 1).First(i => source.AsSpan(i, pattern.Length).SequenceEqual(pattern));

            var modal = program.Manifest.Input!.Cases.Single(c => c.Name == "OriginalDrowningAndRestart1").Modal!;
            byte[] changed = program.Executable.ToArray();
            int descriptor = Find(changed, program.Descriptor);
            int offset = descriptor + Find(program.Descriptor, modal.Checks.Single(c => c.Offset == 0x4000).Bytes) + 0x816;
            changed[offset] = 1;
            var wrongDeath = Run(changed);
            Assert.True(wrongDeath.Exit == 40, wrongDeath.Log);
            Assert.Contains("CleanupCode: 0", wrongDeath.Log);
            byte[] hash = Convert.FromHexString(modal.Pixels[0].Sha256);
            changed = program.Executable.ToArray();
            changed[descriptor + Find(program.Descriptor, hash)] ^= 1;
            var wrongPixels = Run(changed);
            Assert.True(wrongPixels.Exit == 40, wrongPixels.Log);
        }
        finally
        {
            NeApplicationTests.DeleteProcessDirectory(directory);
        }
    }
}
