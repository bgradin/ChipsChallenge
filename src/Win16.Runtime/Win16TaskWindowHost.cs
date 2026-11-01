using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static Win16_Runtime.Win16NativeBuffers;

namespace Win16_Runtime;

/// <summary>A task-level USER host. All reverse entries select approved compiled procedures.
/// Window objects carry only OS adaptation metadata; application state stays in guest memory.</summary>
public sealed class Win16TaskWindowHost : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void TimerProcedure(nint window, uint message, nuint timer, uint ticks);
    private sealed record ClassInfo(string Name, uint Procedure, int Extra, ushort Instance, WindowProcedure Callback, uint Menu = 0);
    private sealed record WindowInfo(ClassInfo Class, ushort Id, Dictionary<int, ushort> Words);
    private sealed record MessageContext(nint Window, uint Message, nuint WParam, nint LParam, ushort GuestMessage, ushort GuestWParam, uint GuestLParam)
    {
        public bool NativeDefaultCalled
        {
            get; set;
        }
    };
    private sealed record PaintLease(nint Window, nint Structure, ushort Dc);
    private readonly SegmentMemory _memory;
    private readonly Win16TaskMemory _task;
    private readonly Win16ProgramControl _control;
    private readonly IWin16GuiBackend _backend;
    private readonly Win16GuiHandles _handles;
    private readonly Win16TaskGuiResources _resources;
    private readonly ushort _instance;
    private readonly Dictionary<string, ClassInfo> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, WindowInfo> _windows = [];
    private readonly Stack<ClassInfo> _creating = [];
    private readonly Stack<MessageContext> _messages = [];
    private readonly Dictionary<uint, PaintLease> _paints = [];
    private readonly Dictionary<(ushort, ushort), TimerProcedure> _timers = [];
    private readonly Dictionary<string, byte[]> _queued = [];
    private MachineState _suspended;
    private Exception? _callbackFailure;
    private bool _disposed;
    private int _depth;
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "USER.#10",
        "USER.#12",
        "USER.#39",
        "USER.#40",
        "USER.#41",
        "USER.#57",
        "USER.#66",
        "USER.#68",
        "USER.#87",
        "USER.#101",
        "USER.#107",
        "USER.#109",
        "USER.#110",
        "USER.#111",
        "USER.#113",
        "USER.#114",
        "USER.#133",
        "USER.#134",
        "USER.#178"
    };

    public Win16TaskWindowHost(SegmentMemory memory, Win16TaskMemory task, Win16ProgramControl control, IWin16GuiBackend backend, Win16GuiHandles handles, Win16TaskGuiResources resources, MachineState initial)
    {
        _memory = memory;
        _task = task;
        _control = control;
        _backend = backend;
        _handles = handles;
        _resources = resources;
        _instance = initial.DS;
        _suspended = initial;
    }

    private nint Call(string name, params nint[] args) => _backend.Call(name is "GetModuleHandleW"
        ? "KERNEL32"
        : "USER32", name, args);
    private nint Handle(uint id, Win16GuiHandleKind kind = Win16GuiHandleKind.Window) => id == 0
        ? 0
        : id <= 65535 && _handles.TryGet((ushort)id, kind, out var h)
            ? h!.Native
            : throw new InvalidDataException("Invalid typed GUI handle.");

    private ushort Window(nint native) => _handles.Add(native, Win16GuiHandleKind.Window);
    private uint Reverse(uint target, ReadOnlySpan<ushort> arguments, bool word = false)
    {
        if (++_depth > 32)
        {
            _depth--;
            throw new InvalidOperationException("Callback nesting exceeds its task bound.");
        }

        try
        {
            return _task.Callback(_control, ref _suspended, new((ushort)(target >> 16), (ushort)target), arguments, Win16CallbackStack.DataSegment, 8192, returnKind: word
                ? Win16CallbackReturn.Word
                : Win16CallbackReturn.DxAx);
        }
        finally
        {
            _depth--;
        }
    }

    public Func<bool>? IsTerminated
    {
        get; set;
    }
    public Func<uint, ushort>? MultimediaDeviceToGuest
    {
        get; set;
    }

    private (ushort Message, ushort WParam, uint LParam) ToGuest(nint window, uint message, nuint wParam, nint lParam)
    {
        ushort msg = checked((ushort)message), w = unchecked((ushort)wParam);
        uint l = unchecked((uint)(nuint)lParam);
        if (message == 0x111)
        {
            l = (uint)Window(lParam) | ((uint)(wParam >> 16) & 0xffff) << 16;
        }
        else if (message is >= 0x132 and <= 0x138)
        {
            msg = 0x19;
            w = _handles.Add((nint)wParam, Win16GuiHandleKind.Dc);
            l = (uint)Window(lParam) | ((message - 0x132) & 0xffff) << 16;
        }
        else if (message == 0x110)
        {
            w = Window((nint)wParam);
        }
        else if (message == 0x11f)
        {
            l = (uint)_handles.Add(lParam, Win16GuiHandleKind.Menu) | ((uint)(wParam >> 16) & 0xffff) << 16;
        }
        else if (message is 0x114 or 0x115)
        {
            l = (uint)(ushort)(wParam >> 16) | ((uint)Window(lParam) << 16);
        }
        else if (message == 0x3b9)
        {
            l = MultimediaDeviceToGuest?.Invoke(l) ?? (ushort)l;
        }
        else if (message == 6)
        {
            l = (uint)Window(lParam) | ((uint)(wParam >> 16) & 0xffff) << 16;
        }
        else if (message is 7 or 8 or 0x20 or 0x21)
        {
            w = Window((nint)wParam);
        }
        else if (message is 0x116 or 0x117)
        {
            w = _handles.Add((nint)wParam, Win16GuiHandleKind.Menu);
        }
        else if (message == 0x30)
        {
            w = _handles.Add((nint)wParam, Win16GuiHandleKind.GdiObject);
        }

        return (msg, w, l);
    }

    private nint Procedure(ClassInfo? selected, nint window, uint message, nuint wParam, nint lParam, bool dialog)
    {
        if (IsTerminated?.Invoke() == true)
        {
            return dialog
                ? 0
                : Call("DefWindowProcW", window, (nint)message, (nint)wParam, lParam);
        }

        uint scratch = 0;
        ushort lease = 0;
        bool mapped = false;
        try
        {
            if (selected is null && !_windows.TryGetValue(window, out _))
            {
                return Call("DefWindowProcW", window, (nint)message, (nint)wParam, lParam);
            }

            if (!_windows.TryGetValue(window, out var info))
            {
                ushort id = Window(window);
                info = new(selected!, id, []);
                _windows.Add(window, info);
                mapped = true;
            }

            selected ??= info.Class;
            var converted = ToGuest(window, message, wParam, lParam);
            if (message is 1 or 0x81)
            {
                // CREATESTRUCT16 retains original strings, parameter and menu identities from CreateWindow.
                if (_createFrames.Count == 0)
                {
                    throw new InvalidDataException("Creation callback has no matching guest frame.");
                }

                var a = _createFrames.Peek();
                lease = _task.Local.Allocate(34, 0x40);
                if (lease == 0)
                {
                    throw new InvalidOperationException("No callback structure space.");
                }

                scratch = (uint)_instance << 16 | lease;
                byte[] data = new byte[34];

                void W(int p, uint v) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(p), (ushort)v);


                void D(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p), v);

                D(0, a[10]);
                W(4, a[9]);
                W(6, a[8]);
                W(8, a[7]);
                W(10, a[6]);
                W(12, a[5]);
                W(14, a[4]);
                W(16, a[3]);
                D(18, a[2]);
                D(22, a[1]);
                D(26, a[0]);
                D(30, 0);
                Win16TaskKernelServices.WriteBuffer(_memory, scratch, data);
                converted = (converted.Message, converted.WParam, scratch);
            }

            // WM_SETTEXT/WM_SETTINGCHANGE expose ANSI temporary strings, never host pointers.
            if (message is 0x0c or 0x1a && lParam != 0)
            {
                string value = Marshal.PtrToStringUni(lParam) ?? "";
                byte[] text = Ansi.GetBytes(value + "\0");
                if (text.Length > 4096)
                {
                    throw new NotSupportedException("Callback string exceeds its bounded reservation.");
                }

                lease = _task.Local.Allocate((uint)text.Length, 0x40);
                if (lease == 0)
                {
                    throw new InvalidOperationException("No callback text space.");
                }

                scratch = (uint)_instance << 16 | lease;
                Win16TaskKernelServices.WriteBuffer(_memory, scratch, text);
                converted = (converted.Message, converted.WParam, scratch);
            }

            int structureBytes = message == 0x24
                ? 20
                : message is 0x46 or 0x47
                    ? 14
                    : message == 0x83
                        ? (wParam == 0
                            ? 8
                            : 28)
                        : message is 0x7c or 0x7d
                            ? 8
                            : 0;
            if (structureBytes != 0 && lParam != 0)
            {
                lease = _task.Local.Allocate((uint)structureBytes, 0x40);
                if (lease == 0)
                {
                    throw new InvalidOperationException("No callback structure space.");
                }

                scratch = (uint)_instance << 16 | lease;
                byte[] data = new byte[structureBytes];
                if (message == 0x24)
                {
                    for (int i = 0; i < 10; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), unchecked((short)I32(lParam, i * 4)));
                    }
                }
                else if (message is 0x46 or 0x47)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(data, Window(Ptr(lParam, 0)));
                    nint after = Ptr(lParam, 8);
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), after is 0 or -1 or -2
                        ? (ushort)after
                        : Window(after));
                    for (int i = 0; i < 4; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(4 + i * 2), unchecked((short)I32(lParam, 16 + i * 4)));
                    }

                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)U32(lParam, 32));
                }
                else if (message == 0x83)
                {
                    int count = wParam == 0
                        ? 4
                        : 12;
                    for (int i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), unchecked((short)I32(lParam, i * 4)));
                    }

                    if (wParam != 0)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 0);
                    }
                }
                else
                {
                    Marshal.Copy(lParam, data, 0, 8);
                }

                Win16TaskKernelServices.WriteBuffer(_memory, scratch, data);
                converted = (converted.Message, converted.WParam, scratch);
            }

            var context = new MessageContext(window, message, wParam, lParam, converted.Message, converted.WParam, converted.LParam);
            _messages.Push(context);
            uint result;
            try
            {
                result = Reverse(selected.Procedure, [(ushort)converted.LParam, (ushort)(converted.LParam >> 16), converted.WParam, converted.Message, info.Id], dialog);
            }
            finally
            {
                _messages.Pop();
            }

            if (structureBytes != 0 && scratch != 0 && !context.NativeDefaultCalled)
            {
                ushort S(int offset) => _memory.ReadWord(_instance, (ushort)(lease + offset));

                if (message == 0x24)
                {
                    for (int i = 0; i < 10; i++)
                    {
                        W32(lParam, i * 4, unchecked((short)S(i * 2)));
                    }
                }
                else if (message is 0x46 or 0x47)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        W32(lParam, 16 + i * 4, unchecked((short)S(4 + i * 2)));
                    }

                    W32(lParam, 32, S(12));
                }
                else if (message == 0x83)
                {
                    for (int i = 0; i < (wParam == 0
                        ? 4
                        : 12); i++)
                    {
                        W32(lParam, i * 4, unchecked((short)S(i * 2)));
                    }
                }
                else
                {
                    for (int i = 0; i < 8; i++)
                    {
                        Marshal.WriteByte(lParam, i, _memory.ReadByte(_instance, (ushort)(lease + i)));
                    }
                }
            }

            if (message == 0x19 || message is >= 0x132 and <= 0x138)
            {
                return Handle((ushort)result, Win16GuiHandleKind.GdiObject);
            }

            if (message == 0x82)
            {
                _windows.Remove(window);
                _handles.Forget(info.Id);
            }

            return dialog
                ? (nint)(ushort)result
                : unchecked((nint)(int)result);
        }
        catch (Exception error)
        {
            _callbackFailure ??= error;
            if (error is Win16TaskExitException exit)
            {
                if (dialog)
                {
                    Call("EndDialog", window, -1);
                }
                else
                {
                    Call("PostQuitMessage", exit.ExitCode);
                }
            }

            if (mapped && message == 0x81)
            {
                _windows.Remove(window);
            }

            return dialog
                ? 0
                : message == 0x81
                    ? 0
                    : message == 1
                        ? -1
                        : 0;
        }
        finally
        {
            if (lease != 0)
            {
                _task.Local.Free(lease);
            }
        }
    }

    private readonly Stack<uint[]> _createFrames = [];
    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var c = Win16ExportContracts.Get(identity);
        var snapshot = state;
        uint[] a = Enumerable.Range(0, c.Arguments.Count).Select(i => c.Argument(i, _memory, snapshot)).ToArray();
        MachineState outer = _suspended;
        _suspended = state;
        uint result = 0;
        using var b = new Win16NativeBuffers(_memory);
        try
        {
            switch (c.Ordinal)
            {
                case 57:
                    {
                        byte[] cls = b.Read(a[0], 26);

                        ushort W(int p) => BinaryPrimitives.ReadUInt16LittleEndian(cls.AsSpan(p));


                        uint D(int p) => BinaryPrimitives.ReadUInt32LittleEndian(cls.AsSpan(p));

                        uint target = D(2);
                        _control.Resolve(new((ushort)(target >> 16), (ushort)target), callback: true);
                        string name = new Win16Marshaller(_memory).ReadAnsi(new((ushort)D(22), (ushort)(D(22) >> 16)), 65536);
                        if (_classes.ContainsKey(name))
                        {
                            break;
                        }

                        if (W(10) != _instance || unchecked((short)W(6)) < 0 || unchecked((short)W(8)) < 0)
                        {
                            break;
                        }

                        ClassInfo? entry = null;
                        WindowProcedure callback = (w, m, wp, lp) => Procedure(entry, w, m, wp, lp, false);
                        entry = new(name, target, W(8), W(10), callback, D(18));
                        nint native = b.Alloc(72);
                        W32(native, 0, W(0));
                        WP(native, 8, Marshal.GetFunctionPointerForDelegate(callback));
                        W32(native, 16, 0);
                        W32(native, 20, W(8));
                        WP(native, 24, Call("GetModuleHandleW", 0));
                        WP(native, 32, Handle(W(12), Win16GuiHandleKind.Icon));
                        WP(native, 40, Handle(W(14), Win16GuiHandleKind.Cursor));
                        WP(native, 48, _handles.TryGet(W(16), Win16GuiHandleKind.GdiObject, out var brush)
                            ? brush!.Native
                            : W(16) <= 32
                                ? (nint)W(16)
                                : Handle(W(16), Win16GuiHandleKind.GdiObject));
                        WP(native, 56, 0);
                        WP(native, 64, b.Wide(name));
                        result = (uint)Call("RegisterClassW", native);
                        if (result != 0)
                        {
                            _classes.Add(name, entry);
                        }

                        break;
                    }

                case 41:
                    {
                        if (a[9] != _instance)
                        {
                            break;
                        }

                        string? name = (a[0] >> 16) == 0
                            ? null
                            : new Win16Marshaller(_memory).ReadAnsi(new((ushort)a[0], (ushort)(a[0] >> 16)), 65536);
                        _classes.TryGetValue(name ?? "", out var entry);
                        nint parent = Handle(a[7]);
                        bool child = (a[2] & 0x40000000) != 0;
                        if (!child && a[8] == 0 && entry is not null && entry.Menu != 0)
                        {
                            a[8] = _resources.LoadMenu(entry.Menu);
                        }

                        nint menu = child
                            ? (ushort)a[8]
                            : Handle(a[8], Win16GuiHandleKind.Menu);
                        if (entry is not null)
                        {
                            _creating.Push(entry);
                        }

                        _createFrames.Push(a);
                        nint native;
                        try
                        {
                            native = Call("CreateWindowExW", 0, b.String(a[0], true), b.String(a[1]), (nint)(nuint)a[2], a[3] == 0x8000
                                ? int.MinValue
                                : (short)a[3], a[4] == 0x8000
                                ? int.MinValue
                                : (short)a[4], a[5] == 0x8000
                                ? int.MinValue
                                : (short)a[5], a[6] == 0x8000
                                ? int.MinValue
                                : (short)a[6], parent, menu, Call("GetModuleHandleW", 0), 0);
                        }
                        finally
                        {
                            _createFrames.Pop();
                            if (entry is not null)
                            {
                                _creating.Pop();
                            }
                        }

                        result = Window(native);
                        if (native != 0)
                        {
                            _handles.TakeOwnership((ushort)result);
                            if (entry is not null && !_windows.ContainsKey(native))
                            {
                                _windows.Add(native, new(entry, (ushort)result, []));
                            }

                            _handles.SetParent((ushort)result, (ushort)a[7]);
                            if (!child && a[8] != 0)
                            {
                                _handles.SetParent((ushort)a[8], (ushort)result);
                            }
                        }

                        break;
                    }

                case 133:
                case 134:
                    {
                        nint window = Handle(a[0]);
                        int index = (short)a[1];
                        if (!_windows.TryGetValue(window, out var info))
                        {
                            break;
                        }

                        if (index >= 0)
                        {
                            if (index + 2 > info.Class.Extra)
                            {
                                break;
                            }

                            result = info.Words.GetValueOrDefault(index);
                            if (c.Ordinal == 134)
                            {
                                info.Words[index] = (ushort)a[2];
                            }
                        }
                        else if (index == -6)
                        {
                            result = info.Class.Instance;
                        }
                        else if (index == -8)
                        {
                            result = Window(Call("GetParent", window));
                            if (c.Ordinal == 134)
                            {
                                result = Window(Call("SetParent", window, Handle(a[2])));
                            }
                        }
                        else if (index == -12)
                        {
                            result = (ushort)Call("GetWindowLongW", window, -12);
                            if (c.Ordinal == 134)
                            {
                                result = (ushort)Call("SetWindowLongW", window, -12, (ushort)a[2]);
                            }
                        }

                        break;
                    }

                case 66:
                    {
                        nint window = Handle(a[0]);
                        nint dc = Call("GetDC", window);
                        result = _handles.Add(dc, Win16GuiHandleKind.Dc, false, (ushort)a[0]);
                        _dcLeases[(ushort)result] = (window, dc);
                        break;
                    }

                case 68:
                    {
                        nint window = Handle(a[0]);
                        ushort id = (ushort)a[1];
                        if (_dcLeases.TryGetValue(id, out var lease) && lease.Window == window)
                        {
                            result = (uint)Call("ReleaseDC", window, lease.Dc);
                            if (result != 0)
                            {
                                _dcLeases.Remove(id);
                                _handles.Forget(id);
                            }
                        }

                        break;
                    }

                case 39:
                    {
                        b.ValidateWrite(a[1], 32);
                        if (_paints.ContainsKey(a[1]))
                        {
                            throw new InvalidOperationException("Paint buffer already has an active lease.");
                        }

                        nint window = Handle(a[0]), paint = Marshal.AllocHGlobal(72);
                        for (int i = 0; i < 72; i++)
                        {
                            Marshal.WriteByte(paint, i, 0);
                        }

                        nint dc = Call("BeginPaint", window, paint);
                        if (dc == 0)
                        {
                            Marshal.FreeHGlobal(paint);
                            break;
                        }

                        ushort id = _handles.Add(dc, Win16GuiHandleKind.Dc, false, (ushort)a[0]);
                        if (id == 0)
                        {
                            Call("EndPaint", window, paint);
                            Marshal.FreeHGlobal(paint);
                            break;
                        }

                        byte[] output = new byte[32];
                        BinaryPrimitives.WriteUInt16LittleEndian(output, id);
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)(I32(paint, 8) != 0
                            ? 1
                            : 0));
                        for (int i = 0; i < 4; i++)
                        {
                            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(4 + i * 2), unchecked((short)I32(paint, 12 + i * 4)));
                        }

                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12), (ushort)(I32(paint, 28) != 0
                            ? 1
                            : 0));
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(14), (ushort)(I32(paint, 32) != 0
                            ? 1
                            : 0));
                        b.Write(a[1], output);
                        _paints.Add(a[1], new(window, paint, id));
                        result = id;
                        break;
                    }

                case 40:
                    {
                        b.Read(a[1], 32);
                        nint window = Handle(a[0]);
                        if (_paints.TryGetValue(a[1], out var paint) && paint.Window == window)
                        {
                            result = (uint)Call("EndPaint", window, paint.Structure);
                            if (result != 0)
                            {
                                _paints.Remove(a[1]);
                                _handles.Forget(paint.Dc);
                                Marshal.FreeHGlobal(paint.Structure);
                            }
                        }

                        break;
                    }

                case 107:
                    {
                        nint window = Handle(a[0]);
                        if (_messages.TryPeek(out var context) && context.Window == window && context.GuestMessage == a[1] && context.GuestWParam == a[2] && context.GuestLParam == a[3])
                        {
                            context.NativeDefaultCalled = true;
                            nint value = Call("DefWindowProcW", window, (nint)context.Message, (nint)context.WParam, context.LParam);
                            result = context.GuestMessage == 0x19
                                ? _handles.Add(value, Win16GuiHandleKind.GdiObject)
                                : unchecked((uint)(nuint)value);
                        }
                        else
                        {
                            var native = FromGuest(window, (ushort)a[1], (ushort)a[2], a[3], b);
                            result = unchecked((uint)(nuint)Call("DefWindowProcW", window, (nint)native.Message, native.WParam, native.LParam));
                        }

                        break;
                    }

                case 110:
                case 111:
                case 101:
                    {
                        nint window = Handle(a[0]);
                        int baseIndex = c.Ordinal == 101
                            ? 2
                            : 1;
                        if (c.Ordinal == 101)
                        {
                            window = Call("GetDlgItem", window, (short)a[1]);
                        }

                        var converted = FromGuest(window, (ushort)a[baseIndex], (ushort)a[baseIndex + 1], a[baseIndex + 2], b);
                        if (c.Ordinal == 110 && (converted.Message is 0x0c or 0x0d))
                        {
                            throw new NotSupportedException("Asynchronous guest-pointer message requires an owned lifetime.");
                        }

                        nint value = Call(c.Ordinal == 110
                            ? "PostMessageW"
                            : "SendMessageW", window, (nint)converted.Message, converted.WParam, converted.LParam);
                        result = unchecked((uint)(nuint)value);
                        if (converted.Message == 0x31)
                        {
                            result = _handles.Add(value, Win16GuiHandleKind.GdiObject);
                        }

                        break;
                    }

                case 109:
                    {
                        b.ValidateWrite(a[0], 18);
                        nint message = b.Alloc(48);
                        result = (uint)Call("PeekMessageW", message, Handle(a[1]), (ushort)a[2], (ushort)a[3], (ushort)a[4]);
                        if (result != 0)
                        {
                            nint window = Ptr(message, 0);
                            var converted = ToGuest(window, U32(message, 8), (nuint)Ptr(message, 16), Ptr(message, 24));
                            byte[] output = new byte[18];

                            void W(int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(p), v);


                            void D(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(p), v);

                            W(0, Window(window));
                            W(2, converted.Message);
                            W(4, converted.WParam);
                            D(6, converted.LParam);
                            D(10, U32(message, 32));
                            W(14, (ushort)I32(message, 36));
                            W(16, (ushort)I32(message, 40));
                            b.Write(a[0], output);
                            byte[] raw = new byte[48];
                            Marshal.Copy(message, raw, 0, 48);
                            if (_queued.Count >= 256)
                            {
                                _queued.Clear();
                            }

                            _queued[Convert.ToHexString(output)] = raw;
                        }

                        break;
                    }

                case 113:
                case 114:
                case 178:
                    {
                        uint pointer = a[c.Ordinal == 178
                            ? 2
                            : 0];
                        nint native = NativeMessage(pointer, b);
                        result = unchecked((uint)(nuint)(c.Ordinal == 178
                            ? Call("TranslateAcceleratorW", Handle(a[0]), Handle(a[1], Win16GuiHandleKind.Accelerator), native)
                            : Call(c.Ordinal == 113
                                ? "TranslateMessage"
                                : "DispatchMessageW", native)));
                        break;
                    }

                case 10:
                    {
                        nint window = Handle(a[0]);
                        uint target = a[3];
                        TimerProcedure? callback = null;
                        nint procedure = 0;
                        if (target != 0)
                        {
                            _control.Resolve(new((ushort)(target >> 16), (ushort)target), callback: true);
                            callback = (hwnd, msg, id, time) =>
                            {
                                try
                                {
                                    Reverse(target, [(ushort)time, (ushort)(time >> 16), (ushort)id, (ushort)msg, Window(hwnd)], true);
                                }
                                catch (Exception error)
                                {
                                    _callbackFailure ??= error;
                                }
                            };
                            procedure = Marshal.GetFunctionPointerForDelegate(callback);
                        }

                        nuint timer = (nuint)Call("SetTimer", window, (ushort)a[1], (ushort)a[2], procedure);
                        if (timer <= 65535)
                        {
                            result = (ushort)timer;
                            if (result != 0 && callback is not null)
                            {
                                _timers[((ushort)a[0], (ushort)result)] = callback;
                            }
                        }
                        else
                        {
                            Call("KillTimer", window, (nint)timer);
                            result = 0;
                        }

                        break;
                    }

                case 12:
                    {
                        result = (uint)Call("KillTimer", Handle(a[0]), (ushort)a[1]);
                        if (result != 0)
                        {
                            _timers.Remove(((ushort)a[0], (ushort)a[1]));
                        }

                        break;
                    }

                case 87:
                    {
                        if (a[0] != _instance)
                        {
                            break;
                        }

                        byte[]? template = _resources.Find(5, a[1]);
                        if (template is null)
                        {
                            result = 0xffff;
                            break;
                        }

                        uint target = a[3];
                        _control.Resolve(new((ushort)(target >> 16), (ushort)target), callback: true);
                        ClassInfo? entry = null;
                        WindowProcedure callback = (w, m, wp, lp) => Procedure(entry, w, m, wp, lp, true);
                        entry = new("#32770", target, 30, _instance, callback);
                        nint value = Call("DialogBoxIndirectParamW", Call("GetModuleHandleW", 0), b.Bytes(Win16TaskGuiResources.ConvertDialogTemplate(template)), Handle(a[2]), Marshal.GetFunctionPointerForDelegate(callback), 0);
                        GC.KeepAlive(callback);
                        result = unchecked((ushort)value);
                        break;
                    }
            }

            if (_callbackFailure is not null)
            {
                var error = _callbackFailure;
                _callbackFailure = null;
                if (error is Win16TaskExitException)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                }

                throw new InvalidOperationException("Original task callback failed.", error);
            }

            if (c.ReturnBytes != 0)
            {
                state.AX = (ushort)result;
                if (c.ReturnBytes == 4)
                {
                    state.DX = (ushort)(result >> 16);
                }
            }

            return c.CleanupBytes;
        }
        finally
        {
            _suspended = outer;
        }
    }

    private readonly Dictionary<ushort, (nint Window, nint Dc)> _dcLeases = [];

    private (uint Message, nint WParam, nint LParam) FromGuest(nint window, ushort message, ushort wParam, uint lParam, Win16NativeBuffers b)
    {
        if (message == 0x111)
        {
            return (message, (nint)((uint)wParam | (lParam & 0xffff0000)), Handle((ushort)lParam));
        }

        if (message == 0x30)
        {
            return (message, Handle(wParam, Win16GuiHandleKind.GdiObject), (nint)(nuint)lParam);
        }

        if (message is 7 or 8 or 0x20 or 0x21)
        {
            return (message, Handle(wParam), (nint)(nuint)lParam);
        }

        if (message == 0x0c)
        {
            return (message, wParam, b.String(lParam));
        }

        // Original dialog listbox messages have Win16 LB_* numbers and ANSI strings.
        nint classBuffer = b.Alloc(128);
        Call("GetClassNameW", window, classBuffer, 64);
        string className = Marshal.PtrToStringUni(classBuffer) ?? "";
        if (className.Equals("EDIT", StringComparison.OrdinalIgnoreCase) && message == 0x401)
        {
            return (0xb1, unchecked((short)lParam), unchecked((short)(lParam >> 16)));
        }

        if (!className.Equals("LISTBOX", StringComparison.OrdinalIgnoreCase))
        {
            return (message, wParam, unchecked((nint)(int)lParam));
        }

        uint mapped = message switch
        {
            0x401 => 0x180,
            0x402 => 0x181,
            0x403 => 0x182,
            0x405 => 0x184,
            0x407 => 0x186,
            0x408 => 0x187,
            0x409 => 0x188,
            0x40a => 0x189,
            0x40b => 0x18a,
            0x40c => 0x18b,
            0x40d => 0x18c,
            0x40e => 0x18d,
            0x40f => 0x18e,
            0x410 => 0x18f,
            _ => message
        };
        if (mapped is 0x180 or 0x181 or 0x18c or 0x18f)
        {
            return (mapped, unchecked((short)wParam), b.String(lParam));
        }

        return (mapped, wParam, unchecked((nint)(int)lParam));
    }

    private nint NativeMessage(uint pointer, Win16NativeBuffers b)
    {
        byte[] source = b.Read(pointer, 18);
        if (_queued.TryGetValue(Convert.ToHexString(source), out var raw))
        {
            return b.Bytes(raw);
        }

        ushort W(int p) => BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(p));


        uint D(int p) => BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(p));

        var converted = FromGuest(Handle(W(0)), W(2), W(4), D(6), b);
        nint native = b.Alloc(48);
        WP(native, 0, Handle(W(0)));
        W32(native, 8, converted.Message);
        WP(native, 16, converted.WParam);
        WP(native, 24, converted.LParam);
        W32(native, 32, D(10));
        W32(native, 36, unchecked((short)W(14)));
        W32(native, 40, unchecked((short)W(16)));
        return native;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var timer in _timers.Keys.ToArray())
        {
            Call("KillTimer", Handle(timer.Item1), timer.Item2);
        }

        _timers.Clear();
        foreach (var paint in _paints.Values)
        {
            Call("EndPaint", paint.Window, paint.Structure);
            Marshal.FreeHGlobal(paint.Structure);
            _handles.Forget(paint.Dc);
        }

        _paints.Clear();
        foreach (var dc in _dcLeases)
        {
            Call("ReleaseDC", dc.Value.Window, dc.Value.Dc);
            _handles.Forget(dc.Key);
        }

        _dcLeases.Clear();
        foreach (var window in _windows.Keys.ToArray())
        {
            Call("DestroyWindow", window);
            if (_windows.Remove(window, out var info))
            {
                _handles.Forget(info.Id);
            }
        }

        _windows.Clear();
        using (var b = new Win16NativeBuffers(_memory))
        {
            foreach (var cls in _classes.Values)
            {
                Call("UnregisterClassW", b.Wide(cls.Name), Call("GetModuleHandleW", 0));
            }
        }

        _classes.Clear();
    }
}
