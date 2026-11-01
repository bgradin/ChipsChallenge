using Translator.Binary;

namespace Win16_Runtime;

/// <summary>NE resources keep resource-reference identities separate from global data handles.
/// Source bytes are copied, not executed; FreeResource returns zero on success.</summary>
public sealed class Win16TaskResources : IDisposable
{
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "KERNEL.#60",
        "KERNEL.#61",
        "KERNEL.#62",
        "KERNEL.#63"
    };

    private sealed class Entry(NeResource resource, ushort reference)
    {
        public NeResource Resource = resource;
        public ushort Reference = reference, DataHandle;
        public int Uses;
    }

    private readonly byte[] _image;
    private readonly SegmentMemory _memory;
    private readonly Win16GlobalMemoryServices _globals;
    private readonly ushort _instance;
    private readonly Entry[] _entries;
    private bool _disposed;

    public Win16TaskResources(byte[] image, SegmentMemory memory, Win16GlobalMemoryServices globals, ushort instance)
    {
        _image = image.ToArray();
        _memory = memory;
        _globals = globals;
        _instance = instance;
        var executable = new NeExecutableReader().Read(_image);
        if (executable.Resources.Count > 0x3fff)
        {
            throw new NotSupportedException("NE resource reference namespace exhausted.");
        }

        _entries = executable.Resources.Select((r, i) => new Entry(r, (ushort)(0x8000 + i))).ToArray();
    }

    private bool Matches(uint requested, NeResourceIdentity identity)
    {
        if ((requested >> 16) == 0)
        {
            return identity.Id == ((ushort)requested & 0x7fff);
        }

        string text = Win16TaskKernelServices.ReadString(_memory, requested);
        if (text.StartsWith('#') && ushort.TryParse(text.AsSpan(1), out ushort id))
        {
            return identity.Id == (id & 0x7fff);
        }

        return identity.Name?.Equals(text, StringComparison.OrdinalIgnoreCase) == true;
    }

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var contract = Win16ExportContracts.Get(identity);
        var copy = state;

        uint Arg(int n) => contract.Argument(n, _memory, copy);

        uint result = 0;
        switch (identity)
        {
            case "KERNEL.#60":
                uint module = Arg(0), name = Arg(1), type = Arg(2);
                if (module == 0 || module == _instance)
                {
                    result = _entries.FirstOrDefault(e => Matches(type, e.Resource.Type) && Matches(name, e.Resource.Identity))?.Reference ?? 0;
                }

                break;
            case "KERNEL.#61":
                module = Arg(0);
                ushort reference = (ushort)Arg(1);
                var entry = _entries.FirstOrDefault(e => e.Reference == reference);
                if ((module != 0 && module != _instance) || entry is null || entry.Uses == 65535)
                {
                    break;
                }

                if (entry.DataHandle == 0)
                {
                    if (entry.Resource.Length == 0)
                    {
                        break;
                    }

                    if (entry.Resource.Length > 65536)
                    {
                        throw new NotSupportedException("Huge NE resource needs a multi-selector resource view.");
                    }

                    ushort handle = _globals.Allocate((uint)entry.Resource.Length, 2);
                    if (handle == 0)
                    {
                        break;
                    }

                    var pointer = _globals.Lock(handle);
                    try
                    {
                        _memory.WriteBytes(pointer.Segment, 0, _image.AsSpan(entry.Resource.FileOffset, entry.Resource.Length));
                    }
                    catch
                    {
                        _globals.FreeForWin16Api(handle);
                        throw;
                    }

                    _globals.Unlock(handle);
                    entry.DataHandle = handle;
                }

                entry.Uses++;
                result = entry.DataHandle;
                break;
            case "KERNEL.#62":
                ushort data = (ushort)Arg(0);
                entry = _entries.FirstOrDefault(e => e.DataHandle != 0 && e.DataHandle == data);
                if (entry is null)
                {
                    break;
                }

                var p = _globals.Lock(data);
                result = (uint)p.Segment << 16 | p.Offset;
                break;
            case "KERNEL.#63":
                data = (ushort)Arg(0);
                entry = _entries.FirstOrDefault(e => e.DataHandle != 0 && e.DataHandle == data);
                if (entry is null)
                {
                    result = _globals.FreeForWin16Api(data)
                        ? 0u
                        : data;
                    break;
                }

                if (entry.Uses > 0)
                {
                    entry.Uses--;
                }

                if (entry.Uses == 0)
                {
                    _globals.FreeForWin16Api(entry.DataHandle);
                    entry.DataHandle = 0;
                }

                result = 0;
                break;
            default:
                throw new NotSupportedException(identity);
        }

        state.AX = (ushort)result;
        if (contract.ReturnBytes == 4)
        {
            state.DX = (ushort)(result >> 16);
        }

        return contract.CleanupBytes;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var e in _entries)
        {
            if (e.DataHandle != 0)
            {
                _globals.FreeForWin16Api(e.DataHandle);
            }

            e.DataHandle = 0;
            e.Uses = 0;
        }

        _disposed = true;
    }
}
