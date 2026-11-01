using Translator.IR;
using Xunit;

namespace Translator.Transform.Tests;

public class PassTests
{
    [Fact]
    public void WidthPassRejectsAmbiguousMemory()
    {
        var i = new IrInstruction("MOV", new[] { new IrOperand("Memory", "[BX]"), new IrOperand("Symbol", "value") }, new SourceLocation("a.asm", 2), "mov [bx],value");
        var m = new IrModule("a.asm", new[] { new IrFunction("F", new[] { i }) });
        var r = new OperandWidthNormalizationPass().Run(m);
        Assert.Equal("AmbiguousOperandWidth", Assert.Single(r.Diagnostics).Code);
    }

    [Fact]
    public void FlagSemanticsPassRecordsWidthAndFlagEffects()
    {
        var source = new SourceLocation("a.asm", 2);
        var add = new IrInstruction("ADD", new[] { new IrOperand("Register", "AX", RegisterWidth.Word), new IrOperand("Immediate", "1") }, source, "add ax,1");
        var increment = new IrInstruction("INC", new[] { new IrOperand("Register", "AX", RegisterWidth.Word) }, source, "inc ax");
        var jump = new IrInstruction("JZ", new[] { new IrOperand("Symbol", "done") }, source, "jz done");
        var module = new IrModule("a.asm", new[] { new IrFunction("F", new[] { add, increment, jump }) });
        var normalized = new FlagSemanticsNormalizationPass().Run(module).Module.Functions[0].Instructions;
        Assert.Equal(RegisterWidth.Word, normalized[0].Semantics!.OperandWidth);
        Assert.Contains(CpuFlag.Carry, normalized[0].Semantics!.FlagsWritten);
        Assert.Contains(CpuFlag.Overflow, normalized[0].Semantics!.FlagsWritten);
        Assert.DoesNotContain(CpuFlag.Carry, normalized[1].Semantics!.FlagsWritten);
        Assert.Contains(CpuFlag.Zero, normalized[2].Semantics!.FlagsRead);
    }

    [Fact]
    public void ControlFlowPassBuildsBlocksAndBranchEdges()
    {
        var source = new SourceLocation("a.asm", 1);
        var instructions = new[]
        {
            new IrInstruction("JZ", new[] { new IrOperand("Symbol", "done") }, source, "jz done"),
            new IrInstruction("NOP", Array.Empty<IrOperand>(), source, "nop"),
            new IrInstruction("LABEL", new[] { new IrOperand("Label", "done") }, source, "done:")
        };
        var module = new IrModule("a.asm", new[] { new IrFunction("F", instructions) });
        var blocks = new ControlFlowNormalizationPass().Run(module).Module.Functions[0].BasicBlocks!;
        Assert.Equal(2, blocks.Count);
        Assert.Equal(new[] { "true", "false" }, blocks[0].Successors.Select(edge => edge.Kind));
        Assert.Equal("done", blocks[0].Successors[0].To);
    }
}
