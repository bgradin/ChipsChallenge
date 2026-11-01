using System.Runtime.InteropServices;
using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Translator.CodeGen.Tests;

public sealed class Win16CallCodeGenTests
{
    [Theory]
    [InlineData(CallDistance.Near)]
    [InlineData(CallDistance.Far)]
    public void NativeCallThunkDispatchesNativeFunctionThroughWin16CallModel(CallDistance distance)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var abi = OperatingSystem.IsWindows()
            ? X64Abi.Windows
            : X64Abi.SystemV;
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        var dispatcher = new Win16CallDispatcher();
        var target = new Win16CodeAddress(distance == CallDistance.Far
            ? (ushort)0x2000
            : (ushort)0x1000, 0x0100);
        using var body = new ExecutableWin16Function(new MachineCodeArtifact(CreateNativeBody(abi), "AMD64", abi, "void NativeWin16Body(MachineState*, ushort* cleanupBytes)"));
        body.Register(dispatcher, "NativeInner", target, distance);
        var callInstruction = new IrInstruction("CALL", new[] { new IrOperand("Symbol", "NativeInner") }, new SourceLocation("calls.asm", 1), $"call {distance.ToString().ToLowerInvariant()} NativeInner", CallDistance: distance);
        using var callThunk = Win16CallThunkGenerator.Generate(callInstruction, dispatcher, memory, abi);
        var state = new MachineState
        {
            AX = 1,
            BX = 2,
            SI = 0x5151,
            DI = 0x6161,
            BP = 0x7171,
            SP = 0x0100,
            IP = 0x0050,
            CS = 0x1000,
            DS = 0x1000,
            SS = 0x1000
        };
        Win16Stack.Push(ref state, memory, 0xCAFE);
        callThunk.Invoke(ref state);
        Assert.Equal((ushort)0x1234, state.AX); // Native code returned AX through machine state.
        Assert.Equal((ushort)0x7777, state.BX); // Caller-saved Win16 register is allowed to change.
        Assert.Equal((ushort)0x5151, state.SI); // Callee-saved register is restored at call boundary.
        Assert.Equal((ushort)0x7171, state.BP);
        Assert.Equal((ushort)0x1000, state.DS);
        Assert.Equal((ushort)0x0050, state.IP);
        Assert.Equal((ushort)0x1000, state.CS);
        Assert.Equal((ushort)0x0100, state.SP); // Matching near/far return plus two bytes of argument cleanup.
        Assert.Equal("AMD64", callThunk.Artifact.Architecture);
        Assert.NotEmpty(callThunk.Artifact.Bytes);
    }

    private static byte[] CreateNativeBody(X64Abi abi) => abi switch
    {
        X64Abi.Windows => new byte[]
        {
            0x66,
            0xC7,
            0x01,
            0x34,
            0x12, // mov word [rcx], 1234h (AX)
            0x66,
            0xC7,
            0x41,
            0x02,
            0x77,
            0x77, // mov word [rcx+2], 7777h (BX)
            0x66,
            0xC7,
            0x41,
            0x08,
            0x00,
            0x00, // clobber SI; dispatcher must restore it
            0x66,
            0xC7,
            0x02,
            0x02,
            0x00, // mov word [rdx], 2 (RETF cleanup bytes)
            0xC3
        },
        X64Abi.SystemV => new byte[]
        {
            0x66,
            0xC7,
            0x07,
            0x34,
            0x12, // mov word [rdi], 1234h (AX)
            0x66,
            0xC7,
            0x47,
            0x02,
            0x77,
            0x77, // mov word [rdi+2], 7777h (BX)
            0x66,
            0xC7,
            0x47,
            0x08,
            0x00,
            0x00, // clobber SI; dispatcher must restore it
            0x66,
            0xC7,
            0x06,
            0x02,
            0x00, // mov word [rsi], 2 (RETF cleanup bytes)
            0xC3
        },
        _ => throw new ArgumentOutOfRangeException(nameof(abi))
    };
}
