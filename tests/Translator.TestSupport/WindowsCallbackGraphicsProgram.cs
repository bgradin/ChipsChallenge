using Translator.ChipsChallenge;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Linker;

/// <summary>Original native-translated UI closure plus freestanding GDI pixel checks and baseline regressions.</summary>
public sealed class WindowsCallbackGraphicsProgram
{
    public static IReadOnlyList<PeNativeImport> Imports { get; } = Array.AsReadOnly(NativeGraphicsRuntime.Imports.Select(i => new PeNativeImport(i.Module, i.Name, i.Symbol)).ToArray());
    public PeFunctionBody Entry
    {
        get;
    }
    public IReadOnlyList<PeFunctionBody> Functions
    {
        get;
    }

    public WindowsCallbackGraphicsProgram(IrModule baseline, IrModule graphics)
    {
        IrFunction Root(IrModule module, string name) => module.Functions.SingleOrDefault(f => f.Name == name) ?? throw new InvalidDataException("Missing graphics acceptance dependency " + name);

        var previous = new CompilerAcceptanceProgram(Root(baseline, "TurnLeft"), Root(baseline, "FindMonster"), Root(baseline, "GrowArray"));
        Entry = new("graphics_entry", new byte[] { 0x48, 0x83, 0xEC, 0x28, 0xE8, 0, 0, 0, 0, 0x48, 0x83, 0xC4, 0x28, 0x85, 0xC0, 0x75, 0x05, 0xE9, 0, 0, 0, 0, 0xC3 }, new[] { new PeRelativeCallFixup(5, "acceptance"), new PeRelativeCallFixup(18, "graphics_acceptance") });
        var runtime = new MachineCodeArtifact(NativeGraphicsRuntime.Bytes, "AMD64", X64Abi.Windows, "int graphics_acceptance()", NativeGraphicsRuntime.TargetFixups.Select(f => new MachineCodeRelocation(f.Value, f.Key, MachineCodeRelocationKind.Relative32Call)).ToArray());
        var functions = new List<PeFunctionBody>
        {
            PeFunctionBody.FromArtifact("native_graphics_runtime", runtime)with
            {
                Exports = NativeGraphicsRuntime.Exports
            }
        };
        functions.AddRange(previous.Functions.Where(f => f.Name != "native_runtime"));
        foreach (string name in new[]
        {
            "IsPointVisible",
            "InvertViewportTile"
        }

        )
        {
            functions.Add(PeFunctionBody.FromArtifact(name, new Win16BodyCodeGenerator().Generate(GraphicsTargetProfile.Configure(Root(graphics, name)), symbols: GraphicsTargetProfile.Symbols, module: graphics, targets: GraphicsTargetProfile.Targets)));
        }

        Functions = functions.AsReadOnly();
    }

    public byte[] WriteExecutable() => new Pe32PlusWriter().WriteProgram(Entry, Functions, Imports);
}
