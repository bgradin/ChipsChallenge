using Translator.Analysis;

namespace Translator.Linker;

public sealed record NeProductionOutputOptions(string ExecutablePath, byte[]? UtilityImage = null, bool EmitDevelopmentPackage = false, bool EnterOriginalStartup = false);
public sealed record NeProductionDiagnostic(string Code, string Message);
public sealed record NeProductionManifest(string Format, string SourceSha256, bool ExecutableEmitted, bool ApplicationAccepted, string PayloadBuild, NeWholeProgramSummary Discovery, IReadOnlyList<NeProductionDiagnostic> Diagnostics)
{
    public NePackageManifest? Package
    {
        get; init;
    }
}

public sealed record NeProductionCompilation(byte[]? Executable, NeProductionManifest Manifest);
/// <summary>Fixture-free production boundary. Application output remains gated; development packages are explicit.
/// Output locations are not included in deterministic diagnostic content.</summary>
public static class NeProductionCompiler
{
    public static NeProductionCompilation Compile(byte[] executable, NeProductionOutputOptions output)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.ExecutablePath);
        // Production discovery derives all entries from the supplied executable.
        var discovery = NeWholeProgramDiscoveryBuilder.Build(executable);
        var diagnostics = new List<NeProductionDiagnostic>();
        if (!discovery.Summary.WorklistExhausted)
        {
            diagnostics.Add(new("DiscoveryLimit", "Whole-program traversal did not exhaust its worklist."));
        }

        if (discovery.Summary.AmbiguousBytes != 0 || discovery.Summary.UnresolvedBytes != 0)
        {
            diagnostics.Add(new("UnresolvedCode", $"Original file-backed code retains {discovery.Summary.AmbiguousBytes} ambiguous bytes and {discovery.Summary.UnresolvedBytes} unresolved bytes."));
        }

        if (output.EmitDevelopmentPackage)
        {
            if (output.UtilityImage is null)
            {
                throw new InvalidDataException("The pinned WEP4UTIL.DLL is required for resource packaging.");
            }

            var package = NeWholeProgramPackager.Build(executable, output.UtilityImage, output.EnterOriginalStartup);
            diagnostics.Add(new(output.EnterOriginalStartup
                ? "ApplicationAcceptancePending"
                : "OriginalStartupPending", output.EnterOriginalStartup
                ? "Development application enters original startup; full feature acceptance remains 7.10."
                : "7.7 development package performs native initialization/resource smoke and exits; original application startup and parity remain 7.8–7.10."));
            return new(package.Executable, new("NeProductionCompilation/v2", discovery.ExecutableSha256, true, false, "Native/rebuild.py --production (NativeWholeTaskRuntime)", discovery.Summary, diagnostics)
            {
                Package = package.Manifest
            });
        }

        diagnostics.Add(new("OriginalStartupPending", "Original-startup application acceptance remains unverified (7.8)."));
        diagnostics.Add(new("IncompletePackaging", "No package requested. Use explicit development-package output with pinned utility resources for the 7.7 bootstrap; application output remains gated."));
        return new(null, new("NeProductionCompilation/v1", discovery.ExecutableSha256, false, false, "Native/rebuild.py --production (NativeWholeTaskRuntime)", discovery.Summary, diagnostics));
    }
}
