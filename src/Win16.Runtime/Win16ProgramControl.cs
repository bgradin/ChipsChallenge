using Translator.IR;

namespace Win16_Runtime;

public enum Win16ProgramAddressKind
{
    Code = 1,
    Import = 2,
    Data = 3,
    NativeHost = 4
}

public delegate ushort Win16NativeBlock(ref MachineState state);
public delegate ushort Win16ProgramImport(ref MachineState state, SegmentMemory memory);
public sealed record Win16ProgramBinding(Win16CodeAddress Address, Win16ProgramAddressKind Kind, string Identity, Win16NativeBlock? Entry);
/// <summary>Bounded lookup of statically compiled native blocks. Reads no guest instructions.
/// CPU CALL/RET frame contents, not inferred function conventions, determine return addresses.</summary>
public sealed class Win16ProgramControl : IDisposable
{
    public const string UnimplementedImportIdentityKey = "Win16.UnimplementedImportIdentity";
    private readonly Dictionary<Win16CodeAddress, Win16ProgramBinding> _bindings;
    private readonly Dictionary<Win16CodeAddress, Win16ProgramImport> _services = new();
    private readonly Stack<Context> _contexts = new();
    private readonly Win16CallDispatcher _dispatcher;
    private readonly SegmentMemory _memory;
    private readonly long _maximumTransfers;
    private readonly int _maximumDepth;
    private readonly int _maximumEntries;
    private readonly HashSet<Win16CodeAddress> _instanceThunks = new();
    private int _nextInstanceThunk;
    private bool _disposed;
    private readonly Dictionary<ushort, (object Storage, int Origin)> _codeStorage = new();
    private sealed class Context(Win16CodeAddress sentinel)
    {
        public Win16CodeAddress Sentinel = sentinel;
        public Win16ProgramBinding? Next;
        public bool Returned;
        public long Transfers;
    }

    public Win16ProgramControl(IEnumerable<Win16ProgramBinding> approved, SegmentMemory memory, Win16CallDispatcher dispatcher, int maximumEntries = 65536, long maximumTransfers = 1000000, int maximumDepth = 8)
    {
        if (maximumEntries is < 1 or > 65536 || maximumTransfers < 1 || maximumDepth is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _maximumEntries = maximumEntries;
        memory.ReserveSelector(0xf200);
        _memory = memory;
        _dispatcher = dispatcher;
        _maximumTransfers = maximumTransfers;
        _maximumDepth = maximumDepth;
        _bindings = new();
        foreach (var binding in approved)
        {
            if (binding.Kind is not (Win16ProgramAddressKind.Code or Win16ProgramAddressKind.Import) || binding.Address.Segment == 0 || string.IsNullOrWhiteSpace(binding.Identity) || (binding.Kind == Win16ProgramAddressKind.Code) != (binding.Entry is not null) || _bindings.Count == maximumEntries || !_bindings.TryAdd(binding.Address, binding))
            {
                throw new InvalidDataException("Invalid, duplicate or over-limit native program binding.");
            }
        }

        foreach (var b in _bindings.Values.Where(b => b.Kind == Win16ProgramAddressKind.Code))
        {
            if (memory.TryGetSegment(b.Address.Segment, out var segment) && segment!.IsExecutable)
            {
                _codeStorage.TryAdd(b.Address.Segment, (segment.StorageIdentity, segment.Origin));
            }
        }

        if (dispatcher.ControlTransfer is not null)
        {
            throw new InvalidOperationException("Dispatcher already has a program owner.");
        }

        dispatcher.ControlTransfer = Transfer;
    }

    public Win16ProgramBinding Resolve(Win16CodeAddress pointer, bool callback = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _bindings.TryGetValue(pointer, out var binding);
        if (binding is null && _memory.TryGetSegment(pointer.Segment, out var view) && view!.IsExecutable && pointer.Offset < view.Size)
        {
            foreach (var original in _codeStorage)
            {
                if (ReferenceEquals(original.Value.Storage, view.StorageIdentity) && original.Value.Origin == view.Origin && _bindings.TryGetValue(new(original.Key, pointer.Offset), out var canonical) && canonical.Kind == Win16ProgramAddressKind.Code)
                {
                    binding = canonical with
                    {
                        Address = pointer
                    };
                    break;
                }
            }
        }

        if (binding is null || callback && binding.Kind != Win16ProgramAddressKind.Code)
        {
            throw new InvalidOperationException($"Unapproved original code address {pointer.Segment:X4}:{pointer.Offset:X4}.");
        }

        return binding;
    }

    /// <summary>Win16 MakeProcInstance semantics use a precompiled runtime thunk:
    /// MOV AX,instance-selector; FAR JMP approved original procedure. No guest code
    /// bytes or native machine code are generated at runtime.</summary>
    public Win16CodeAddress CreateInstanceThunk(Win16CodeAddress target, ushort instance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Resolve(target, callback: true);
        if (!_memory.TryGetSegment(instance, out var data) || data!.IsDisposed || data.IsExecutable || !data.CanRead || _nextInstanceThunk >= 4096 || _bindings.Count >= _maximumEntries || _memory.TryGetSegment(0xf200, out _))
        {
            return default;
        }

        var address = new Win16CodeAddress(0xf200, checked((ushort)(0x1000 + _nextInstanceThunk * 8)));
        if (_bindings.ContainsKey(address))
        {
            return default;
        }

        try
        {
            _bindings.EnsureCapacity(_bindings.Count + 1);
            _instanceThunks.EnsureCapacity(_instanceThunks.Count + 1);
            var thunk = new Win16ProgramBinding(address, Win16ProgramAddressKind.Code, $"MakeProcInstance:{target.Segment:X4}:{target.Offset:X4}:{instance:X4}", (ref MachineState state) =>
            {
                if (!_memory.TryGetSegment(instance, out var current) || !ReferenceEquals(current, data) || current!.IsDisposed)
                {
                    throw new InvalidOperationException("Procedure instance data selector has expired.");
                }

                state.AX = instance;
                Transfer(ref state, _memory, (uint)target.Segment << 16 | target.Offset, 1);
                return 0;
            });
            _bindings.Add(address, thunk);
            _instanceThunks.Add(address);
            _nextInstanceThunk++;
            return address;
        }
        catch (OutOfMemoryException)
        {
            return default;
        }
    }

    public void FreeInstanceThunk(Win16CodeAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_instanceThunks.Remove(address))
        {
            _bindings.Remove(address);
        }
    }

    public void RegisterImport(Win16CodeAddress address, Win16ProgramImport service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Resolve(address).Kind != Win16ProgramAddressKind.Import || !_services.TryAdd(address, service))
        {
            throw new InvalidOperationException("Import adapter requires one approved import identity.");
        }
    }

    public void RegisterModuleProcedure(Win16CodeAddress address, string identity, Win16ProgramImport service)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var expected = Win16TaskModules.Procedures.SingleOrDefault(p => "MMSYSTEM." + p.Name == identity);
        if (address.Segment != 0xf100 || address.Offset != expected.Ordinal || expected.Name is null || _bindings.Count >= 65536 || _bindings.ContainsKey(address))
        {
            throw new InvalidOperationException("Dynamic service is not an approved source-evidenced module export.");
        }

        _bindings.Add(address, new(address, Win16ProgramAddressKind.Import, identity, null));
        _services.Add(address, service);
    }

    private void ValidateStack(ushort ss, ushort sp, int count)
    {
        var segment = _memory.GetSegment(ss);
        if (!segment.CanRead || !segment.CanWrite)
        {
            throw new InvalidOperationException("Control stack requires readable/writable storage.");
        }

        for (int n = 0; n < count; n++)
        {
            _memory.ReadByte(ss, unchecked((ushort)(sp + n)));
        }
    }

    public void Invoke(ref MachineState state, Win16CodeAddress target, CallDistance distance = CallDistance.Far, ReadOnlySpan<ushort> arguments = default) => Execute(ref state, target, distance, arguments, true);

    /// <summary>Enter loader startup with its original stack; do not fabricate a procedure return frame.</summary>
    public void Enter(ref MachineState state, Win16CodeAddress target) => Execute(ref state, target, CallDistance.Far, default, false);
    private void Execute(ref MachineState state, Win16CodeAddress target, CallDistance distance, ReadOnlySpan<ushort> arguments, bool pushFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (distance is not (CallDistance.Near or CallDistance.Far) || _contexts.Count >= _maximumDepth)
        {
            throw new InvalidOperationException("Invalid invocation distance or nested execution limit.");
        }

        var entry = Resolve(target, callback: true);
        if (distance == CallDistance.Near && target.Segment != state.CS)
        {
            throw new InvalidOperationException("Near entry crosses code segments.");
        }

        // A reserved original-width token on the guest stack, never an AMD64 pointer.
        ushort token = 0xffff;
        ushort sentinelSegment = distance == CallDistance.Far
            ? (ushort)0xfff8
            : target.Segment;
        while (_bindings.ContainsKey(new(sentinelSegment, token)))
        {
            if (token == 0)
            {
                throw new InvalidOperationException("No available original-width return token.");
            }

            token--;
        }

        var sentinel = new Win16CodeAddress(sentinelSegment, token);
        if (pushFrame)
        {
            int bytes = checked(arguments.Length * 2 + (distance == CallDistance.Far
                ? 4
                : 2));
            if (bytes > 65536)
            {
                throw new InvalidOperationException("Invocation stack arguments exceed original stack width.");
            }

            ValidateStack(state.SS, unchecked((ushort)(state.SP - bytes)), bytes);
            for (int n = arguments.Length - 1; n >= 0; n--)
            {
                Win16Stack.Push(ref state, _memory, arguments[n]);
            }

            if (distance == CallDistance.Far)
            {
                Win16Stack.Push(ref state, _memory, sentinel.Segment);
            }

            Win16Stack.Push(ref state, _memory, sentinel.Offset);
        }

        var context = new Context(sentinel)
        {
            Next = entry
        };
        _contexts.Push(context);
        ushort callerCs = state.CS, callerIp = state.IP;
        try
        {
            while (!context.Returned)
            {
                if (context.Transfers >= _maximumTransfers)
                {
                    throw new InvalidOperationException("Native block transfer budget exhausted.");
                }

                context.Transfers++;
                var block = context.Next ?? throw new InvalidOperationException("Compiled block did not select a continuation.");
                context.Next = null;
                state.CS = block.Address.Segment;
                state.IP = block.Address.Offset;
                block.Entry!(ref state);
            }

            state.CS = callerCs;
            state.IP = callerIp;
        }
        finally
        {
            _contexts.Pop();
        }
    }

    private void Transfer(ref MachineState state, SegmentMemory suppliedMemory, uint value, ushort form)
    {
        if (_disposed || _contexts.Count == 0 || !ReferenceEquals(_memory, suppliedMemory) || form > 5)
        {
            throw new InvalidOperationException("Invalid native control-flow context or effect.");
        }

        var context = _contexts.Peek();
        if (context.Next is not null || context.Returned)
        {
            throw new InvalidOperationException("Block scheduled multiple continuations.");
        }

        if (form >= 4)
        {
            bool far = form == 5;
            int bytes = far
                ? 4
                : 2;
            ValidateStack(state.SS, state.SP, bytes);
            ushort ip = _memory.ReadWord(state.SS, state.SP);
            ushort cs = far
                ? _memory.ReadWord(state.SS, unchecked((ushort)(state.SP + 2)))
                : state.CS;
            var pointer = new Win16CodeAddress(cs, ip);
            var next = pointer == context.Sentinel
                ? null
                : Resolve(pointer, callback: true);
            if (value > ushort.MaxValue)
            {
                throw new InvalidOperationException("Return cleanup exceeds original width.");
            }

            state.SP = unchecked((ushort)(state.SP + bytes + value));
            state.CS = cs;
            state.IP = ip;
            if (pointer == context.Sentinel)
            {
                context.Returned = true;
            }
            else
            {
                context.Next = next;
            }

            return;
        }

        var target = new Win16CodeAddress((ushort)(value >> 16), (ushort)value);
        bool call = form is 2 or 3, farTransfer = form is 1 or 3;
        if (!farTransfer && target.Segment != state.CS)
        {
            throw new InvalidOperationException("Near control transfer crosses segments.");
        }

        var binding = Resolve(target);
        if (!call && !farTransfer && binding.Kind != Win16ProgramAddressKind.Code)
        {
            throw new InvalidOperationException("A near jump cannot enter an import namespace.");
        }

        if (binding.Kind == Win16ProgramAddressKind.Import && (!farTransfer || !_services.ContainsKey(target)))
        {
            var failure = new NotSupportedException($"Unimplemented original import {binding.Identity}; no signature or return value guessed.");
            failure.Data[UnimplementedImportIdentityKey] = binding.Identity;
            throw failure;
        }

        if (call)
        {
            int bytes = farTransfer
                ? 4
                : 2;
            ValidateStack(state.SS, unchecked((ushort)(state.SP - bytes)), bytes);
            if (farTransfer)
            {
                Win16Stack.Push(ref state, _memory, state.CS);
            }

            Win16Stack.Push(ref state, _memory, state.IP);
        }

        if (binding.Kind == Win16ProgramAddressKind.Code)
        {
            context.Next = binding;
        }
        else
        {
            ushort ss = state.SS;
            state.CS = target.Segment;
            state.IP = target.Offset;
            ushort cleanup = _services[target](ref state, _memory);
            if (state.SS != ss)
            {
                throw new InvalidOperationException("Import adapter changed the active original stack segment.");
            }

            Transfer(ref state, _memory, cleanup, 5);
        }
    }

    public void Dispose()
    {
        if (_contexts.Count != 0)
        {
            throw new InvalidOperationException("Cannot release active native program entries.");
        }

        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dispatcher.ControlTransfer = null;
        _services.Clear();
        _bindings.Clear();
        _instanceThunks.Clear();
    }
}
