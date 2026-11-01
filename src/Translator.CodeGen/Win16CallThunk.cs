using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Translator.IR;
using Win16_Runtime;

namespace Translator.CodeGen;

/// <summary>Owns an AMD64 Win16 function body with the internal native ABI (state pointer, cleanup out parameter).</summary>
public sealed class ExecutableWin16Function : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeEntry(ref MachineState state, out ushort cleanupBytes);
    private readonly IntPtr _code;
    private readonly NativeEntry _entry;
    private bool _disposed;

    public ExecutableWin16Function(MachineCodeArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Architecture != "AMD64")
        {
            throw new ArgumentException("Win16 function entry points must use AMD64 code.", nameof(artifact));
        }

        if (artifact.Relocations is { Count: > 0 })
        {
            throw new ArgumentException("Resolve helper relocations with NativeWin16Body before executing this artifact.", nameof(artifact));
        }

        if (artifact.Bytes.Length == 0)
        {
            throw new ArgumentException("Function body cannot be empty.", nameof(artifact));
        }

        if (OperatingSystem.IsWindows() != (artifact.Abi == X64Abi.Windows))
        {
            throw new PlatformNotSupportedException("Function ABI must match the host process ABI.");
        }

        IntPtr code = ExecutableMemory.Allocate(artifact.Bytes);
        try
        {
            _entry = Marshal.GetDelegateForFunctionPointer<NativeEntry>(code);
            _code = code;
        }
        catch
        {
            ExecutableMemory.Free(code, artifact.Bytes.Length);
            throw;
        }

        Size = artifact.Bytes.Length;
    }

    public int Size
    {
        get;
    }

    public ushort Invoke(ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _entry(ref state, out ushort cleanup);
        return cleanup;
    }

    /// <summary>Registers this native body behind the Win16 runtime call/return bridge.</summary>
    public void Register(Win16CallDispatcher dispatcher, string name, Win16CodeAddress address, Translator.IR.CallDistance distance)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.Register(name, address, distance, (ref MachineState state, SegmentMemory _, Win16CallDispatcher _, Win16CallContext _) => Invoke(ref state));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ExecutableMemory.Free(_code, Size);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Lowers a typed internal CALL instruction to an AMD64 runtime-dispatch thunk.</summary>
public static class Win16CallThunkGenerator
{
    public static Win16CallThunk Generate(IrInstruction call, Win16CallDispatcher dispatcher, SegmentMemory memory, X64Abi abi)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!call.Operation.Equals("CALL", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Expected CALL, received '{call.Operation}'.", nameof(call));
        }

        if (call.Operands.Count != 1 || call.Operands[0].Kind != "Symbol")
        {
            throw new NotSupportedException("Win16 internal CALL lowering currently requires one named function target operand.");
        }

        if (call.CallDistance is not (Translator.IR.CallDistance.Near or Translator.IR.CallDistance.Far))
        {
            throw new InvalidOperationException("CALL instruction has no resolved near/far distance.");
        }

        return new Win16CallThunk(dispatcher, memory, call.Operands[0].Text, call.CallDistance, abi);
    }
}

/// <summary>
/// Emits an AMD64 entry point for an internal Win16 CALL. The native stub calls a reverse-P/Invoke
/// bridge, which enters the dispatcher; registered function bodies can themselves be executable AMD64.
/// </summary>
public sealed class Win16CallThunk : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeCallBridge(IntPtr machineState, IntPtr routeHandle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeEntry(ref MachineState state);
    private sealed class Route
    {
        public required Win16CallDispatcher Dispatcher
        {
            get; init;
        }
        public required SegmentMemory Memory
        {
            get; init;
        }
        public required string FunctionName
        {
            get; init;
        }
        public required Translator.IR.CallDistance Distance
        {
            get; init;
        }
        public Exception? Error
        {
            get; set;
        }
    }

    private readonly Route _route;
    private readonly GCHandle _routeHandle;
    private readonly NativeCallBridge _bridge;
    private readonly NativeEntry _entry;
    private readonly IntPtr _code;
    private readonly object _sync = new();
    private bool _disposed;

    public Win16CallThunk(Win16CallDispatcher dispatcher, SegmentMemory memory, string functionName, Translator.IR.CallDistance distance, X64Abi abi)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        if (OperatingSystem.IsWindows() != (abi == X64Abi.Windows))
        {
            throw new PlatformNotSupportedException("Call thunk ABI must match the host process ABI.");
        }

        _route = new Route
        {
            Dispatcher = dispatcher,
            Memory = memory,
            FunctionName = functionName,
            Distance = distance
        };
        _routeHandle = GCHandle.Alloc(_route);
        _bridge = Dispatch;
        try
        {
            IntPtr bridgeAddress = Marshal.GetFunctionPointerForDelegate(_bridge);
            Artifact = new(Emit(abi, bridgeAddress, GCHandle.ToIntPtr(_routeHandle)), "AMD64", abi, "void Win16CallThunk(MachineState*)");
            IntPtr code = ExecutableMemory.Allocate(Artifact.Bytes);
            try
            {
                _entry = Marshal.GetDelegateForFunctionPointer<NativeEntry>(code);
                _code = code;
            }
            catch
            {
                ExecutableMemory.Free(code, Artifact.Bytes.Length);
                throw;
            }
        }
        catch
        {
            if (_routeHandle.IsAllocated)
            {
                _routeHandle.Free();
            }

            throw;
        }
    }

    public MachineCodeArtifact Artifact
    {
        get;
    }

    public void Invoke(ref MachineState state)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _route.Error = null;
            _entry(ref state);
            if (_route.Error is not null)
            {
                throw new InvalidOperationException($"Win16 call to '{_route.FunctionName}' failed.", _route.Error);
            }
        }
    }

    private static void Dispatch(IntPtr stateAddress, IntPtr routeAddress)
    {
        var route = (Route?)GCHandle.FromIntPtr(routeAddress).Target ?? throw new InvalidOperationException("Win16 call route has expired.");
        MachineState state = Marshal.PtrToStructure<MachineState>(stateAddress);
        try
        {
            route.Dispatcher.Call(ref state, route.Memory, route.FunctionName, route.Distance);
        }
        catch (Exception error)
        {
            route.Error = error;
        }
        finally
        {
            Marshal.StructureToPtr(state, stateAddress, false);
        }
    }

    private static byte[] Emit(X64Abi abi, IntPtr bridge, IntPtr route)
    {
        var bytes = new List<byte>();
        // At function entry RSP is 8 mod 16. Align it before the bridge call; Windows also needs shadow space.
        if (abi == X64Abi.Windows)
        {
            bytes.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });
        }
        else
        {
            bytes.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x08 });
        }

        // The state pointer is already arg0 (RCX on Win64, RDI on System V); set route as arg1.
        bytes.AddRange(abi == X64Abi.Windows
            ? new byte[] { 0x48, 0xBA }
            : new byte[] { 0x48, 0xBE });
        AddInt64(bytes, route.ToInt64());
        bytes.AddRange(new byte[] { 0x48, 0xB8 });
        AddInt64(bytes, bridge.ToInt64());
        bytes.AddRange(new byte[] { 0xFF, 0xD0 }); // call rax
        if (abi == X64Abi.Windows)
        {
            bytes.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });
        }
        else
        {
            bytes.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x08 });
        }

        bytes.Add(0xC3);
        return bytes.ToArray();
    }

    private static void AddInt64(List<byte> bytes, long value)
    {
        Span<byte> encoded = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, value);
        bytes.AddRange(encoded.ToArray());
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ExecutableMemory.Free(_code, Artifact.Bytes.Length);
            if (_routeHandle.IsAllocated)
            {
                _routeHandle.Free();
            }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}
