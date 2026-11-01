using Iced.Intel;
using Translator.Binary;

namespace Translator.Analysis;

public sealed record NeMemoryWriteSite(NeBinaryAddress Site, string Instruction, string SegmentRegister, int Width, string Addressing, IReadOnlyList<NeBinaryAddress> ProvenTargets, bool RuntimeSelectorOrOffset, bool ExecutableTarget, bool PossibleProcedurePointerUpdate);
public sealed record NePointerStorage(NeBinaryAddress Site, string Instruction, string SegmentRegister, ulong Displacement, bool Indexed, string Policy);
public sealed record NeMemoryAudit(string Format, string ExecutableSha256, string Policy, int InstructionsAudited, int MemoryWriteInstructions, int MemoryWriteAccesses, int ProvenExecutableWriteAccesses, int RuntimeGuardedWriteAccesses, bool StaticResolutionRequired, bool ProvesAbsenceOfAllExecutableWrites, IReadOnlyList<NeMemoryWriteSite> Writes, IReadOnlyList<NePointerStorage> ProcedurePointerSources, IReadOnlyList<NeCodeRegion> ReadonlyCodeTables, IReadOnlyList<string> Evidence);
/// <summary>Reports writes, not inferred gameplay. Unknown selectors are never called proof of no self-modification.</summary>
public static class NeMemoryAuditBuilder
{
    public static NeMemoryAudit Build(byte[] image, NeWholeProgramDiscovery discovery)
    {
        var executable = new NeExecutableReader().Read(image);
        if (executable.Sha256 != discovery.ExecutableSha256)
        {
            throw new InvalidDataException("Memory audit image/discovery hash mismatch.");
        }

        var factory = new InstructionInfoFactory();
        var writes = new List<NeMemoryWriteSite>();
        var pointers = new List<NePointerStorage>();

        bool Write(OpAccess a) => a is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite;

        var pointerOffsets = new HashSet<ulong>();
        foreach (var item in discovery.Instructions)
        {
            var ins = Decode(item.Original);
            if (ins.FlowControl is FlowControl.IndirectCall or FlowControl.IndirectBranch && ins.Op0Kind == OpKind.Memory)
            {
                bool indexed = ins.MemoryBase != Register.None || ins.MemoryIndex != Register.None;
                if (!indexed && ins.MemorySegment == Register.DS)
                {
                    pointerOffsets.Add(ins.MemoryDisplacement64);
                }

                pointers.Add(new(item.Original.Address, item.Original.Text, ins.MemorySegment.ToString(), ins.MemoryDisplacement64, indexed, ins.MemorySegment == Register.CS
                    ? "Readonly original code storage; verified table or explicit runtime target validation"
                    : "Mutable original-width pointer storage; read at each transfer and validate against approved compiled entries; never cache or reinterpret as a native pointer"));
            }
        }

        // Per-basic-block selector facts supplement the existing context-sensitive graph.
        // Facts are killed by every register write and call. No entry DS/ES value is assumed.
        var originals = discovery.Instructions.ToDictionary(i => i.Original.Address, i => i.Original);
        var selectorFacts = new Dictionary<NeBinaryAddress, Dictionary<Register, int>>();
        foreach (var block in discovery.BasicBlocks)
        {
            var facts = new Dictionary<Register, int>
            {
                [Register.CS] = block.Address.SegmentIndex
            };
            int? pushedSelector = null;
            foreach (var address in block.Instructions)
            {
                var original = originals[address];
                var ins = Decode(original);
                selectorFacts[address] = new(facts);
                var info = factory.GetInfo(ins);
                int? source = ins.OpCount >= 2 && ins.Op1Kind == OpKind.Register && facts.TryGetValue(ins.Op1Register, out int known)
                    ? known
                    : null;
                var relocation = executable.Relocations.FirstOrDefault(r => r.SourceSegmentIndex == address.SegmentIndex && r.RawAddressType == 2 && !r.Additive && r.Supported && r.Import is null && r.SourceOffsets.Any(o => o >= address.Offset && o < address.Offset + original.Size));
                if (ins.Mnemonic == Mnemonic.Mov && ins.Op1Kind == OpKind.Immediate16 && relocation is not null)
                {
                    source = relocation.TargetSegmentIndex;
                }

                int? pop = ins.Mnemonic == Mnemonic.Pop
                    ? pushedSelector
                    : null;
                foreach (var used in info.GetUsedRegisters())
                {
                    if (Write(used.Access))
                    {
                        foreach (var register in facts.Keys.Where(r => r.GetFullRegister() == used.Register.GetFullRegister()).ToArray())
                        {
                            facts.Remove(register);
                        }
                    }
                }

                if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && source is int segment)
                {
                    facts[ins.Op0Register] = segment;
                }

                if (ins.Mnemonic == Mnemonic.Pop && ins.Op0Kind == OpKind.Register && pop is int popped)
                {
                    facts[ins.Op0Register] = popped;
                }

                pushedSelector = ins.Mnemonic == Mnemonic.Push && ins.Op0Kind == OpKind.Register && facts.TryGetValue(ins.Op0Register, out int pushed)
                    ? pushed
                    : null;
                if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                {
                    facts.Clear();
                }

                facts[Register.CS] = block.Address.SegmentIndex;
            }
        }

        foreach (var item in discovery.Instructions)
        {
            var original = item.Original;
            var ins = Decode(original);
            var info = factory.GetInfo(ins);
            foreach (var memory in info.GetUsedMemory().Where(m => Write(m.Access)))
            {
                bool indexed = memory.Base != Register.None || memory.Index != Register.None;
                var targets = discovery.Graph.Dependencies.Where(d => d.Site == original.Address && d.Kind == "DataReference" && d.Target is not null).Select(d => d.Target!.Value).Distinct().Order().ToList();
                if (!indexed && memory.Displacement <= ushort.MaxValue && selectorFacts.TryGetValue(original.Address, out var facts) && facts.TryGetValue(memory.Segment, out int segment))
                {
                    targets.Add(new(segment, (ushort)memory.Displacement));
                }

                // CS is architectural and remains known even when the offset is dynamic.
                bool executableTarget = memory.Segment == Register.CS || selectorFacts.TryGetValue(original.Address, out var f) && f.TryGetValue(memory.Segment, out int si) && !executable.Segments[si - 1].IsData || !indexed && targets.Any(t => !executable.Segments[t.SegmentIndex - 1].IsData);
                bool runtime = indexed || targets.Count == 0 || discovery.Graph.Dependencies.Any(d => d.Site == original.Address && d.Kind == "DataReference" && d.Target is null);
                writes.Add(new(original.Address, original.Text, memory.Segment.ToString(), memory.MemorySize.GetSize(), $"{memory.Base}+{memory.Index}*{memory.Scale}+0x{memory.Displacement:X}", targets.Distinct().Order().ToArray(), runtime, executableTarget, memory.Segment == Register.DS && (indexed || pointerOffsets.Any(o => memory.Displacement < o + 4 && memory.Displacement + (ulong)Math.Max(1, memory.MemorySize.GetSize()) > o))));
            }
        }

        int proven = writes.Count(w => w.ExecutableTarget);
        return new("NeMemoryAudit.v1", executable.Sha256, "Audit all discovered original instruction interpretations with Iced explicit/implicit memory access metadata and context-sensitive data references. Code is immutable across all aliases. Proven code writes require explicit static resolution; unknown dynamic selectors/offsets remain guarded at runtime. Mutable procedure-pointer cells are data and select precompiled entries on every use. This audit does not claim exhaustive absence of self-modification on undiscovered or unexecuted paths.", discovery.Instructions.Count, writes.Select(w => w.Site).Distinct().Count(), writes.Count, proven, writes.Count(w => w.RuntimeSelectorOrOffset), proven != 0, false, writes, pointers, discovery.CodeRegions.Where(r => r.Classification == "VerifiedEmbeddedData").ToArray(), ["Original initialized bytes, BSS, stack/heap reservations and relocation results are supplied by the NE loader, before code storage is sealed.", "Writes through original selectors, far pointers and aliases are permission-checked before mutation. Executable-storage writes record selector, offset and width, then fail explicitly.", "Stack CALL/RET/RETF and interrupt frames use original guest storage and 16-bit arithmetic. Callback CPU snapshots do not roll back persistent task memory.", "Whole-program discovery still contains unresolved bytes; runtime guards supplement, rather than erase, that uncertainty."]);
    }

    private static Instruction Decode(NeInstructionInventory item)
    {
        var decoder = Decoder.Create(16, new ByteArrayCodeReader(Convert.FromHexString(item.BytesHex)));
        decoder.IP = item.Address.Offset;
        return decoder.Decode();
    }
}
