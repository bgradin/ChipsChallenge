using System.Text.Json;
using Translator.Analysis;
using Translator.Binary;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeWholeProgramDiscoveryTests
{
    private static byte[] Fixture(params byte[] code)
    {
        var bytes = NeDiscoveryTests.Fixture(code);
        NeLoaderTests.W(bytes, 0x94, 0); // original NE initial IP
        return bytes;
    }

    private static byte[] DataPointerFixture(byte flags = 0)
    {
        var bytes = Fixture(0xcb);
        bytes[0x218] = 0xcb;
        NeLoaderTests.W(bytes, 0xcc, 0x101); // data segment has relocations
        NeLoaderTests.W(bytes, 0x400, 0xffff);
        NeLoaderTests.W(bytes, 0x410, 1);
        bytes[0x412] = 3;
        bytes[0x413] = flags;
        NeLoaderTests.W(bytes, 0x414, 0);
        NeLoaderTests.W(bytes, 0x416, 1);
        NeLoaderTests.W(bytes, 0x418, 0x18);
        return bytes;
    }

    [Fact]
    public void SeedsStartupEntriesAndDataOnlyFarPointersWithoutSelectingFunctions()
    {
        byte[] bytes = DataPointerFixture();
        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.Equal(new ushort[] { 0, 24, 48 }, report.Roots.Select(r => r.Address.Offset));
        Assert.All(report.Roots, r => Assert.True(r.Visited));
        Assert.Contains(report.Roots.Single(r => r.Address.Offset == 24).Evidence, e => e.Contains("SourceSegment:2"));
        Assert.Equal("NotReachedByResolvedStartupControlFlow", report.Roots.Single(r => r.Address.Offset == 24).StartupReachability);
        Assert.Single(new NeProgramAnalyzer().Analyze(bytes, "1:0").Functions);
        Assert.True(report.Summary.WorklistExhausted);
        Assert.False(report.Summary.ReadyForNativeTranslation);
        Assert.Equal("WholeProgram", report.Graph.DiscoveryMode);
        Assert.DoesNotContain(report.Graph.Entries.SelectMany(e => e.Evidence), e => e == "ExplicitRoot");
    }

    [Fact]
    public void AdditivePointerAddendsAreNotGuessedAsNewProcedureEntries()
    {
        var report = NeWholeProgramDiscoveryBuilder.Build(DataPointerFixture(4));
        Assert.DoesNotContain(report.Roots, r => r.Address.Offset == 24);
        Assert.Contains(report.Graph.Diagnostics, d => d.Code == "UnprovenRelocationCodePointer");
        Assert.Contains(report.CodeRegions, r => r.Classification == "Unresolved" && r.Offset <= 24 && r.Offset + r.Length > 24);
    }

    [Fact]
    public void NearCallMutationChangesAutomaticDiscoveryAndPreservesReturnAndFallthrough()
    {
        var bytes = Fixture(0xe8, 13, 0, 0xcb);
        bytes[0x210] = 0xc3;
        bytes[0x218] = 0xc3;
        var first = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.Contains(first.ControlFlow, e => e.Site.Offset == 0 && e.Kind == "DirectNearCall" && e.Target == new NeBinaryAddress(1, 16));
        Assert.Contains(first.ControlFlow, e => e.Site.Offset == 0 && e.Kind == "Fallthrough" && e.Target == new NeBinaryAddress(1, 3));
        Assert.Contains(first.ControlFlow, e => e.Site.Offset == 16 && e.Kind == "Return");
        bytes[0x201] = 21; // target changes from 16 to 24
        var changed = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.DoesNotContain(changed.Instructions, i => i.Original.Address.Offset == 16);
        Assert.Contains(changed.Instructions, i => i.Original.Address.Offset == 24);
        Assert.NotEqual(first.ExecutableSha256, changed.ExecutableSha256);
    }

    [Fact]
    public void CrossSegmentFarCallsAndTailJumpsFollowRelocationTargets()
    {
        var bytes = NeLoaderTests.Fixture((3, 0, 1, 2, 0));
        bytes.AsSpan(0x200, 64).Fill(0x90);
        new byte[]
        {
            0x9a,
            0xff,
            0xff,
            0,
            0,
            0xcb
        }.CopyTo(bytes, 0x200);
        bytes[0x204] = 0; // raw selector is not a segment identity
        bytes[0x400] = 0xcb;
        NeLoaderTests.W(bytes, 0xcc, 0); // second segment is code
        NeLoaderTests.W(bytes, 0x94, 0);
        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.Contains(report.ControlFlow, e => e.Site == new NeBinaryAddress(1, 0) && e.Kind == "DirectFarCall" && e.Target == new NeBinaryAddress(2, 0));
        Assert.Contains(report.Instructions, i => i.Original.Address == new NeBinaryAddress(2, 0));
        bytes[0x200] = 0xea; // original far tail jump, same relocation
        var tail = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.Contains(tail.ControlFlow, e => e.Kind == "FarTailTransfer" && e.Target == new NeBinaryAddress(2, 0));
        Assert.DoesNotContain(tail.ControlFlow, e => e.Site == new NeBinaryAddress(1, 0) && e.Kind == "Fallthrough");
    }

    [Fact]
    public void InterruptReturnPossibilityIsPreservedWithoutClaimingKnownSemantics()
    {
        var report = NeWholeProgramDiscoveryBuilder.Build(Fixture(0xcd, 0x21, 0xcb));
        Assert.Contains(report.ControlFlow, e => e.Kind == "PossibleInterruptReturn" && e.Target == new NeBinaryAddress(1, 2) && e.TargetStatus.StartsWith("Unresolved"));
        Assert.Contains(report.Instructions, i => i.Original.Address.Offset == 2);
        Assert.Contains(report.Graph.Diagnostics, d => d.Code == "UnresolvedInterrupt");
    }

    [Fact]
    public void SharedEpilogueHasOneCanonicalInstructionAndMultipleRegionOwners()
    {
        var bytes = Fixture(0x74, 14, 0xcb); // conditional branch to 16
        bytes[0x210] = 0xcb;
        new byte[]
        {
            0xeb,
            0xde
        }.CopyTo(bytes, 0x230); // exported entry 48 -> 16
        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        var shared = report.BasicBlocks.Single(b => b.Address.Offset == 16);
        Assert.Equal(new ushort[] { 0, 48 }, shared.RegionEntries.Select(a => a.Offset));
        Assert.Single(report.Instructions, i => i.Original.Address.Offset == 16);
        Assert.Contains(report.ControlFlow, e => e.Kind == "ConditionalBranch" && e.Target == new NeBinaryAddress(1, 16));
        Assert.DoesNotContain(report.Graph.Diagnostics, d => d.Code == "SharedFunctionBlock");
    }

    [Fact]
    public void FallthroughAndTailTransfersToIndependentEntriesRemainExplicit()
    {
        var bytes = Fixture(0x90);
        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.Contains(report.Graph.Dependencies, d => d.Kind == "SharedFallthrough" && d.Target == new NeBinaryAddress(1, 48));
        Assert.Contains(report.ControlFlow, e => e.Site.Offset == 47 && e.Kind == "Fallthrough" && e.Target == new NeBinaryAddress(1, 48));
        Assert.DoesNotContain(report.Graph.Diagnostics, d => d.Code == "SharedFallthrough");
        bytes[0x200] = 0xeb;
        bytes[0x201] = 46; // tail jump to entry 48
        var tail = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.Contains(tail.Graph.Dependencies, d => d.Kind == "TailTransfer" && d.Target == new NeBinaryAddress(1, 48));
        Assert.DoesNotContain(tail.ControlFlow, e => e.Site.Offset == 0 && e.Kind == "Fallthrough");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompetingInstructionBoundariesRetainBothOriginalInterpretations(bool separateRoot)
    {
        byte[] bytes;
        if (separateRoot)
        {
            bytes = Fixture(0xb8, 0xcb, 0xcb, 0xcb);
            NeLoaderTests.W(bytes, 0x12d, 1); // exported entry enters MOV's immediate
        }
        else
        {
            bytes = Fixture(0x74, 1, 0xb8, 0x90, 0x90, 0xcb);
        }

        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.True(report.Summary.AmbiguousBytes > 0);
        Assert.Contains(report.Instructions, i => i.OverlapsAnotherInterpretation && i.Original.Mnemonic == "Mov");
        Assert.Contains(report.CodeRegions, r => r.Classification == "AmbiguousCodeData" && r.Evidence.Count > 1);
        Assert.Contains(report.Graph.Diagnostics, d => d.Code is "OverlappingInstructions" or "OverlappingFunctionInstructions");
        var forged = report.Graph with
        {
            ClosureComplete = true,
            Diagnostics = []
        };
        Assert.Throws<InvalidDataException>(() => new NeBinaryIrBuilder().Build(bytes, forged));
        Assert.Throws<InvalidDataException>(() => new NeBinaryIrBuilder().BuildWithContracts(bytes, forged, new Dictionary<string, Win16ApiMetadata>()));
    }

    [Fact]
    public void BoundedJumpTablesSupplyTargetsAndEmbeddedDataWithoutDecodingTheirSlots()
    {
        var bytes = NeIndirectTranslationTests.TableFixture();
        NeLoaderTests.W(bytes, 0x94, 0);
        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        var table = Assert.Single(report.IndirectTransfers);
        Assert.True(table.Resolved);
        Assert.Equal(new ushort[] { 24, 28 }, table.Entries.Select(e => e.Target.Offset));
        Assert.Equal(4, report.Summary.EmbeddedDataBytes);
        Assert.DoesNotContain(report.Instructions, i => i.Original.Address.Offset is >= 32 and < 36);
        Assert.Contains(report.ControlFlow, e => e.Kind == "JumpTableTarget" && e.Target == new NeBinaryAddress(1, 24));
        NeLoaderTests.W(bytes, 0x220, 0xffff); // outside file-backed executable code
        var changed = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.False(Assert.Single(changed.IndirectTransfers).Resolved);
        Assert.Contains(changed.ControlFlow, e => e.Site.Offset == 8 && e.TargetStatus == "UnresolvedTargetSet");
        Assert.Equal(0, changed.Summary.EmbeddedDataBytes);
    }

    [Fact]
    public void AnIndependentEntryIntoTableIndexComputationInvalidatesItsGuardProof()
    {
        var bytes = NeIndirectTranslationTests.TableFixture();
        NeLoaderTests.W(bytes, 0x94, 0);
        NeLoaderTests.W(bytes, 0x12d, 5); // exported entry at SHL AX,1 bypasses CMP/JA
        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.False(Assert.Single(report.IndirectTransfers).Resolved);
        Assert.Equal(0, report.Summary.EmbeddedDataBytes);
        Assert.Contains(report.ControlFlow, e => e.Site.Offset == 8 && e.TargetStatus == "UnresolvedTargetSet");
    }

    [Fact]
    public void UnknownIndirectCallsAndOpaqueResourcesAreRetainedWithoutTargets()
    {
        var bytes = Fixture(0xff, 0xd0, 0xcb);
        NeLoaderTests.W(bytes, 0xd2, 0x800a); // opaque RCDATA; arbitrary contents are not code roots
        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.Contains(report.ControlFlow, e => e.Site.Offset == 0 && e.Target is null && e.TargetStatus == "UnresolvedTargetSet");
        Assert.Contains(report.ControlFlow, e => e.Site.Offset == 0 && e.Kind == "Fallthrough");
        Assert.True(report.Summary.UnresolvedControlFlowSites > 0);
        Assert.Contains(report.ResourceReferences, r => r.Status == "OpaqueResourceUnresolved");
        Assert.True(report.Summary.WorklistExhausted); // unresolved runtime values do not imply truncated traversal
    }

    [Fact]
    public void BudgetExhaustionIsNotReportedAsCompletedDiscovery()
    {
        var report = NeWholeProgramDiscoveryBuilder.Build(Fixture(0x90, 0x90, 0xcb), limits: new(MaximumFunctions: 1, MaximumInstructions: 1));
        Assert.False(report.Summary.WorklistExhausted);
        Assert.True(report.Summary.VisitedRoots < report.Summary.Roots);
        Assert.Contains(report.Graph.Diagnostics, d => d.Code == "FunctionLimit");
        Assert.Contains(report.Graph.Diagnostics, d => d.Code == "InstructionLimit");
    }

    [Fact]
    public void DiscoveryIsDeterministicAndPartitionsEveryOriginalCodeByte()
    {
        byte[] bytes = DataPointerFixture();
        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(NeWholeProgramDiscoveryBuilder.Build(bytes)));
        Assert.All(report.CodeRegions, r => Assert.Equal(r.Length, Convert.FromHexString(r.BytesHex).Length));
        var executable = new NeExecutableReader().Read(bytes);
        foreach (var segment in executable.Segments.Where(s => !s.IsData))
        {
            int next = 0;
            foreach (var region in report.CodeRegions.Where(r => r.SegmentIndex == segment.Index))
            {
                Assert.Equal(next, region.Offset);
                Assert.Equal(Convert.ToHexString(bytes.AsSpan(segment.FileOffset + region.Offset, region.Length)), region.BytesHex);
                next += region.Length;
            }

            Assert.Equal(segment.FileLength, next);
        }

        Assert.Equal(executable.Segments.Where(s => !s.IsData).Sum(s => (long)s.FileLength), report.Summary.InstructionBytes + report.Summary.EmbeddedDataBytes + report.Summary.AmbiguousBytes + report.Summary.UnresolvedBytes);
        Assert.Equal(report.Instructions.Count, report.BasicBlocks.Sum(b => b.Instructions.Count));
    }

    [Fact]
    public void CliSupportsRootFreeAnalysisAndRejectsConflictingOrPrematureCompilationModes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"whole-discovery-{Guid.NewGuid():N}.exe");
        string output = path + ".json";
        try
        {
            File.WriteAllBytes(path, DataPointerFixture());
            var result = CliIntegrationTests.Run("analyze", "--input", path, "--whole-program", "--output", output);
            Assert.True(result.Code == 0, result.Output);
            using var json = JsonDocument.Parse(File.ReadAllText(output));
            Assert.Equal("NeWholeProgramDiscovery.v1", json.RootElement.GetProperty("Format").GetString());
            Assert.NotEqual(0, CliIntegrationTests.Run("analyze", "--input", path, "--whole-program", "--root", "1:0").Code);
            Assert.NotEqual(0, CliIntegrationTests.Run("translate", "--input", path, "--whole-program", "--output", output).Code);
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }

    [SuppliedNeFact]
    public void OriginalBinaryDiscoveryRetainsEveryEntryAndIsIndependentOfSelectedApplicationClosures()
    {
        var bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var report = NeWholeProgramDiscoveryBuilder.Build(bytes);
        var executable = new NeExecutableReader().Read(bytes);
        Assert.True(report.Summary.WorklistExhausted);
        Assert.All(executable.EntryPoints.Where(e => e.SegmentIndex is { } s && !executable.Segments[s - 1].IsData), e => Assert.Contains(report.Roots, r => r.Visited && r.Address == new NeBinaryAddress(e.SegmentIndex!.Value, e.Offset!.Value)));
        Assert.True(report.Graph.Functions.Count > 80);
        Assert.Contains(report.Roots, r => r.Evidence.Contains("NEInitialCsIp"));
        Assert.Contains(report.Roots, r => r.StartupReachability == "NotReachedByResolvedStartupControlFlow");
        Assert.False(report.Summary.ReadyForNativeTranslation);
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(NeWholeProgramDiscoveryBuilder.Build(bytes)));
    }
}
