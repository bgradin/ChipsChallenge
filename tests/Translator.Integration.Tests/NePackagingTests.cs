using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Translator.Binary;
using Translator.Analysis;
using Translator.ChipsChallenge;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NePackagingTests
{
    private static readonly Lazy<NeProductionCompilation> _package = new(() => Compile("first-location.exe"));
    private static string Source => Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
    private static string Utility => Path.Combine(Path.GetDirectoryName(Source)!, "WEP4UTIL.DLL");

    private static NeProductionCompilation Compile(string output) => NeProductionCompiler.Compile(File.ReadAllBytes(Source), new(output, File.ReadAllBytes(Utility), true));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [SuppliedNeFact]
    public void WholeProgramPackageIsDeterministicCompleteAndHasSeparatedNativePeLayout()
    {
        var first = _package.Value;
        var second = Compile("another-machine-directory/CHIPS64.EXE");
        Assert.Equal(first.Executable, second.Executable);
        Assert.Equal(JsonSerializer.Serialize(first.Manifest), JsonSerializer.Serialize(second.Manifest));
        var manifest = first.Manifest.Package!;
        byte[] bytes = first.Executable!;
        Assert.Equal(Hash(bytes), manifest.ExecutableSha256);
        Assert.False(first.Manifest.ApplicationAccepted);
        Assert.False(manifest.OriginalStartupEntered);
        Assert.False(manifest.OriginalExecutableRequiredAtRuntime);
        Assert.False(manifest.UtilityRequiredAtRuntime);
        Assert.Equal(122, manifest.Control.Imports.Count(i => i.Kind != "Constant"));
        Assert.Equal(1, manifest.Control.Imports.Count(i => i.Kind == "Constant"));
        Assert.DoesNotContain(typeof(SupportedChipsSource).Assembly.GetManifestResourceNames(), name => name.EndsWith("EntryEvidence.json", StringComparison.Ordinal));
        var discovered = NeWholeProgramDiscoveryBuilder.Build(File.ReadAllBytes(Source));
        Assert.Null(discovered.Graph.SymbolVerification);
        Assert.DoesNotContain(discovered.Roots, root => root.Evidence.Contains("ExternalDebugSymbol"));
        foreach (var entry in discovered.Roots.Where(r => r.Visited))
        {
            Assert.Contains(manifest.Control.Entries, e => e.Original == entry.Address);
        }

        Assert.True(manifest.Memory.FullyRelocated);
        Assert.True(manifest.Control.Summary.AllStaticTargetsResolved);
        using var reader = new PEReader(new MemoryStream(bytes));
        var headers = reader.PEHeaders;
        var pe = headers.PEHeader!;
        Assert.Equal(System.Reflection.PortableExecutable.Machine.Amd64, headers.CoffHeader.Machine);
        Assert.Equal(PEMagic.PE32Plus, pe.Magic);
        Assert.Equal(Subsystem.WindowsGui, pe.Subsystem);
        Assert.Equal(0, pe.CorHeaderTableDirectory.Size);
        Assert.NotEqual(0, pe.ImportTableDirectory.Size);
        Assert.NotEqual(0, pe.ExceptionTableDirectory.Size);
        Assert.Equal(manifest.UnwindRanges * 12, pe.ExceptionTableDirectory.Size);
        Assert.Equal(12, pe.BaseRelocationTableDirectory.Size);
        Assert.Equal(0, headers.CoffHeader.TimeDateStamp);
        Assert.Equal(6, headers.SectionHeaders.Length);
        foreach (var section in headers.SectionHeaders)
        {
            Assert.Equal(0, section.VirtualAddress % 4096);
            Assert.Equal(0, section.PointerToRawData % 512);
            Assert.Equal(0, section.SizeOfRawData % 512);
            bool execute = (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0;
            bool write = (section.SectionCharacteristics & SectionCharacteristics.MemWrite) != 0;
            Assert.False(execute && write);
            Assert.Equal(section.Name == ".text", execute);
            Assert.Equal(section.Name == ".data", write);
            Assert.InRange(section.PointerToRawData + section.SizeOfRawData, 1, bytes.Length);
        }

        var ro = headers.SectionHeaders.Single(s => s.Name == ".rdata");
        foreach (var module in new[]
        {
            ("CHIPS", Source),
            ("WEP4UTIL", Utility)
        }

        )
        {
            using var catalog = new NeResourceCatalog(File.ReadAllBytes(module.Item2));
            var entries = manifest.Resources.Where(r => r.Module == module.Item1).ToArray();
            Assert.Equal(catalog.Resources.Count, entries.Length);
            foreach (var entry in entries)
            {
                var original = catalog.Extract(entry.Index);
                Assert.Equal(original.Metadata.Type, entry.Type);
                Assert.Equal(original.Metadata.Identity, entry.Name);
                byte[] embedded = bytes.AsSpan(ro.PointerToRawData + entry.DescriptorPayloadOffset, entry.Bytes).ToArray();
                Assert.Equal(original.Bytes, embedded);
                Assert.Equal(entry.Sha256, Hash(embedded));
                if (entry.DerivedBytes != 0)
                {
                    Assert.Equal(entry.DerivedSha256, Hash(bytes.AsSpan(ro.PointerToRawData + entry.DerivedOffset, entry.DerivedBytes).ToArray()));
                }
            }
        }

        Assert.Contains(manifest.Resources, r => r.Bytes > 65536);
        Assert.Contains(manifest.Resources, r => r.Name.Name is not null);
        Assert.All(manifest.NativeImports, i => Assert.Contains(i.Module.ToUpperInvariant(), new[] { "KERNEL32.DLL", "USER32.DLL", "GDI32.DLL" }));
        Assert.Contains(first.Manifest.Diagnostics, d => d.Code == "UnresolvedCode"); // retained uncertainty, never a parity claim
    }

    [SuppliedNeFact]
    public void PackagingRequiresPinnedUtilityAndRejectsMissingOrChangedInputs()
    {
        byte[] source = File.ReadAllBytes(Source);
        Assert.Throws<InvalidDataException>(() => NeProductionCompiler.Compile(source, new("out.exe", EmitDevelopmentPackage: true)));
        byte[] utility = File.ReadAllBytes(Utility);
        utility[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => NeProductionCompiler.Compile(source, new("out.exe", utility, true)));
    }

    [WindowsSuppliedNeFact]
    public void CopiedOutputAloneRunsNativeBootstrapAndRejectsDamagedCatalog()
    {
        string directory = Path.Combine(Path.GetTempPath(), "package-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes = _package.Value.Executable!;
            string path = Path.Combine(directory, "CHIPS64.EXE");
            File.WriteAllBytes(path, bytes);
            Assert.Single(Directory.GetFiles(directory));
            Assert.Equal(0, Launch(path, directory));
            using var pe = new PEReader(new MemoryStream(bytes));
            int ro = pe.PEHeaders.SectionHeaders.Single(s => s.Name == ".rdata").PointerToRawData;
            var dialog = _package.Value.Manifest.Package!.Resources.First(r => r.Type.Id == 5);
            byte[] damaged = bytes.ToArray();
            damaged[ro + dialog.DerivedOffset] ^= 1;
            string bad = Path.Combine(directory, "damaged.exe");
            File.WriteAllBytes(bad, damaged);
            Assert.Equal(10, Launch(bad, directory));
            damaged = bytes.ToArray();
            damaged[ro] ^= 1;
            File.WriteAllBytes(bad, damaged);
            Assert.Equal(10, Launch(bad, directory));
            int map = ro + 80;
            int segmentCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(ro + 12));
            for (int i = 0; i < segmentCount; i++)
            {
                map += 12 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(map + 8));
            }

            damaged = bytes.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(map + 8), uint.MaxValue);
            File.WriteAllBytes(bad, damaged);
            Assert.Equal(10, Launch(bad, directory));
        }
        finally
        {
            NeApplicationTests.DeleteProcessDirectory(directory);
        }
    }

    private static int Launch(string path, string directory)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = directory })!;
        if (!process.WaitForExit(30000))
        {
            process.Kill(true);
            throw new TimeoutException("Packaged native bootstrap timed out.");
        }

        return process.ExitCode;
    }
}
