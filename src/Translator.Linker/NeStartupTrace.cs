using System.Runtime.InteropServices;
using Translator.Binary;
using Win16_Runtime;

namespace Translator.Linker;

public sealed record NeStartupTrace(string Format, string ExecutableSha256, string NativeCodeSha256, string ExecutionMode, bool WindowsHost, NeBinaryAddress Startup, string InitialCpuHex, string StoppedCpuHex, ushort InitialStackPointer, ushort StoppedStackPointer, bool LifecycleInitialized, ushort PspSelector, IReadOnlyList<string> LifecycleCalls, string NextUnsupportedImport, bool OriginalStartupComplete, bool ApplicationAccepted);
/// <summary>Bounded verification of original startup, reporting only structured missing-import boundaries.
/// Unexpected faults propagate; the trace cannot convert CPU or memory failures into service progress.</summary>
public static class NeStartupTraceBuilder
{
    private static string Cpu(MachineState state) => Convert.ToHexString(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref state, 1)));

    public static NeStartupTrace Run(byte[] executable)
    {
        var source = new NeExecutableReader().Read(executable);
        using var program = new NeWholeProgramControlProgram(executable, initializeKernelServices: true, lifecycleOptions: new(), maximumTransfers: 100000);
        var state = program.InitialState;
        string initial = Cpu(state);
        try
        {
            program.EnterStartup(ref state);
        }
        catch (Exception error) when (error.GetBaseException().Data[Win16ProgramControl.UnimplementedImportIdentityKey] is string)
        {
            string identity = (string)error.GetBaseException().Data[Win16ProgramControl.UnimplementedImportIdentityKey]!;
            return new("NeStartupTrace.v1", source.Sha256, program.Artifact.Report.NativeCodeSha256, "Managed verification host executing statically translated original AMD64 blocks; not a packaged production executable", OperatingSystem.IsWindows(), new(source.NeHeader.InitialCs, source.NeHeader.InitialIp), initial, Cpu(state), program.InitialState.SP, state.SP, program.LifecycleServices!.Initialized, program.LifecycleServices.PspSelector, program.LifecycleCalls.ToArray(), identity, false, false);
        }

        throw new InvalidOperationException("Startup did not stop at a structured missing-import boundary; review execution before changing this checkpoint.");
    }
}
