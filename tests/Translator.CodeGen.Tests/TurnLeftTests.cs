using Translator.IR;
using Xunit;

namespace Translator.CodeGen.Tests;

public class TurnLeftTests
{
    [Theory]
    [InlineData(-1, 0, 0, 1)]
    [InlineData(0, -1, -1, 0)]
    [InlineData(0, 1, 1, 0)]
    [InlineData(1, 0, 0, -1)]
    [InlineData(-1, -1, 21845, 21845)]
    [InlineData(-1, 1, -1, 0)]
    [InlineData(0, 0, 21845, 21845)]
    [InlineData(1, -1, 1, 0)]
    [InlineData(1, 1, 21845, 21845)]
    [InlineData(32767, 4, 0, -1)]
    [InlineData(-32768, 2, 0, -1)]
    public void EmitsAndExecutesTurnLeft(short x, short y, short ox, short oy)
    {
        if (!OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        var g = new TurnLeftCodeGenerator();
        var abi = OperatingSystem.IsWindows()
            ? X64Abi.Windows
            : X64Abi.SystemV;
        Assert.Equal((ox, oy), g.Execute(g.Generate(CreateTurnLeftIr(), abi), x, y));
    }

    [Fact]
    public void ExampleWithoutLocalStackReservationRetainsOutputPointerArguments()
    {
        var function = CreateTurnLeftIr();
        function = function with
        {
            Instructions = function.Instructions.Skip(1).ToArray()
        };
        var generator = new TurnLeftCodeGenerator();
        var abi = OperatingSystem.IsWindows()
            ? X64Abi.Windows
            : X64Abi.SystemV;
        Assert.Equal((0, -1), generator.Execute(generator.Generate(function, abi), 1, 0));
    }

    private static IrFunction CreateTurnLeftIr() => new Translator.Assembly.AssemblyParser().Parse(Path.GetFullPath("../../../../../tests/Fixtures/TurnLeft/TurnLeft.asm")).Module.Functions.Single();
}
