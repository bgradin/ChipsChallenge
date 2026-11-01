using System.Buffers.Binary;
using System.Buffers;
using System.Runtime.InteropServices;
using Translator.IR;

namespace Win16_Runtime;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MachineState
{
    public const int AbiVersion = 2;
    public const int AbiSize = 44;
    public ushort AX, BX, CX, DX, SI, DI, BP, SP, IP, CS, DS, ES, SS, Flags;
    // Append high halves: the established 28-byte word-state ABI retains its offsets.
    public ushort AXHigh, BXHigh, CXHigh, DXHigh, SIHigh, DIHigh, BPHigh, SPHigh;
    public uint EAX
    {
        readonly get => (uint)AX | ((uint)AXHigh << 16);
        set
        {
            AX = (ushort)value;
            AXHigh = (ushort)(value >> 16);
        }
    }

    public uint EBX
    {
        readonly get => (uint)BX | ((uint)BXHigh << 16);
        set
        {
            BX = (ushort)value;
            BXHigh = (ushort)(value >> 16);
        }
    }

    public uint ECX
    {
        readonly get => (uint)CX | ((uint)CXHigh << 16);
        set
        {
            CX = (ushort)value;
            CXHigh = (ushort)(value >> 16);
        }
    }

    public uint EDX
    {
        readonly get => (uint)DX | ((uint)DXHigh << 16);
        set
        {
            DX = (ushort)value;
            DXHigh = (ushort)(value >> 16);
        }
    }

    public uint ESI
    {
        readonly get => (uint)SI | ((uint)SIHigh << 16);
        set
        {
            SI = (ushort)value;
            SIHigh = (ushort)(value >> 16);
        }
    }

    public uint EDI
    {
        readonly get => (uint)DI | ((uint)DIHigh << 16);
        set
        {
            DI = (ushort)value;
            DIHigh = (ushort)(value >> 16);
        }
    }

    public uint EBP
    {
        readonly get => (uint)BP | ((uint)BPHigh << 16);
        set
        {
            BP = (ushort)value;
            BPHigh = (ushort)(value >> 16);
        }
    }

    public uint ESP
    {
        readonly get => (uint)SP | ((uint)SPHigh << 16);
        set
        {
            SP = (ushort)value;
            SPHigh = (ushort)(value >> 16);
        }
    }
}

public sealed class Win16Segment : IDisposable
{
    private sealed class Storage(Memory<byte> bytes, IMemoryOwner<byte>? owner, bool executable)
    {
        public Memory<byte> Bytes = bytes;
        public IMemoryOwner<byte>? Owner = owner;
        public readonly bool Executable = executable;
        public int References = 1;

        public void Release()
        {
            if (--References != 0)
            {
                return;
            }

            if (Owner is null)
            {
                Bytes.Span.Clear();
            }
            else
            {
                Owner.Dispose();
            }
        }
    }

    private readonly Storage _storage;
    private readonly int _origin;
    private readonly int? _limit;
    public bool IsDisposed
    {
        get; private set;
    }
    public ushort Selector
    {
        get;
    }
    public bool CanRead
    {
        get;
    }
    public bool CanWrite
    {
        get;
    }
    public bool IsExecutable => _storage.Executable;
    public bool IsAlias
    {
        get;
    }
    public int Size => Math.Max(0, Math.Min(_limit ?? 65536, _storage.Bytes.Length - _origin));

    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            return _storage.Bytes.Slice(Math.Min(_origin, _storage.Bytes.Length), Size);
        }
    }

    internal Span<byte> Buffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _storage.Bytes.Span.Slice(Math.Min(_origin, _storage.Bytes.Length), Size);
        }
    }

    internal object StorageIdentity => _storage;
    internal int Origin => _origin;

    public Win16Segment(ushort selector, int size = 0x10000, bool canRead = true, bool canWrite = true, byte[]? backing = null, bool executable = false)
    {
        if (size is < 1 or > 65536 || backing is not null && backing.Length != size)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        if (executable && canWrite)
        {
            throw new ArgumentException("Executable original storage cannot be writable.");
        }

        Selector = selector;
        CanRead = canRead;
        CanWrite = canWrite;
        _storage = new(backing ?? new byte[size], null, executable);
    }

    internal Win16Segment(ushort selector, int size, bool canRead, bool canWrite, IMemoryOwner<byte> owner, bool executable = false)
    {
        if (size is < 1 or > 65536 || executable && canWrite)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        Selector = selector;
        CanRead = canRead;
        CanWrite = canWrite;
        _storage = new(owner.Memory.Slice(0, size), owner, executable);
    }

    internal Win16Segment(ushort selector, Win16Segment source, int offset, int? length, bool canRead, bool canWrite)
    {
        ObjectDisposedException.ThrowIf(source.IsDisposed, source);
        if (offset < 0 || offset >= source.Size || length is < 1 || (long)offset + (length ?? source.Size - offset) > source.Size || canRead && !source.CanRead || canWrite && (!source.CanWrite || source.IsExecutable))
        {
            throw new ArgumentException("Alias range or permissions exceed the source view.");
        }

        Selector = selector;
        CanRead = canRead;
        CanWrite = canWrite;
        IsAlias = true;
        _storage = source._storage;
        _origin = source._origin + offset;
        _limit = length ?? (offset == 0 && source._limit is null
            ? null
            : source.Size - offset);
        _storage.References++;
    }

    internal void ReplacePayload(Win16Segment replacement)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsAlias || _origin != 0 || IsExecutable || replacement.IsExecutable || replacement.IsAlias || replacement._storage.References != 1)
        {
            throw new InvalidOperationException("Only owned data backing can be resized.");
        }

        // Swap only after allocation/copy succeeded. Every existing alias follows the
        // same storage identity; disposing replacement releases the previous payload.
        (_storage.Bytes, replacement._storage.Bytes) = (replacement._storage.Bytes, _storage.Bytes);
        (_storage.Owner, replacement._storage.Owner) = (replacement._storage.Owner, _storage.Owner);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _storage.Release();
    }
}

public readonly record struct FarPointer(ushort Offset, ushort Segment)
{
    public bool IsNull => Offset == 0 && Segment == 0;

    public FarPointer Add(int amount) => new(unchecked((ushort)(Offset + amount)), Segment);
}

public readonly record struct Win16MemoryReference(SegmentMemory Memory, ushort Segment, ushort Offset, RegisterWidth? Width = null)
{
    public byte ReadByte() => Memory.ReadByte(Segment, Offset);

    public ushort ReadWord() => Memory.ReadWord(Segment, Offset);

    public uint ReadDword() => Memory.ReadDword(Segment, Offset);

    public void WriteByte(byte value) => Memory.WriteByte(Segment, Offset, value);

    public void WriteWord(ushort value) => Memory.WriteWord(Segment, Offset, value);

    public void WriteDword(uint value) => Memory.WriteDword(Segment, Offset, value);

    public uint ReadValue() => Width switch
    {
        RegisterWidth.Byte => ReadByte(),
        RegisterWidth.Word => ReadWord(),
        RegisterWidth.Dword => ReadDword(),
        _ => throw new InvalidOperationException("This memory reference has no resolved operand width.")
    };

    public void WriteValue(uint value)
    {
        switch (Width)
        {
            case RegisterWidth.Byte when value <= byte.MaxValue:
                WriteByte((byte)value);
                break;
            case RegisterWidth.Word when value <= ushort.MaxValue:
                WriteWord((ushort)value);
                break;
            case RegisterWidth.Dword:
                WriteDword(value);
                break;
            case RegisterWidth.Byte:
                throw new ArgumentOutOfRangeException(nameof(value), "Value does not fit in a byte operand.");
            case RegisterWidth.Word:
                throw new ArgumentOutOfRangeException(nameof(value), "Value does not fit in a word operand.");
            default:
                throw new InvalidOperationException("This memory reference has no resolved operand width.");
        }
    }
}

public sealed record Win16ExecutableWriteFault(ushort Selector, ushort Offset, int Bytes, bool ThroughAlias);
/// <summary>Selector based memory. Selectors are identifiers, never host addresses.</summary>
public sealed class SegmentMemory
{
    private readonly Dictionary<ushort, Win16Segment> _segments = new();
    private readonly IWin16MemoryAllocator? _allocator;
    private readonly HashSet<ushort> _issuedSelectors = new();
    private int _nextDynamicSelector = 0x5000;
    private readonly List<Win16ExecutableWriteFault> _executableWriteFaults = new();
    public IReadOnlyList<Win16ExecutableWriteFault> ExecutableWriteFaults => _executableWriteFaults.AsReadOnly();
    public string Backend => _allocator?.Name ?? "Managed";

    public SegmentMemory(IWin16MemoryAllocator? allocator = null) => _allocator = allocator;

    public Win16Segment CreateSegment(ushort selector, int size = 65536, bool canRead = true, bool canWrite = true, ReadOnlySpan<byte> initialized = default, bool executable = false)
    {
        if (size is < 1 or > 65536 || initialized.Length > size)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        Win16Segment segment;
        if (_allocator is null)
        {
            segment = new(selector, size, canRead, canWrite, executable: executable);
        }
        else
        {
            var storage = _allocator.Allocate(size);
            try
            {
                segment = new(selector, size, canRead, canWrite, storage, executable);
            }
            catch
            {
                storage.Dispose();
                throw;
            }
        }

        try
        {
            segment.Buffer.Clear();
            initialized.CopyTo(segment.Buffer);
            return segment;
        }
        catch
        {
            segment.Dispose();
            throw;
        }
    }

    public IReadOnlyCollection<Win16Segment> Segments => _segments.Values;

    public void Map(Win16Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ObjectDisposedException.ThrowIf(segment.IsDisposed, segment);
        _issuedSelectors.Add(segment.Selector);
        if (!_segments.TryAdd(segment.Selector, segment))
        {
            throw new InvalidOperationException($"Segment 0x{segment.Selector:X4} is already mapped.");
        }
    }

    internal void ReserveSelector(ushort selector) => _issuedSelectors.Add(selector);

    public ushort AllocateSelector()
    {
        while (_nextDynamicSelector <= 65535)
        {
            ushort candidate = (ushort)_nextDynamicSelector++;
            if (candidate is 0xf000 or 0xfff8 || !_issuedSelectors.Add(candidate))
            {
                continue;
            }

            return candidate;
        }

        return 0;
    }

    public Win16Segment CreateAlias(ushort selector, ushort sourceSelector, int offset = 0, int? length = null, bool? canRead = null, bool? canWrite = null)
    {
        var source = GetSegment(sourceSelector);
        if (_segments.ContainsKey(selector) || selector is 0 or 0xf000 or 0xfff8)
        {
            throw new InvalidOperationException("Alias selector is reserved or already mapped.");
        }

        var alias = new Win16Segment(selector, source, offset, length, canRead ?? source.CanRead, canWrite ?? source.CanWrite);
        try
        {
            Map(alias);
            return alias;
        }
        catch
        {
            alias.Dispose();
            throw;
        }
    }

    public bool SharesOrigin(ushort first, ushort second)
    {
        var a = GetSegment(first);
        var b = GetSegment(second);
        return ReferenceEquals(a.StorageIdentity, b.StorageIdentity) && a.Origin == b.Origin;
    }

    public void ResizeDataSegment(ushort selector, int bytes)
    {
        var segment = GetSegment(selector);
        if (!segment.CanWrite || segment.IsExecutable || segment.IsAlias)
        {
            throw new InvalidOperationException("Only owned writable data segments can resize.");
        }

        using var replacement = CreateSegment(selector, bytes);
        segment.Memory.Span[..Math.Min(segment.Size, bytes)].CopyTo(replacement.Buffer);
        segment.ReplacePayload(replacement);
    }

    internal void ReplaceBacking(Win16Segment expected, Win16Segment replacement)
    {
        if (!ReferenceEquals(GetSegment(expected.Selector), expected) || expected.Selector != replacement.Selector)
        {
            throw new InvalidOperationException("Backing replacement belongs to another mapping.");
        }

        expected.ReplacePayload(replacement);
    }

    internal void InvalidateAliases(Win16Segment owner)
    {
        foreach (var alias in _segments.Values.Where(s => s.IsAlias && ReferenceEquals(s.StorageIdentity, owner.StorageIdentity)).ToArray())
        {
            _segments.Remove(alias.Selector);
            alias.Dispose();
        }
    }

    private void RejectExecutableWrite(Win16Segment segment, ushort offset, int bytes)
    {
        if (!segment.IsExecutable)
        {
            return;
        }

        if (_executableWriteFaults.Count < 1024)
        {
            _executableWriteFaults.Add(new(segment.Selector, offset, bytes, segment.IsAlias));
        }

        throw new InvalidOperationException($"Executable-memory write rejected at {segment.Selector:X4}:{offset:X4}, length {bytes}; static code may not be silently modified.");
    }

    public void ValidateBufferWrite(ushort selector, ushort offset, int bytes)
    {
        if (bytes < 0 || bytes > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        if (bytes == 0)
        {
            return;
        }

        var segment = GetSegment(selector);
        RejectExecutableWrite(segment, offset, bytes);
        if (!segment.CanWrite || bytes > segment.Size || offset > segment.Size - bytes)
        {
            throw new InvalidDataException("Win16 API output exceeds its writable segment view.");
        }
    }

    public bool Unmap(ushort selector) => _segments.Remove(selector);

    public bool TryGetSegment(ushort selector, out Win16Segment? segment) => _segments.TryGetValue(selector, out segment);
    public Win16Segment GetSegment(ushort selector) => _segments.TryGetValue(selector, out var segment)
        ? segment
        : throw new InvalidOperationException($"Win16 segment 0x{selector:X4} is not mapped.");

    public byte ReadByte(ushort segment, ushort offset) => Access(segment, offset, false)[0];

    public void WriteByte(ushort segment, ushort offset, byte value) => Access(segment, offset, true)[0] = value;

    public ushort ReadWord(ushort segment, ushort offset) => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(segment, offset, 2));

    public uint ReadDword(ushort segment, ushort offset) => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(segment, offset, 4));

    public void WriteWord(ushort segment, ushort offset, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        WriteBytes(segment, offset, bytes);
    }

    public void WriteDword(ushort segment, ushort offset, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteBytes(segment, offset, bytes);
    }

    public byte[] ReadBytes(ushort segment, ushort offset, int count)
    {
        if (count < 0 || count > 0x10000)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var result = new byte[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = ReadByte(segment, unchecked((ushort)(offset + i)));
        }

        return result;
    }

    public void WriteBytes(ushort segment, ushort offset, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 0x10000)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        var mapped = GetSegment(segment);
        RejectExecutableWrite(mapped, offset, bytes.Length);
        if (!mapped.CanWrite)
        {
            throw new InvalidOperationException($"Win16 segment 0x{segment:X4} is read-only.");
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            ushort current = unchecked((ushort)(offset + i));
            if (current >= mapped.Size)
            {
                throw new InvalidOperationException($"Offset 0x{current:X4} is outside segment 0x{segment:X4} (size 0x{mapped.Size:X}).");
            }
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            mapped.Buffer[unchecked((ushort)(offset + i))] = bytes[i];
        }
    }

    public ushort ReadFarPointerOffset(ushort segment, ushort offset) => ReadWord(segment, offset);

    public ushort ReadFarPointerSegment(ushort segment, ushort offset) => ReadWord(segment, unchecked((ushort)(offset + 2)));

    public FarPointer ReadFarPointer(ushort segment, ushort offset) => new(ReadFarPointerOffset(segment, offset), ReadFarPointerSegment(segment, offset));

    public void WriteFarPointerOffset(ushort segment, ushort offset, ushort value) => WriteWord(segment, offset, value);

    public void WriteFarPointer(ushort segment, ushort offset, FarPointer pointer)
    {
        WriteDword(segment, offset, (uint)pointer.Segment << 16 | pointer.Offset);
    }

    public Win16MemoryReference Resolve(FarPointer pointer)
    {
        GetSegment(pointer.Segment);
        return new(this, pointer.Segment, pointer.Offset);
    }

    private Span<byte> Access(ushort selector, ushort offset, bool write)
    {
        var segment = GetSegment(selector);
        if (write)
        {
            RejectExecutableWrite(segment, offset, 1);
        }

        if (write && !segment.CanWrite)
        {
            throw new InvalidOperationException($"Win16 segment 0x{selector:X4} is read-only.");
        }

        if (!write && !segment.CanRead)
        {
            throw new InvalidOperationException($"Win16 segment 0x{selector:X4} is not readable.");
        }

        if (offset >= segment.Size)
        {
            throw new InvalidOperationException($"Offset 0x{offset:X4} is outside segment 0x{selector:X4} (size 0x{segment.Size:X}).");
        }

        return segment.Buffer.Slice(offset, 1);
    }
}

public static class EffectiveAddress
{
    public static ushort Calculate(ushort? @base, ushort? index, int displacement) => unchecked((ushort)((@base ?? 0) + (index ?? 0) + displacement));
    public static ushort DefaultSegment(MachineState state, bool usesBp) => usesBp
        ? state.SS
        : state.DS;

    public static bool IsLegal8086Addressing(X86Register? @base, X86Register? index)
    {
        if (@base is not null && @base is not (X86Register.BX or X86Register.BP or X86Register.SI or X86Register.DI))
        {
            return false;
        }

        if (index is not null && index is not (X86Register.BX or X86Register.BP or X86Register.SI or X86Register.DI))
        {
            return false;
        }

        if (@base is null || index is null)
        {
            return true;
        }

        bool bxSi = @base == X86Register.BX && index == X86Register.SI || @base == X86Register.SI && index == X86Register.BX;
        bool bxDi = @base == X86Register.BX && index == X86Register.DI || @base == X86Register.DI && index == X86Register.BX;
        bool bpSi = @base == X86Register.BP && index == X86Register.SI || @base == X86Register.SI && index == X86Register.BP;
        bool bpDi = @base == X86Register.BP && index == X86Register.DI || @base == X86Register.DI && index == X86Register.BP;
        return bxSi || bxDi || bpSi || bpDi;
    }

    public static ushort GetRegister16(MachineState state, X86Register register) => register switch
    {
        X86Register.AX => state.AX,
        X86Register.BX => state.BX,
        X86Register.CX => state.CX,
        X86Register.DX => state.DX,
        X86Register.SI => state.SI,
        X86Register.DI => state.DI,
        X86Register.BP => state.BP,
        X86Register.SP => state.SP,
        X86Register.CS => state.CS,
        X86Register.DS => state.DS,
        X86Register.ES => state.ES,
        X86Register.SS => state.SS,
        _ => throw new ArgumentException($"{register} is not a 16-bit register view.", nameof(register))
    };
    public static Win16MemoryReference Resolve(SegmentMemory memory, MachineState state, MemoryReference operand, Func<X86Register, ushort>? registerValue = null, Func<string, int>? symbolResolver = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(operand);
        if (!IsLegal8086Addressing(operand.Base, operand.Index))
        {
            throw new ArgumentException($"Illegal 8086 effective-address register pair: {operand.Base}+{operand.Index}.", nameof(operand));
        }

        registerValue ??= register => GetRegister16(state, register);
        int symbolDisplacement = 0;
        if (operand.Symbol is not null)
        {
            if (symbolResolver is null)
            {
                throw new InvalidOperationException($"Memory symbol expression '{operand.Symbol}' requires a symbol resolver.");
            }

            symbolDisplacement = symbolResolver(operand.Symbol);
        }

        ushort offset = Calculate(operand.Base is null
            ? null
            : registerValue(operand.Base.Value), operand.Index is null
            ? null
            : registerValue(operand.Index.Value), operand.Displacement + symbolDisplacement);
        bool usesBp = operand.Base == X86Register.BP || operand.Index == X86Register.BP;
        ushort segment = SelectSegment(state, operand.Segment, usesBp);
        memory.GetSegment(segment);
        return new(memory, segment, offset, operand.Width);
    }

    public static ushort SelectSegment(MachineState state, string? segmentOverride, bool usesBp)
    {
        if (segmentOverride is null)
        {
            return DefaultSegment(state, usesBp);
        }

        return segmentOverride.ToUpperInvariant() switch
        {
            "CS" => state.CS,
            "DS" => state.DS,
            "ES" => state.ES,
            "SS" => state.SS,
            _ => throw new ArgumentException($"Unknown segment register '{segmentOverride}'.", nameof(segmentOverride))
        };
    }
}
