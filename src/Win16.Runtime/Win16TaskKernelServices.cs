using System.Text;

namespace Win16_Runtime;

/// <summary>Task-scoped portable KERNEL services. All values cross an original-width ABI.
/// This does not register window, DOS-register, lifecycle or multimedia substitutes.</summary>
public sealed class Win16TaskKernelServices : IDisposable
{
    public static IReadOnlySet<string> Implemented { get; } = new HashSet<string>(new[] { "KERNEL.#5", "KERNEL.#6", "KERNEL.#7", "KERNEL.#10", "KERNEL.#15", "KERNEL.#16", "KERNEL.#17", "KERNEL.#18", "KERNEL.#19", "KERNEL.#20", "KERNEL.#23", "KERNEL.#24", "KERNEL.#88", "KERNEL.#90", "KERNEL.#107", "KERNEL.#131", "USER.#471" });

    private readonly Win16TaskMemory _task;
    private readonly SegmentMemory _memory;
    private readonly ushort _dataSelector;
    private readonly List<Win16Segment> _owned = new();
    private readonly Dictionary<ushort, int> _segmentLocks = new();
    private ushort _errorMode;
    private bool _disposed;
    private readonly Dictionary<string, long> _calls = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, long> Calls => new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(_calls);

    public Win16TaskKernelServices(Win16TaskMemory task, SegmentMemory memory, ushort dataSelector)
    {
        _task = task;
        _memory = memory;
        _dataSelector = dataSelector;
    }

    private void GrowLocalHeap()
    {
        int size = _memory.GetSegment(_dataSelector).Size;
        if (size < 65536)
        {
            _task.GrowDataSegment(65536);
        }
    }

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException($"No whole-task implementation for {identity}.");
        }

        var contract = Win16ExportContracts.Get(identity);
        var snapshot = state;
        var arguments = Enumerable.Range(0, contract.Arguments.Count).Select(index => contract.Argument(index, _memory, snapshot)).ToArray();

        uint Arg(int index) => arguments[index];

        if (identity is "KERNEL.#5" or "KERNEL.#6" or "KERNEL.#7" or "KERNEL.#10")
        {
            if (!_memory.SharesOrigin(state.DS, _dataSelector) || _memory.GetSegment(state.DS).Size != _memory.GetSegment(_dataSelector).Size)
            {
                throw new NotSupportedException("Local memory requires the current module's full DS heap view.");
            }
        }

        _calls[identity] = _calls.GetValueOrDefault(identity) + 1;
        uint result;
        switch (identity)
        {
            case "KERNEL.#5":
                result = _task.Local.Allocate(Arg(1), (ushort)Arg(0));
                if (result == 0 && Arg(1) <= 65534 && (Arg(0) & ~0x60u) == 0)
                {
                    GrowLocalHeap();
                    result = _task.Local.Allocate(Arg(1), (ushort)Arg(0));
                }

                break;
            case "KERNEL.#6":
                result = _task.Local.Reallocate((ushort)Arg(0), Arg(1), (ushort)Arg(2));
                if (result == 0 && Arg(1) != 0 && Arg(1) <= 65534 && (Arg(2) & ~0x62u) == 0 && _task.Local.Size((ushort)Arg(0)) != 0)
                {
                    GrowLocalHeap();
                    result = _task.Local.Reallocate((ushort)Arg(0), Arg(1), (ushort)Arg(2));
                }

                break;
            case "KERNEL.#7":
                result = Arg(0) == 0 || _task.Local.Free((ushort)Arg(0))
                    ? 0
                    : Arg(0);
                break;
            case "KERNEL.#10":
                result = (uint)_task.Local.Size((ushort)Arg(0));
                break;
            case "KERNEL.#15":
                result = _task.Global.Allocate(Arg(1), (ushort)Arg(0));
                break;
            case "KERNEL.#16":
                result = _task.Global.Reallocate((ushort)Arg(0), Arg(1), (ushort)Arg(2));
                break;
            case "KERNEL.#17":
                result = _task.Global.FreeForWin16Api((ushort)Arg(0))
                    ? 0
                    : Arg(0);
                break;
            case "KERNEL.#18":
                var pointer = _task.Global.Lock((ushort)Arg(0));
                result = (uint)pointer.Segment << 16 | pointer.Offset;
                break;
            case "KERNEL.#19":
                result = _task.Global.Unlock((ushort)Arg(0))
                    ? 1u
                    : 0;
                break;
            case "KERNEL.#20":
                result = _task.Global.TryGetAllocation((ushort)Arg(0), out var allocation)
                    ? (uint)allocation!.Size
                    : 0;
                break;
            case "KERNEL.#23":
            case "KERNEL.#24":
                ushort selector = (ushort)Arg(0);
                if (selector == 0xffff)
                {
                    selector = state.DS;
                }

                if (_task.Global.TryGetAllocation(selector, out var global))
                {
                    if (identity == "KERNEL.#23")
                    {
                        result = _task.Global.Lock(selector).IsNull
                            ? 0u
                            : selector;
                    }
                    else
                    {
                        _task.Global.Unlock(selector);
                        return contract.CleanupBytes;
                    }

                    break;
                }

                if (!_memory.TryGetSegment(selector, out _))
                {
                    result = 0;
                    break;
                }

                _segmentLocks.TryGetValue(selector, out int locks);
                if (identity == "KERNEL.#23")
                {
                    if (locks == 65535)
                    {
                        result = 0;
                        break;
                    }

                    _segmentLocks[selector] = locks + 1;
                    result = selector;
                }
                else
                {
                    if (locks > 0)
                    {
                        _segmentLocks[selector] = locks - 1;
                    }

                    return contract.CleanupBytes;
                }

                break;
            case "KERNEL.#88":
                byte[] source = ReadStringBytes(_memory, Arg(1));
                WriteBuffer(_memory, Arg(0), source);
                result = Arg(0);
                break;
            case "KERNEL.#90":
                result = (uint)ReadStringBytes(_memory, Arg(0)).Length - 1;
                break;
            case "USER.#471":
                string left = ReadString(_memory, Arg(0)), right = ReadString(_memory, Arg(1));
                if (left.Any(c => c >= 128) || right.Any(c => c >= 128))
                {
                    throw new NotSupportedException("Whole-task string comparison currently supports observed ASCII operands only.");
                }

                result = unchecked((ushort)(short)Math.Sign(string.Compare(left, right, StringComparison.OrdinalIgnoreCase)));
                break;
            case "KERNEL.#107":
                result = _errorMode;
                _errorMode = (ushort)Arg(0);
                break;
            case "KERNEL.#131":
                // Stable empty environment with a DOS environment trailer. Host environment
                // variables are not implicitly exposed to the original task.
                var existing = _owned.FirstOrDefault();
                if (existing is null)
                {
                    ushort selectorId = _memory.AllocateSelector();
                    if (selectorId == 0)
                    {
                        result = 0;
                        break;
                    }

                    try
                    {
                        _owned.EnsureCapacity(_owned.Count + 1);
                        existing = _memory.CreateSegment(selectorId, 8, true, false, new byte[] { 0, 0, 1, 0, 0, 0, 0, 0 });
                        _memory.Map(existing);
                        _owned.Add(existing);
                    }
                    catch (OutOfMemoryException)
                    {
                        if (existing is not null)
                        {
                            if (_memory.TryGetSegment(selectorId, out var mapped) && ReferenceEquals(mapped, existing))
                            {
                                _memory.Unmap(selectorId);
                            }

                            existing.Dispose();
                        }

                        result = 0;
                        goto Completed;
                    }
                }

                result = (uint)existing.Selector << 16;
                break;
            default:
                throw new NotSupportedException(identity);
        }

        // Word-return APIs do not arbitrarily clear DX. Far/long results use DX:AX,
        // independent of EAX high halves in the explicit CPU state.
    Completed:
        state.AX = (ushort)result;
        if (contract.ReturnBytes == 4)
        {
            state.DX = (ushort)(result >> 16);
        }

        return contract.CleanupBytes;
    }

    public static byte[] ReadStringBytes(SegmentMemory memory, uint pointer)
    {
        ushort selector = (ushort)(pointer >> 16);
        int offset = (ushort)pointer;
        var segment = memory.GetSegment(selector);
        if (!segment.CanRead || offset >= segment.Size)
        {
            throw new InvalidDataException("Invalid Win16 string view.");
        }

        ReadOnlySpan<byte> bytes = segment.Memory.Span[offset..];
        int end = bytes.IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException("Win16 ANSI string has no terminator before the segment limit.");
        }

        return bytes[..(end + 1)].ToArray();
    }

    public static string ReadString(SegmentMemory memory, uint pointer) => Encoding.Latin1.GetString(ReadStringBytes(memory, pointer).AsSpan()[..^1]);

    public static void WriteBuffer(SegmentMemory memory, uint pointer, ReadOnlySpan<byte> bytes)
    {
        ushort selector = (ushort)(pointer >> 16);
        int offset = (ushort)pointer;
        var segment = memory.GetSegment(selector);
        if (bytes.Length > segment.Size || offset > segment.Size - bytes.Length)
        {
            throw new InvalidDataException("Win16 API buffer crosses a segment limit.");
        }

        memory.WriteBytes(selector, (ushort)offset, bytes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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
        _segmentLocks.Clear();
        _disposed = true;
    }
}
