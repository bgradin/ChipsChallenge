using Translator.Binary;
using Win16_Runtime;

namespace Translator.Analysis;

public enum Win16ServiceCoverage
{
    ImplementedAndTested,
    ImplementedButNotExercised,
    RecognizedButUnsupported,
    UnknownIdentity,
    UnusedOrUnreachable,
    ConstantExport
}

public sealed record NeImportContractRow(NeImportReference Original, Win16ExportContract? Contract, Win16ServiceCoverage Status, string Marshalling, string Handles, string Callbacks, string NativeEquivalent, string Implementation, IReadOnlyList<NeImportSite> Sources)
{
    public Win16TaskServiceBinding? Binding
    {
        get; init;
    }
    public string ImplementationStatus { get; init; } = "unsupported";
    public bool NativeDispatcherBound
    {
        get; init;
    }
    public bool ManagedWholeProgramOwnerWired
    {
        get; init;
    }
    public bool WindowsVerified
    {
        get; init;
    }
    public string OriginalCodeEvidence { get; init; } = "Not certified by inventory; caller fixups are provenance, not execution evidence.";
}

public sealed record NeDynamicServiceAudit(string Identity, ushort Ordinal, string NativeProcedure, ushort CleanupBytes, string NativeExport, bool NativeExportPresent, string ImplementationStatus, string Evidence, string RemainingWork);
public sealed record NeServiceDependency(string Module, string SourceIdentity, string Policy, string RemainingWork);
public sealed record NeImportContractAudit(string Format, string ExecutableSha256, string Policy, int TotalImports, int VerifiedContracts, int CallableImports, int ConstantExports, int UnknownIdentities, int ImplementedButNotExercised, int RecognizedButUnsupported, bool CompleteApplicationServiceCoverage, IReadOnlyList<NeImportContractRow> Imports, IReadOnlyList<string> DynamicProcedureNames)
{
    public NeUtilityDependencyAudit? UtilityDependency
    {
        get; init;
    }
    public int SourceAdapters => Imports.Count(i => i.Binding is not null);
    public int NativeBoundUnverified => Imports.Count(i => i.NativeDispatcherBound);
    public int ManagedOwnerAdapters => Imports.Count(i => i.ManagedWholeProgramOwnerWired);
    public int WindowsVerifiedAdapters => Imports.Count(i => i.WindowsVerified);
    public int ProductionVerifiedAdapters => 0; // This inventory consumes no production execution evidence.
    public string NativePayloadSha256 => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(NativeWholeTaskRuntime.Bytes)).ToLowerInvariant();
    public IReadOnlyList<NeDynamicServiceAudit> DynamicServices => DynamicProcedureNames.Select(identity =>
    {
        var procedure = Win16TaskModules.Procedures.Single(p => "MMSYSTEM." + p.Name == identity);
        return new NeDynamicServiceAudit(identity, procedure.Ordinal, procedure.Native, procedure.Cleanup, "__win16_mm_service", NativeWholeTaskRuntime.Exports.ContainsKey("__win16_mm_service"), "implemented/unverified", "Name bytes in initialized application data; module adapter uses a fixed precompiled entry. This is not an original execution trace.", "Native module/provider installation, full caller/flag/structure coverage, audio assets and Windows execution remain required.");
    }).ToArray();
    public IReadOnlyList<NeServiceDependency> Dependencies => new[]
    {
        new NeServiceDependency("WEP4UTIL", Win16ExportContracts.All.First(c => c.Module == "WEP4UTIL").SourceSha256, "Pinned utility compatibility/resource input. Never loaded as a native 16-bit module.", "Consult UtilityDependency when a DLL is supplied and the package manifest for embedded resources and runtime dependencies; import inventory alone does not verify packaging."),
        new NeServiceDependency("MMSYSTEM", "Original names only; host native implementation is WINMM.dll", "Approved dynamic procedures use fixed compiled guest entries and typed conversion to WinMM.", "Original feature traces, all multimedia operands, companion audio assets and Windows verification remain incomplete.")
    };
    public string RemainingIntegration => "The production packager installs native task providers and import thunks. This inventory verifies declarations and exported routes only; complete operand and feature support requires execution evidence.";
}

public static class NeImportContractAuditBuilder
{
    public static NeImportContractAudit Build(byte[] image, byte[]? utilityImage = null)
    {
        var executable = new NeExecutableReader().Read(image);
        var imports = NeImportResolver.Discover(executable, includeExportContracts: true).OrderBy(i => i.Original.Identity, StringComparer.Ordinal).Select(i =>
        {
            var contract = Win16ExportContracts.Find(i.Original);
            var status = contract is null
                ? Win16ServiceCoverage.UnknownIdentity
                : contract.Kind == "Constant"
                    ? Win16ServiceCoverage.ConstantExport
                    : Win16TaskServiceBindings.All.ContainsKey(contract.Identity)
                        ? Win16ServiceCoverage.ImplementedButNotExercised
                        : Win16ServiceCoverage.RecognizedButUnsupported;
            string marshalling = contract is null
                ? "Unknown"
                : string.Join("; ", contract.Arguments.Select(a => $"{a.Name}: {a.OriginalType ?? a.WireType}, {a.Bytes} bytes, SP+{4 + a.StackWord * 2}; {a.Marshalling}"));
            string handles = "16-bit task-owned identities; never native pointers. Per-service adapter must enforce type, lifetime and ownership; no generic Win32 signature forwarding.";
            string callback = contract?.Name is "RegisterClass" or "CreateWindow" or "DialogBox" or "SetTimer" or "MakeProcInstance" or "FreeProcInstance" or "GRAYDLGPROC"
                ? "Original 16:16 compiled entries; preserve suspended CPU state and persistent task memory. Structure/message conversion required."
                : "No direct callback argument; reentrancy still requires the task execution context.";
            string native = contract?.Kind == "Constant"
                ? "No native function; NE word offset fixup receives constant 0x0413."
                : contract?.Module switch
                {
                    "USER" => "USER32; per-service Win16 structure/message/handle conversion required",
                    "GDI" => "GDI32; per-service Win16 structure/handle conversion required",
                    "WEP4UTIL" => "No one-to-one Win32 export; original DLL behavior requires verified compatibility implementation",
                    "KERNEL" => "Task memory/file/resource/module/profile compatibility; Win32 KERNEL32 equivalents require explicit adaptation",
                    _ => "Unknown"
                };
            return new NeImportContractRow(i.Original, contract, status, marshalling, handles, callback, native, status == Win16ServiceCoverage.ImplementedButNotExercised
                ? "Source adapter present; consult Binding for native route, contract test references and operand limitations. This report certifies no Windows/production behavior."
                : status == Win16ServiceCoverage.ConstantExport
                    ? "Original constant-export relocation; explicitly not callable"
                    : "Whole-task adapter absent. Selective endpoints are not counted as complete application contracts.", i.Sources)
            {
                Binding = Win16TaskServiceBindings.All.GetValueOrDefault(i.Original.Identity),
                ImplementationStatus = status == Win16ServiceCoverage.ConstantExport
                    ? "constant"
                    : status == Win16ServiceCoverage.ImplementedButNotExercised
                        ? "implemented/unverified"
                        : "unsupported",
                NativeDispatcherBound = Win16TaskServiceBindings.All.TryGetValue(i.Original.Identity, out var binding) && Win16TaskServiceBindings.IsNativeBound(binding, NativeWholeTaskRuntime.Exports),
                ManagedWholeProgramOwnerWired = Win16TaskServiceBindings.IsManagedOwnerWired(i.Original.Identity)
            };
        }).ToArray();
        return new("NeImportContracts.v2", executable.Sha256, "Original relocation identities select pinned ABI evidence. Names, prototypes and selective adapters never imply whole-task implementation. No import is called unreachable without proof. Candidate procedure names are retained only when their NUL-terminated bytes occur in initialized original data; string presence alone is not a call-site or signature proof. Runtime target strings additionally require dynamic module contracts. Register procedures and constants are not ordinary Pascal APIs.", imports.Length, imports.Count(i => i.Contract is not null), imports.Count(i => i.Contract is not null && i.Contract.Kind != "Constant"), imports.Count(i => i.Status == Win16ServiceCoverage.ConstantExport), imports.Count(i => i.Status == Win16ServiceCoverage.UnknownIdentity), imports.Count(i => i.Status == Win16ServiceCoverage.ImplementedButNotExercised), imports.Count(i => i.Status == Win16ServiceCoverage.RecognizedButUnsupported), false, imports, new[] { "sndPlaySound", "mciSendCommand", "mciGetErrorString", "midiOutGetNumDevs", "waveOutGetNumDevs" }.Where(name => executable.Segments.Where(s => s.IsData && s.FileLength > 0).Any(segment => image.AsSpan(segment.FileOffset, segment.FileLength).IndexOf(System.Text.Encoding.ASCII.GetBytes(name + "\0")) >= 0)).Select(name => "MMSYSTEM." + name).ToArray())
        {
            UtilityDependency = utilityImage is null
                ? null
                : NeUtilityDependencyAuditBuilder.Build(utilityImage)
        };
    }
}
