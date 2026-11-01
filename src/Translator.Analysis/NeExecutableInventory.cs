using Translator.Binary;

namespace Translator.Analysis;

public sealed record NeInventoryRange(int Offset, int Length, string Classification, string Evidence);
public sealed record NeInventoryCoverage(long TotalSegmentBytes, long TotalExecutableSegmentBytes, long InstructionBytes, long EmbeddedDataBytes, long PaddingBytes, long AmbiguousBytes, long UnresolvedBytes, long UnreachableCodeBytes, long OverlappingClassificationBytes, double InstructionCoveragePercent, string Denominator);
public sealed record NeSegmentInventory(NeSegment Segment, string SegmentType, IReadOnlyList<NeInventoryRange> Ranges, IReadOnlyList<NeEntry> EntryReferences, IReadOnlyList<NeEntry> ExportedEntries, IReadOnlyList<NeRelocation> Relocations, IReadOnlyList<NeImportReference> ImportedReferences);
public sealed record NeExecutableInventory(string Format, string ExecutableSha256, int FileSize, string ClassificationPolicy, NeExecutable OriginalImageMetadata, IReadOnlyList<NeInstructionInventory> InstructionEvidence, IReadOnlyList<NeInventoryRange> FileRanges, IReadOnlyList<NeSegmentInventory> Segments, NeInventoryCoverage Coverage, IReadOnlyList<NeDiscoveryDiagnostic> Diagnostics);
/// <summary>Conservative byte accounting. Absence of code evidence never proves data or padding.</summary>
public static class NeExecutableInventoryBuilder
{
    public static NeExecutableInventory Build(byte[] image)
    {
        var executable = new NeExecutableReader().Read(image);
        var fileClaims = Enumerable.Range(0, image.Length).Select(_ => new SortedSet<string>(StringComparer.Ordinal)).ToArray();

        void Claim(int offset, int length, string owner)
        {
            for (int i = offset; i < checked(offset + length); i++)
            {
                fileClaims[i].Add(owner);
            }
        }


        Claim(0, executable.DosHeader.HeaderParagraphs * 16, "DosHeader");
        Claim(executable.NeHeader.FileOffset, 64, "NeHeader");
        foreach (var resource in executable.Resources)
        {
            Claim(resource.FileOffset, resource.Length, "Resource");
        }

        // Unclaimed bytes remain explicit: this includes table bytes, alignment, overlays and the DOS stub.
        // Do not infer padding from a byte value or assign overlapping physical ranges a single owner.
        var diagnostics = new List<NeDiscoveryDiagnostic>();
        NeProgramGraph? graph = null;
        var header = executable.NeHeader;
        if (header.InitialCs > 0 && header.InitialCs <= executable.Segments.Count && !executable.Segments[header.InitialCs - 1].IsData && header.InitialIp < executable.Segments[header.InitialCs - 1].FileLength)
        {
            graph = new NeProgramAnalyzer().Analyze(image, new NeBinaryAddress(header.InitialCs, header.InitialIp).Identity);
            diagnostics.AddRange(graph.Diagnostics);
            diagnostics.AddRange(graph.Functions.SelectMany(f => f.Diagnostics));
        }
        else
        {
            diagnostics.Add(new("NoExecutableStartup", new(1, 0), "No file-backed code entry; all executable bytes remain unresolved."));
        }

        var instructions = (graph?.Functions.SelectMany(f => f.Instructions) ?? []).DistinctBy(i => (i.Address, i.Size, i.BytesHex)).ToArray();
        var segments = new List<NeSegmentInventory>();
        long instructionBytes = 0, ambiguousBytes = 0, unresolvedBytes = 0, overlaps = 0;
        foreach (var segment in executable.Segments)
        {
            Claim(segment.FileOffset, segment.FileLength, $"Segment:{segment.Index}");
            var relocations = executable.Relocations.Where(r => r.SourceSegmentIndex == segment.Index).ToArray();
            if (segment.HasRelocations)
            {
                Claim(segment.FileOffset + segment.FileLength, 2 + relocations.Length * 8, $"RelocationTable:{segment.Index}");
            }

            var owners = new List<int>[segment.FileLength];
            int id = 0;
            foreach (var instruction in instructions.Where(i => i.Address.SegmentIndex == segment.Index))
            {
                for (int i = instruction.Address.Offset; i < instruction.Address.Offset + instruction.Size && i < owners.Length; i++)
                {
                    (owners[i] ??= []).Add(id);
                }

                id++;
            }

            var classes = new string[segment.MemoryLength];
            for (int i = 0; i < classes.Length; i++)
            {
                if (i >= segment.FileLength)
                {
                    classes[i] = "UninitializedData";
                }
                else if (segment.IsData)
                {
                    classes[i] = "InitializedData";
                }
                else if (owners[i]?.Count > 1)
                {
                    classes[i] = "AmbiguousCodeData";
                    ambiguousBytes++;
                    overlaps++;
                }
                else if (owners[i]?.Count == 1)
                {
                    classes[i] = "VerifiedInstruction";
                    instructionBytes++;
                }
                else
                {
                    classes[i] = "Unresolved";
                    unresolvedBytes++;
                }
            }

            var entries = executable.EntryPoints.Where(e => e.SegmentIndex == segment.Index).ToArray();
            segments.Add(new(segment, segment.IsData
                ? "Data"
                : "Executable", Ranges(classes, c => c switch
            {
                "VerifiedInstruction" => "Original-byte CFG decoding from NE initial CS:IP; not a native-lowering claim",
                "AmbiguousCodeData" => "Distinct decoded instruction boundaries overlap",
                "InitializedData" => "NE data-segment file payload",
                "UninitializedData" => "Allocated tail beyond file payload; not original file bytes",
                _ => "No verified interpretation; byte values alone do not establish code, embedded data or padding"
            }), entries, entries.Where(e => e.Exported).ToArray(), relocations, relocations.Where(r => r.Import is not null).Select(r => r.Import!).DistinctBy(r => r.Identity).OrderBy(r => r.Identity, StringComparer.Ordinal).ToArray()));
        }

        var fileClasses = fileClaims.Select(c => c.Count == 0
            ? "UnresolvedFileBytes"
            : string.Join("+", c)).ToArray();
        long executableBytes = executable.Segments.Where(s => !s.IsData).Sum(s => (long)s.FileLength);
        return new("NeExecutableInventory.v1", executable.Sha256, image.Length, "Half-open ranges [Offset, Offset+Length). FileRanges partition the entire original file; segment ranges partition allocated memory. Executable coverage counts only file-backed executable-segment bytes. Only startup CFG evidence is used in 7.1; no linear sweep, padding guesses, unreachable-code guesses or unverified metadata. Relocation sites are annotations, not automatically data. Zero counts mean no verified evidence, not proven absence.", executable, instructions.OrderBy(i => i.Address).ThenBy(i => i.Size).ToArray(), Ranges(fileClasses, _ => "Physical original-file ownership; combined labels preserve aliases; unresolved bytes are retained"), segments, new(executable.Segments.Sum(s => (long)s.MemoryLength), executableBytes, instructionBytes, 0, 0, ambiguousBytes, unresolvedBytes, 0, overlaps, executableBytes == 0
            ? 0
            : 100.0 * instructionBytes / executableBytes, "TotalExecutableSegmentBytes = sum of file-backed lengths of NE code segments; allocated tails excluded; overlapping byte count is a subset of ambiguous bytes"), diagnostics.Distinct().OrderBy(d => d.Address).ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<NeInventoryRange> Ranges(string[] classifications, Func<string, string> evidence)
    {
        var ranges = new List<NeInventoryRange>();
        for (int start = 0; start < classifications.Length;)
        {
            int end = start + 1;
            while (end < classifications.Length && classifications[end] == classifications[start])
            {
                end++;
            }

            ranges.Add(new(start, end - start, classifications[start], evidence(classifications[start])));
            start = end;
        }

        return ranges;
    }
}
