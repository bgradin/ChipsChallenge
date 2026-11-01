using Translator.IR;
using Xunit;

namespace Translator.CodeGen.Tests;

public sealed class UnsupportedInstructionTests
{
    [Fact]
    public void ReportsActionableInstructionAndSourceForUnsupportedTargetForms()
    {
        var source = new SourceLocation("FindMonster.asm", 30);
        var reserve = new IrInstruction("SUB", new[] { new IrOperand("Register", "SP", RegisterWidth.Word), new IrOperand("Immediate", "+0x2", RegisterWidth.Byte, ImmediateValue: 2) }, new("FindMonster.asm", 25), "sub sp,byte +0x2");
        var push = new IrInstruction("PUSH", new[] { new IrOperand("Register", "DI", RegisterWidth.Word) }, source, "push di");
        var function = new IrFunction("FindMonster", new[] { reserve, push });
        var error = Assert.Throws<UnsupportedInstructionException>(() => new TurnLeftCodeGenerator().Generate(function, X64Abi.SystemV));
        Assert.Equal("FindMonster", error.FunctionName);
        Assert.Equal("PUSH", error.Operation);
        Assert.Equal(source, error.Location);
        Assert.Contains("no AMD64 lowering", error.Message);
        Assert.Contains("push di", error.OriginalText);
    }

    [Fact]
    public void DoesNotSilentlyDropPrefixesOrSegmentOverrides()
    {
        var source = new SourceLocation("target.asm", 5);
        var prefixedNop = new IrInstruction("NOP", Array.Empty<IrOperand>(), source, "rep nop", Prefix: RepeatPrefix.Rep);
        var prefixError = Assert.Throws<UnsupportedInstructionException>(() => new TurnLeftCodeGenerator().Generate(new("F", new[] { prefixedNop }), X64Abi.SystemV));
        Assert.Contains("prefix Rep", prefixError.Message);
        var memory = new IrOperand("Memory", "[es:bx]", RegisterWidth.Word, new MemoryReference("bx", RegisterWidth.Word, "ES", X86Register.BX, HasExplicitSegment: true));
        var segmentInstruction = new IrInstruction("MOV", new[] { new IrOperand("Register", "AX", RegisterWidth.Word), memory }, source, "mov ax,[es:bx]");
        var segmentError = Assert.Throws<UnsupportedInstructionException>(() => new TurnLeftCodeGenerator().Generate(new("F", new[] { segmentInstruction }), X64Abi.SystemV));
        Assert.Contains("explicit segment override", segmentError.Message);
    }

    [Theory]
    [InlineData("KERNEL.Missing", "Unresolved")]
    [InlineData("InternalFar", "Internal")]
    public void CallDiagnosticsRetainIdentityAndSource(string target, string classification)
    {
        var source = new SourceLocation("calls.asm", 42);
        var call = new IrInstruction("CALL", new[] { new IrOperand("Symbol", target) }, source, $"call far {target}", CallDistance: CallDistance.Far);
        var function = new IrFunction("Caller", new[] { call }, Arguments: Array.Empty<IrArgument>());
        var module = new IrModule("calls.asm", new[] { function, new IrFunction("InternalFar", Array.Empty<IrInstruction>()) });
        var error = Assert.Throws<UnsupportedInstructionException>(() => new Win16BodyCodeGenerator().Generate(function, module: module));
        Assert.Equal(source, error.Location);
        Assert.Equal("Caller", error.FunctionName);
        Assert.Contains(target, error.Message);
        Assert.Contains(classification, error.Message);
    }
}
