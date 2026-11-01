using System.Text.Json;
using Xunit;

namespace Translator.IR.Tests;

public class IrTests
{
    [Fact]
    public void JsonRoundTripRetainsWidthAndLocation()
    {
        var m = new IrModule("f.asm", new[] { new IrFunction("F", new[] { new IrInstruction("MOV", new[] { new IrOperand("Register", "AX", RegisterWidth.Word) }, new SourceLocation("f.asm", 7), "mov ax,1") }) });
        var json = JsonSerializer.Serialize(m);
        var copy = JsonSerializer.Deserialize<IrModule>(json)!;
        Assert.Equal(RegisterWidth.Word, copy.Functions[0].Instructions[0].Operands[0].Width);
        Assert.Equal(7, copy.Functions[0].Instructions[0].Source.Line);
    }
}
