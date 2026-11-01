using Translator.Assembly;
using Translator.CodeGen;
using Translator.IR;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class CompilerAcceptanceTests
{
    public static IrModule ParseAcceptance()
    {
        string path = Path.GetFullPath("../../../../../tests/Fixtures/Compiler/Acceptance.asm");
        var result = new AssemblyParser().Parse(path);
        Assert.Empty(result.Diagnostics);
        return result.Module;
    }

    public static CompilerAcceptanceProgram Build(IrModule? module = null)
    {
        module ??= ParseAcceptance();
        return new(module.Functions.Single(f => f.Name == "TurnLeft"), module.Functions.Single(f => f.Name == "FindMonster"), module.Functions.Single(f => f.Name == "GrowArray"));
    }

    [Fact]
    public void RealArtifactsPassTheFreestandingAcceptanceSuite()
    {
        using var body = new NativeWin16Body(Build().LinkNative());
        var state = new MachineState();
        Assert.Equal((ushort)0, body.Invoke(ref state, new SegmentMemory(), new Win16CallDispatcher()));
    }

    [Fact]
    public void FreestandingAcceptanceActuallyDetectsIncorrectTranslatedResults()
    {
        var module = ParseAcceptance();
        var grow = module.Functions.Single(f => f.Name == "GrowArray");
        var instructions = grow.Instructions.ToArray();
        int index = Array.FindLastIndex(instructions, i => i.Operation == "MOV" && i.Operands[0].Register == X86Register.AX);
        instructions[index] = instructions[index] with
        {
            Operands = new[]
            {
                instructions[index].Operands[0],
                new IrOperand("Immediate", "2", ImmediateValue: 2)
            }
        };
        module = module with
        {
            Functions = module.Functions.Select(f => f.Name == "GrowArray"
                ? f with
                {
                    Instructions = instructions
                }
                : f).ToArray()
        };
        using var body = new NativeWin16Body(Build(module).LinkNative());
        var state = new MachineState();
        Assert.NotEqual((ushort)0, body.Invoke(ref state, new SegmentMemory(), new Win16CallDispatcher()));
    }
}
