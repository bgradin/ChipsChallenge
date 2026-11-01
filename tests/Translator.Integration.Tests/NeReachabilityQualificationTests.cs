using System.Text.Json;
using Translator.Analysis;
using Translator.Binary;
using Translator.ChipsChallenge;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [SuppliedNeFact]
    public void BinaryDerivedControlEntriesRetainUnknownRangesAndRejectUncompiledTargets()
    {
        byte[] source = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        SupportedChipsSource.Validate(source);
        var discovery = NeWholeProgramDiscoveryBuilder.Build(source);
        var control = NeWholeProgramControlCompiler.Compile(source).Report;
        Assert.True(discovery.Summary.WorklistExhausted);
        Assert.True(control.Summary.AllStaticTargetsResolved);
        Assert.Equal(0, control.Summary.UnsupportedTransfers);
        Assert.NotEmpty(control.NativeDispatchTables);
        Assert.NotEmpty(control.RejectedContinuations);
        var unknown = discovery.CodeRegions.Where(r => r.Classification == "Unresolved").ToArray();
        Assert.NotEmpty(unknown);
        foreach (var range in unknown)
        {
            bool Contains(NeBinaryAddress address) => address.SegmentIndex == range.SegmentIndex && address.Offset >= range.Offset && address.Offset < range.Offset + range.Length;

            Assert.DoesNotContain(discovery.Roots, r => Contains(r.Address));
            Assert.DoesNotContain(discovery.ControlFlow, edge => edge.Target is { } target && Contains(target));
            Assert.DoesNotContain(control.Entries, entry => Contains(entry.Original));
        }

        foreach (var site in control.Sites.Where(s => s.Kind == "Direct"))
        {
            Assert.True(site.Resolved);
            Assert.All(site.OriginalTargets, target => Assert.Contains(control.Entries, entry => entry.Original == target));
        }

        foreach (var slot in control.NativeDispatchTables.SelectMany(t => t.Slots))
        {
            Assert.Contains(control.Entries, entry => entry.Original == slot.Target && entry.NativeOffset == slot.NativeOffset);
        }

        Assert.All(control.RejectedContinuations, rejected => Assert.DoesNotContain(control.Entries, entry => entry.Original == rejected.Address));
        string directory = Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "reachability.json"), JsonSerializer.Serialize(new
        {
            Format = "NeReachabilityQualification/v1",
            ExecutableSha256 = Hash(_package.Value.Executable!),
            Complete = true,
            SourceSha256 = SupportedChipsSource.Sha256,
            RawAmbiguousBytes = discovery.Summary.AmbiguousBytes,
            RawUnknownBytes = discovery.Summary.UnresolvedBytes,
            UnknownRanges = unknown.Select(r => new { r.SegmentIndex, r.Offset, r.Length }),
            CompiledDispatchTables = control.NativeDispatchTables.Count,
            control.RejectedContinuations,
            FormalWholeProgramProof = false,
            Qualification = "Binary-derived static entries and transfer targets checked; unknown ranges remain uncompiled. Dynamic reachability requires separate application acceptance and is not inferred from static non-observation."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
