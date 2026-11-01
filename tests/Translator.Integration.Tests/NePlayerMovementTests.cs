using System.Diagnostics;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NePlayerMovementTests
{
    internal static NeApplicationProgram Build() => NeApplicationProgram.Build(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true, KeyboardInput: true, AuthenticLevel: true, PlayerMovement: true), levelData: File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"))!, "CHIPS.DAT")));

    [SuppliedNeFact]
    public void MovementExportContainsOriginalStateOwnersAndNoLaunchCoordinateFixture()
    {
        var first = Build();
        var second = Build();
        Assert.Equal(first.Executable, second.Executable);
        foreach (string address in new[]
        {
            "2:225C",
            "7:1184",
            "7:0000",
            "3:1A56",
            "3:0486",
            "2:056E",
            "2:0306"
        }

        )
        {
            Assert.Contains(first.Manifest.Translation.Functions, f => f.Address == Translator.Binary.NeBinaryAddress.Parse(address));
        }

        Assert.DoesNotContain(first.Manifest.Input!.InitialWrites, w => w.Offset >= 0x4000 && w.Offset < 0x4a42);
        Assert.DoesNotContain(first.Manifest.Scene!.Writes, w => w.Offset >= 0x4000 && w.Offset < 0x4a42);
        Assert.Contains(first.Manifest.Translation.Module.Functions.SelectMany(f => f.Instructions), i => i.Operation == "FAULT" && i.BinarySource?.SegmentIndex == 8 && i.BinarySource.Offset == 0x5ad);
    }

    [WindowsSuppliedNeFact]
    public void KeyboardBufferedTicksExecuteOriginalMovementCollisionViewportAndPixels()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "movement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Movement64.exe"), log = Path.Combine(directory, "native.txt");
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
                Assert.Fail("Original movement timed out.");
            }

            Assert.True(process.ExitCode == 0, File.Exists(log)
                ? File.ReadAllText(log)
                : "No native diagnostic");
            Assert.Contains(program.Manifest.Translation.FatalBoundaries, b => b.Contains("0008:05AD"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr key, IntPtr lparam);
    [WindowsSuppliedNeFact]
    public void LiveNativeTimerAndKeyboardRemainResponsiveAndCloseCleanly()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "movement-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? process = null;
        try
        {
            string path = Path.Combine(directory, "Movement64.exe"), log = Path.Combine(directory, "native.txt"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
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
            Assert.True(PostMessageW(window, 0x100, (UIntPtr)0x25, new IntPtr(1)));
            Assert.True(PostMessageW(window, 0x101, (UIntPtr)0x25, (IntPtr)unchecked((int)0xc0000001)));
            // Live-timer smoke check only; exact tick/state assertions are in the
            // deterministic native cases, and timing expansion belongs to 6.5.
            Thread.Sleep(1500);
            Assert.False(process.HasExited);
            Assert.True(PostMessageW(window, 0x10, UIntPtr.Zero, IntPtr.Zero));
            Assert.True(process.WaitForExit(10000));
            Assert.True(process.ExitCode == 0, File.Exists(log)
                ? File.ReadAllText(log)
                : "No live native diagnostic");
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

    [WindowsSuppliedNeFact]
    public void ChangedQueuedDirectionAndExpectedPixelsAreDetectedByIndependentOracles()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "movement-mutation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Movement64.exe"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;

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
                    Assert.Fail("Movement mutation timed out.");
                }

                return process.ExitCode;
            }


            int Find(byte[] source, byte[] pattern) => Enumerable.Range(0, source.Length - pattern.Length + 1).First(i => source.AsSpan(i, pattern.Length).SequenceEqual(pattern));

            int descriptor = Find(program.Executable, program.Descriptor);
            byte[] changed = program.Executable.ToArray();
            byte[] leftEvent = [0, 0, 0, 1, 0x25, 0, 1, 0, 0, 0];
            int at = descriptor + Find(program.Descriptor, leftEvent);
            changed[at + 4] = 0x27;
            Assert.Equal(40, Run(changed));
            byte[] expected = Convert.FromHexString(program.Manifest.Input!.Cases.Single(c => c.Name == "MoveLeft").Pixels[0].Sha256);
            changed = program.Executable.ToArray();
            at = descriptor + Find(program.Descriptor, expected);
            changed[at] ^= 1;
            Assert.Equal(40, Run(changed));
            // Deliberately disable the emitted original MoveChip body. A successful
            // native return without its state effects must fail the independent oracle.
            int move = program.Manifest.Translation.Functions.Single(f => f.Address == Translator.Binary.NeBinaryAddress.Parse("7:1184")).NativeOffset;
            var functions = program.Functions.Select(f =>
            {
                if (f.Name != "application_bodies")
                {
                    return f;
                }

                byte[] code = f.Bytes.ToArray();
                code[move] = 0x31;
                code[move + 1] = 0xc0;
                code[move + 2] = 0xc3;
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
}

public sealed class FatalIndirectBoundaryTests
{
    [Fact]
    public void ExplicitIndirectFaultCannotBecomeASuccessfulStubOrAuthorizeOtherSites()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xff, 0xd0, 0xcb);
        using var runtime = Win16_Runtime.NeRuntimeProgram.Load(bytes);
        var selected = new HashSet<Translator.Binary.NeBinaryAddress>
        {
            new(1, 0)
        };
        Assert.Throws<InvalidDataException>(() => NeNativeTranslation.Compile(bytes, "1:0", runtime.Image, nativeContracts: new Dictionary<string, Win16_Runtime.Win16ApiMetadata>(), includedFunctions: selected));
        var artifact = NeNativeTranslation.Compile(bytes, "1:0", runtime.Image, nativeContracts: new Dictionary<string, Win16_Runtime.Win16ApiMetadata>(), includedFunctions: selected, rejectedIndirectSites: selected);
        Assert.Contains(artifact.Module.Functions.Single().Instructions, i => i.Operation == "FAULT" && i.BinarySource!.BytesHex == "FFD0");
        using var body = new Translator.CodeGen.NativeWin16Body(artifact.LinkedCode, artifact.EntryPoints);
        var state = runtime.Image.InitialState;
        Assert.Throws<InvalidOperationException>(() => body.Invoke(ref state, runtime.Image.Memory, runtime.Dispatcher));
        Assert.Throws<InvalidDataException>(() => NeNativeTranslation.Compile(bytes, "1:0", runtime.Image, nativeContracts: new Dictionary<string, Win16_Runtime.Win16ApiMetadata>(), includedFunctions: selected, rejectedIndirectSites: new HashSet<Translator.Binary.NeBinaryAddress> { new(1, 2) }));
    }
}
