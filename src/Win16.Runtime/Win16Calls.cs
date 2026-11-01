using Translator.IR;

namespace Win16_Runtime;

public readonly record struct Win16CodeAddress(ushort Segment, ushort Offset);
public readonly record struct Win16CallContext(string FunctionName, Win16CodeAddress Target, CallDistance Distance, Win16CodeAddress Caller, ushort EntryStackPointer, bool IsRuntimeService = false);
/// <summary>
/// Adapter boundary for a translated Win16 function. The function body may be backed by a native
/// AMD64 entry point, but it receives and updates the explicit emulated machine state.
/// It returns the RET/RETF immediate cleanup byte count; return distance is fixed at registration.
/// </summary>
public delegate ushort Win16TranslatedFunction(ref MachineState state, SegmentMemory memory, Win16CallDispatcher dispatcher, Win16CallContext context);
/// <summary>
/// Win16 C functions in this corpus preserve BP, DS, SI, and DI. AX, BX, CX, DX, and ES are
/// caller-saved. SP and the return address are managed by the call dispatcher.
/// </summary>
public static class Win16CallingConvention
{
    public static readonly IReadOnlyList<string> PreservedRegisters = new[]
    {
        "BP",
        "DS",
        "SI",
        "DI"
    };

    internal static Snapshot Capture(in MachineState state) => new(state.BP, state.DS, state.SI, state.DI);

    internal static void Restore(ref MachineState state, Snapshot snapshot)
    {
        state.BP = snapshot.BP;
        state.DS = snapshot.DS;
        state.SI = snapshot.SI;
        state.DI = snapshot.DI;
    }

    internal readonly record struct Snapshot(ushort BP, ushort DS, ushort SI, ushort DI);
}

public delegate void Win16ControlTransfer(ref MachineState state, SegmentMemory memory, uint value, ushort form);
public delegate void Win16InterruptService(ref MachineState state, SegmentMemory memory, byte vector);
/// <summary>Resolves translated functions and applies their Win16 stack/calling-convention effects.</summary>
public sealed class Win16CallDispatcher
{
    private sealed record Function(string Name, Win16CodeAddress Address, CallDistance Distance, Win16TranslatedFunction Body, bool IsRuntimeService, Win16ApiMetadata? Api = null);
    private readonly Dictionary<string, Function> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Win16CodeAddress, Function> _byAddress = new();
    public Win16ControlTransfer? ControlTransfer
    {
        get; set;
    }

    private readonly Dictionary<byte, Win16InterruptService> _interrupts = new();

    public void RegisterInterrupt(byte vector, Win16InterruptService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!_interrupts.TryAdd(vector, service))
        {
            throw new InvalidOperationException($"Interrupt {vector:X2} already has a service adapter.");
        }
    }

    /// <summary>One software interrupt with a real 16-bit frame; the adapter returns flags through the saved frame.
    /// No original interrupt handler instructions are interpreted or selected implicitly.</summary>
    public void Interrupt(ref MachineState state, SegmentMemory memory, byte vector)
    {
        if (!_interrupts.TryGetValue(vector, out var service))
        {
            throw new NotSupportedException($"No interrupt service adapter for vector {vector:X2}.");
        }

        ushort ss = state.SS, sp = state.SP, cs = state.CS, ip = state.IP, flags = state.Flags;
        // Validate the entire frame before modifying CPU state or memory.
        foreach (int delta in new[]
        {
            2,
            4,
            6
        }

        )
        {
            var segment = memory.GetSegment(ss);
            if (!segment.CanWrite)
            {
                throw new InvalidOperationException("Interrupt stack is read-only.");
            }

            memory.ReadWord(ss, unchecked((ushort)(sp - delta)));
        }

        Win16Stack.Push(ref state, memory, flags);
        Win16Stack.Push(ref state, memory, cs);
        Win16Stack.Push(ref state, memory, ip);
        state.Flags = (ushort)(flags & ~0x300); // interrupt entry clears IF and TF
        try
        {
            service(ref state, memory, vector);
            if (state.SS != ss || state.SP != unchecked((ushort)(sp - 6)))
            {
                throw new InvalidOperationException("Interrupt service corrupted the original stack frame.");
            }

            if (memory.ReadWord(ss, state.SP) != ip || memory.ReadWord(ss, unchecked((ushort)(state.SP + 2))) != cs)
            {
                throw new NotSupportedException("Interrupt return to a different original address requires verified control-flow resolution.");
            }

            state.IP = Win16Stack.Pop(ref state, memory);
            state.CS = Win16Stack.Pop(ref state, memory);
            state.Flags = Win16Stack.Pop(ref state, memory);
        }
        catch
        {
            state.SS = ss;
            state.SP = sp;
            state.CS = cs;
            state.IP = ip;
            state.Flags = flags;
            throw;
        }
    }

    public void Register(string name, Win16CodeAddress address, CallDistance distance, Win16TranslatedFunction body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        if (distance is not (CallDistance.Near or CallDistance.Far))
        {
            throw new ArgumentException("Translated functions must declare near or far entry distance.", nameof(distance));
        }

        if (_byName.ContainsKey(name))
        {
            throw new InvalidOperationException($"Translated function '{name}' is already registered.");
        }

        if (_byAddress.ContainsKey(address))
        {
            throw new InvalidOperationException($"Translated function address {address.Segment:X4}:{address.Offset:X4} is already registered.");
        }

        var function = new Function(name, address, distance, body, false);
        _byName.Add(name, function);
        _byAddress.Add(address, function);
    }

    /// <summary>Registers a runtime API/service through the same Win16 call and stack boundary as translated functions.</summary>
    public void RegisterRuntimeService(string name, Win16CodeAddress address, CallDistance distance, Win16TranslatedFunction body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        if (distance is not (CallDistance.Near or CallDistance.Far))
        {
            throw new ArgumentException("Runtime services must declare near or far entry distance.", nameof(distance));
        }

        if (_byName.ContainsKey(name))
        {
            throw new InvalidOperationException($"Win16 target '{name}' is already registered.");
        }

        if (_byAddress.ContainsKey(address))
        {
            throw new InvalidOperationException($"Win16 target address {address.Segment:X4}:{address.Offset:X4} is already registered.");
        }

        var service = new Function(name, address, distance, body, true);
        _byName.Add(name, service);
        _byAddress.Add(address, service);
    }

    public void RegisterApi(Win16ApiMetadata api, ushort segment, Win16TranslatedFunction implementation)
    {
        if (!api.Supported)
        {
            throw new NotSupportedException($"Unsupported import {api.Identity.CanonicalName}");
        }

        RegisterRuntimeService(api.Identity.CanonicalName, new(segment, api.EntryOffset), CallDistance.Far, (ref MachineState state, SegmentMemory memory, Win16CallDispatcher dispatcher, Win16CallContext call) =>
        {
            implementation(ref state, memory, dispatcher, call);
            return api.CleanupBytes;
        });
        var registered = _byName[api.Identity.CanonicalName] with
        {
            Api = api
        };
        _byName[api.Identity.CanonicalName] = registered;
        _byAddress[registered.Address] = registered;
    }

    public bool IsTranslatedFar(Win16CodeAddress target) => _byAddress.TryGetValue(target, out var function) && !function.IsRuntimeService && function.Distance == CallDistance.Far;

    /// <summary>Requires a registry-backed service registration, not merely a matching symbol name.</summary>
    public bool TryResolveApi(Win16ApiMetadata api, out Win16CodeAddress address)
    {
        if (_byName.TryGetValue(api.Identity.CanonicalName, out var function) && function.IsRuntimeService && function.Api == api)
        {
            address = function.Address;
            return true;
        }

        address = default;
        return false;
    }

    public bool TryResolve(string name, out Win16CodeAddress address)
    {
        if (_byName.TryGetValue(name, out var function))
        {
            address = function.Address;
            return true;
        }

        address = default;
        return false;
    }

    public void Call(ref MachineState state, SegmentMemory memory, string functionName, CallDistance distance)
    {
        if (!_byName.TryGetValue(functionName, out var function))
        {
            throw new KeyNotFoundException($"No translated function named '{functionName}' is registered.");
        }

        Call(ref state, memory, function, distance);
    }

    public void Call(ref MachineState state, SegmentMemory memory, Win16CodeAddress target, CallDistance distance)
    {
        if (!_byAddress.TryGetValue(target, out var function))
        {
            throw new KeyNotFoundException($"No translated function at {target.Segment:X4}:{target.Offset:X4} is registered.");
        }

        Call(ref state, memory, function, distance);
    }

    public ushort Tail(ref MachineState state, SegmentMemory memory, Win16CodeAddress target, bool farJump)
    {
        if (!_byAddress.TryGetValue(target, out var function) || function.IsRuntimeService || (!farJump && target.Segment != state.CS))
        {
            throw new InvalidOperationException($"Invalid translated tail target {target.Segment:X4}:{target.Offset:X4}.");
        }

        state.CS = target.Segment;
        state.IP = target.Offset;
        return function.Body(ref state, memory, this, new(function.Name, target, farJump
            ? CallDistance.Far
            : CallDistance.Near, target, state.SP));
    }

    private void Call(ref MachineState state, SegmentMemory memory, Function function, CallDistance distance)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (distance is not (CallDistance.Near or CallDistance.Far))
        {
            throw new ArgumentException("Call distance must be near or far.", nameof(distance));
        }

        if (distance != function.Distance)
        {
            throw new InvalidOperationException($"Call distance {distance} does not match {function.Distance} entry '{function.Name}'.");
        }

        if (distance == CallDistance.Near && state.CS != function.Address.Segment)
        {
            throw new InvalidOperationException($"Near call to '{function.Name}' crosses from CS 0x{state.CS:X4} to segment 0x{function.Address.Segment:X4}.");
        }

        var caller = new Win16CodeAddress(state.CS, state.IP);
        ushort callerSs = state.SS;
        var preserved = Win16CallingConvention.Capture(state);
        if (distance == CallDistance.Near)
        {
            Win16Stack.CallNear(ref state, memory, function.Address.Offset);
        }
        else
        {
            Win16Stack.CallFar(ref state, memory, function.Address.Segment, function.Address.Offset);
        }

        var context = new Win16CallContext(function.Name, function.Address, distance, caller, state.SP, function.IsRuntimeService);
        ushort cleanupBytes = function.Body(ref state, memory, this, context);
        // SS remains the active stack segment for an ordinary Win16 near/far call.
        state.SS = callerSs;
        if (function.Distance == CallDistance.Near)
        {
            Win16Stack.ReturnNear(ref state, memory, cleanupBytes);
        }
        else
        {
            Win16Stack.ReturnFar(ref state, memory, cleanupBytes);
        }

        // CS is unchanged by a near transfer. A far return restored it from the emulated stack.
        if (distance == CallDistance.Near)
        {
            state.CS = caller.Segment;
        }

        Win16CallingConvention.Restore(ref state, preserved);
    }
}

public static class Win16StackArguments
{
    /// <summary>Reads a word at the function-entry stack pointer. Word zero is the topmost argument word.</summary>
    public static ushort ReadWord(SegmentMemory memory, ushort ss, ushort entryStackPointer, CallDistance returnDistance, int wordIndex)
    {
        ushort address = ArgumentAddress(entryStackPointer, returnDistance, wordIndex);
        return memory.ReadWord(ss, address);
    }

    public static void WriteWord(SegmentMemory memory, ushort ss, ushort entryStackPointer, CallDistance returnDistance, int wordIndex, ushort value)
    {
        ushort address = ArgumentAddress(entryStackPointer, returnDistance, wordIndex);
        memory.WriteWord(ss, address, value);
    }

    /// <summary>Reads offset then segment from adjacent stack words; callers push segment before offset.</summary>
    public static FarPointer ReadFarPointer(SegmentMemory memory, ushort ss, ushort entryStackPointer, CallDistance returnDistance, int firstWordIndex = 0)
    {
        ushort offset = ReadWord(memory, ss, entryStackPointer, returnDistance, firstWordIndex);
        ushort segment = ReadWord(memory, ss, entryStackPointer, returnDistance, checked(firstWordIndex + 1));
        return new FarPointer(offset, segment);
    }

    public static void WriteFarPointer(SegmentMemory memory, ushort ss, ushort entryStackPointer, CallDistance returnDistance, FarPointer pointer, int firstWordIndex = 0)
    {
        WriteWord(memory, ss, entryStackPointer, returnDistance, firstWordIndex, pointer.Offset);
        WriteWord(memory, ss, entryStackPointer, returnDistance, checked(firstWordIndex + 1), pointer.Segment);
    }

    private static ushort ArgumentAddress(ushort entryStackPointer, CallDistance returnDistance, int wordIndex)
    {
        if (wordIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wordIndex));
        }

        int returnBytes = returnDistance switch
        {
            CallDistance.Near => 2,
            CallDistance.Far => 4,
            _ => throw new ArgumentException("Return distance must be near or far.", nameof(returnDistance))
        };
        return unchecked((ushort)(entryStackPointer + returnBytes + checked(wordIndex * 2)));
    }
}

public static class Win16Stack
{
    public static void Push(ref MachineState state, SegmentMemory memory, ushort value)
    {
        state.SP = unchecked((ushort)(state.SP - 2));
        memory.WriteWord(state.SS, state.SP, value);
    }

    public static ushort Pop(ref MachineState state, SegmentMemory memory)
    {
        ushort value = memory.ReadWord(state.SS, state.SP);
        state.SP = unchecked((ushort)(state.SP + 2));
        return value;
    }

    public static void PushFarPointer(ref MachineState state, SegmentMemory memory, FarPointer pointer)
    {
        Push(ref state, memory, pointer.Segment);
        Push(ref state, memory, pointer.Offset);
    }

    public static FarPointer PopFarPointer(ref MachineState state, SegmentMemory memory)
    {
        ushort offset = Pop(ref state, memory);
        ushort segment = Pop(ref state, memory);
        return new(offset, segment);
    }

    public static void CallNear(ref MachineState state, SegmentMemory memory, ushort targetIp)
    {
        Push(ref state, memory, state.IP);
        state.IP = targetIp;
    }

    public static void ReturnNear(ref MachineState state, SegmentMemory memory, ushort cleanup = 0)
    {
        state.IP = Pop(ref state, memory);
        state.SP = unchecked((ushort)(state.SP + cleanup));
    }

    public static void CallFar(ref MachineState state, SegmentMemory memory, ushort targetCs, ushort targetIp)
    {
        Push(ref state, memory, state.CS);
        Push(ref state, memory, state.IP);
        state.CS = targetCs;
        state.IP = targetIp;
    }

    public static void ReturnFar(ref MachineState state, SegmentMemory memory, ushort cleanup = 0)
    {
        state.IP = Pop(ref state, memory);
        state.CS = Pop(ref state, memory);
        state.SP = unchecked((ushort)(state.SP + cleanup));
    }
}
