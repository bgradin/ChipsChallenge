using Translator.Assembly;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NativeLoweringTests
{
    private static MachineState Execute(string instructions, CallDistance distance = CallDistance.Far)
    {
        var parsed = new AssemblyParser().ParseText("func_nods F\n" + instructions + "\nendfunc");
        Assert.Empty(parsed.Diagnostics);
        var function = Assert.Single(parsed.Module.Functions) with
        {
            EntryDistance = distance
        };
        using var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(function));
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        memory.Map(new Win16Segment(0x2000));
        var calls = new Win16CallDispatcher();
        body.Register(calls, memory, "F", new(0x1000, 0x100), distance);
        var state = new MachineState
        {
            DS = 0x1000,
            SS = 0x2000,
            CS = 0x1000,
            SP = 0x8000,
            BP = 0x1234,
            IP = 0x50,
            Flags = 0x400
        };
        calls.Call(ref state, memory, "F", distance);
        Assert.Equal((ushort)0x8000, state.SP);
        Assert.Equal((ushort)0x1234, state.BP);
        Assert.Equal((ushort)0x50, state.IP);
        Assert.Equal((ushort)0x1000, state.CS);
        Assert.Equal(0x400, state.Flags & 0x400);
        return state;
    }

    [Theory]
    [InlineData(CallDistance.Near)]
    [InlineData(CallDistance.Far)]
    public void NativeArithmeticConsumesCarryAndPreservesItAcrossHelpers(CallDistance distance)
    {
        var state = Execute("""
            mov ax,0xffff
            stc
            adc ax,0
            push ax
            pop bx
            jnz .bad
            jnc .bad
            mov ax,0
            stc
            sbb ax,0
            jnc .bad
            jns .bad
            mov ax,1
            jmp .done
            .bad:
            mov ax,0
            .done:
            """, distance);
        Assert.Equal((ushort)1, state.AX);
    }

    [Fact]
    public void EncodedByteImmediateSignExtendsIntoWordOperation()
    {
        Assert.Equal(ushort.MaxValue, Execute("mov ax,0\nadd ax,byte 0xff").AX);
    }

    [Fact]
    public void NativeLoopUsesSixteenBitCountAndDoesNotOverwriteMachineFlags()
    {
        Assert.Equal((ushort)3, Execute("mov cx,3\nmov ax,0\n.loop:\ninc ax\nloop .loop").AX);
    }

    [Theory]
    [InlineData("rep nop")]
    [InlineData("mov ax,[UnknownField]")]
    [InlineData("mov ax,[cs:bx+si+UnknownField]")]
    [InlineData("int 0x100")]
    [InlineData("jmp .missing")]
    [InlineData("mov ax,ecx")]
    public void UnsupportedFormsProduceSourceDiagnostics(string instruction)
    {
        var function = Assert.Single(new AssemblyParser().ParseText($"func F\n{instruction}\nendfunc", "unsupported.asm").Module.Functions);
        var error = Assert.Throws<UnsupportedInstructionException>(() => new Win16BodyCodeGenerator().Generate(function));
        Assert.Equal("F", error.FunctionName);
        Assert.Equal("unsupported.asm", error.Location.File);
        Assert.Contains(instruction, error.Message);
    }

    [Fact]
    public void ChangedOperandsChangeNativeBehavior()
    {
        Assert.Equal((ushort)1, Execute("mov ax,1").AX);
        Assert.Equal((ushort)2, Execute("mov ax,2").AX);
        Assert.Equal((ushort)7, Execute("mov ax,7\ncmp ax,7\njz .done\nmov ax,0\n.done:").AX);
        Assert.Equal((ushort)0, Execute("mov ax,7\ncmp ax,8\njz .done\nmov ax,0\n.done:").AX);
    }
}

public sealed class FindMonsterBoundaryTests
{
    [Theory]
    [InlineData((short)0)]
    [InlineData((short)-1)]
    [InlineData(short.MinValue)]
    public void EmptyOrNegativeLengthDoesNotDereferenceNullList(short length)
    {
        var function = CompilerAcceptanceTests.ParseAcceptance().Functions.Single(f => f.Name == "FindMonster");
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        memory.WriteWord(0x1000, 0x1680, 0x3000);
        memory.WriteWord(0x1000, 0x3928, unchecked((ushort)length));
        Assert.Equal((short)-1, FindMonsterCodeGenerator.Generate(function)(new(memory, 0x1000), short.MinValue, short.MaxValue));
    }

    [Fact]
    public void NonemptyUnmappedListProducesAnExplicitMemoryFault()
    {
        var function = CompilerAcceptanceTests.ParseAcceptance().Functions.Single(f => f.Name == "FindMonster");
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        memory.WriteWord(0x1000, 0x1680, 0x3000);
        memory.WriteWord(0x1000, 0x3928, 1);
        var error = Assert.Throws<InvalidOperationException>(() => FindMonsterCodeGenerator.Generate(function)(new(memory, 0x1000), 1, 2));
        Assert.Contains("not mapped", error.InnerException!.Message);
    }

    [Fact]
    public void SymbolBindingsAndOffsetWrapAreHonoredByGeneratedCode()
    {
        var function = CompilerAcceptanceTests.ParseAcceptance().Functions.Single(f => f.Name == "FindMonster");
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        memory.Map(new Win16Segment(0x2000));
        memory.WriteWord(0x1000, 0x10, 0x100);
        memory.WriteWord(0x1000, 0x120, 2);
        memory.WriteFarPointer(0x1000, 0x130, new(0xfff8, 0x2000));
        memory.WriteWord(0x2000, 0x5, 0x8000);
        memory.WriteWord(0x2000, 0x7, 0x7fff);
        var context = new FindMonsterContext(memory, 0x1000, 0x10, 0x20, 0x30, 12);
        Assert.Equal((short)1, FindMonsterCodeGenerator.Generate(function)(context, short.MinValue, short.MaxValue));
    }
}

public sealed class NativeShiftTests
{
    [Theory]
    [InlineData("shl")]
    [InlineData("shr")]
    [InlineData("sar")]
    [InlineData("rcl")]
    public void NativeShiftResultsAndDefinedFlagsMatchTheSemanticModel(string operation)
    {
        foreach (ushort value in new ushort[]
        {
            0,
            1,
            0x8000,
            0xffff
        }

        )
        {
            foreach (int count in new[]
            {
                0,
                1,
                2,
                16,
                17,
                18,
                31,
                32,
                33
            }

            )
            {
                foreach (bool carry in new[]
                {
                    false,
                    true
                }

                )
                {
                    // Set a known OF=1, then CF independently. Macro prologue has already executed.
                    string prefix = $"mov ax,0x7fff\nadd ax,1\n{(carry
                        ? "stc"
                        : "clc")}\nmov ax,{value}\nmov cx,{count}\n{operation} ax,cl\n";
                    var prior = Translator.Core.X86Semantics.Add16(0x7fff, 1).Flags with
                    {
                        CF = carry
                    };
                    var expected = operation switch
                    {
                        "shl" => Translator.Core.X86Semantics.ShiftLeft16(value, count, prior),
                        "shr" => Translator.Core.X86Semantics.ShiftRight16(value, count, prior),
                        "sar" => Translator.Core.X86Semantics.ShiftArithmeticRight16(value, count, prior),
                        _ => Translator.Core.X86Semantics.RotateCarryLeft16(value, count, prior)
                    };
                    foreach (var (condition, flag) in new[]
                    {
                        ("jc", expected.Flags.CF),
                        ("jo", expected.Flags.OF)
                    }

                    )
                    {
                        string source = "func_nods F\n" + prefix + $"mov bx,0\n{condition} .set\njmp .done\n.set:\nmov bx,1\n.done:\nendfunc";
                        var function = Assert.Single(new AssemblyParser().ParseText(source).Module.Functions);
                        using var native = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(function));
                        var memory = new SegmentMemory();
                        memory.Map(new Win16Segment(0x1000));
                        var calls = new Win16CallDispatcher();
                        native.Register(calls, memory, "F", new(0x1000, 0x100), CallDistance.Far);
                        var state = new MachineState
                        {
                            DS = 0x1000,
                            SS = 0x1000,
                            CS = 0x1000,
                            SP = 0xf000
                        };
                        calls.Call(ref state, memory, "F", CallDistance.Far);
                        Assert.Equal(expected.Result, state.AX);
                        Assert.True(state.BX == (flag
                            ? (ushort)1
                            : (ushort)0), $"{operation} value={value:X4} count={count} carry={carry} condition={condition} expected={flag} actual={state.BX}");
                    }
                }
            }
        }
    }
}
