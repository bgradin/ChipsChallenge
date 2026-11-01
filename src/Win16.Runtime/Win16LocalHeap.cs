namespace Win16_Runtime;

/// <summary>Fixed local allocations are original DS offsets, never host addresses.
/// Metadata lives outside guest storage; API/LocalInit ABI adaptation is separate.</summary>
public sealed class Win16LocalHeap
{
    private readonly SegmentMemory _memory;
    private readonly ushort _selector;
    private readonly int _start;
    private int _end;
    private readonly SortedDictionary<int, int> _blocks = new();
    public ushort Selector => _selector;
    public int AllocationCount => _blocks.Count;

    public Win16LocalHeap(SegmentMemory memory, ushort selector, int start, int bytes)
    {
        var segment = memory.GetSegment(selector);
        if (!segment.CanWrite || start < 2 || (start & 1) != 0 || bytes < 0 || (long)start + bytes > segment.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        _memory = memory;
        _selector = selector;
        _start = start;
        _end = start + bytes;
    }

    public void ExtendTo(int limit)
    {
        if (limit < _end || limit > _memory.GetSegment(_selector).Size)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        _end = limit;
    }

    private static int Rounded(uint bytes) => bytes > 65534
        ? 0
        : ((int)Math.Max(bytes, 1) + 1) & ~1;
    private int Gap(int size)
    {
        int offset = _start;
        foreach (var b in _blocks)
        {
            if (b.Key - offset >= size)
            {
                return offset;
            }

            offset = b.Key + b.Value;
        }

        return _end - offset >= size
            ? offset
            : 0;
    }

    public ushort Allocate(uint bytes, ushort flags = 0)
    {
        // Observed fixed LMEM_FIXED/LMEM_ZEROINIT; movable local handles need a different ABI.
        if ((flags & ~0x60) != 0)
        {
            return 0;
        }

        int size = Rounded(bytes), offset = size == 0
            ? 0
            : Gap(size);
        var segment = _memory.GetSegment(_selector);
        if (offset == 0 || !segment.CanWrite || (long)offset + size > segment.Size)
        {
            return 0;
        }

        _blocks.Add(offset, size);
        if ((flags & 0x40) != 0)
        {
            _memory.WriteBytes(_selector, (ushort)offset, new byte[size]);
        }

        return (ushort)offset;
    }

    public int Size(ushort pointer) => _blocks.TryGetValue(pointer, out int size)
        ? size
        : 0;

    public bool Free(ushort pointer) => _blocks.Remove(pointer);

    public ushort Reallocate(ushort pointer, uint bytes, ushort flags = 0)
    {
        // LMEM_MOVEABLE permits moving a fixed block; LMEM_NOCOMPACT has no observable
        // compaction effect because this heap never moves unrelated fixed allocations.
        if ((flags & ~0x62) != 0 || !_blocks.TryGetValue(pointer, out int old))
        {
            return 0;
        }

        if (bytes == 0)
        {
            _blocks.Remove(pointer);
            return 0;
        }

        int size = Rounded(bytes);
        if (size == 0 || (long)pointer + old > _memory.GetSegment(_selector).Size)
        {
            return 0;
        }

        int next = _blocks.Keys.FirstOrDefault(k => k > pointer, _end);
        if (size <= next - pointer && (long)pointer + size <= _memory.GetSegment(_selector).Size)
        {
            if (size > old && (flags & 0x40) != 0)
            {
                _memory.WriteBytes(_selector, (ushort)(pointer + old), new byte[size - old]);
            }

            _blocks[pointer] = size;
            return pointer;
        }

        if ((flags & 2) == 0)
        {
            return 0;
        }

        ushort replacement = Allocate(bytes, (ushort)(flags & 0x40));
        if (replacement == 0)
        {
            return 0;
        }

        _memory.WriteBytes(_selector, replacement, _memory.ReadBytes(_selector, pointer, Math.Min(old, size)));
        _blocks.Remove(pointer);
        return replacement;
    }
}
