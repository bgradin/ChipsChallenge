using System.Runtime.InteropServices;
using Translator.IR;

namespace Win16_Runtime;

public enum Win16CallbackSignature
{
    EnumWindows,
    WindowProcedure
}

public sealed record Win16CallbackReference(Win16CodeAddress Target, Win16CallbackSignature Signature);
/// <summary>Borrowed HWND identities. The native owner unregisters them before destruction.</summary>
public sealed class Win16WindowHandles : IDisposable
{
    private readonly Win16HandleTable<nint> _table = new();
    private readonly Dictionary<nint, ushort> _reverse = new();

    public ushort Register(nint hwnd)
    {
        if (hwnd == 0 || hwnd == -1)
        {
            throw new ArgumentException("Invalid HWND.");
        }

        if (_reverse.TryGetValue(hwnd, out var id))
        {
            return id;
        }

        id = _table.Add(hwnd);
        _reverse.Add(hwnd, id);
        return id;
    }

    public ushort GetId(nint hwnd) => _reverse.TryGetValue(hwnd, out var id)
        ? id
        : throw new KeyNotFoundException("Native HWND has no Win16 binding.");

    public nint GetNative(ushort id) => _table.Get(id);

    public void Dispose()
    {
        _reverse.Clear();
        _table.Dispose();
    }
}

/// <summary>Synchronous, owner-thread callbacks use isolated emulated stacks and restore all CPU state.</summary>
public sealed class Win16CallbackMachine
{
    public MachineState State;
    public SegmentMemory Memory
    {
        get;
    }
    public Win16CallDispatcher Dispatcher
    {
        get;
    }
    public int Depth
    {
        get; private set;
    }
    public int MaxDepth
    {
        get;
    }

    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    public Win16CallbackMachine(SegmentMemory memory, Win16CallDispatcher dispatcher, MachineState state, int maxDepth = 8)
    {
        if (maxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }

        Memory = memory;
        Dispatcher = dispatcher;
        State = state;
        MaxDepth = maxDepth;
    }

    public uint Invoke(Win16CallbackReference callback, IReadOnlyList<ushort> words)
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            throw new NotSupportedException("Cross-thread callbacks are not supported.");
        }

        if (!Enum.IsDefined(callback.Signature))
        {
            throw new ArgumentException("Unknown callback signature.");
        }

        if (Depth >= MaxDepth)
        {
            throw new InvalidOperationException("Callback nesting limit exceeded.");
        }

        int count = callback.Signature == Win16CallbackSignature.EnumWindows
            ? 3
            : 5;
        if (words.Count != count)
        {
            throw new ArgumentException("Callback argument count does not match its signature.");
        }

        if (callback.Target == default || !Dispatcher.IsTranslatedFar(callback.Target))
        {
            throw new KeyNotFoundException("Callback target is not a registered translated far procedure.");
        }

        ushort selector = 0;
        for (int id = 0xD000; id <= 0xEFFF; id++)
        {
            if (!Memory.TryGetSegment((ushort)id, out _))
            {
                selector = (ushort)id;
                break;
            }
        }

        if (selector == 0)
        {
            throw new InvalidOperationException("No callback stack selector is available.");
        }

        var stack = new Win16Segment(selector);
        Memory.Map(stack);
        var saved = State;
        Depth++;
        try
        {
            State.SS = selector;
            State.SP = 0xFF00;
            State.SPHigh = 0;
            State.BP = 0;
            State.BPHigh = 0;
            State.Flags &= unchecked((ushort)~0x400);
            for (int i = words.Count - 1; i >= 0; i--)
            {
                Win16Stack.Push(ref State, Memory, words[i]);
            }

            Dispatcher.Call(ref State, Memory, callback.Target, CallDistance.Far);
            if (State.SP != 0xFF00 || State.SS != selector)
            {
                throw new InvalidOperationException("Callback callee did not clean its declared arguments.");
            }

            return callback.Signature == Win16CallbackSignature.EnumWindows
                ? State.AX
                : (uint)State.AX | ((uint)State.DX << 16);
        }
        finally
        {
            State = saved;
            Depth--;
            if (Memory.TryGetSegment(selector, out var current) && ReferenceEquals(current, stack))
            {
                Memory.Unmap(selector);
            }
        }
    }
}

/// <summary>Owns reverse-P/Invoke entry points. Native callers must release references before Dispose.</summary>
public sealed class Win16CallbackBinding : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumEntry(nint hwnd, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowEntry(nint hwnd, uint message, nuint wParam, nint lParam);
    private readonly Win16CallbackMachine _machine;
    private readonly Win16WindowHandles _handles;
    private readonly Delegate _entry;
    private readonly HashSet<uint>? _allowedMessages;
    private bool _disposed;
    public Win16CallbackReference Reference
    {
        get;
    }
    public Exception? LastError
    {
        get; private set;
    }
    public int InvocationCount
    {
        get; private set;
    }

    public nint NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Native callback entries use the Windows ABI.");
            }

            return Marshal.GetFunctionPointerForDelegate(_entry);
        }
    }

    public Win16CallbackBinding(Win16CallbackMachine machine, Win16WindowHandles handles, Win16CallbackReference reference, IEnumerable<uint>? allowedMessages = null)
    {
        if (!Enum.IsDefined(reference.Signature))
        {
            throw new ArgumentException("Unknown callback signature.");
        }

        if (reference.Target == default || !machine.Dispatcher.IsTranslatedFar(reference.Target))
        {
            throw new ArgumentException("Callback target must be a registered translated far procedure.");
        }

        _machine = machine;
        _handles = handles;
        Reference = reference;
        _allowedMessages = allowedMessages is null
            ? null
            : new(allowedMessages);
        _entry = reference.Signature == Win16CallbackSignature.EnumWindows
            ? new EnumEntry(InvokeEnum)
            : new WindowEntry(InvokeWindow);
    }

    public int InvokeEnum(nint hwnd, nint user)
    {
        LastError = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Reference.Signature != Win16CallbackSignature.EnumWindows)
            {
                throw new InvalidOperationException("Wrong callback signature.");
            }

            long value = user.ToInt64();
            if (value < int.MinValue || value > uint.MaxValue)
            {
                throw new NotSupportedException("Enumeration user value must fit a Win16 dword, not a native pointer.");
            }

            InvocationCount++;
            uint result = _machine.Invoke(Reference, new ushort[] { (ushort)value, (ushort)(value >> 16), _handles.GetId(hwnd) });
            return result != 0
                ? 1
                : 0;
        }
        catch (Exception error)
        {
            LastError = error;
            return 0;
        }
    }

    public nint InvokeWindow(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        LastError = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Reference.Signature != Win16CallbackSignature.WindowProcedure)
            {
                throw new InvalidOperationException("Wrong callback signature.");
            }

            if (message > ushort.MaxValue || wParam > ushort.MaxValue || lParam.ToInt64() < int.MinValue || lParam.ToInt64() > int.MaxValue)
            {
                throw new NotSupportedException("Window message arguments exceed the supported Win16 value widths.");
            }

            if (_allowedMessages is null || !_allowedMessages.Contains(message))
            {
                throw new NotSupportedException($"Window message 0x{message:X} is not declared safe by this callback binding.");
            }

            InvocationCount++;
            uint value = unchecked((uint)lParam.ToInt64());
            uint result = _machine.Invoke(Reference, new ushort[] { (ushort)value, (ushort)(value >> 16), (ushort)wParam, (ushort)message, _handles.GetId(hwnd) });
            return (nint)unchecked((int)result);
        }
        catch (Exception error)
        {
            LastError = error;
            return 0;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        GC.KeepAlive(_entry);
    }
}
