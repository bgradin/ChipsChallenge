using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Linker;

/// <summary>Links the real translated targets with freestanding deterministic services and acceptance fixtures.</summary>
public sealed class CompilerAcceptanceProgram
{
    public PeFunctionBody Entry
    {
        get;
    }
    public IReadOnlyList<PeFunctionBody> Functions
    {
        get;
    }

    public CompilerAcceptanceProgram(IrFunction turnLeft, IrFunction findMonster, IrFunction growArray)
    {
        Entry = new("acceptance_entry", new byte[] { 0xE9, 0, 0, 0, 0 }, new[] { new PeRelativeCallFixup(1, "acceptance") });
        var runtime = new MachineCodeArtifact(NativeAcceptanceRuntime.Bytes, "AMD64", X64Abi.Windows, "int acceptance()", NativeAcceptanceRuntime.TargetFixups.Select(f => new MachineCodeRelocation(f.Value, f.Key, MachineCodeRelocationKind.Relative32Call)).ToArray());
        Functions = new[]
        {
            PeFunctionBody.FromArtifact("native_runtime", runtime)with
            {
                Exports = NativeAcceptanceRuntime.Exports
            },
            PeFunctionBody.FromArtifact("TurnLeft", new TurnLeftCodeGenerator().Generate(turnLeft, X64Abi.Windows)),
            PeFunctionBody.FromArtifact("FindMonster", FindMonsterCodeGenerator.GenerateArtifact(findMonster, X64Abi.Windows)),
            PeFunctionBody.FromArtifact("GrowArray", GrowArrayCodeGenerator.GenerateArtifact(growArray, X64Abi.Windows))
        };
    }

    public byte[] WriteExecutable() => new Pe32PlusWriter().WriteProgram(Entry, Functions);

    /// <summary>Links exactly the PE payload for direct native execution on an AMD64 host.</summary>
    public MachineCodeArtifact LinkNative()
    {
        var bodies = new[]
        {
            Entry
        }.Concat(Functions).ToArray();
        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        int size = 0;
        foreach (var body in bodies)
        {
            size = (size + 15) & ~15;
            symbols.Add(body.Name, (ulong)size);
            foreach (var export in body.Exports ?? new Dictionary<string, int>())
            {
                symbols.Add(export.Key, (ulong)(size + export.Value));
            }

            size += body.Bytes.Length;
        }

        byte[] bytes = new byte[size];
        foreach (var body in bodies)
        {
            var relocations = (body.RelativeCallFixups ?? Array.Empty<PeRelativeCallFixup>()).Select(f => new MachineCodeRelocation(f.DisplacementOffset, f.TargetFunction, MachineCodeRelocationKind.Relative32Call)).ToArray();
            var artifact = new MachineCodeArtifact(body.Bytes, "AMD64", X64Abi.Windows, "", relocations);
            MachineCodeArtifactRelocator.Relocate(artifact, symbols[body.Name], symbols).CopyTo(bytes, (int)symbols[body.Name]);
        }

        return new(bytes, "AMD64", X64Abi.Windows, "int acceptance()");
    }
}
