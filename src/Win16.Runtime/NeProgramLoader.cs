using System.Buffers.Binary;
using Translator.Binary;

namespace Win16_Runtime;

public sealed record NeLoadOptions(ushort FirstSelector = 0x1000, int SelectorStride = 8, long MaximumBytes = 16 * 1024 * 1024, bool AllowIncompleteRelocations = false);
public sealed record NeSegmentMapping(int SegmentIndex, ushort Selector, int FileOffset, int InitializedLength, int AllocatedLength, ushort Flags, bool IsData, bool CanRead, bool CanWrite);
public sealed record NeStartupRegisters(ushort CS, ushort IP, ushort SS, ushort SP, ushort DS, ushort HeapSize, ushort StackSize, int? StackBottom, int? HeapOffset, string DataSegmentPolicy);
public sealed record NeAppliedRelocation(NeRelocation Relocation, string Status, Win16CodeAddress? Target, string? Diagnostic, IReadOnlyList<NeRelocationPatch> Patches)
{
    public ushort? ConstantValue
    {
        get; init;
    }
}

public sealed record NeRelocationPatch(ushort SourceOffset, string OriginalBytesHex, string ResultBytesHex);
public sealed record NeLoadedImageReport(string Sha256, int MachineStateAbiVersion, long AllocatedBytes, NeStartupRegisters InitialRegisters, IReadOnlyList<NeSegmentMapping> Segments, bool FullyRelocated, IReadOnlyList<NeAppliedRelocation> Relocations)
{
    public string MemoryBackend { get; init; } = "Managed";
    public IReadOnlyList<NeDiscoveredImport> Imports { get; init; } = Array.Empty<NeDiscoveredImport>();
}

/// <summary>Owns initialized Win16 mappings; no application code or import service is executed.</summary>
public sealed class NeLoadedProgram : IDisposable
{
    private readonly SegmentMemory _memory;
    private readonly List<Win16Segment> _owned;
    private bool _disposed;
    public NeLoadedImageReport Report
    {
        get;
    }
    public MachineState InitialState
    {
        get;
    }

    public SegmentMemory Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _memory;
        }
    }

    internal NeLoadedProgram(SegmentMemory memory, List<Win16Segment> owned, NeLoadedImageReport report, MachineState state)
    {
        _memory = memory;
        _owned = owned;
        Report = report;
        InitialState = state;
    }

    public ushort SelectorFor(int segmentIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Report.Segments.FirstOrDefault(s => s.SegmentIndex == segmentIndex)?.Selector ?? throw new ArgumentOutOfRangeException(nameof(segmentIndex));
    }

    public int SegmentIndexFor(ushort selector)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Report.Segments.FirstOrDefault(s => s.Selector == selector)?.SegmentIndex ?? throw new ArgumentOutOfRangeException(nameof(selector));
    }

    public Win16CodeAddress Resolve(int segmentIndex, ushort offset)
    {
        ushort selector = SelectorFor(segmentIndex);
        if (offset >= _memory.GetSegment(selector).Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return new(selector, offset);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var segment in _owned)
        {
            if (_memory.TryGetSegment(segment.Selector, out var current) && ReferenceEquals(current, segment))
            {
                _memory.InvalidateAliases(segment);
                _memory.Unmap(segment.Selector);
            }

            segment.Dispose();
        }

        _owned.Clear();
    }
}

/// <summary>Builds runtime storage from parsed metadata. Imports are explicit 16:16 bindings;
/// discovering their API contracts belongs to the import resolver, not this loader.</summary>
public sealed class NeProgramLoader
{
    public NeLoadedProgram Load(ReadOnlySpan<byte> image, NeLoadOptions? options = null, Func<NeImportReference, Win16CodeAddress?>? importResolver = null, SegmentMemory? memory = null, Func<NeImportReference, ushort?>? constantResolver = null)
    {
        // Take one immutable snapshot; parsing and copying must observe the same file bytes.
        byte[] original = image.ToArray();
        var executable = new NeExecutableReader().Read(original);
        try
        {
            return LoadParsed(original, executable, options ?? new(), importResolver, memory ?? new(), constantResolver);
        }
        catch (OutOfMemoryException error)
        {
            throw new InvalidDataException("NE program storage allocation failed; owned mappings were rolled back.", error);
        }
    }

    private static NeLoadedProgram LoadParsed(byte[] image, NeExecutable executable, NeLoadOptions options, Func<NeImportReference, Win16CodeAddress?>? importResolver, SegmentMemory memory, Func<NeImportReference, ushort?>? constantResolver)
    {
        if (options.FirstSelector == 0 || options.SelectorStride < 1 || options.MaximumBytes < 1)
        {
            throw new ArgumentException("Invalid selector assignment or memory limit.");
        }

        var header = executable.NeHeader;
        // Self-loading, iterated, huge and 32-bit segments require different image construction.
        if ((header.Flags & 0x800) != 0)
        {
            throw new NotSupportedException("NE self-loading modules are unsupported.");
        }

        foreach (var segment in executable.Segments)
        {
            if ((segment.Flags & (0x8 | 0x2000 | 0x4000 | 0x8000)) != 0)
            {
                throw new NotSupportedException($"NE segment {segment.Index:X4}: iterated, huge or 32-bit storage is unsupported (flags {segment.Flags:X4}).");
            }
        }

        if (header.AutoDataSegment != 0 && !executable.Segments[header.AutoDataSegment - 1].IsData)
        {
            throw new InvalidDataException("NE automatic data segment must be a data segment.");
        }

        if (header.InitialSs == 0 && header.InitialSp != 0)
        {
            throw new InvalidDataException("NE initial SP has no corresponding stack segment.");
        }

        if (header.AutoDataSegment != 0 && header.HeapSize != 0 && !executable.Segments[header.AutoDataSegment - 1].IsWritable)
        {
            throw new InvalidDataException("NE local heap requires a writable data segment.");
        }

        if (header.InitialSs != 0 && !executable.Segments[header.InitialSs - 1].IsWritable)
        {
            throw new InvalidDataException("NE initial stack requires a writable data segment.");
        }

        if (header.HeapSize != 0 && header.AutoDataSegment == 0 || header.StackSize != 0 && header.InitialSs == 0)
        {
            throw new InvalidDataException("NE heap or stack requirement has no corresponding segment.");
        }

        var mappings = new List<NeSegmentMapping>();
        long total = 0;
        int stackTop = 0;
        int? stackBottom = null, heapOffset = null;
        foreach (var segment in executable.Segments)
        {
            long selector = options.FirstSelector + (long)(segment.Index - 1) * options.SelectorStride;
            if (selector > ushort.MaxValue)
            {
                throw new InvalidDataException("NE selector space exhausted.");
            }

            if (memory.TryGetSegment((ushort)selector, out _))
            {
                throw new InvalidDataException($"NE selector collision at {selector:X4}.");
            }

            int size = segment.MemoryLength;
            if (segment.Index == header.InitialSs)
            {
                stackTop = header.InitialSp == 0
                    ? (size + header.StackSize) & ~1
                    : header.InitialSp;
                stackBottom = stackTop - header.StackSize;
                if (stackBottom < 0 || header.StackSize != 0 && stackBottom < segment.FileLength)
                {
                    throw new InvalidDataException("NE initial stack overlaps initialized bytes or exceeds storage.");
                }

                size = Math.Max(size + header.StackSize, stackTop);
            }

            if (segment.Index == header.AutoDataSegment)
            {
                heapOffset = size;
                size += header.HeapSize;
            }

            if (size > 65536)
            {
                throw new InvalidDataException($"NE segment {segment.Index:X4}: allocation including heap/stack exceeds 64 KiB.");
            }

            total += size;
            if (total > options.MaximumBytes)
            {
                throw new InvalidDataException("NE program image exceeds configured memory limit.");
            }

            mappings.Add(new(segment.Index, (ushort)selector, segment.FileOffset, segment.FileLength, size, segment.Flags, segment.IsData, segment.IsReadable, segment.IsWritable));
        }

        // Work in private buffers. A failed load never exposes half-applied segments.
        var buffers = mappings.ToDictionary(m => m.SegmentIndex, m => new byte[m.AllocatedLength]);
        foreach (var mapping in mappings)
        {
            image.AsSpan(mapping.FileOffset, mapping.InitializedLength).CopyTo(buffers[mapping.SegmentIndex]);
        }

        var byIndex = mappings.ToDictionary(m => m.SegmentIndex);
        var imports = new Dictionary<NeImportReference, Win16CodeAddress?>();
        var constants = new Dictionary<NeImportReference, ushort?>();
        var audit = new List<NeAppliedRelocation>();
        foreach (var record in executable.Relocations)
        {
            Win16CodeAddress? target = null;
            ushort? constantValue = null;
            string? diagnostic = record.Diagnostic;
            if (record.Supported)
            {
                if (record.Import is not null)
                {
                    if (!constants.TryGetValue(record.Import, out constantValue))
                    {
                        constantValue = constantResolver?.Invoke(record.Import);
                        constants.Add(record.Import, constantValue);
                    }

                    if (constantValue is { } value)
                    {
                        if (record.RawAddressType != 5)
                        {
                            throw new NotSupportedException("Constant export requires an explicit 16-bit offset fixup.");
                        }

                        target = new(0, value); // A value-only relocation, never a procedure address.
                    }
                    else
                    {
                        if (!imports.TryGetValue(record.Import, out target))
                        {
                            target = importResolver?.Invoke(record.Import);
                            if (target is { Segment: 0 })
                            {
                                throw new InvalidDataException($"NE import {record.Import.Identity}: a null selector is not a procedure binding.");
                            }

                            if (target is not null && byIndex.Values.Any(m => m.Selector == target.Value.Segment))
                            {
                                throw new InvalidDataException($"NE import {record.Import.Identity}: import selector collides with an owned NE segment.");
                            }

                            imports.Add(record.Import, target);
                        }
                    }

                    if (target is null)
                    {
                        diagnostic = $"Unresolved import {record.Import.Identity}; supply a 16:16 procedure binding";
                    }
                }
                else if (record.TargetSegmentIndex is int index)
                {
                    target = new(byIndex[index].Selector, record.TargetOffset!.Value);
                }
            }

            var patches = new List<NeRelocationPatch>();
            if (target is not null)
            {
                byte[] buffer = buffers[record.SourceSegmentIndex];
                foreach (ushort offset in record.SourceOffsets)
                {
                    Span<byte> site = buffer.AsSpan(offset, record.PatchWidth);
                    string before = Convert.ToHexString(site);
                    Apply(site, record, target.Value);
                    patches.Add(new(offset, before, Convert.ToHexString(site)));
                }
            }

            string status = !record.Supported
                ? "Unsupported"
                : target is null
                    ? "Unresolved"
                    : "Applied";
            audit.Add(new(record, status, constantValue is null
                ? target
                : null, diagnostic, patches.AsReadOnly())
            {
                ConstantValue = constantValue
            });
            if (status != "Applied" && !options.AllowIncompleteRelocations)
            {
                throw new InvalidDataException($"NE relocation {record.SourceSegmentIndex:X4}:{record.SourceOffset:X4}, file 0x{record.SourceFileOffset:X}: {diagnostic}.");
            }
        }

        ushort Selector(int index) => index == 0
            ? (ushort)0
            : byIndex[index].Selector;

        var state = new MachineState
        {
            CS = Selector(header.InitialCs),
            IP = header.InitialIp,
            SS = Selector(header.InitialSs),
            SP = unchecked((ushort)stackTop),
            DS = Selector(header.AutoDataSegment),
            BX = header.StackSize,
            CX = header.HeapSize,
            Flags = 2
        };
        var registers = new NeStartupRegisters(state.CS, state.IP, state.SS, state.SP, state.DS, header.HeapSize, header.StackSize, stackBottom, heapOffset, header.AutoDataSegment == 0
            ? "No automatic DS; DS=0"
            : "Single loaded module instance: DS=automatic data selector; function-entry DS conventions are not inferred");
        var report = new NeLoadedImageReport(executable.Sha256, MachineState.AbiVersion, total, registers, mappings.AsReadOnly(), audit.All(a => a.Status == "Applied"), audit.AsReadOnly())
        {
            MemoryBackend = memory.Backend
        };
        var owned = new List<Win16Segment>();
        try
        {
            foreach (var mapping in mappings)
            {
                var segment = memory.CreateSegment(mapping.Selector, mapping.AllocatedLength, mapping.CanRead, mapping.CanWrite, buffers[mapping.SegmentIndex], executable: !mapping.IsData);
                try
                {
                    owned.Add(segment);
                    memory.Map(segment);
                }
                catch
                {
                    segment.Dispose();
                    throw;
                }
            }

            return new(memory, owned, report, state);
        }
        catch
        {
            foreach (var segment in owned)
            {
                if (memory.TryGetSegment(segment.Selector, out var current) && ReferenceEquals(current, segment))
                {
                    memory.Unmap(segment.Selector);
                }

                segment.Dispose();
            }

            throw;
        }
    }

    private static void Apply(Span<byte> site, NeRelocation relocation, Win16CodeAddress target)
    {
        ushort Add(ushort original) => unchecked((ushort)(target.Offset + (relocation.Additive
            ? original
            : 0)));

        switch (relocation.RawAddressType)
        {
            case 0:
                site[0] = unchecked((byte)(target.Offset + (relocation.Additive
                    ? site[0]
                    : 0)));
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(site, target.Segment);
                break;
            case 5:
                BinaryPrimitives.WriteUInt16LittleEndian(site, Add(BinaryPrimitives.ReadUInt16LittleEndian(site)));
                break;
            case 3:
                BinaryPrimitives.WriteUInt16LittleEndian(site, Add(BinaryPrimitives.ReadUInt16LittleEndian(site)));
                // A far-pointer addend changes the offset only; its segment is replaced, without carry.
                BinaryPrimitives.WriteUInt16LittleEndian(site[2..], target.Segment);
                break;
            default:
                throw new NotSupportedException("Unsupported NE relocation reached the application stage.");
        }
    }
}
