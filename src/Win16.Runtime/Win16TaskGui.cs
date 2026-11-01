using System.Runtime.InteropServices;
using System.Text.Json;

namespace Win16_Runtime;

public enum Win16GuiHandleKind
{
    Window,
    Dc,
    GdiObject,
    Menu,
    Cursor,
    Icon,
    Accelerator
}

public sealed record Win16GuiHandle(ushort Id, nint Native, Win16GuiHandleKind Kind, bool Owned, ushort Parent = 0);
/// <summary>One typed task namespace; native pointer values never become Win16 IDs.</summary>
public sealed class Win16GuiHandles
{
    private readonly Dictionary<ushort, Win16GuiHandle> _entries = new();
    private int _next = 1;
    public IReadOnlyCollection<Win16GuiHandle> Entries => _entries.Values;

    public ushort Add(nint native, Win16GuiHandleKind kind, bool owned = false, ushort parent = 0)
    {
        if (native == 0 || native == -1)
        {
            return 0;
        }

        var existing = _entries.Values.FirstOrDefault(e => e.Native == native && e.Kind == kind);
        if (existing is not null)
        {
            if (owned && !existing.Owned)
            {
                throw new InvalidOperationException("Borrowed GUI object cannot silently acquire ownership.");
            }

            return existing.Id;
        }

        if (_next >= 65535)
        {
            return 0;
        }

        ushort id = (ushort)_next++;
        _entries.Add(id, new(id, native, kind, owned, parent));
        return id;
    }

    public bool TryGet(ushort id, Win16GuiHandleKind kind, out Win16GuiHandle? entry)
    {
        if (_entries.TryGetValue(id, out entry) && entry.Kind == kind)
        {
            return true;
        }

        entry = null;
        return false;
    }

    public void TakeOwnership(ushort id)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            _entries[id] = entry with
            {
                Owned = true
            };
        }
        else
        {
            throw new InvalidOperationException("Unknown GUI object.");
        }
    }

    public void SetParent(ushort id, ushort parent)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            _entries[id] = entry with
            {
                Parent = parent
            };
        }
    }

    public void Forget(ushort id)
    {
        foreach (var child in _entries.Values.Where(e => e.Parent == id).Select(e => e.Id).ToArray())
        {
            Forget(child);
        }

        _entries.Remove(id);
    }
}

public interface IWin16GuiBackend
{
    nint Call(string module, string function, ReadOnlySpan<nint> arguments);
}

public sealed record Win16GuiPlan(string Module, ushort Ordinal, string NativeName, IReadOnlyList<string> Arguments, string Result, uint Failure, int ReleaseArgument)
{
    public string Identity => $"{Module}.#{Ordinal}";
}

/// <summary>Explicit Win16 scalar/handle/string adaptation. Complex message and structure
/// APIs have dedicated adapters; this is not generic Win16-to-Win32 forwarding.</summary>
public sealed class Win16TaskGui : IDisposable
{
    public static IReadOnlyList<Win16GuiPlan> Plans { get; } = Load();
    public static IReadOnlySet<string> Implemented { get; } = Plans.Select(p => p.Identity).ToHashSet(StringComparer.Ordinal);

    private readonly SegmentMemory _memory;
    private readonly IWin16GuiBackend _backend;
    private bool _disposed;
    public Win16GuiHandles Handles
    {
        get;
    }

    public Win16TaskGui(SegmentMemory memory, IWin16GuiBackend backend, Win16GuiHandles? handles = null)
    {
        _memory = memory;
        _backend = backend;
        Handles = handles ?? new();
    }

    private static IReadOnlyList<Win16GuiPlan> Load()
    {
        using var s = typeof(Win16TaskGui).Assembly.GetManifestResourceStream("Win16.Runtime.Win16GuiPlans.json")!;
        return Array.AsReadOnly(JsonSerializer.Deserialize<Win16GuiPlan[]>(s)!);
    }

    private static Win16GuiHandleKind Kind(char type) => type switch
    {
        'h' => Win16GuiHandleKind.Window,
        'd' => Win16GuiHandleKind.Dc,
        'g' => Win16GuiHandleKind.GdiObject,
        'm' => Win16GuiHandleKind.Menu,
        'c' => Win16GuiHandleKind.Cursor,
        _ => throw new InvalidDataException("Unknown GUI handle type.")
    };
    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var plan = Plans.SingleOrDefault(p => p.Identity == identity) ?? throw new NotSupportedException(identity);
        var contract = Win16ExportContracts.Get(identity);
        var snapshot = state;
        uint[] words = Enumerable.Range(0, contract.Arguments.Count).Select(i => contract.Argument(i, _memory, snapshot)).ToArray();
        var args = new nint[words.Length];
        var strings = new List<nint>();
        uint result = plan.Failure;
        bool valid = true;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string type = plan.Arguments[i];
                uint value = words[i];
                if (type == "s")
                {
                    args[i] = unchecked((short)value);
                }
                else if (type is "u" or "l")
                {
                    args[i] = (nint)(nuint)value;
                }
                else if (type == "text")
                {
                    string text = new Win16Marshaller(_memory).ReadAnsi(new((ushort)value, (ushort)(value >> 16)), 65536);
                    nint native = Marshal.StringToHGlobalUni(text);
                    strings.Add(native);
                    args[i] = native;
                }
                else
                {
                    bool owned = type.StartsWith("owned-", StringComparison.Ordinal);
                    char token = owned
                        ? type[6]
                        : type[0];
                    if (value == 0 && type.EndsWith('?'))
                    {
                        args[i] = 0;
                    }
                    else if (value <= 65535 && Handles.TryGet((ushort)value, Kind(token), out var handle) && (!owned || handle!.Owned))
                    {
                        args[i] = handle!.Native;
                    }
                    else
                    {
                        valid = false;
                        break;
                    }
                }
            }

            if (valid)
            {
                string module = plan.Module == "GDI" && plan.Ordinal == 128
                    ? "KERNEL32"
                    : plan.Module == "GDI"
                        ? "GDI32"
                        : "USER32";
                nint native = _backend.Call(module, plan.NativeName, args);
                // NUMCOLORS=-1 means true color on Win32. Legacy signed Win16
                // comparisons need a positive color count that fits in an int16.
                if (plan.Identity == "GDI.#80" && args[1] == 24 && unchecked((int)native) == -1)
                {
                    native = short.MaxValue;
                }

                if (plan.Result is "h" or "d" or "g" or "m" or "c" or "owned-d")
                {
                    bool owned = plan.Result.StartsWith("owned-", StringComparison.Ordinal);
                    char token = owned
                        ? plan.Result[6]
                        : plan.Result[0];
                    ushort parent = plan.NativeName is "GetDlgItem" or "GetSubMenu"
                        ? (ushort)words[0]
                        : (ushort)0;
                    result = Handles.Add(native, Kind(token), owned, parent);
                    if (owned && native != 0 && result == 0)
                    {
                        _backend.Call("GDI32", "DeleteDC", [native]);
                    }
                }
                else if (plan.Result == "muldiv")
                {
                    result = unchecked((ushort)(unchecked((int)(nuint)native) is >= short.MinValue and <= short.MaxValue
                        ? (short)unchecked((int)(nuint)native)
                        : (short)-1));
                }
                else
                {
                    result = unchecked((uint)(nuint)native);
                }

                if (plan.ReleaseArgument >= 0 && native != 0)
                {
                    Handles.Forget((ushort)words[plan.ReleaseArgument]);
                }
            }

            if (plan.Result != "void")
            {
                state.AX = (ushort)result;
                if (plan.Result == "long")
                {
                    state.DX = (ushort)(result >> 16);
                }
            }

            return contract.CleanupBytes;
        }
        finally
        {
            foreach (nint p in strings)
            {
                Marshal.FreeHGlobal(p);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var h in Handles.Entries.Where(e => e.Owned).OrderBy(e => e.Kind == Win16GuiHandleKind.Window
            ? 0
            : e.Kind == Win16GuiHandleKind.Dc
                ? 1
                : 2).ToArray())
        {
            string module = h.Kind is Win16GuiHandleKind.Dc or Win16GuiHandleKind.GdiObject
                ? "GDI32"
                : "USER32";
            string? function = h.Kind switch
            {
                Win16GuiHandleKind.Window => "DestroyWindow",
                Win16GuiHandleKind.Dc => "DeleteDC",
                Win16GuiHandleKind.GdiObject => "DeleteObject",
                Win16GuiHandleKind.Menu => "DestroyMenu",
                Win16GuiHandleKind.Icon => "DestroyIcon",
                Win16GuiHandleKind.Cursor => "DestroyCursor",
                Win16GuiHandleKind.Accelerator => "DestroyAcceleratorTable",
                _ => null
            };
            if (function is not null)
            {
                _backend.Call(module, function, [h.Native]);
            }

            Handles.Forget(h.Id);
        }
    }
}
