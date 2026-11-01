using System.Diagnostics;
using Translator.Assembly;
using Translator.ChipsChallenge;
using Translator.CodeGen;
using Translator.IR;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NativeGdiIntegrationTests
{
    public static IrModule Graphics() => new AssemblyParser().ParseSelected(Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Ui.asm"), new[] { "IsPointVisible", "InvertViewportTile" }).Module;

    private static int Pattern(int x, int y) => (0x123456 + x * 0x010203 + y * 0x030107) & 0xFFFFFF;
    [WindowsFact]
    public void SyntheticTranslatedGraphicsChangesTheExpectedPixelsThroughManagedAdapter()
    {
        var source = Graphics();
        var memory = new SegmentMemory();
        memory.Map(new(0x1000));
        memory.Map(new(0x2000));
        memory.WriteWord(0x1000, 0x0040, 0x3000);

        void Field(string name, ushort value) => memory.WriteWord(0x1000, (ushort)(0x3000 + GraphicsTargetProfile.Symbols[name]), value);

        Field("ViewportX", 3);
        Field("ViewportY", 4);
        Field("ViewportWidth", 4);
        Field("ViewportHeight", 4);
        var calls = new Win16CallDispatcher();
        using var surface = new WindowsOffscreenBitmap(64, 64);
        using var gdi = new Win16GdiServices(calls);
        ushort hdc = gdi.RegisterDc(surface.DeviceContext);
        var bodies = new List<NativeWin16Body>();
        try
        {
            foreach (var fn in source.Functions)
            {
                var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(GraphicsTargetProfile.Configure(fn), symbols: GraphicsTargetProfile.Symbols, module: source, targets: GraphicsTargetProfile.Targets));
                bodies.Add(body);
                body.Register(calls, memory, fn.Name, GraphicsTargetProfile.Targets[fn.Name], CallDistance.Far);
            }

            for (int test = 0; test < 5; test++)
            {
                surface.SetOrigin(test == 1
                    ? 16
                    : 0, test == 1
                    ? 16
                    : 0);
                int[] input = Enumerable.Range(0, 4096).Select(n => Pattern(n % 64, n / 64)).ToArray();
                surface.SetPixels(input);
                Field("PaddingX", (ushort)(test == 1
                    ? 1
                    : 0));
                Field("PaddingY", 0);
                var state = new MachineState
                {
                    DS = 0x1000,
                    SS = 0x2000,
                    CS = 0x4000,
                    SP = 0x8000,
                    BP = 0x1234,
                    SI = 0xAAAA,
                    DI = 0xBBBB
                };
                int count = gdi.NativeCallCount;
                for (int repeat = 0; repeat < (test == 3
                    ? 2
                    : 1); repeat++)
                {
                    Win16Stack.Push(ref state, memory, (ushort)(test == 1
                        ? 4
                        : 5));
                    Win16Stack.Push(ref state, memory, (ushort)(test == 1
                        ? 3
                        : test == 2
                            ? 2
                            : 4));
                    Win16Stack.Push(ref state, memory, test == 4
                        ? ushort.MaxValue
                        : hdc);
                    calls.Call(ref state, memory, "InvertViewportTile", CallDistance.Far);
                    state.SP += 6;
                    Assert.Equal((ushort)(test == 2 || test == 4
                        ? 0
                        : 1), state.AX);
                    Assert.Equal((ushort)0x8000, state.SP);
                    Assert.Equal((ushort)0x1234, state.BP);
                }

                Assert.Equal(test == 2 || test == 4
                    ? 0
                    : test == 3
                        ? 2
                        : 1, gdi.NativeCallCount - count);
                int[] output = surface.GetPixels();
                for (int n = 0; n < 4096; n++)
                {
                    int x = n % 64, y = n / 64;
                    bool inverted = test == 0
                        ? x >= 32 && y >= 32
                        : test == 1 && x < 16 && y >= 16 && y < 48;
                    Assert.Equal(input[n] ^ (inverted
                        ? 0xFFFFFF
                        : 0), output[n] & 0xFFFFFF);
                }
            }

            Assert.True(gdi.UnregisterDc(hdc));
            Assert.False(gdi.UnregisterDc(hdc));
        }
        finally
        {
            foreach (var body in bodies)
            {
                body.Dispose();
            }
        }
    }

    [WindowsFact]
    public void UnsupportedRasterPoliciesAndInvalidDcHandlesReturnFalseWithCleanup()
    {
        var memory = new SegmentMemory();
        memory.Map(new(0x2000));
        var calls = new Win16CallDispatcher();
        using var surface = new WindowsOffscreenBitmap(8, 8);
        using var gdi = new Win16GdiServices(calls);
        ushort dc = gdi.RegisterDc(surface.DeviceContext);
        var state = new MachineState
        {
            SS = 0x2000,
            CS = 0x4000,
            SP = 0x8000,
            IP = 0x50
        };

        void Invoke(ushort handle, uint rop)
        {
            Win16Stack.Push(ref state, memory, handle);
            Win16Stack.Push(ref state, memory, 0);
            Win16Stack.Push(ref state, memory, 0);
            Win16Stack.Push(ref state, memory, 4);
            Win16Stack.Push(ref state, memory, 4);
            Win16Stack.Push(ref state, memory, (ushort)(rop >> 16));
            Win16Stack.Push(ref state, memory, (ushort)rop);
            calls.Call(ref state, memory, "GDI.PatBlt", CallDistance.Far);
            Assert.Equal((ushort)0, state.AX);
            Assert.Equal((ushort)0x8000, state.SP);
            Assert.Equal((ushort)0x4000, state.CS);
        }


        Invoke(dc, 0x00F00021);
        Invoke(ushort.MaxValue, Win16GdiServices.DstInvert);
        Assert.True(gdi.UnregisterDc(dc));
        Invoke(dc, Win16GdiServices.DstInvert);
        Assert.Equal(0, gdi.NativeCallCount);
    }

    public static int ExecutePe(IrModule graphics)
    {
        string executable = Path.Combine(Path.GetTempPath(), $"graphics-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(executable, new WindowsCallbackGraphicsProgram(CompilerAcceptanceTests.ParseAcceptance(), graphics).WriteExecutable());
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })!;
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Graphics PE timed out.");
            }

            return process.ExitCode;
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [WindowsFact]
    public void StandalonePePerformsTheRealGdiOperationAndReleasesItsResources() => Assert.Equal(0, ExecutePe(Graphics()));
    [WindowsFact]
    public void PixelHarnessDetectsAMutatedSyntheticApiArgument()
    {
        var module = Graphics();
        var target = module.Functions.Single(f => f.Name == "InvertViewportTile");
        var code = target.Instructions.ToArray();
        int at = Array.FindIndex(code, i => i.Operation == "PUSH" && i.Operands[0].ImmediateValue == 32);
        Assert.True(at >= 0);
        code[at] = code[at] with
        {
            Operands = new[]
            {
                new IrOperand("Immediate", "16", RegisterWidth.Byte, ImmediateValue: 16)
            }
        };
        module = module with
        {
            Functions = module.Functions.Select(f => f.Name == target.Name
                ? target with
                {
                    Instructions = code
                }
                : f).ToArray()
        };
        Assert.Equal(117, ExecutePe(module));
    }

    [Fact]
    public void GraphicsPeContainsTheRequiredImportsAndSyntheticTranslatedTargets()
    {
        var program = new WindowsCallbackGraphicsProgram(CompilerAcceptanceTests.ParseAcceptance(), Graphics());
        var image = program.WriteExecutable();
        Assert.True(Pe32PlusWriter.IsAmd64Pe32Plus(image));
        Assert.Contains(program.Functions, f => f.Name == "InvertViewportTile");
        Assert.Contains(program.Functions, f => f.Name == "IsPointVisible");
        foreach (var import in WindowsCallbackGraphicsProgram.Imports)
        {
            Assert.True(image.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(import.Name + "\0")) >= 0);
        }

        Assert.True(image.AsSpan().IndexOf("GDI32.dll\0"u8) >= 0);
    }
}
