namespace Win16_Runtime;

public sealed class Win16TaskExitException(ushort code, string? diagnostic = null) : Exception(diagnostic ?? $"Win16 task exited with code {code}.")
{
    public ushort ExitCode { get; } = code;
    public string? Diagnostic { get; } = diagnostic;
}

/// <summary>Startup, DOS register services and task termination. InitTask initializes OS-owned
/// instance/PSP data only; the original CRT remains responsible for application initialization.</summary>
public sealed class Win16TaskLifecycle : IDisposable
{
    public bool Initialized => _initialized;
    public ushort PspSelector => _psp?.Selector ?? 0;
    public bool Terminated
    {
        get; private set;
    }
    public ushort? ExitCode
    {
        get; private set;
    }

    private Win16TaskExitException Exit(ushort code, string? diagnostic = null)
    {
        Terminated = true;
        ExitCode = code;
        return new(code, diagnostic);
    }

    private readonly NeLoadedProgram _image;
    private readonly Win16ProgramControl _control;
    private readonly Win16TaskKernelServices _kernel;
    private readonly SegmentMemory _memory;
    private readonly Func<bool>? _waitEvent;
    private Win16Segment? _psp;
    private bool _initialized, _disposed;
    private int _events = 1;
    private readonly Dictionary<byte, Win16CodeAddress> _vectors = [];
    private readonly string _commandLine;
    private readonly ushort _show;
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "KERNEL.#1",
        "KERNEL.#30",
        "KERNEL.#91",
        "KERNEL.#102",
        "KERNEL.#137"
    };

    public Win16TaskLifecycle(NeLoadedProgram image, Win16ProgramControl control, Win16CallDispatcher dispatcher, Win16TaskKernelServices kernel, Func<bool>? waitEvent = null, string commandLine = "", ushort show = 1)
    {
        _image = image;
        _control = control;
        _kernel = kernel;
        _memory = image.Memory;
        _waitEvent = waitEvent;
        _commandLine = commandLine;
        _show = show;
        dispatcher.RegisterInterrupt(0x21, (ref MachineState s, SegmentMemory m, byte _) =>
        {
            Dos(ref s);
            ushort flags = m.ReadWord(s.SS, (ushort)(s.SP + 4));
            m.WriteWord(s.SS, (ushort)(s.SP + 4), (ushort)(flags & ~1));
        });
    }

    public void PostEvent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_events < int.MaxValue)
        {
            _events++;
        }
    }

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var c = Win16ExportContracts.Get(identity);
        if (c.Ordinal == 1)
        {
            throw Exit((ushort)c.Argument(0, _memory, state));
        }

        if (c.Ordinal == 137)
        {
            throw Exit(255, Win16TaskKernelServices.ReadString(_memory, c.Argument(1, _memory, state)));
        }

        if (c.Ordinal == 102)
        {
            Dos(ref state);
        }
        else if (c.Ordinal == 30)
        {
            ushort task = (ushort)c.Argument(0, _memory, state);
            if (task != 0 && task != _image.InitialState.DS)
            {
                throw new NotSupportedException("WaitEvent supports the current task only.");
            }

            if (_events > 0)
            {
                _events--;
                state.AX = 0;
            }
            else
            {
                if (_waitEvent is null)
                {
                    throw new PlatformNotSupportedException("Task has no event wait provider.");
                }

                if (!_waitEvent())
                {
                    throw new InvalidOperationException("Host task event wait failed.");
                }

                state.AX = 1;
            }
        }
        else if (c.Ordinal == 91)
        {
            Init(ref state);
        }

        return c.CleanupBytes;
    }

    private void Init(ref MachineState state)
    {
        if (_initialized)
        {
            state.AX = 0;
            return;
        }

        ushort ds = _image.InitialState.DS;
        if (state.DS != ds || state.SS != ds)
        {
            throw new NotSupportedException("InitTask requires the current automatic data/stack segment.");
        }

        byte[] command = Win16NativeBuffers.Ansi.GetBytes(_commandLine);
        if (command.Length > 126)
        {
            throw new ArgumentOutOfRangeException("commandLine");
        }

        ushort selector = _memory.AllocateSelector();
        if (selector == 0)
        {
            state.AX = 0;
            return;
        }

        _psp = _memory.CreateSegment(selector, 256);
        _memory.Map(_psp);
        var environment = state;
        _kernel.Invoke("KERNEL.#131", ref environment);
        _memory.WriteWord(selector, 0x2c, environment.DX);
        _memory.WriteByte(selector, 0x80, (byte)command.Length);
        _memory.WriteBytes(selector, 0x81, command);
        _memory.WriteByte(selector, (ushort)(0x81 + command.Length), 13);
        ushort minimum = unchecked((ushort)(state.SP + 4)), stackSize = state.BX != 0
            ? state.BX
            : _image.Report.InitialRegisters.StackSize;
        ushort top = checked((ushort)((minimum > stackSize
            ? minimum - stackSize
            : 0) + 150));
        _memory.ValidateBufferWrite(ds, 0x0a, 6);
        _memory.WriteWord(ds, 0x0a, top);
        _memory.WriteWord(ds, 0x0c, minimum);
        _memory.WriteWord(ds, 0x0e, minimum);
        // InitTask leaves a zero BP word under the ordinary far-return frame.
        ushort old = state.SP;
        uint returnAddress = _memory.ReadDword(state.SS, old);
        _memory.WriteDword(state.SS, (ushort)(old - 2), returnAddress);
        _memory.WriteWord(state.SS, (ushort)(old + 2), 0);
        state.SP = (ushort)(old - 2);
        state.AX = 1;
        state.CX = top;
        state.DX = _show;
        state.SI = 0;
        state.DI = ds;
        state.ES = selector;
        state.BX = command.Length == 0
            ? (ushort)0x80
            : (ushort)0x81;
        _initialized = true;
    }

    private void Dos(ref MachineState state)
    {
        byte ah = (byte)(state.AX >> 8), vector = (byte)state.AX;
        if (ah == 0x30)
        {
            state.AX = 0x0005;
            state.BX = 0xff00;
            state.CX = 0;
        }
        else if (ah == 0x35)
        {
            var target = _vectors.GetValueOrDefault(vector);
            state.ES = target.Segment;
            state.BX = target.Offset;
        }
        else if (ah == 0x25)
        {
            var target = new Win16CodeAddress(state.DS, state.DX);
            if (target != default)
            {
                _control.Resolve(target, callback: true);
            }

            _vectors[vector] = target;
        }
        else if (ah == 0x4c)
        {
            throw Exit((byte)state.AX);
        }
        else
        {
            throw new NotSupportedException($"Unobserved DOS INT21 function AH={ah:X2}.");
        }

        state.Flags = (ushort)(state.Flags & ~1);
    }

    public Win16CodeAddress InterruptVector(byte number) => _vectors.GetValueOrDefault(number);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_psp is not null)
        {
            _memory.Unmap(_psp.Selector);
            _psp.Dispose();
        }

        _disposed = true;
    }
}
