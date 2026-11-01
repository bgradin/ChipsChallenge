using Translator.CodeGen;
using Translator.Linker;
using Win16_Runtime;

namespace Translator.TestSupport;

/// <summary>Developer acceptance only: executes the checked-in production payload's real Windows API probe.</summary>
public static class NativeWholeTaskProbeProgram
{
    public static byte[] Build(bool productionSections = false)
    {
        var entry = new PeFunctionBody("service_probe_entry", [0xe9, 0, 0, 0, 0], [new PeRelativeCallFixup(1, "__win16_windows_ui_probe")]);
        var payload = new MachineCodeArtifact(NativeWholeTaskRuntime.Bytes, "AMD64", X64Abi.Windows, "whole-task ABI v2/44", NativeWholeTaskRuntime.TargetFixups.Select(p => new MachineCodeRelocation(p.Value, p.Key, MachineCodeRelocationKind.Relative32Call)).ToArray());
        var body = PeFunctionBody.FromArtifact("whole_task_payload", payload) with
        {
            Exports = NativeWholeTaskRuntime.Exports
        };
        var imports = NativeWholeTaskRuntime.Imports.Select(p => new PeNativeImport(p.Module, p.Name, p.Symbol)).ToArray();
        if (productionSections)
        {
            return new Pe32PlusWriter().WriteWholeProgram(entry, [body], [new("probe_read_only", [0])], imports, NativeWholeTaskRuntime.Unwind.Select(u => new PeUnwindRange(body.Name, u.Start, u.End, u.Info)).ToArray()).Bytes;
        }

        return new Pe32PlusWriter().WriteProgram(entry, [PeFunctionBody.FromArtifact("whole_task_payload", payload) with { Exports = NativeWholeTaskRuntime.Exports }], NativeWholeTaskRuntime.Imports.Select(p => new PeNativeImport(p.Module, p.Name, p.Symbol)).ToArray());
    }
}
