using Translator.IR;
using Xunit;

namespace Translator.Assembly.Tests;

public class ParserTests
{
    [Fact]
    public void OriginalLocalsWhitespaceArgumentsAndConditionAliasesKeepTheirFrameMeaning()
    {
        var parsed = new AssemblyParser().ParseText("func F\n%arg unused:word x:word, y:word\n%local offset:word\nmov ax,[offset]\nif ne\nmov ax,[x]\nendif\nendfunc");
        Assert.Empty(parsed.Diagnostics);
        var function = Assert.Single(parsed.Module.Functions);
        Assert.Equal(3, function.Arguments!.Count);
        var local = function.Instructions.First(i => i.Operation == "MOV").Operands[1].Memory!;
        Assert.Equal(X86Register.BP, local.Base);
        Assert.Equal("SS", local.Segment);
        Assert.Equal(-4, local.Displacement);
        Assert.Contains(function.Instructions, i => i.Condition == BranchCondition.Equal);
    }

    [Fact]
    public void ParsesFunctionAndKeepsSourceLines()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "func F\n mov ax,1\nendfunc\n");
            var result = new AssemblyParser().Parse(file);
            Assert.Empty(result.Diagnostics);
            Assert.Equal("F", result.Module.Functions[0].Name);
            Assert.Equal(2, result.Module.Functions[0].Instructions[0].Source.Line);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ReportsUnsupportedInstruction()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "func F\n fadd st0,st1\nendfunc\n");
            Assert.Equal("UnsupportedInstruction", Assert.Single(new AssemblyParser().Parse(file).Diagnostics).Code);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void RetainsWin16PrefixesSegmentsCallsAndScopedLabels()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "func_nods First\n repe cmpsb\n mov ax,[es:bx+si+4]\n call far Helper\n retf 8\n .done:\nendfunc_crt\nfunc Second\n .done:\n jz .done\nendfunc\n");
            var result = new AssemblyParser().Parse(file);
            Assert.Empty(result.Diagnostics);
            var first = result.Module.Functions[0];
            Assert.Equal(FunctionVariant.Crt, first.Variant);
            Assert.Equal(RepeatPrefix.Repe, first.Instructions[0].Prefix);
            Assert.Contains(CpuFlag.Zero, first.Instructions[0].Semantics!.FlagsRead);
            var memory = first.Instructions[1].Operands[1].Memory!;
            Assert.Equal("ES", memory.Segment);
            Assert.True(memory.HasExplicitSegment);
            Assert.Equal(X86Register.BX, memory.Base);
            Assert.Equal(X86Register.SI, memory.Index);
            Assert.Equal(4, memory.Displacement);
            Assert.Equal(CallDistance.Far, first.Instructions[2].CallDistance);
            Assert.Equal(CallDistance.Far, first.Instructions[3].CallDistance);
            Assert.Equal((ushort)8, first.Instructions[3].ReturnCleanup);
            Assert.NotEqual(first.Instructions[^1].Operands[0].Text, result.Module.Functions[1].Instructions[1].Operands[0].Text);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SyntheticOperandsRetainSymbolicOffsetsWidthsAndControlFlow()
    {
        var result = new AssemblyParser().ParseText("""
            func WalkRecords
                mov si,[Records]
                cmp word [es:si+Record.value-3],7
                jne .advance
                call far TEST.RecordValue
            .advance:
                add si,byte Record_size
                jmp short .finish
            .finish:
            endfunc
            func StoreValue
                mov word [bx+6],9
                if nz
                    mov dx,12
                endif
            endfunc
            """);
        Assert.Empty(result.Diagnostics);
        var walk = result.Module.Functions.Single(f => f.Name == "WalkRecords");
        var compare = Assert.Single(walk.Instructions, i => i.Operation == "CMP");
        Assert.Equal(RegisterWidth.Word, compare.Semantics!.OperandWidth);
        Assert.Contains(CpuFlag.Zero, compare.Semantics.FlagsWritten);
        var access = compare.Operands[0].Memory!;
        Assert.Equal("ES", access.Segment);
        Assert.Equal("Record.value", access.Symbol);
        Assert.Equal(-3, access.Displacement);
        Assert.Equal(X86Register.SI, access.Base);
        var branch = Assert.Single(walk.Instructions, i => i.Operation == "JNE");
        Assert.Equal(BranchCondition.NotEqual, branch.Condition);
        Assert.Contains(CpuFlag.Zero, branch.Semantics!.FlagsRead);
        Assert.Equal("WalkRecords::.finish", walk.Instructions.Last(i => i.Operation == "JMP").Operands[0].Text);
        Assert.Equal(CallDistance.Far, Assert.Single(walk.Instructions, i => i.Operation == "CALL").CallDistance);
        var store = result.Module.Functions.Single(f => f.Name == "StoreValue");
        Assert.Equal(6, store.Instructions[0].Operands[0].Memory!.Displacement);
        Assert.Equal(9L, store.Instructions[0].Operands[1].ImmediateValue);
        Assert.Contains(store.Instructions, i => i.Condition == BranchCondition.Equal);
    }
}

public sealed class CompilerMetadataTests
{
    [Fact]
    public void PreservesArgumentsSegmentAndIndependentFunctionVariants()
    {
        var parsed = new AssemblyParser().ParseText("segment CODE\nextern KERNEL.GlobalLock\nfunc_nods F\n%arg x:word, ptr:dword\nendfunc_crt\n");
        Assert.Empty(parsed.Diagnostics);
        var function = Assert.Single(parsed.Module.Functions);
        Assert.Equal("CODE", function.Segment);
        Assert.False(function.SavesDataSegment);
        Assert.Equal(FunctionVariant.Crt, function.Variant);
        Assert.Equal(CallDistance.Far, function.EntryDistance);
        Assert.Equal(new[] { new IrArgument("x", RegisterWidth.Word, 0), new IrArgument("ptr", RegisterWidth.Dword, 2) }, function.Arguments);
        Assert.Equal(new[] { "KERNEL.GlobalLock" }, parsed.Module.Externals);
        Assert.Equal(new[] { "CODE" }, parsed.Module.Segments);
    }

    [Theory]
    [InlineData("[ax]")]
    [InlineData("[bx+bp]")]
    [InlineData("[bx+si+di]")]
    [InlineData("[bx-si]")]
    public void RejectsIllegal8086AddressesWithoutDroppingTerms(string address)
    {
        var parsed = new AssemblyParser().ParseText($"func F\nmov ax,{address}\nendfunc");
        Assert.Equal("InvalidOperand", Assert.Single(parsed.Diagnostics).Code);
    }

    [Fact]
    public void ReportsUnterminatedStructuredConditional()
    {
        var parsed = new AssemblyParser().ParseText("func F\nif nz\nmov ax,1\nendfunc");
        Assert.Equal("UnterminatedIf", Assert.Single(parsed.Diagnostics).Code);
    }

    [Theory]
    [InlineData("al", RegisterWidth.Byte)]
    [InlineData("AH", RegisterWidth.Byte)]
    [InlineData("es", RegisterWidth.Word)]
    [InlineData("eax", RegisterWidth.Dword)]
    [InlineData("EBX", RegisterWidth.Dword)]
    public void RegisterViewsHaveCaseIndependentWidths(string name, RegisterWidth width)
    {
        var parsed = new AssemblyParser().ParseText($"func F\nmov {name},1\nendfunc");
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(width, Assert.Single(parsed.Module.Functions).Instructions[0].Operands[0].Width);
    }

    [Fact]
    public void ExplicitMacroCleanupIsRetainedWithoutChangingTheDefault()
    {
        var parser = new AssemblyParser();
        var parsed = parser.ParseText("func F\n%assign %$argsize 0xa\n%arg value:dword,x:word,y:word,z:word\nendfunc");
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal((ushort)10, Assert.Single(parsed.Module.Functions).CleanupBytes);
        var invalid = parser.ParseText("func F\n%assign %$argsize 3\nendfunc");
        Assert.Equal("InvalidCleanup", Assert.Single(invalid.Diagnostics).Code);
    }
}
