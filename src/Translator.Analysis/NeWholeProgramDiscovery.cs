using Iced.Intel;
using Translator.Binary;

namespace Translator.Analysis;

public sealed record NeWholeProgramRoot(NeBinaryAddress Address, IReadOnlyList<string> Names, IReadOnlyList<string> Evidence, bool Visited, string StartupReachability);
public sealed record NeWholeProgramInstruction(NeInstructionInventory Original, IReadOnlyList<NeBinaryAddress> RegionEntries, bool OverlapsAnotherInterpretation);
public sealed record NeControlFlowEdge(NeBinaryAddress Site, string Kind, NeBinaryAddress? Target, NeImportReference? Import, string TargetStatus);
public sealed record NeWholeProgramBlock(NeBinaryAddress Address, IReadOnlyList<NeBinaryAddress> Instructions, IReadOnlyList<NeBinaryAddress> RegionEntries, IReadOnlyList<NeControlFlowEdge> Successors);
public sealed record NeCodeRegion(int SegmentIndex, int Offset, int Length, string BytesHex, string Classification, IReadOnlyList<string> Evidence);
public sealed record NeResourceCodeReferenceAudit(int ResourceIndex, string Status, string Evidence);
public sealed record NeWholeProgramSummary(int Roots, int VisitedRoots, int Regions, int UniqueInstructions, int BasicBlocks, int SharedBlocks, int IndirectSites, int UnresolvedControlFlowSites, long InstructionBytes, long EmbeddedDataBytes, long AmbiguousBytes, long UnresolvedBytes, bool WorklistExhausted, bool ReadyForNativeTranslation);
public sealed record NeWholeProgramDiscovery(string Format, string ExecutableSha256, int ExecutableSize, string Policy, NeBinaryAddress? Startup, NeWholeProgramSummary Summary, IReadOnlyList<NeWholeProgramRoot> Roots, IReadOnlyList<NeWholeProgramInstruction> Instructions, IReadOnlyList<NeControlFlowEdge> ControlFlow, IReadOnlyList<NeWholeProgramBlock> BasicBlocks, IReadOnlyList<NeCodeRegion> CodeRegions, IReadOnlyList<NeJumpTableAudit> IndirectTransfers, IReadOnlyList<NeResourceCodeReferenceAudit> ResourceReferences, NeProgramGraph Graph);
/// <summary>Root-free binary discovery and canonical shared CFG. Never lowers speculative interpretations.</summary>
public static class NeWholeProgramDiscoveryBuilder
{
    public static NeWholeProgramDiscovery Build(byte[] image, NeVerifiedSymbols? symbols = null, NeDiscoveryLimits? limits = null)
    {
        var executable = new NeExecutableReader().Read(image);
        var graph = new NeProgramAnalyzer().AnalyzeWholeProgram(image, symbols, limits);
        var grouped = graph.Functions.SelectMany(f => f.Instructions.Select(i => (Instruction: i, Owner: f.Address))).GroupBy(p => p.Instruction.Address).OrderBy(g => g.Key).ToArray();
        var instructions = grouped.Select(g => g.First().Instruction).ToDictionary(i => i.Address);
        var owners = grouped.ToDictionary(g => g.Key, g => g.Select(p => p.Owner).Distinct().Order().ToArray());
        var byteOwners = new Dictionary<NeBinaryAddress, HashSet<NeBinaryAddress>>();
        foreach (var instruction in instructions.Values)
        {
            for (int offset = instruction.Address.Offset; offset < instruction.Address.Offset + instruction.Size; offset++)
            {
                if (offset > ushort.MaxValue)
                {
                    continue;
                }

                var address = new NeBinaryAddress(instruction.Address.SegmentIndex, (ushort)offset);
                if (!byteOwners.TryGetValue(address, out var set))
                {
                    byteOwners.Add(address, set = []);
                }

                set.Add(instruction.Address);
            }
        }

        bool Overlapping(NeInstructionInventory instruction) => Enumerable.Range(instruction.Address.Offset, instruction.Size).Any(o => o <= ushort.MaxValue && byteOwners[new(instruction.Address.SegmentIndex, (ushort)o)].Count > 1);


        string Status(NeBinaryAddress target) => instructions.ContainsKey(target)
            ? Overlapping(instructions[target])
                ? "AmbiguousInstructionBoundary"
                : "DiscoveredInstructionBoundary"
            : "UnresolvedInstructionBoundary";

        var segmentInstructions = instructions.Values.GroupBy(i => i.Address.SegmentIndex).ToDictionary(g => g.Key, g => g.OrderBy(i => i.Address).ToArray());
        var independentEntries = graph.Entries.Select(e => e.Address).ToHashSet();
        var tables = instructions.Values.Where(i => i.FlowControl is "IndirectBranch" or "IndirectCall").OrderBy(i => i.Address).Select(i => NeJumpTableAnalyzer.Analyze(image, executable, segmentInstructions[i.Address.SegmentIndex], i.Address, independentEntries)).ToArray();
        var tableBySite = tables.ToDictionary(t => t.Site);
        var dependenciesBySite = graph.Dependencies.GroupBy(d => d.Site).ToDictionary(g => g.Key, g => g.Distinct().ToArray());
        var relocatedBytes = executable.Relocations.SelectMany(r => r.SourceOffsets.SelectMany(o => Enumerable.Range(o, Math.Max(1, r.PatchWidth)).Select(p => new NeBinaryAddress(r.SourceSegmentIndex, unchecked((ushort)p))))).ToHashSet();
        var edges = new List<NeControlFlowEdge>();
        foreach (var item in instructions.Values.OrderBy(i => i.Address))
        {
            var decoder = Decoder.Create(16, new ByteArrayCodeReader(Convert.FromHexString(item.BytesHex)));
            decoder.IP = item.Address.Offset;
            var instruction = decoder.Decode();
            var dependencies = dependenciesBySite.GetValueOrDefault(item.Address) ?? [];

            void Target(string kind, NeBinaryAddress target) => edges.Add(new(item.Address, kind, target, null, Status(target)));


            void Fallthrough()
            {
                int next = item.Address.Offset + item.Size;
                // A near IP wraps at 16 bits. File/allocated boundaries remain explicit in the target status.
                Target("Fallthrough", new(item.Address.SegmentIndex, unchecked((ushort)next)));
            }


            bool Relocated() => Enumerable.Range(item.Address.Offset, item.Size).Any(o => relocatedBytes.Contains(new(item.Address.SegmentIndex, unchecked((ushort)o))));


            void Transfer()
            {
                var transfers = dependencies.Where(d => d.Kind is not ("DataReference" or "DataPointerReference" or "CallbackOrProcedureReference" or "SharedFallthrough")).ToArray();
                if (instruction.Op0Kind == OpKind.NearBranch16 && !Relocated())
                {
                    Target(instruction.FlowControl == FlowControl.Call
                        ? "DirectNearCall"
                        : "DirectJump", new(item.Address.SegmentIndex, instruction.NearBranch16));
                }
                else if (transfers.Any(d => d.Kind is "JumpTableTarget" or "IndexedNearCallTarget") && !tableBySite[item.Address].Resolved)
                {
                    edges.Add(new(item.Address, instruction.FlowControl.ToString(), null, null, "UnresolvedTargetSet"));
                }
                else if (transfers.Length == 0)
                {
                    edges.Add(new(item.Address, instruction.FlowControl.ToString(), null, null, "UnresolvedTargetSet"));
                }
                else
                {
                    foreach (var dependency in transfers)
                    {
                        edges.Add(new(item.Address, dependency.Kind, dependency.Target, dependency.Import, dependency.Target is { } target
                            ? Status(target)
                            : dependency.Import is not null
                                ? "IdentifiedImport"
                                : "UnresolvedTargetSet"));
                    }
                }
            }


            switch (instruction.FlowControl)
            {
                case FlowControl.Next:
                    Fallthrough();
                    break;
                case FlowControl.Call:
                case FlowControl.IndirectCall:
                    Transfer();
                    Fallthrough();
                    break;
                case FlowControl.ConditionalBranch:
                    if (instruction.Op0Kind == OpKind.NearBranch16 && !Relocated())
                    {
                        Target("ConditionalBranch", new(item.Address.SegmentIndex, instruction.NearBranch16));
                    }
                    else
                    {
                        edges.Add(new(item.Address, "ConditionalBranch", null, null, "UnresolvedTargetSet"));
                    }

                    Fallthrough();
                    break;
                case FlowControl.UnconditionalBranch:
                case FlowControl.IndirectBranch:
                    Transfer();
                    break;
                case FlowControl.Return:
                    edges.Add(new(item.Address, "Return", null, null, "RuntimeReturnFrame"));
                    break;
                case FlowControl.Interrupt:
                    edges.Add(new(item.Address, "Interrupt", null, null, "UnresolvedControlEffect"));
                    edges.Add(new(item.Address, "PossibleInterruptReturn", new(item.Address.SegmentIndex, unchecked((ushort)(item.Address.Offset + item.Size))), null, "UnresolvedInterruptReturnSemantics"));
                    break;
                default:
                    edges.Add(new(item.Address, instruction.FlowControl.ToString(), null, null, "UnresolvedControlEffect"));
                    break;
            }
        }

        var orderedEdges = edges.Distinct().OrderBy(e => e.Site).ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Target).ThenBy(e => e.Import?.Identity, StringComparer.Ordinal).ToArray();
        var outgoing = orderedEdges.GroupBy(e => e.Site).ToDictionary(g => g.Key, g => g.ToArray());
        var incoming = orderedEdges.Where(e => e.Target is not null).GroupBy(e => e.Target!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        var entryAddresses = graph.Entries.Select(e => e.Address).ToHashSet();
        var leaders = instructions.Keys.Where(address => entryAddresses.Contains(address) || !incoming.TryGetValue(address, out var ins) || ins.Length != 1 || ins[0].Kind != "Fallthrough" || outgoing[ins[0].Site].Length != 1 || Overlapping(instructions[address])).ToHashSet();
        var blocks = new List<NeWholeProgramBlock>();
        var assigned = new HashSet<NeBinaryAddress>();

        void Block(NeBinaryAddress start)
        {
            if (!assigned.Add(start))
            {
                return;
            }

            var body = new List<NeBinaryAddress>
            {
                start
            };
            var cursor = start;
            while (outgoing[cursor] is [var edge] && edge.Kind == "Fallthrough" && edge.Target is { } next && instructions.ContainsKey(next) && !leaders.Contains(next) && assigned.Add(next))
            {
                body.Add(next);
                cursor = next;
            }

            blocks.Add(new(start, body, body.SelectMany(i => owners[i]).Distinct().Order().ToArray(), outgoing[cursor]));
        }


        foreach (var leader in leaders.Order())
        {
            Block(leader);
        }

        foreach (var address in instructions.Keys.Order())
        {
            Block(address);
        }

        var startup = executable.NeHeader.InitialCs != 0
            ? new NeBinaryAddress(executable.NeHeader.InitialCs, executable.NeHeader.InitialIp)
            : (NeBinaryAddress?)null;
        var reachable = new HashSet<NeBinaryAddress>();
        var pending = new Queue<NeBinaryAddress>();
        if (startup is { } initial && instructions.ContainsKey(initial))
        {
            reachable.Add(initial);
            pending.Enqueue(initial);
        }

        while (pending.TryDequeue(out var current))
        {
            foreach (var edge in outgoing[current])
            {
                if (edge.TargetStatus is "DiscoveredInstructionBoundary" && edge.Target is { } target && instructions.ContainsKey(target) && reachable.Add(target))
                {
                    pending.Enqueue(target);
                }
            }
        }

        var roots = graph.Entries.Where(e => e.Evidence.Any(v => v.StartsWith("NE", StringComparison.Ordinal) || v.StartsWith("ApiCallback:", StringComparison.Ordinal) || v.StartsWith("CodePointerCandidate", StringComparison.Ordinal) || v == "ExternalDebugSymbol")).Select(e => new NeWholeProgramRoot(e.Address, e.Names, e.Evidence, graph.Functions.Any(f => f.Address == e.Address), reachable.Contains(e.Address)
            ? "ReachedByResolvedStartupControlFlow"
            : "NotReachedByResolvedStartupControlFlow")).ToArray();
        var dataEvidence = new Dictionary<NeBinaryAddress, SortedSet<string>>();
        foreach (var table in tables.Where(t => t.Resolved))
        {
            foreach (var slot in table.Entries)
            {
                for (int n = 0; n < 2; n++)
                {
                    var address = slot.Slot with
                    {
                        Offset = (ushort)(slot.Slot.Offset + n)
                    };
                    if (!dataEvidence.TryGetValue(address, out var evidence))
                    {
                        dataEvidence.Add(address, evidence = new(StringComparer.Ordinal));
                    }

                    evidence.Add($"VerifiedJumpTable:{table.Site.Identity}");
                }
            }
        }

        var regions = new List<NeCodeRegion>();
        long instructionBytes = 0, embeddedBytes = 0, ambiguousBytes = 0, unresolvedBytes = 0;
        foreach (var segment in executable.Segments.Where(s => !s.IsData))
        {
            (string Kind, string[] Evidence) Classify(int offset)
            {
                var address = new NeBinaryAddress(segment.Index, (ushort)offset);
                byteOwners.TryGetValue(address, out var code);
                dataEvidence.TryGetValue(address, out var data);
                bool ambiguous = code?.Count > 1 || code?.Count > 0 && data is not null;
                var evidence = (code?.Select(i => $"DecodedInstruction:{i.Identity}") ?? []).Concat(data ?? []).Order(StringComparer.Ordinal).ToArray();
                return (ambiguous
                    ? "AmbiguousCodeData"
                    : code?.Count > 0
                        ? "DiscoveredInstruction"
                        : data is not null
                            ? "VerifiedEmbeddedData"
                            : "Unresolved", evidence);
            }

            // Preserve exact original bytes and competing interpretations, including unknown gaps; no linear sweep.
            for (int start = 0; start < segment.FileLength;)
            {
                var classification = Classify(start);
                int end = start + 1;
                while (end < segment.FileLength)
                {
                    var next = Classify(end);
                    if (next.Kind != classification.Kind || !next.Evidence.SequenceEqual(classification.Evidence))
                    {
                        break;
                    }

                    end++;
                }

                int length = end - start;
                switch (classification.Kind)
                {
                    case "DiscoveredInstruction":
                        instructionBytes += length;
                        break;
                    case "VerifiedEmbeddedData":
                        embeddedBytes += length;
                        break;
                    case "AmbiguousCodeData":
                        ambiguousBytes += length;
                        break;
                    default:
                        unresolvedBytes += length;
                        break;
                }

                regions.Add(new(segment.Index, start, length, Convert.ToHexString(image.AsSpan(segment.FileOffset + start, length)), classification.Kind, classification.Evidence));
                start = end;
            }
        }

        bool exhausted = !graph.Diagnostics.Any(d => d.Code is "FunctionLimit" or "InstructionLimit" or "AnalysisLimit");
        var resources = executable.Resources.Select((r, index) => new NeResourceCodeReferenceAudit(index + 1, r.Type.Id is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 9 or 12 or 14
            ? "StandardResourceNoDirectProcedureAddress"
            : "OpaqueResourceUnresolved", "Resource type " + (r.Type.Id?.ToString() ?? r.Type.Name) + "; standard Win16 resource identifiers/names are not segment:offset procedure pointers. No raw byte scan promotes resource data to code; opaque formats require verified metadata.")).ToArray();
        return new("NeWholeProgramDiscovery.v1", executable.Sha256, image.Length, "Original-byte recursive discovery from all evidenced entries, including code targets established by verified register-API contracts, not an explicit application function list. Relocation-backed code pointers are candidates, not proof that executable-segment data is code. RegionEntries describe shared traversal regions, not unique function ownership. All instruction interpretations, unresolved bytes, dynamic targets and opaque resources are retained. NotReachedByResolvedStartupControlFlow is not proof of unreachability. WorklistExhausted means bounded traversal finished, not that all indirect control flow or instruction lowering is supported. This report cannot authorize native translation.", startup, new(roots.Length, roots.Count(r => r.Visited), graph.Functions.Count, instructions.Count, blocks.Count, blocks.Count(b => b.RegionEntries.Count > 1), tables.Length, orderedEdges.Where(e => e.TargetStatus.StartsWith("Unresolved", StringComparison.Ordinal)).Select(e => e.Site).Distinct().Count(), instructionBytes, embeddedBytes, ambiguousBytes, unresolvedBytes, exhausted, false), roots, instructions.Values.OrderBy(i => i.Address).Select(i => new NeWholeProgramInstruction(i, owners[i.Address], Overlapping(i))).ToArray(), orderedEdges, blocks.OrderBy(b => b.Address).ToArray(), regions, tables, resources, graph);
    }
}
