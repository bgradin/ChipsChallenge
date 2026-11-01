using System.Text.Json;
using Translator.Analysis;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeExecutableInventoryTests
{
    [Fact]
    public void AccountsForEveryFileAndAllocatedByteWithoutGuessingPadding()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xcb);
        NeLoaderTests.W(bytes, 0x94, 0); // startup IP
        var report = NeExecutableInventoryBuilder.Build(bytes);
        int next = 0;
        foreach (var range in report.FileRanges)
        {
            Assert.Equal(next, range.Offset);
            Assert.True(range.Length > 0);
            next += range.Length;
        }

        Assert.Equal(bytes.Length, next);
        foreach (var segment in report.Segments)
        {
            next = 0;
            foreach (var range in segment.Ranges)
            {
                Assert.Equal(next, range.Offset);
                next += range.Length;
            }

            Assert.Equal(segment.Segment.MemoryLength, next);
        }

        var coverage = report.Coverage;
        Assert.Equal(coverage.TotalExecutableSegmentBytes, coverage.InstructionBytes + coverage.EmbeddedDataBytes + coverage.PaddingBytes + coverage.AmbiguousBytes + coverage.UnresolvedBytes + coverage.UnreachableCodeBytes);
        Assert.Equal(0, coverage.PaddingBytes);
        Assert.Contains(report.Segments[0].Ranges, r => r.Classification == "Unresolved");
        Assert.Contains(report.Segments[1].Ranges, r => r.Classification == "UninitializedData");
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(NeExecutableInventoryBuilder.Build(bytes)));
        bytes[0x200] = 0x90; // replacing RET with NOP must change instruction evidence/coverage
        var changed = NeExecutableInventoryBuilder.Build(bytes);
        Assert.NotEqual(report.ExecutableSha256, changed.ExecutableSha256);
        Assert.NotEqual(coverage.InstructionBytes, changed.Coverage.InstructionBytes);
    }

    [Fact]
    public void RelocationAnnotationsDoNotAutomaticallyClassifyInstructionsAsData()
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 1, 8, 1, 7));
        var report = NeExecutableInventoryBuilder.Build(bytes);
        var segment = report.Segments[0];
        Assert.Single(segment.Relocations);
        Assert.Single(segment.ImportedReferences);
        Assert.Contains(report.FileRanges, r => r.Classification.Contains("RelocationTable:1"));
        Assert.Equal(0, report.Coverage.EmbeddedDataBytes);
    }
}
