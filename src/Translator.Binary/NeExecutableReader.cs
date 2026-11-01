using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Translator.Binary;

/// <summary>Windows NE metadata only. No selector assignment, fixup application, or code generation.</summary>
public sealed class NeExecutableReader
{
    public NeHeader ReadHeader(ReadOnlySpan<byte> image)
    {
        int ne = HeaderOffset(image);
        return new(U16(image, ne + 2), U16(image, ne + 0x16), U16(image, ne + 0x14), U16(image, ne + 0x1c), U16(image, ne + 0x1e), U16(image, ne + 0x22));
    }

    public NeExecutable Read(ReadOnlySpan<byte> image) => new Parser(image.ToArray()).Read();

    private static int HeaderOffset(ReadOnlySpan<byte> image)
    {
        Range(image.Length, 0, 64, "DOS header");
        if (U16(image, 0) != 0x5a4d)
        {
            throw Error(0, "Missing DOS MZ signature");
        }

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(image[0x3c..]);
        Range(image.Length, offset, 64, "NE header");
        if (offset < 64 || U16(image, (int)offset) != 0x454e)
        {
            throw Error(offset, "Missing NE signature");
        }

        int dosSize = U16(image, 8) * 16;
        if (dosSize < 64 || dosSize > offset)
        {
            throw Error(8, "Invalid DOS header size");
        }

        Range(dosSize, U16(image, 24), (long)U16(image, 6) * 4, "DOS relocations");
        return (int)offset;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);

    private static InvalidDataException Error(long offset, string message) => new($"NE format at file 0x{offset:X}: {message}.");

    private static void Range(int limit, long offset, long length, string table)
    {
        if (offset < 0 || length < 0 || offset > limit || length > limit - offset)
        {
            throw Error(offset, $"Truncated or out-of-bounds {table} (length {length})");
        }
    }

    private sealed class Parser(byte[] bytes)
    {
        private int _ne;
        private NeMetadata _h = null!;
        private readonly List<NeSegment> _segments = new();

        private ushort W(int p)
        {
            Range(bytes.Length, p, 2, "word");
            return U16(bytes, p);
        }

        private uint D(int p)
        {
            Range(bytes.Length, p, 4, "dword");
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p));
        }

        private int Relative(ushort offset) => _ne + offset;
        private int End(int start) => new[]
        {
            _h.SegmentTableOffset,
            _h.ResourceTableOffset,
            _h.ResidentNameTableOffset,
            _h.ModuleReferenceTableOffset,
            _h.ImportedNameTableOffset,
            _h.EntryTableOffset
        }.Where(x => x != 0).Select(Relative).Concat(new[] { bytes.Length, _h.NonResidentNameTableOffset > 0 && _h.NonResidentNameTableOffset <= bytes.Length
            ? (int)_h.NonResidentNameTableOffset
            : bytes.Length }).Concat(_segments.Where(s => s.FileLength > 0).Select(s => s.FileOffset)).Where(x => x > start).DefaultIfEmpty(bytes.Length).Min();

        private void Table(int start, long length, string name)
        {
            if (start < _ne + 64)
            {
                throw Error(start, $"{name} overlaps NE header");
            }

            Range(End(start), start, length, name);
        }

        private string Text(ref int p, int end)
        {
            Range(end, p, 1, "name length");
            int size = bytes[p++];
            Range(end, p, size, "name bytes");
            string value = Encoding.Latin1.GetString(bytes, p, size);
            p += size;
            return value;
        }

        private int Shift(ushort raw, int shift, int source)
        {
            if (shift > 31 || ((long)raw << shift) > int.MaxValue)
            {
                throw Error(source, "Invalid alignment or shifted offset");
            }

            return (int)((long)raw << shift);
        }

        public NeExecutable Read()
        {
            _ne = HeaderOffset(bytes);
            _h = new(_ne, W(_ne + 2), D(_ne + 8), W(_ne + 12), W(_ne + 14), W(_ne + 16), W(_ne + 18), W(_ne + 22), W(_ne + 20), W(_ne + 26), W(_ne + 24), W(_ne + 28), W(_ne + 30), W(_ne + 4), W(_ne + 6), W(_ne + 34), W(_ne + 36), W(_ne + 38), W(_ne + 40), W(_ne + 42), D(_ne + 44), W(_ne + 32), W(_ne + 48), W(_ne + 50), W(_ne + 52), bytes[_ne + 54], bytes[_ne + 55], W(_ne + 56), W(_ne + 58), W(_ne + 60), W(_ne + 62));
            if (_h.TargetOs != 2 && _h.TargetOs != 4)
            {
                throw Error(_ne + 54, $"Unsupported NE target OS {_h.TargetOs}; Windows resource layout required");
            }

            if (_h.AlignmentShift > 31)
            {
                throw Error(_ne + 50, "Invalid segment alignment");
            }

            foreach (ushort offset in new[]
            {
                _h.SegmentTableOffset,
                _h.ResourceTableOffset,
                _h.ResidentNameTableOffset,
                _h.ModuleReferenceTableOffset,
                _h.ImportedNameTableOffset,
                _h.EntryTableOffset
            }.Where(x => x != 0))
            {
                Table(Relative(offset), 0, "table offset");
            }

            var tableOffsets = new[]
            {
                _h.SegmentTableOffset,
                _h.ResourceTableOffset,
                _h.ResidentNameTableOffset,
                _h.ModuleReferenceTableOffset,
                _h.ImportedNameTableOffset,
                _h.EntryTableOffset
            };
            for (int i = 0; i < tableOffsets.Length; i++)
            {
                for (int j = i + 1; j < tableOffsets.Length; j++)
                {
                    if (tableOffsets[i] != 0 && tableOffsets[i] == tableOffsets[j] && !(i == 1 && j == 2))
                    {
                        throw Error(Relative(tableOffsets[i]), "Overlapping table starts");
                    }
                }
            }

            int st = Relative(_h.SegmentTableOffset);
            if (_h.SegmentCount > 0)
            {
                Table(st, (long)_h.SegmentCount * 8, "segment table");
            }

            for (int i = 0; i < _h.SegmentCount; i++)
            {
                int p = st + i * 8;
                ushort sector = W(p), length = W(p + 2), flags = W(p + 4), alloc = W(p + 6);
                int shift = _h.AlignmentShift == 0
                    ? 9
                    : _h.AlignmentShift;
                int file = Shift(sector, shift, p), count = sector == 0
                    ? 0
                    : length == 0
                        ? 65536
                        : length;
                Range(bytes.Length, file, count, $"segment {i + 1}");
                int minimum = alloc == 0
                    ? 65536
                    : alloc;
                _segments.Add(new(i + 1, p, sector, length, flags, alloc, file, count, minimum, Math.Max(count, minimum), shift));
                // Validate the record envelope before parsing symbolic targets and source chains.
                if ((flags & 0x100) != 0)
                {
                    if (count == 0)
                    {
                        throw Error(p, "Unbacked segment declares relocations");
                    }

                    int fixups = file + count;
                    Range(bytes.Length, fixups, 2, "relocation count");
                    Range(bytes.Length, fixups + 2, (long)W(fixups) * 8, "relocation records");
                }
            }

            foreach (int index in new[]
            {
                (int)_h.InitialCs,
                _h.InitialSs,
                _h.AutoDataSegment
            }

            )
            {
                if (index > _segments.Count)
                {
                    throw Error(_ne, $"Invalid header segment index {index}");
                }
            }

            if (_h.InitialCs != 0 && (_segments[_h.InitialCs - 1].IsData || _h.InitialIp >= _segments[_h.InitialCs - 1].MemoryLength))
            {
                throw Error(_ne + 20, "Invalid initial CS:IP");
            }

            var entries = Entries();
            var resident = Names(_h.ResidentNameTableOffset == 0
                ? 0
                : Relative(_h.ResidentNameTableOffset), null);
            Range(bytes.Length, _h.NonResidentNameTableOffset, _h.NonResidentNameTableLength, "nonresident names");
            var nonresident = Names(_h.NonResidentNameTableLength == 0
                ? 0
                : (int)_h.NonResidentNameTableOffset, _h.NonResidentNameTableLength);
            var validOrdinals = entries.Where(e => e.Kind != "Unused").Select(e => e.Ordinal).ToHashSet();
            foreach (var name in resident.Concat(nonresident))
            {
                if (name.Ordinal != 0 && !validOrdinals.Contains(name.Ordinal))
                {
                    throw Error(name.SourceFileOffset, $"Name references invalid entry ordinal {name.Ordinal}");
                }
            }

            var imported = new List<NeImportedName>();
            int importStart = Relative(_h.ImportedNameTableOffset);
            if (_h.ImportedNameTableOffset != 0)
            {
                int p = importStart, end = End(p);
                while (p < end)
                {
                    int source = p;
                    string name = Text(ref p, end);
                    imported.Add(new(source, source - importStart, name));
                }
            }

            var importedByOffset = imported.ToDictionary(n => n.TableOffset);
            var modules = new List<NeModuleReference>();
            if (_h.ModuleReferenceCount > 0)
            {
                int start = Relative(_h.ModuleReferenceTableOffset);
                Table(start, (long)_h.ModuleReferenceCount * 2, "module references");
                for (int i = 0; i < _h.ModuleReferenceCount; i++)
                {
                    ushort offset = W(start + i * 2);
                    importedByOffset.TryGetValue(offset, out var name);
                    if (name is null || name.Name.Length == 0)
                    {
                        throw Error(start + i * 2, "Invalid imported module name offset");
                    }

                    modules.Add(new(i + 1, start + i * 2, offset, name.Name));
                }
            }

            var executable = new NeExecutable(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), new(0, W(2), W(4), W(6), W(8), W(10), W(12), W(14), W(16), W(18), W(20), W(22), W(24), W(26), Convert.ToHexString(bytes.AsSpan(28, 32)), (uint)_ne), _h, _segments, entries, resident, nonresident, imported, modules, Resources());
            return executable with
            {
                Relocations = NeRelocationReader.Read(bytes, executable)
            };
        }

        private List<NeName> Names(int start, int? length)
        {
            var result = new List<NeName>();
            if (start == 0)
            {
                return result;
            }

            int p = start, end = length.HasValue
                ? start + length.Value
                : End(start);
            Table(start, end - start, "name table");
            while (p < end)
            {
                int source = p;
                string name = Text(ref p, end);
                if (name.Length == 0)
                {
                    return result;
                }

                Range(end, p, 2, "name ordinal");
                result.Add(new(source, name, W(p)));
                p += 2;
            }

            throw Error(p, "Unterminated name table");
        }

        private List<NeEntry> Entries()
        {
            var result = new List<NeEntry>();
            if (_h.EntryTableLength == 0)
            {
                return result;
            }

            int p = Relative(_h.EntryTableOffset), end = p + _h.EntryTableLength;
            Table(p, _h.EntryTableLength, "entry table");
            while (p < end)
            {
                int count = bytes[p++];
                if (count == 0)
                {
                    return result;
                }

                Range(end, p, 1, "entry bundle");
                int kind = bytes[p++];
                for (int i = 0; i < count; i++)
                {
                    if (result.Count == 65535)
                    {
                        throw Error(p, "Entry ordinal overflow");
                    }

                    int source = p, ordinal = result.Count + 1;
                    if (kind == 0)
                    {
                        result.Add(new(ordinal, source, "Unused", 0, null, null, null));
                        continue;
                    }

                    Range(end, p, kind == 255
                        ? 6
                        : 3, "entry record");
                    byte flags = bytes[p++];
                    ushort? signature = null;
                    int segment = kind;
                    if (kind == 255)
                    {
                        signature = W(p);
                        p += 2;
                        segment = bytes[p++];
                        if (signature != 0x3fcd)
                        {
                            throw Error(source, "Invalid movable entry signature");
                        }
                    }

                    ushort offset = W(p);
                    p += 2;
                    if (kind != 254 && (segment == 0 || segment > _segments.Count || offset >= _segments[segment - 1].MemoryLength))
                    {
                        throw Error(source, "Invalid entry segment or offset");
                    }

                    result.Add(new(ordinal, source, kind == 254
                        ? "Constant"
                        : kind == 255
                            ? "Movable"
                            : "Fixed", flags, kind == 254
                        ? null
                        : segment, offset, signature));
                }
            }

            throw Error(p, "Unterminated entry table");
        }

        private List<NeResource> Resources()
        {
            var result = new List<NeResource>();
            if (_h.ResourceTableOffset == 0 || _h.ResourceTableOffset == _h.ResidentNameTableOffset)
            {
                return result;
            }

            int start = Relative(_h.ResourceTableOffset), p = start, end = End(start);
            Table(start, 2, "resource alignment");
            int shift = W(p);
            p += 2;
            if (shift > 31)
            {
                throw Error(start, "Invalid resource alignment");
            }

            NeResourceIdentity Identity(ushort raw)
            {
                if ((raw & 0x8000) != 0)
                {
                    return new(raw, raw & 0x7fff, null);
                }

                int name = start + raw;
                if (raw == 0)
                {
                    throw Error(name, "Invalid resource name offset");
                }

                return new(raw, null, Text(ref name, end));
            }

            while (p < end)
            {
                Range(end, p, 2, "resource type");
                ushort type = W(p);
                p += 2;
                if (type == 0)
                {
                    return result;
                }

                Range(end, p, 6, "resource type header");
                int count = W(p);
                uint reserved = D(p + 2);
                p += 6;
                Range(end, p, (long)count * 12, "resource records");
                var identity = Identity(type);
                for (int i = 0; i < count; i++, p += 12)
                {
                    ushort raw = W(p), length = W(p + 2);
                    int file = Shift(raw, shift, p), size = Shift(length, shift, p + 2);
                    Range(bytes.Length, file, size, "resource payload");
                    result.Add(new(p, identity, Identity(W(p + 6)), raw, length, shift, file, size, W(p + 4), W(p + 8), W(p + 10), reserved));
                }
            }

            throw Error(p, "Unterminated resource table");
        }
    }
}
