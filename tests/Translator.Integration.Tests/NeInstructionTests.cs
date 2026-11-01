using Translator.Analysis;
using Translator.Binary;
using Translator.CodeGen;
using Translator.IR;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeInstructionTests
{
    private static readonly SourceLocation _source = new("ne synthetic CPU contract", 1);

    private static IrOperand Reg(X86Register reg, RegisterWidth width = RegisterWidth.Word) => new("Register", reg.ToString(), width, Register: reg);

    private static IrInstruction Op(string name, params IrOperand[] operands) => new(name, operands, _source, name);

    private static NativeWin16Body Body(params IrInstruction[] operations)
    {
        var code = operations.Concat([Op("RETF")]).ToArray();
        var function = new IrFunction("cpu_contract", code, Arguments: [], SavesDataSegment: false)
        {
            ExplicitFrame = true
        };
        function = NeNativeTranslation.Normalize(new("ne synthetic", [function])).Functions.Single();
        return new(new Win16BodyCodeGenerator().Generate(function));
    }

    private static SegmentMemory Memory()
    {
        var memory = new SegmentMemory();
        foreach (ushort selector in new ushort[]
        {
            0x1000,
            0x2000,
            0x3000
        }

        )
        {
            memory.Map(new Win16Segment(selector));
        }

        return memory;
    }

    private static MachineState Initial() => new()
    {
        EAX = 0x12345a5a,
        EBX = 0x23456789,
        EDX = 0x3456789a,
        ECX = 0x45670003,
        SI = 0xffff,
        DI = 0xffff,
        BP = 0x4b4b,
        CS = 0x4000,
        IP = 0x1234,
        DS = 0x1000,
        ES = 0x3000,
        SS = 0x2000,
        SP = 1,
        Flags = 0x302
    };

    private static void Write(SegmentMemory memory, ushort segment, ushort offset, int width, ushort value)
    {
        if (width == 8)
        {
            memory.WriteByte(segment, offset, (byte)value);
        }
        else
        {
            memory.WriteWord(segment, offset, value);
        }
    }

    private static ushort NativeCmpFlags(int width, ushort left, ushort right, ushort flags)
    {
        var state = Initial();
        state.AX = left;
        state.DX = right;
        state.Flags = flags;
        using var cmp = Body(Op("CMP", Reg(width == 8
            ? X86Register.AL
            : X86Register.AX, width == 8
            ? RegisterWidth.Byte
            : RegisterWidth.Word), Reg(width == 8
            ? X86Register.DL
            : X86Register.DX, width == 8
            ? RegisterWidth.Byte
            : RegisterWidth.Word)));
        cmp.Invoke(ref state, Memory(), new());
        return state.Flags;
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public void CmpsMatchesHardwareCmpForDefinedFlagsAndPreservesRegistersMemoryAndStack(int width, bool backwards)
    {
        using var body = Body(Op(width == 8
            ? "CMPSB"
            : "CMPSW"));
        foreach (var (left, right) in new (ushort, ushort)[]
        {
            (0, 0),
            (0, 1),
            (0x10, 1),
            (0x80, 1),
            (0x7f, 0xff),
            (0x8000, 1),
            (0x7fff, 0xffff),
            (0xffff, 0xff)
        }

        )
        {
            var memory = Memory();
            var state = Initial();
            state.Flags = (ushort)(0xb17 | (backwards
                ? 0x400
                : 0));
            var before = state;
            Write(memory, state.DS, state.SI, width, left);
            Write(memory, state.ES, state.DI, width, right);
            byte[] source = memory.ReadBytes(state.DS, 0xfffe, 4), destination = memory.ReadBytes(state.ES, 0xfffe, 4);
            ushort expectedFlags = NativeCmpFlags(width, left, right, state.Flags);
            Assert.Equal((ushort)0, body.Invoke(ref state, memory, new()));
            int step = (backwards
                ? -1
                : 1) * width / 8;
            Assert.Equal(unchecked((ushort)(before.SI + step)), state.SI);
            Assert.Equal(unchecked((ushort)(before.DI + step)), state.DI);
            Assert.Equal(expectedFlags, state.Flags); // all six arithmetic flags defined for CMPS
            Assert.Equal(before.EAX, state.EAX);
            Assert.Equal(before.EBX, state.EBX);
            Assert.Equal(before.ECX, state.ECX);
            Assert.Equal(before.EDX, state.EDX);
            Assert.Equal(before.BP, state.BP);
            Assert.Equal(before.CS, state.CS);
            Assert.Equal(before.DS, state.DS);
            Assert.Equal(before.ES, state.ES);
            Assert.Equal(before.SS, state.SS);
            Assert.Equal(before.SP, state.SP);
            Assert.Equal(source, memory.ReadBytes(state.DS, 0xfffe, 4));
            Assert.Equal(destination, memory.ReadBytes(state.ES, 0xfffe, 4));
        }
    }

    [Theory]
    [InlineData(8, false, false)]
    [InlineData(8, false, true)]
    [InlineData(8, true, false)]
    [InlineData(8, true, true)]
    [InlineData(16, false, false)]
    [InlineData(16, false, true)]
    [InlineData(16, true, false)]
    [InlineData(16, true, true)]
    public void CmpsRepeatStopsAtTheFirstMismatchOrMatchWithWrappedIndices(int width, bool backwards, bool repne)
    {
        using var body = Body(Op(width == 8
            ? "CMPSB"
            : "CMPSW") with
        {
            Prefix = repne
            ? RepeatPrefix.Repne
            : RepeatPrefix.Repe
        });
        var memory = Memory();
        var state = Initial();
        state.Flags = (ushort)(0x202 | (backwards
            ? 0x400
            : 0));
        int step = (backwards
            ? -1
            : 1) * width / 8;
        for (int n = 0; n < 3; n++)
        {
            ushort offset = unchecked((ushort)(0xffff + n * step));
            bool equal = repne
                ? n == 1
                : n != 1;
            Write(memory, state.DS, offset, width, 0x80);
            Write(memory, state.ES, offset, width, equal
                ? (ushort)0x80
                : (ushort)1);
        }

        body.Invoke(ref state, memory, new());
        Assert.Equal((ushort)1, state.CX);
        Assert.Equal(unchecked((ushort)(0xffff + 2 * step)), state.SI);
        Assert.Equal(state.SI, state.DI);
        Assert.Equal(repne, (state.Flags & 64) != 0);
        Assert.Equal(NativeCmpFlags(width, 0x80, repne
            ? (ushort)0x80
            : (ushort)1, (ushort)(0x202 | (backwards
            ? 0x400
            : 0))), state.Flags);
        // Causal memory mutation changes both stopping point and final comparison flags.
        Write(memory, state.ES, unchecked((ushort)(0xffff + step)), width, repne
            ? (ushort)1
            : (ushort)0x80);
        state = Initial();
        state.Flags = (ushort)(0x202 | (backwards
            ? 0x400
            : 0));
        body.Invoke(ref state, memory, new());
        Assert.Equal((ushort)0, state.CX);
        Assert.Equal(unchecked((ushort)(0xffff + 3 * step)), state.SI);
    }

    [Theory]
    [InlineData("CMPSB", RepeatPrefix.Repe)]
    [InlineData("CMPSB", RepeatPrefix.Repne)]
    [InlineData("CMPSW", RepeatPrefix.Repe)]
    [InlineData("CMPSW", RepeatPrefix.Repne)]
    public void ZeroCountDoesNotAccessMemoryAndFaultsDoNotAdvanceIndicesOrFlags(string operation, RepeatPrefix prefix)
    {
        using var body = Body(Op(operation) with
        {
            Prefix = prefix
        });
        var state = Initial();
        state.CX = 0;
        state.DS = state.ES = 0;
        var before = state;
        body.Invoke(ref state, Memory(), new());
        Assert.Equal(before, state);
        state.CX = 1;
        before = state;
        Assert.Throws<InvalidOperationException>(() => body.Invoke(ref state, Memory(), new()));
        Assert.Equal(before, state);
    }

    [Fact]
    public void InterruptUsesARealWrappedFrameAndReturnsDefinedFlagsThroughSavedFlags()
    {
        var interrupt = Op("INT", new IrOperand("Immediate", "0x21", RegisterWidth.Byte, ImmediateValue: 0x21)) with
        {
            BinarySource = new(1, 0x200, "CD21", "Int_imm8", "", ["Immediate8"]),
            Size = 2
        };
        using var body = Body(interrupt);
        var calls = new Win16CallDispatcher();
        var memory = Memory();
        var state = Initial();
        var before = state;
        calls.RegisterInterrupt(0x21, (ref MachineState s, SegmentMemory m, byte vector) =>
        {
            Assert.Equal((byte)0x21, vector);
            Assert.Equal((ushort)0xfffb, s.SP);
            Assert.Equal((ushort)0x202, m.ReadWord(s.SS, s.SP)); // next original IP, not a native address
            Assert.Equal(before.CS, m.ReadWord(s.SS, unchecked((ushort)(s.SP + 2))));
            Assert.Equal(before.Flags, m.ReadWord(s.SS, unchecked((ushort)(s.SP + 4))));
            Assert.Equal(0, s.Flags & 0x300);
            s.AX = 0x4321;
            m.WriteWord(s.SS, unchecked((ushort)(s.SP + 4)), (ushort)(before.Flags | 1));
        });
        body.Invoke(ref state, memory, calls);
        Assert.Equal((ushort)0x4321, state.AX);
        Assert.Equal(before.EAX & 0xffff0000, state.EAX & 0xffff0000);
        Assert.Equal(before.SP, state.SP);
        Assert.Equal(before.SS, state.SS);
        Assert.Equal(before.CS, state.CS);
        Assert.Equal((ushort)0x202, state.IP);
        Assert.Equal((ushort)(before.Flags | 1), state.Flags);
        Assert.Equal(before.EBX, state.EBX);
        Assert.Equal(before.ECX, state.ECX);
        Assert.Equal(before.EDX, state.EDX);
        Assert.Equal(before.SI, state.SI);
        Assert.Equal(before.DI, state.DI);
        Assert.Equal(before.BP, state.BP);
        // Vector mutation must not silently use the 21h service.
        using var mutated = Body(interrupt with
        {
            Operands = [new("Immediate", "0x20", RegisterWidth.Byte, ImmediateValue: 0x20)]
        }, Op("MOV", Reg(X86Register.CX), new IrOperand("Immediate", "0xbad", RegisterWidth.Word, ImmediateValue: 0xbad)));
        var failure = Assert.Throws<InvalidOperationException>(() => mutated.Invoke(ref state, memory, calls));
        Assert.IsType<NotSupportedException>(failure.InnerException);
        Assert.Equal(before.ECX, state.ECX); // execution stops at the failed service boundary
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptRejectsMissingOrCorruptedFramesAndUnsupportedReturnAddresses(bool corruptReturn)
    {
        var calls = new Win16CallDispatcher();
        var memory = Memory();
        var state = Initial();
        var before = state;
        if (corruptReturn)
        {
            calls.RegisterInterrupt(0x21, (ref MachineState s, SegmentMemory m, byte _) => m.WriteWord(s.SS, s.SP, 0x7777));
        }

        Assert.Throws<NotSupportedException>(() => calls.Interrupt(ref state, memory, 0x21));
        Assert.Equal(before, state);
        Assert.Throws<ArgumentNullException>(() => calls.RegisterInterrupt(0x20, null!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DynamicTailOperandsUseRegisteredOriginalAddressesWithoutPushingReturnFrames(bool memoryOperand, bool far)
    {
        var memory = Memory();
        var state = Initial();
        var before = state;
        var calls = new Win16CallDispatcher();
        ushort cs = far
            ? (ushort)0x5000
            : state.CS;
        calls.Register("approved", new(cs, 0x100), CallDistance.Far, (ref MachineState s, SegmentMemory _, Win16CallDispatcher _, Win16CallContext _) =>
        {
            Assert.Equal(before.SP, s.SP);
            Assert.Equal(before.Flags, s.Flags);
            Assert.Equal(cs, s.CS);
            Assert.Equal((ushort)0x100, s.IP);
            s.AX = 0x4242;
            return 6;
        });
        IrOperand operand;
        if (memoryOperand)
        {
            memory.WriteDword(state.DS, 0x100, (uint)(cs << 16 | 0x100));
            operand = new("Memory", "[0x100]", far
                ? RegisterWidth.Dword
                : RegisterWidth.Word, new("[0x100]", far
                ? RegisterWidth.Dword
                : RegisterWidth.Word, "DS", Displacement: 0x100));
        }
        else
        {
            state.BX = 0x100;
            operand = Reg(X86Register.BX);
        }

        using var body = Body(Op("DYNAMICJMP", operand) with
        {
            CallDistance = far
            ? CallDistance.Far
            : CallDistance.Near
        });
        Assert.Equal((ushort)6, body.Invoke(ref state, memory, calls));
        Assert.Equal((ushort)0x4242, state.AX);
        Assert.Equal(before.SP, state.SP);
        if (memoryOperand)
        {
            memory.WriteWord(state.DS, 0x100, 0x101);
        }
        else
        {
            state.BX = 0x101;
        }

        Assert.Throws<InvalidOperationException>(() => body.Invoke(ref state, memory, calls));
    }

    [SuppliedNeFact]
    public void OriginalInterruptSitesEmitAnExplicitBoundaryWithExactOriginalReturnIp()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var graph = new NeProgramAnalyzer().AnalyzeWholeProgram(bytes);
        var sites = graph.Functions.SelectMany(f => f.Instructions.Where(i => i.Mnemonic == "Int").Select(i => (f.Address, Instruction: i))).DistinctBy(p => p.Instruction.Address).ToArray();
        Assert.Equal(5, sites.Length);
        foreach (var site in sites)
        {
            var ir = new NeBinaryIrBuilder().AuditInstruction(bytes, graph, site.Address, site.Instruction.Address);
            using var body = Body(ir);
            var state = Initial();
            var memory = Memory();
            var calls = new Win16CallDispatcher();
            ushort expectedIp = unchecked((ushort)(site.Instruction.Address.Offset + site.Instruction.Size));
            calls.RegisterInterrupt(0x21, (ref MachineState s, SegmentMemory m, byte _) =>
            {
                Assert.Equal(expectedIp, m.ReadWord(s.SS, s.SP));
                s.AX = 0x070a;
            });
            body.Invoke(ref state, memory, calls);
            Assert.Equal(expectedIp, state.IP);
            Assert.Equal((ushort)0x070a, state.AX);
            Assert.Equal(Initial().SP, state.SP);
            Assert.Equal(Initial().Flags, state.Flags);
        }
    }

    [SuppliedNeFact]
    public void OriginalCmpsInstructionsLowerAndExecuteAndOpcodeMutationChangesWidth()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var graph = new NeProgramAnalyzer().AnalyzeWholeProgram(bytes);
        foreach (string mnemonic in new[]
        {
            "Cmpsb",
            "Cmpsw"
        }

        )
        {
            var owner = graph.Functions.First(f => f.Instructions.Any(i => i.Mnemonic == mnemonic));
            var original = owner.Instructions.First(i => i.Mnemonic == mnemonic);
            var ir = new NeBinaryIrBuilder().AuditInstruction(bytes, graph, owner.Address, original.Address);
            Assert.Equal(mnemonic.ToUpperInvariant(), ir.Operation);
            Assert.Equal(RepeatPrefix.Repe, ir.Prefix);
            Assert.Contains(CpuFlag.AuxiliaryCarry, ir.Semantics!.FlagsWritten);
            using var body = Body(ir);
            var state = Initial();
            state.CX = 1;
            var memory = Memory();
            memory.WriteWord(state.DS, state.SI, 0x8000);
            memory.WriteWord(state.ES, state.DI, 1);
            body.Invoke(ref state, memory, new());
            Assert.Equal((ushort)(mnemonic == "Cmpsb"
                ? 0
                : 1), state.SI);
            Assert.Equal(NativeCmpFlags(mnemonic == "Cmpsb"
                ? 8
                : 16, 0x8000, 1, Initial().Flags), state.Flags);
            var segment = new NeExecutableReader().Read(bytes).Segments[original.Address.SegmentIndex - 1];
            byte[] mutation = (byte[])bytes.Clone();
            mutation[segment.FileOffset + original.Address.Offset + original.Size - 1] ^= 1; // A6 <-> A7
            Assert.Throws<InvalidDataException>(() => new NeBinaryIrBuilder().AuditInstruction(mutation, graph, owner.Address, original.Address));
            var mutatedGraph = new NeProgramAnalyzer().AnalyzeWholeProgram(mutation);
            var mutatedOwner = mutatedGraph.Functions.First(f => f.Instructions.Any(i => i.Address == original.Address));
            var mutatedIr = new NeBinaryIrBuilder().AuditInstruction(mutation, mutatedGraph, mutatedOwner.Address, original.Address);
            Assert.NotEqual(ir.Operation, mutatedIr.Operation);
        }
    }
}
