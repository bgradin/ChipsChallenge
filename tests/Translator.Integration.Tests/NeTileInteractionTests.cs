using System.Diagnostics;
using Translator.Linker;
using Xunit;
using Translator.Binary;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Integration.Tests;

public sealed class NeTileInteractionTests
{
    internal static NeApplicationProgram Build() => NeApplicationProgram.Build(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true, KeyboardInput: true, AuthenticLevel: true, PlayerMovement: true, GameClock: true, TileInteractions: true), levelData: File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"))!, "CHIPS.DAT")));

    [SuppliedNeFact]
    public void TileExportIsDeterministicAndIncludesOriginalPickupAndHintProcedures()
    {
        var program = Build();
        Assert.Equal(program.Executable, Build().Executable);
        Assert.NotNull(program.Manifest.Tiles);
        foreach (string address in new[]
        {
            "3:1770",
            "2:0C1A",
            "2:0C7E",
            "2:2BBE",
            "2:0F06"
        }

        )
        {
            Assert.Contains(program.Manifest.Translation.Functions, f => f.Address == Translator.Binary.NeBinaryAddress.Parse(address));
        }

        Assert.Contains(program.Manifest.Translation.FatalBoundaries, b => b.Contains("0007:0CCA"));
        var route = program.Manifest.Input!.Cases.Where(c => c.Name.StartsWith("AuthenticLesson1", StringComparison.Ordinal)).ToArray();
        Assert.Equal(97, route.Length);
        Assert.All(route, c => Assert.DoesNotContain(c.InitialWrites, w => w.Offset >= 0x4000 && w.Offset < 0x4a42));
        Assert.Equal(new byte[2], route[^1].Checks.Single(c => c.Offset == 0x1692).Bytes);
        using var matrix = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(program.Manifest.Tiles));
        var tiles = matrix.RootElement.GetProperty("Tiles");
        Assert.Equal(112, tiles.GetArrayLength());
        Assert.Equal("0007:1460", tiles[2].GetProperty("OriginalDispatch").GetProperty("MoveChipActionTarget").GetString());
        Assert.Equal("0003:1BA0", tiles[22].GetProperty("OriginalDispatch").GetProperty("PassabilityTarget").GetString());
        Assert.Equal("EntryEligibleCompletionDeferred", tiles[21].GetProperty("TranslationStatus").GetString());
        var format = Win16WindowContracts.TileByImportIdentity["USER.#420"];
        Assert.Equal(Win16ApiConvention.Cdecl, format.Convention);
        Assert.Equal(Win16ArgumentOrder.RightToLeft, format.ArgumentOrder);
        Assert.Equal(0, format.CleanupBytes);
    }

    [WindowsSuppliedNeFact]
    public void OriginalTilesChangeBoardInventoryCountersBootEffectsAndHintPixels()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "tiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Tiles64.exe"), log = Path.Combine(directory, "native.txt");
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
                Assert.Fail("Original tile acceptance timed out.");
            }

            string diagnostic = File.Exists(log)
                ? File.ReadAllText(log)
                : "No native diagnostic";
            Assert.True(process.ExitCode == 0, diagnostic);

            int Value(string name) => int.Parse(diagnostic.Split('\n').Single(l => l.StartsWith(name + ": ", StringComparison.Ordinal)).Split(':')[1].Trim());

            Assert.True(Value("HintCreates") >= 2, diagnostic);
            Assert.Equal(Value("HintCreates"), Value("HintDestroys"));
            Assert.True(Value("HintPaints") >= 2, diagnostic);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [SuppliedNeFact]
    public void OriginalExitEligibilityAndCompletionRemainsFatal()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        using var runtime = NeRuntimeProgram.Load(bytes, new(AllowIncompleteRelocations: true));
        var artifact = NeNativeTranslation.Compile(bytes, "3:1A56", runtime.Image, nativeContracts: new Dictionary<string, Win16ApiMetadata>(), includedFunctions: new HashSet<NeBinaryAddress> { new(3, 0x1a56) }, resolveIndirectTransfers: true);
        using var native = new NativeWin16Body(artifact.LinkedCode, artifact.EntryPoints);
        foreach (var f in artifact.Functions)
        {
            native.Register(runtime.Dispatcher, runtime.Image.Memory, f.Name, runtime.Image.Resolve(f.Address.SegmentIndex, f.Address.Offset), f.Distance, f.NativeSymbol);
        }

        var memory = runtime.Image.Memory;
        using var data = memory.CreateSegment(0x6000, 65536);
        memory.Map(data);
        memory.WriteBytes(0x6000, 0, memory.ReadBytes(runtime.Image.InitialState.DS, 0, memory.GetSegment(runtime.Image.InitialState.DS).Size));
        try
        {
            var state = runtime.Image.InitialState;
            state.DS = state.SS = 0x6000;
            state.SP = 0x9000;
            memory.WriteWord(state.DS, 0x1680, 0x4000);
            memory.WriteByte(state.DS, 0x4000 + 2 * 32 + 2, 0x15);
            memory.WriteByte(state.DS, 0x4400 + 2 * 32 + 2, 0);
            foreach (ushort arg in new ushort[]
            {
                1,
                0,
                0x3500,
                0,
                1,
                2,
                2
            }

            )
            {
                Win16Stack.Push(ref state, memory, arg);
            }

            runtime.Dispatcher.Call(ref state, memory, artifact.Functions.Single().Name, CallDistance.Far);
            Assert.Equal(1, state.AX);
            Assert.Equal(4, memory.ReadWord(state.DS, 0x3500));
            Assert.Equal(0x9000 - 14, state.SP);
        }
        finally
        {
            memory.Unmap(0x6000);
        }
    }

    [WindowsSuppliedNeFact]
    public void TileMutationsAndRemovedPickupBodyAreDetectedAndDeferredPathsRemainFatal()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "tile-mutation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Tiles64.exe"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;

            int Run(byte[] bytes)
            {
                File.WriteAllBytes(path, bytes);
                string log = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
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
                    Assert.Fail("Tile mutation timed out.");
                }

                Assert.Contains("CleanupCode: 0", File.ReadAllText(log));
                return process.ExitCode;
            }


            int Find(byte[] source, byte[] pattern) => Enumerable.Range(0, source.Length - pattern.Length + 1).First(i => source.AsSpan(i, pattern.Length).SequenceEqual(pattern));

            int descriptor = Find(program.Executable, program.Descriptor);
            int pickup = program.Manifest.Translation.Functions.Single(f => f.Address == NeBinaryAddress.Parse("3:1770")).NativeOffset;
            var functions = program.Functions.Select(f =>
            {
                if (f.Name != "application_bodies")
                {
                    return f;
                }

                byte[] code = f.Bytes.ToArray();
                code[pickup] = 0x31;
                code[pickup + 1] = 0xc0;
                code[pickup + 2] = 0xc3;
                return f with
                {
                    Bytes = code
                };
            }).ToArray();
            Assert.Equal(40, Run(new Pe32PlusWriter().WriteGuiProgram(program.Entry, functions, program.Imports)));
            byte[] changed = program.Executable.ToArray();
            byte[] hash = Convert.FromHexString(program.Manifest.Input!.Cases.Single(c => c.Name == "CollectBlueKey").Pixels.Single(p => p.Window == 6).Sha256);
            changed[descriptor + Find(program.Descriptor, hash)] ^= 1;
            Assert.Equal(40, Run(changed));
            // Change the fixture's destination, without changing original code or
            // expected success state. These dependencies must fail explicitly.
            int fixture = descriptor + Find(program.Descriptor, program.Manifest.Input.Cases.Single(c => c.Name == "CollectChip").InitialWrites[0].Bytes);
            foreach (byte tile in new byte[]
            {
                0x15,
                0x0a,
                0x29,
                0x0d,
                0x23,
                0x03
            }

            )
            {
                changed = program.Executable.ToArray();
                changed[fixture + 14 * 32 + 16] = tile;
                Assert.Equal(30, Run(changed));
            }

            // A corrupted original hint format must fail, release any owned font,
            // and report successful native cleanup even on that failure path.
            changed = program.Executable.ToArray();
            byte[] text = "Hint: %s\0"u8.ToArray();
            changed[Find(changed, text) + 7] = (byte)'d';
            Assert.Equal(30, Run(changed));
            changed = program.Executable.ToArray();
            byte[] hintHash = Convert.FromHexString(Translator.ChipsChallenge.ChipsTileProfile.HintTextHash);
            changed[descriptor + Find(program.Descriptor, hintHash)] ^= 1;
            Assert.Equal(30, Run(changed));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr key, IntPtr lparam);
    [WindowsSuppliedNeFact]
    public void LiveAuthenticHintPaintsWithRealTimersAndClosesWhileVisible()
    {
        var program = Build();
        string directory = Path.Combine(Path.GetTempPath(), "tile-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? process = null;
        try
        {
            string path = Path.Combine(directory, "Tiles64.exe"), log = Path.Combine(directory, "native.txt"), original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
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
            Assert.True(PostMessageW(window, 0x100, (UIntPtr)0x26, new IntPtr(1)));
            Assert.True(PostMessageW(window, 0x101, (UIntPtr)0x26, (IntPtr)unchecked((int)0xc0000001)));
            Thread.Sleep(500);
            Assert.False(process.HasExited);
            Assert.True(PostMessageW(window, 0x10, UIntPtr.Zero, IntPtr.Zero));
            Assert.True(process.WaitForExit(10000));
            string diagnostic = File.Exists(log)
                ? File.ReadAllText(log)
                : "No live diagnostic";
            Assert.True(process.ExitCode == 0, diagnostic);
            Assert.Contains("HintCreates: 1", diagnostic);
            Assert.DoesNotContain("HintPaints: 0", diagnostic);
            Assert.Contains("CleanupCode: 0", diagnostic);
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
