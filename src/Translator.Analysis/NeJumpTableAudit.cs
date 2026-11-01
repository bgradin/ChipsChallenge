using System.Buffers.Binary;
using Iced.Intel;
using Translator.Binary;

namespace Translator.Analysis;

public sealed record NeJumpTableEntry(int Index, NeBinaryAddress Slot, NeBinaryAddress Target, bool Valid, string Evidence);
public sealed record NeJumpTableAudit(NeBinaryAddress Site, string Form, NeBinaryAddress? TableBase, string IndexComputation, int? MaximumIndex, NeBinaryAddress? DefaultTarget, IReadOnlyList<NeJumpTableEntry> Entries, bool Resolved, string Diagnostic);
/// <summary>Proves the bounded CS:[BX+disp16] absolute-near idiom from decoded instructions.
/// Other forms remain explicit, including relative/far/data-segment and register dispatch.</summary>
public static class NeJumpTableAnalyzer
{
    public static NeJumpTableAudit Analyze(byte[] image, NeExecutable executable, IReadOnlyList<NeInstructionInventory> instructions, NeBinaryAddress site, IReadOnlySet<NeBinaryAddress>? independentEntries = null)
    {
        Instruction Decode(NeInstructionInventory i)
        {
            var decoder = Decoder.Create(16, new ByteArrayCodeReader(Convert.FromHexString(i.BytesHex)));
            decoder.IP = i.Address.Offset;
            return decoder.Decode();
        }

        var ordered = instructions.OrderBy(i => i.Address).ToArray();
        int pos = Array.FindIndex(ordered, i => i.Address == site);
        if (pos < 0)
        {
            throw new ArgumentException("Indirect site is absent from instruction inventory.", nameof(site));
        }

        var transfer = Decode(ordered[pos]);

        NeJumpTableAudit Unknown(string reason) => new(site, transfer.Code.ToString(), null, "Unproven", null, null, [], false, reason);

        if (transfer.Code is not (Code.Jmp_rm16 or Code.Call_rm16) || transfer.Op0Kind != OpKind.Memory || transfer.MemorySegment != Register.CS || transfer.MemoryBase != Register.BX || transfer.MemoryIndex != Register.None)
        {
            return Unknown("Observed indirect form is not the verified bounded absolute-near CS:[BX+disp16] idiom; relative/far/data-segment/register targets remain unresolved.");
        }

        if (pos < 4)
        {
            return Unknown("Insufficient bound/index evidence.");
        }

        var xchg = Decode(ordered[pos - 1]);
        var scale = Decode(ordered[pos - 2]);
        if (xchg.Mnemonic != Mnemonic.Xchg || !((xchg.Op0Register == Register.AX && xchg.Op1Register == Register.BX) || (xchg.Op0Register == Register.BX && xchg.Op1Register == Register.AX)) || scale.Mnemonic != Mnemonic.Shl || scale.Op0Register != Register.AX || scale.Immediate8 != 1)
        {
            return Unknown("Index is not the verified AX*2 -> BX sequence.");
        }

        int cmpPos = pos - 4;
        var branch = Decode(ordered[pos - 3]);
        ushort fallback;
        if (branch.Mnemonic == Mnemonic.Ja)
        {
            fallback = branch.NearBranch16;
        }
        else
        {
            // Compiler's inverted unsigned guard: JBE scale; JMP default.
            var guard = Decode(ordered[pos - 4]);
            if (pos < 5 || guard.Mnemonic != Mnemonic.Jbe || guard.NearBranch16 != ordered[pos - 2].Address.Offset || branch.Mnemonic != Mnemonic.Jmp || branch.Op0Kind != OpKind.NearBranch16)
            {
                return Unknown("No verified unsigned upper-bound/default branch.");
            }

            fallback = branch.NearBranch16;
            cmpPos--;
        }

        var cmp = Decode(ordered[cmpPos]);
        if (cmp.Mnemonic != Mnemonic.Cmp || cmp.Op0Register != Register.AX || cmp.Op1Kind is not (OpKind.Immediate16 or OpKind.Immediate8to16))
        {
            return Unknown("Guard does not bound AX.");
        }

        int maximum = cmp.Op1Kind == OpKind.Immediate16
            ? cmp.Immediate16
            : unchecked((ushort)cmp.Immediate8to16);
        if (maximum > 1023)
        {
            return Unknown("Table exceeds the bounded audit budget or 16-bit scaling proof.");
        }

        for (int i = cmpPos; i < pos; i++)
        {
            if (ordered[i].Address.Offset + ordered[i].Size != ordered[i + 1].Address.Offset)
            {
                return Unknown("Guard/index instructions are not contiguous.");
            }
        }

        // An alternate incoming edge could bypass the guard or change the index.
        var protectedOffsets = ordered.Skip(cmpPos + 1).Take(pos - cmpPos).Select(i => i.Address.Offset).ToHashSet();
        if (independentEntries?.Any(e => e.SegmentIndex == site.SegmentIndex && protectedOffsets.Contains(e.Offset)) == true)
        {
            return Unknown("An independent entry bypasses the verified index guard.");
        }

        foreach (var other in ordered)
        {
            var ins = Decode(other);
            if (ins.Op0Kind == OpKind.NearBranch16 && protectedOffsets.Contains(ins.NearBranch16) && !(other.Address == ordered[pos - 4].Address && ins.Mnemonic == Mnemonic.Jbe))
            {
                return Unknown("Alternate incoming edge bypasses the verified index guard.");
            }
        }

        if (executable.Relocations.Any(r => r.SourceSegmentIndex == site.SegmentIndex && r.SourceOffsets.Any(o => o >= ordered[cmpPos].Address.Offset && o < site.Offset + transfer.Length)))
        {
            return Unknown("Relocation overlaps guard/index/dispatch evidence.");
        }

        int start = checked((int)transfer.MemoryDisplacement64);
        var segment = executable.Segments[site.SegmentIndex - 1];
        if (start + (maximum + 1) * 2 > segment.FileLength)
        {
            return Unknown("Table exceeds file-backed segment bytes.");
        }

        if (instructions.Any(i => i.Address.Offset < start + (maximum + 1) * 2 && i.Address.Offset + i.Size > start))
        {
            return Unknown("Table overlaps reachable instructions; code/data ownership is ambiguous.");
        }

        var entries = new List<NeJumpTableEntry>();
        for (int i = 0; i <= maximum; i++)
        {
            int offset = start + i * 2;
            ushort target = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(segment.FileOffset + offset, 2));
            bool relocated = executable.Relocations.Any(r => r.SourceSegmentIndex == site.SegmentIndex && r.SourceOffsets.Any(s => s < offset + 2 && s + (r.RawAddressType == 3
                ? 4
                : 2) > offset));
            bool valid = !relocated && target < segment.FileLength && !(target >= start && target < start + (maximum + 1) * 2) && !instructions.Any(ins => target > ins.Address.Offset && target < ins.Address.Offset + ins.Size);
            if (valid)
            {
                var decoder = Decoder.Create(16, new ByteArrayCodeReader(image, segment.FileOffset + target, segment.FileLength - target));
                valid = !decoder.Decode().IsInvalid;
            }

            entries.Add(new(i, new(site.SegmentIndex, (ushort)offset), new(site.SegmentIndex, target), valid, relocated
                ? "Relocation overlaps slot; raw bytes are not an address"
                : "Guard-bounded absolute near word in CS; shared targets retained"));
        }

        bool resolved = entries.All(e => e.Valid);
        return new(site, "AbsoluteNear", new(site.SegmentIndex, (ushort)start), "AX is unsigned <= maximum; SHL AX,1; XCHG AX,BX; CS:[BX+base]", maximum, new(site.SegmentIndex, fallback), entries, resolved, resolved
            ? "Statically bounded target set; instruction ownership checked by CFG discovery; no native translation authorized."
            : "Invalid table target; entire dispatch remains unresolved.");
    }
}
