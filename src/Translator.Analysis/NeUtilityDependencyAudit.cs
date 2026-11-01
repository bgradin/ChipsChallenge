using Translator.Binary;
using Win16_Runtime;

namespace Translator.Analysis;

public sealed record NeUtilityDependencyAudit(string Module, string Sha256, int Bytes, IReadOnlyList<string> OriginalTransitiveImports, string Treatment, bool RequiredAtTranslation, bool RequiredAtRuntime, string ResourcePolicy, IReadOnlyList<string> NativeDependencies);
public static class NeUtilityDependencyAuditBuilder
{
    public static NeUtilityDependencyAudit Build(byte[] image)
    {
        Win16ExportContracts.VerifyWepImage(image);
        var source = new NeExecutableReader().Read(image);
        var imports = NeImportResolver.Discover(source, includeExportContracts: true).Select(i => i.Original.Identity).Distinct().Order(StringComparer.Ordinal).ToArray();
        return new("WEP4UTIL", source.Sha256, image.Length, imports, "Verified compatibility adapters for the five imported exports; original utility instructions and their imports are not loaded or executed.", true, false, "Translation input supplies pinned original utility dialog/string resources; consult the package manifest for the embedded resource inventory.", ["USER32.dll", "GDI32.dll", "KERNEL32.dll", "HHCTRL.OCX (system HTML Help for converted original topics)"]);
    }
}
