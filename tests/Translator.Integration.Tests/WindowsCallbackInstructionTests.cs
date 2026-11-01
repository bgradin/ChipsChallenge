using Translator.Assembly;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class WindowsCallbackInstructionTests
{
    private static (MachineState State, SegmentMemory Memory) Run(string text, Action<SegmentMemory>? setup = null, bool crt = false)
    {
        var parsed = new AssemblyParser().ParseText("func_nods F\n" + text + "\n" + (crt
            ? "endfunc_crt"
            : "endfunc"));
        Assert.Empty(parsed.Diagnostics);
        using var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(Assert.Single(parsed.Module.Functions)));
        var memory = new SegmentMemory();
        foreach (ushort s in new ushort[]
        {
            0x1000,
            0x2000,
            0x3000
        }

        )
        {
            memory.Map(new(s));
        }

        setup?.Invoke(memory);
        var calls = new Win16CallDispatcher();
        body.Register(calls, memory, "F", new(0x4000, 0x100), CallDistance.Far);
        var state = new MachineState
        {
            DS = 0x1000,
            ES = 0x3000,
            SS = 0x2000,
            CS = 0x4000,
            SP = 0x8000,
            BP = 0x1234,
            IP = 0x50
        };
        calls.Call(ref state, memory, "F", CallDistance.Far);
        Assert.Equal((ushort)0x8000, state.SP);
        Assert.Equal((ushort)0x1234, state.BP);
        Assert.Equal((ushort)0x4000, state.CS);
        return (state, memory);
    }

    [Fact]
    public void RegisterSubviewsAndDwordMemoryPreserveIndependentUpperHalves()
    {
        var (state, memory) = Run("mov eax,0x11223344\nmov al,0xff\nmov ah,0x80\nmov [0x100],eax\nmov ecx,[0x100]\nmov cx,0x1234\nmov [0x104],ecx\npush ecx\npop edx");
        Assert.Equal(0x112280FFu, state.EAX);
        Assert.Equal(0x11221234u, state.EDX);
        Assert.Equal(0x112280FFu, memory.ReadDword(0x1000, 0x100));
        Assert.Equal(0x11221234u, memory.ReadDword(0x1000, 0x104));
    }

    [Fact]
    public void ByteAndDwordArithmeticUseTheirOwnOverflowCarryAndTruncation()
    {
        var (state, _) = Run("""
            mov byte [0x100],0xff
            add byte [0x100],1
            jnz .bad
            jnc .bad
            mov eax,0x7fffffff
            add eax,1
            jno .bad
            jns .bad
            mov bl,0x80
            neg bl
            jno .bad
            mov eax,1
            jmp .done
            .bad:
            mov eax,0
            .done:
            """);
        Assert.Equal(1u, state.EAX);
    }

    [Fact]
    public void CrtEpilogueRestoresTheObservedFrame()
    {
        Assert.Equal((ushort)7, Run("sub sp,8\nmov ax,7", crt: true).State.AX);
    }

    [Theory]
    [InlineData("mov ax,257\nmov bl,3\ndiv bl", 0x255u, 0u)]
    [InlineData("mov ax,17\nmov dx,0\nmov bx,3\ndiv bx", 5u, 2u)]
    [InlineData("mov ax,-17\ncwd\nmov bx,3\nidiv bx", 65531u, 65534u)]
    [InlineData("mov eax,0xffffffff\nmov edx,0\nmov ebx,2\ndiv ebx", 0x7fffffffu, 1u)]
    public void DivisionLowersThroughCheckedRuntimeOperations(string source, uint quotient, uint remainder)
    {
        var state = Run(source).State;
        Assert.Equal(quotient, state.EAX);
        Assert.Equal(remainder, state.EDX);
    }

    [Theory]
    [InlineData("mov ax,1\nmov bx,0\ndiv bx")]
    [InlineData("mov ax,0xffff\nmov dx,0xffff\nmov bx,1\ndiv bx")]
    public void DivideFaultsAreControlledHelperFailures(string source)
    {
        Assert.Throws<InvalidOperationException>(() => Run(source));
    }

    [Fact]
    public void FarLoadsResolveSegmentAndOffsetAndLeaDoesNotReadMemory()
    {
        var (state, memory) = Run("mov bx,0x100\nles dx,[bx]\nlea cx,[bx+si+2]\nmov [0x110],cx", m => m.WriteDword(0x1000, 0x100, 0x30000123));
        Assert.Equal((ushort)0x3000, state.ES);
        Assert.Equal((ushort)0x123, state.DX);
        Assert.Equal((ushort)0x102, memory.ReadWord(0x1000, 0x110));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepMovswUsesBothSegmentsDirectionAndWrappedOffsets(bool backwards)
    {
        var (_, memory) = Run($"{(backwards
            ? "std"
            : "cld")}\nmov si,0xffff\nmov di,0xffff\nmov cx,2\nrep movsw\nmov [0x100],si\nmov [0x102],di\nmov [0x104],cx", m =>
        {
            m.WriteWord(0x1000, 0xFFFF, 0x1234);
            m.WriteWord(0x1000, (ushort)(backwards
                ? 0xFFFD
                : 1), 0xABCD);
        });
        Assert.Equal((ushort)0x1234, memory.ReadWord(0x3000, 0xFFFF));
        Assert.Equal((ushort)0xABCD, memory.ReadWord(0x3000, (ushort)(backwards
            ? 0xFFFD
            : 1)));
        Assert.Equal((ushort)(backwards
            ? 0xFFFB
            : 3), memory.ReadWord(0x1000, 0x100));
        Assert.Equal((ushort)0, memory.ReadWord(0x1000, 0x104));
    }

    [Fact]
    public void LodsbLodswMovsbAndStosbPreserveTheirAccumulatorAndCountSemantics()
    {
        var (state, memory) = Run("cld\nmov ah,0xab\nmov si,0x100\nlodsb\nmov [0x120],ax\nmov di,0x200\nmov cx,3\nrep stosb\nmov cx,2\nrep movsb\nlodsw", m => m.WriteBytes(0x1000, 0x100, new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Equal((ushort)0xAB01, memory.ReadWord(0x1000, 0x120));
        Assert.Equal((ushort)0x0504, state.AX);
        Assert.Equal(new byte[] { 1, 1, 1, 2, 3 }, memory.ReadBytes(0x3000, 0x200, 5));
    }

    [Theory]
    [InlineData("repne")]
    [InlineData("repe")]
    public void ScasbRepeatsUntilItsComparisonTerminates(string prefix)
    {
        var (_, memory) = Run($"cld\nmov al,7\nmov di,0\nmov cx,4\n{prefix} scasb\nmov [0x100],cx\nmov [0x102],di", m => m.WriteBytes(0x3000, 0, new byte[] { 7, 6, 7, 7 }));
        // REPNE stops on the first equal byte; REPE stops on the second, unequal byte.
        Assert.Equal((ushort)(prefix == "repne"
            ? 3
            : 2), memory.ReadWord(0x1000, 0x100));
        Assert.Equal((ushort)(prefix == "repne"
            ? 1
            : 2), memory.ReadWord(0x1000, 0x102));
    }

    [Fact]
    public void ZeroRepeatDoesNotDereferenceNullSegments()
    {
        Assert.Equal((ushort)9, Run("mov cx,0\nmov ax,0\nmov es,ax\nrep stosb\nmov ax,9").State.AX);
    }

    [Theory]
    [InlineData("call ax", false)]
    [InlineData("call word [0x180]", false)]
    [InlineData("call far dword [0x180]", true)]
    [InlineData("call far dx:ax", true)]
    public void IndirectCallsUseRegisteredTranslatedCodeAndPreserveTheStack(string call, bool far)
    {
        var parser = new AssemblyParser();
        var outer = Assert.Single(parser.ParseText("func_nods Outer\nmov ax,0x220\nmov dx,0x5000\n" + call + "\nendfunc").Module.Functions);
        var inner = Assert.Single(parser.ParseText("func_nods Inner\nmov ax,0xbeef\nendfunc").Module.Functions) with
        {
            EntryDistance = far
                ? CallDistance.Far
                : CallDistance.Near
        };
        using var a = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(outer));
        using var b = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(inner));
        var memory = new SegmentMemory();
        memory.Map(new(0x1000));
        memory.Map(new(0x2000));
        memory.WriteDword(0x1000, 0x180, (uint)((far
            ? 0x5000u
            : 0x4000u) << 16 | 0x220));
        var calls = new Win16CallDispatcher();
        a.Register(calls, memory, "Outer", new(0x4000, 0x100), CallDistance.Far);
        b.Register(calls, memory, "Inner", new((ushort)(far
            ? 0x5000
            : 0x4000), 0x220), inner.EntryDistance);
        var state = new MachineState
        {
            DS = 0x1000,
            SS = 0x2000,
            CS = 0x4000,
            SP = 0x8000
        };
        calls.Call(ref state, memory, "Outer", CallDistance.Far);
        Assert.Equal((ushort)0xBEEF, state.AX);
        Assert.Equal((ushort)0x8000, state.SP);
        Assert.Equal((ushort)0x4000, state.CS);
    }

    [Fact]
    public void UnknownIndirectTargetFailsInsteadOfExecutingAPointer()
    {
        Assert.Throws<InvalidOperationException>(() => Run("mov ax,0x1234\ncall ax"));
    }

    [Fact]
    public void DwordArgumentsAreStackValuesAndReturnCleanupCountsBothWords()
    {
        var parsed = new AssemblyParser().ParseText("func_nods F\n%arg value:dword\nmov eax,[value]\nendfunc");
        using var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(Assert.Single(parsed.Module.Functions)));
        var memory = new SegmentMemory();
        memory.Map(new(0x2000));
        var calls = new Win16CallDispatcher();
        body.Register(calls, memory, "F", new(0x4000, 0x100), CallDistance.Far);
        var state = new MachineState
        {
            SS = 0x2000,
            SP = 0x8000,
            CS = 0x4000
        };
        Win16Stack.Push(ref state, memory, 0x1234);
        Win16Stack.Push(ref state, memory, 0xABCD);
        calls.Call(ref state, memory, "F", CallDistance.Far);
        Assert.Equal(0x1234ABCDu, state.EAX);
        Assert.Equal((ushort)0x8000, state.SP);
    }

    [Fact]
    public void IndirectImportedProcedureAddressesUseTheRegisteredApiMapping()
    {
        var parsed = new AssemblyParser().ParseText("func_nods F\npush byte 0x42\npush byte 0\npush byte 8\ncall far dword [0x180]\nendfunc");
        using var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(Assert.Single(parsed.Module.Functions)));
        var memory = new SegmentMemory();
        memory.Map(new(0x1000));
        memory.Map(new(0x2000));
        memory.WriteDword(0x1000, 0x180, 0xF0000010);
        var calls = new Win16CallDispatcher();
        using var services = new Win16GlobalMemoryServices(memory, calls);
        body.Register(calls, memory, "F", new(0x4000, 0x100), CallDistance.Far);
        var state = new MachineState
        {
            DS = 0x1000,
            SS = 0x2000,
            CS = 0x4000,
            SP = 0x8000
        };
        calls.Call(ref state, memory, "F", CallDistance.Far);
        Assert.True(services.TryGetAllocation(state.AX, out var allocation));
        Assert.Equal(8, allocation!.Size);
        Assert.Equal((ushort)0x8000, state.SP);
    }

    [Theory]
    [InlineData("mov al,0x80\nmov cl,1\nshr al,cl", 0x40u)]
    [InlineData("mov eax,0x40000000\nshl eax,1", 0x80000000u)]
    [InlineData("mov al,12\nmov bl,12\nmul bl", 144u)]
    [InlineData("mov eax,0xffffffff\nmov ebx,2\nmul ebx", 0xFFFFFFFEu)]
    public void WidthSpecificShiftsAndMultiplyExecuteNatively(string source, uint result)
    {
        Assert.Equal(result, Run(source).State.EAX);
    }

    [Fact]
    public void AConfiguredNearTargetCannotSilentlyCrossSegments()
    {
        var function = Assert.Single(new AssemblyParser().ParseText("func_nods F\ncall Other\nendfunc").Module.Functions);
        using var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(function, targets: new Dictionary<string, Win16CodeAddress> { ["Other"] = new(0x5000, 0x100) }));
        var memory = new SegmentMemory();
        memory.Map(new(0x2000));
        var calls = new Win16CallDispatcher();
        body.Register(calls, memory, "F", new(0x4000, 0x100), CallDistance.Far);
        calls.Register("Other", new(0x5000, 0x100), CallDistance.Near, (ref MachineState s, SegmentMemory m, Win16CallDispatcher d, Win16CallContext c) => 0);
        var state = new MachineState
        {
            SS = 0x2000,
            SP = 0x8000,
            CS = 0x4000
        };
        Assert.Throws<InvalidOperationException>(() => calls.Call(ref state, memory, "F", CallDistance.Far));
    }

    [Fact]
    public void LdsUsesTheLoadedSegmentInsideTheBodyAndRestoresCallerDs()
    {
        var (state, _) = Run("lds bx,[0x100]\nmov ax,[bx]", m =>
        {
            m.WriteDword(0x1000, 0x100, 0x30000123);
            m.WriteWord(0x3000, 0x123, 0xBEEF);
        });
        Assert.Equal((ushort)0xBEEF, state.AX);
        Assert.Equal((ushort)0x1000, state.DS);
        Assert.Equal(MachineState.AbiSize, System.Runtime.InteropServices.Marshal.SizeOf<MachineState>());
    }
}
