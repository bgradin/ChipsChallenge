using System.Buffers.Binary;

namespace Translator.Binary;

public enum NeRelocationTargetKind
{
    InternalSegment,
    InternalEntry,
    ImportOrdinal,
    ImportName,
    OsFixup
}

public sealed record NeImportReference(int ModuleIndex, string Module, string? Name, ushort? Ordinal)
{
    public string Identity => $"{Module.ToUpperInvariant()}.{Name ?? $"#{Ordinal}"}";
}

public sealed record NeRelocation(int SourceFileOffset, int SourceSegmentIndex, byte RawAddressType, byte RawFlags, ushort SourceOffset, ushort RawTarget1, ushort RawTarget2, NeRelocationTargetKind TargetKind, int? TargetSegmentIndex, ushort? TargetOffset, ushort? EntryOrdinal, NeImportReference? Import, IReadOnlyList<ushort> SourceOffsets, int PatchWidth, bool Supported, string? Diagnostic)
{
    public bool Additive => (RawFlags & 4) != 0;
}

/// <summary>Parses symbolic fixups and chains from original bytes, before any selector assignment.</summary>
internal static class NeRelocationReader
{
    public static IReadOnlyList<NeRelocation> Read(byte[] image, NeExecutable executable)
    {
        var records = new List<NeRelocation>();
        var modules = executable.ModuleReferences.ToDictionary(m => m.Index);
        var names = executable.ImportedNames.ToDictionary(n => n.TableOffset);
        var entries = executable.EntryPoints.ToDictionary(e => e.Ordinal);
        foreach (var segment in executable.Segments.Where(s => s.HasRelocations))
        {
            int start = segment.FileOffset + segment.FileLength;
            int count = Word(image, start);
            var data = new byte[segment.MemoryLength];
            image.AsSpan(segment.FileOffset, segment.FileLength).CopyTo(data);
            var claimed = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int file = start + 2 + i * 8;
                byte addressType = image[file], flags = image[file + 1];
                ushort source = Word(image, file + 2), a = Word(image, file + 4), b = Word(image, file + 6);

                InvalidDataException Error(string message) => new($"NE relocation at file 0x{file:X}, {segment.Index:X4}:{source:X4}: {message}.");

                int width = addressType switch
                {
                    0 => 1,
                    2 or 5 => 2,
                    3 => 4,
                    _ => 0
                };
                bool supported = width != 0 && (flags & ~7) == 0 && (flags & 3) != 3;
                string? diagnostic = supported
                    ? null
                    : $"Unsupported relocation address type 0x{addressType:X2} or flags 0x{flags:X2}";
                NeRelocationTargetKind kind;
                int? targetSegment = null;
                ushort? targetOffset = null, ordinal = null;
                NeImportReference? import = null;
                switch (flags & 3)
                {
                    case 0:
                        if ((a & 255) == 255)
                        {
                            kind = NeRelocationTargetKind.InternalEntry;
                            ordinal = b;
                            if (!entries.TryGetValue(b, out var entry) || entry.SegmentIndex is null || entry.Offset is null)
                            {
                                throw Error($"Invalid internal entry ordinal {b}");
                            }

                            targetSegment = entry.SegmentIndex;
                            targetOffset = entry.Offset;
                        }
                        else
                        {
                            kind = NeRelocationTargetKind.InternalSegment;
                            targetSegment = a & 255;
                            targetOffset = b;
                        }

                        if (targetSegment < 1 || targetSegment > executable.Segments.Count)
                        {
                            throw Error($"Invalid target segment {targetSegment}");
                        }

                        if (addressType != 2 && targetOffset >= executable.Segments[targetSegment!.Value - 1].MemoryLength)
                        {
                            throw Error($"Target offset {targetOffset:X4} exceeds segment {targetSegment}");
                        }

                        break;
                    case 1:
                    case 2:
                        if (!modules.TryGetValue(a, out var module))
                        {
                            throw Error($"Invalid module reference {a}");
                        }

                        if ((flags & 3) == 1)
                        {
                            if (b == 0)
                            {
                                throw Error("Invalid import ordinal 0");
                            }

                            kind = NeRelocationTargetKind.ImportOrdinal;
                            import = new(a, module.Name, null, b);
                        }
                        else
                        {
                            if (!names.TryGetValue(b, out var name) || name.Name.Length == 0)
                            {
                                throw Error($"Invalid imported name offset {b:X4}");
                            }

                            kind = NeRelocationTargetKind.ImportName;
                            import = new(a, module.Name, name.Name, null);
                        }

                        break;
                    default:
                        kind = NeRelocationTargetKind.OsFixup;
                        break;
                }

                var sites = new List<ushort>();
                var visited = new HashSet<ushort>();
                ushort current = source;
                while (true)
                {
                    int required = (flags & 4) != 0
                        ? Math.Max(width, 1)
                        : Math.Max(width, 2);
                    if (current > data.Length - required)
                    {
                        throw Error($"Out-of-range source site {current:X4}");
                    }

                    if (!visited.Add(current))
                    {
                        throw Error($"Cyclic source chain at {current:X4}");
                    }

                    sites.Add(current);
                    // Overlapping records cannot be applied independently without an explicit policy.
                    for (int n = 0; n < Math.Max(width, 1); n++)
                    {
                        if (!claimed.Add(current + n))
                        {
                            throw Error($"Overlapping relocation at {current:X4}");
                        }
                    }

                    if ((flags & 4) != 0)
                    {
                        break;
                    }

                    ushort next = Word(data, current);
                    if (next == ushort.MaxValue)
                    {
                        break;
                    }

                    current = next;
                }

                if (addressType == 2 && (flags & 4) != 0 && Word(data, source) != 0)
                {
                    supported = false;
                    diagnostic = "Additive selector with nonzero addend is unsupported";
                }

                records.Add(new(file, segment.Index, addressType, flags, source, a, b, kind, targetSegment, targetOffset, ordinal, import, sites.AsReadOnly(), width, supported, diagnostic));
            }
        }

        return records.AsReadOnly();
    }

    private static ushort Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
}
