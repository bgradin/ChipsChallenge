namespace Win16_Runtime;

public sealed record Win16GlobalMemoryLimits(int MaxAllocations = 4096, long MaxBytes = 64 * 1024 * 1024);
public sealed record Win16GlobalAllocation(ushort Handle, ushort Selector, int Size, ushort LockCount);
/// <summary>Runtime-owned movable global blocks. Handles and selectors occupy separate namespaces.</summary>
public sealed class Win16GlobalMemoryServices : IDisposable
{
    private readonly SegmentMemory _memory;
    private readonly Win16GlobalMemoryLimits _limits;
    private readonly Dictionary<ushort, Win16GlobalAllocation> _allocations = new();
    private readonly Dictionary<ushort, Win16Segment> _ownedSegments = new();
    private int _nextHandle = 1;
    private bool _disposed;
    private readonly bool _wholeProgram;
    private readonly HashSet<ushort> _fixedHandles = new();

    public Win16GlobalMemoryServices(SegmentMemory memory, Win16CallDispatcher dispatcher, ushort serviceSegment = 0xF000, Win16GlobalMemoryLimits? limits = null, bool wholeProgram = false)
    {
        _wholeProgram = wholeProgram;
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        ArgumentNullException.ThrowIfNull(dispatcher);
        _limits = limits ?? new();
        if (_limits.MaxAllocations is < 1 or > 0x3FFF || _limits.MaxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }

        _memory.ReserveSelector(serviceSegment);
        foreach (var api in Win16ApiRegistry.All.Where(a => a.Module == "KERNEL" && a.Name is "GlobalAlloc" or "GlobalReAlloc" or "GlobalLock" or "GlobalUnlock"))
        {
            dispatcher.RegisterApi(api, serviceSegment, Dispatch);
        }
    }

    // Optional deterministic fault injection; normal failures are driven by limits and allocator availability.
    public bool FailNextAllocation
    {
        get; set;
    }
    public bool FailNextReallocation
    {
        get; set;
    }
    public int AllocationCount => _allocations.Count;
    public long AllocatedBytes
    {
        get; private set;
    }

    public bool TryGetAllocation(ushort handle, out Win16GlobalAllocation? allocation) => _allocations.TryGetValue(handle, out allocation);
    private ushort Dispatch(ref MachineState state, SegmentMemory memory, Win16CallDispatcher _, Win16CallContext call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var api = Win16ApiRegistry.Find(call.FunctionName)!;
        var stateCopy = state;

        uint Arg(string name) => api.ReadArgument(name, memory, stateCopy, call);

        switch (api.Name)
        {
            case "GlobalAlloc":
                state.AX = Allocate(Arg("bytes"), (ushort)Arg("flags"));
                break;
            case "GlobalReAlloc":
                state.AX = Reallocate((ushort)Arg("handle"), Arg("bytes"), (ushort)Arg("flags"));
                break;
            case "GlobalLock":
                var pointer = Lock((ushort)Arg("handle"));
                state.AX = pointer.Offset;
                state.DX = pointer.Segment;
                break;
            case "GlobalUnlock":
                state.AX = (ushort)(Unlock((ushort)Arg("handle"))
                    ? 1
                    : 0);
                break;
        }

        return api.CleanupBytes;
    }

    public ushort Allocate(uint bytes, ushort flags = 0x42)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_wholeProgram && (flags & 2) == 0 && bytes == 0)
        {
            bytes = 1;
        }

        bool fail = FailNextAllocation;
        FailNextAllocation = false;
        if (fail || !SupportedFlags(flags) || bytes > 65536 || _allocations.Count >= _limits.MaxAllocations || AllocatedBytes + bytes > _limits.MaxBytes || ((!_wholeProgram || (flags & 2) != 0) && _nextHandle > 0x3FFF))
        {
            return 0;
        }

        ushort selector = bytes == 0
            ? (ushort)0
            : NextSelector();
        if (bytes != 0 && selector == 0)
        {
            return 0;
        }

        ushort handle = _wholeProgram && (flags & 2) == 0
            ? selector
            : (ushort)_nextHandle;
        if (handle == 0)
        {
            return 0;
        }

        Win16Segment? owned = null;
        try
        {
            _allocations.EnsureCapacity(_allocations.Count + 1);
            _ownedSegments.EnsureCapacity(_ownedSegments.Count + 1);
            if (bytes != 0)
            {
                var segment = _memory.CreateSegment(selector, (int)bytes);
                owned = segment;
                _memory.Map(segment);
                _ownedSegments.Add(handle, segment);
            }

            _allocations.Add(handle, new(handle, selector, (int)bytes, 0));
        }
        catch (OutOfMemoryException)
        {
            if (owned is not null)
            {
                if (_memory.TryGetSegment(selector, out var current) && ReferenceEquals(current, owned))
                {
                    _memory.Unmap(selector);
                }

                owned.Dispose();
            }

            _ownedSegments.Remove(handle);
            return 0;
        }

        if (_wholeProgram && (flags & 2) == 0)
        {
            _fixedHandles.Add(handle);
        }
        else
        {
            _nextHandle++;
        }

        AllocatedBytes += bytes;
        return handle;
    }

    public ushort Reallocate(ushort handle, uint bytes, ushort flags = 2)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fixedHandles.Contains(handle) && bytes == 0)
        {
            bytes = 1;
        }

        bool fail = FailNextReallocation;
        FailNextReallocation = false;
        if (fail || !SupportedFlags(flags) || bytes > 65536 || !_allocations.TryGetValue(handle, out var old) || AllocatedBytes - old.Size + bytes > _limits.MaxBytes || (bytes == 0 && old.LockCount != 0))
        {
            return 0;
        }

        if (old.Size != 0 && !OwnsMapping(handle))
        {
            return 0;
        }

        // Locked blocks keep their selector; unlocked blocks remap and invalidate stale pointers.
        ushort selector = bytes == 0
            ? (ushort)0
            : (old.LockCount != 0 || _fixedHandles.Contains(handle))
                ? old.Selector
                : NextSelector();
        if (bytes != 0 && selector == 0)
        {
            return 0;
        }

        _ownedSegments.TryGetValue(handle, out var previous);
        Win16Segment? replacement = null;
        Win16GlobalAllocation updated;
        try
        {
            updated = old with
            {
                Selector = selector,
                Size = (int)bytes
            };
            _ownedSegments.EnsureCapacity(_ownedSegments.Count + 1);
            if (bytes != 0)
            {
                replacement = _memory.CreateSegment(selector, (int)bytes);
                if (old.Size != 0)
                {
                    _memory.GetSegment(old.Selector).Memory.Span[..Math.Min(old.Size, (int)bytes)].CopyTo(replacement.Buffer);
                }
            }

            // All allocations precede committing a replacement, including map-table growth.
            if (replacement is not null)
            {
                if (selector == old.Selector)
                {
                    _memory.ReplaceBacking(_ownedSegments[handle], replacement);
                    replacement.Dispose();
                    replacement = _ownedSegments[handle];
                    previous = null;
                }
                else
                {
                    _memory.Map(replacement);
                }
            }
        }
        catch (OutOfMemoryException)
        {
            replacement?.Dispose();
            return 0;
        }

        if (old.Selector != 0 && old.Selector != selector)
        {
            if (previous is not null)
            {
                _memory.InvalidateAliases(previous);
            }

            _memory.Unmap(old.Selector);
        }

        previous?.Dispose();
        if (replacement is not null)
        {
            _ownedSegments[handle] = replacement;
        }
        else
        {
            _ownedSegments.Remove(handle);
        }

        _allocations[handle] = updated;
        AllocatedBytes += (long)bytes - old.Size;
        return handle;
    }

    public FarPointer Lock(ushort handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_allocations.TryGetValue(handle, out var block) || block.Size == 0 || block.LockCount == ushort.MaxValue || !OwnsMapping(handle))
        {
            return default;
        }

        _allocations[handle] = block with
        {
            LockCount = (ushort)(block.LockCount + 1)
        };
        return new(0, block.Selector);
    }

    /// <summary>True if locks remain; false on final unlock, already unlocked or invalid handles.</summary>
    public bool Unlock(ushort handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_allocations.TryGetValue(handle, out var block) || block.LockCount == 0)
        {
            return false;
        }

        ushort locks = (ushort)(block.LockCount - 1);
        _allocations[handle] = block with
        {
            LockCount = locks
        };
        return locks != 0;
    }

    /// <summary>Host lifecycle operation; GlobalFree is not an imported API in this milestone.</summary>
    public bool Free(ushort handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_allocations.TryGetValue(handle, out var block) || block.LockCount != 0)
        {
            return false;
        }

        Release(block);
        return true;
    }

    // Win16 GlobalFree can release locked blocks. Keep the historical selective
    // host Free contract separate; original API dispatch uses this operation.
    internal bool FreeForWin16Api(ushort handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_allocations.TryGetValue(handle, out var block))
        {
            return true;
        }

        Release(block);
        return true;
    }

    private void Release(Win16GlobalAllocation block)
    {
        if (block.Selector != 0 && OwnsMapping(block.Handle))
        {
            _memory.InvalidateAliases(_ownedSegments[block.Handle]);
            _memory.Unmap(block.Selector);
        }

        if (_ownedSegments.Remove(block.Handle, out var owned))
        {
            owned.Dispose();
        }

        _allocations.Remove(block.Handle);
        _fixedHandles.Remove(block.Handle);
        AllocatedBytes -= block.Size;
    }

    private bool OwnsMapping(ushort handle) => _ownedSegments.TryGetValue(handle, out var owned) && _memory.TryGetSegment(owned.Selector, out var mapped) && ReferenceEquals(mapped, owned);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var block in _allocations.Values)
        {
            if (block.Selector != 0 && OwnsMapping(block.Handle))
            {
                _memory.InvalidateAliases(_ownedSegments[block.Handle]);
                _memory.Unmap(block.Selector);
            }
        }

        foreach (var owned in _ownedSegments.Values)
        {
            owned.Dispose();
        }

        _allocations.Clear();
        _ownedSegments.Clear();
        _fixedHandles.Clear();
        AllocatedBytes = 0;
        _disposed = true;
    }

    private bool SupportedFlags(ushort flags) => _wholeProgram
        ? (flags & ~0x62) == 0
        : (flags & 2) != 0 && (flags & ~0x42) == 0;

    private ushort NextSelector()
    {
        // All dynamic selector producers share one retirement namespace.
        return _memory.AllocateSelector();
    }
}
