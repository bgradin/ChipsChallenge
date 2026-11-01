using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Win16_Runtime;

namespace Translator.CodeGen;

/// <summary>Executes the same relocatable body used in a PE, with helpers bound to the shared runtime.</summary>
public sealed class NativeWin16Body : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort Entry(IntPtr context, ref MachineState state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort ReadHelper(IntPtr context, ushort segment, ushort offset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WriteHelper(IntPtr context, ushort segment, ushort offset, ushort value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ServiceHelper(IntPtr context, IntPtr state, ushort service);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ReadValue(IntPtr context, ushort segment, ushort offset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WriteValue(IntPtr context, ushort segment, ushort offset, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OperationHelper(IntPtr context, IntPtr state, uint value, ushort form);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort TailHelper(IntPtr context, IntPtr state, uint pointer, ushort farJump);
    private sealed class Route
    {
        public required SegmentMemory Memory;
        public required Win16CallDispatcher Dispatcher;
        public Exception? Error;
    }

    private static readonly ReadHelper _read = (context, segment, offset) =>
    {
        var route = Get(context);
        if (route.Error is not null)
        {
            return 0;
        }

        try
        {
            return route.Memory.ReadWord(segment, offset);
        }
        catch (Exception error)
        {
            route.Error = error;
            return 0;
        }
    };
    private static readonly WriteHelper _write = (context, segment, offset, value) =>
    {
        var route = Get(context);
        if (route.Error is not null)
        {
            return;
        }

        try
        {
            route.Memory.WriteWord(segment, offset, value);
        }
        catch (Exception error)
        {
            route.Error = error;
        }
    };
    private static readonly ServiceHelper _service = (context, stateAddress, service) =>
    {
        var route = Get(context);
        if (route.Error is not null)
        {
            return;
        }

        var state = Marshal.PtrToStructure<MachineState>(stateAddress);
        try
        {
            string name = Win16ApiRegistry.Get(service).Identity.CanonicalName;
            route.Dispatcher.Call(ref state, route.Memory, name, Translator.IR.CallDistance.Far);
        }
        catch (Exception error)
        {
            route.Error = error;
        }
        finally
        {
            Marshal.StructureToPtr(state, stateAddress, false);
        }
    };
    private static readonly ReadValue _readByte = (c, s, o) => GuardRead(c, () => Get(c).Memory.ReadByte(s, o));
    private static readonly ReadValue _readDword = (c, s, o) => GuardRead(c, () => Get(c).Memory.ReadDword(s, o));
    private static readonly WriteValue _writeByte = (c, s, o, v) => Guard(c, () => Get(c).Memory.WriteByte(s, o, (byte)v));
    private static readonly WriteValue _writeDword = (c, s, o, v) => Guard(c, () => Get(c).Memory.WriteDword(s, o, v));
    private static readonly OperationHelper _stringOperation = (c, s, v, f) => StateOperation(c, s, (ref MachineState state) => Win16InstructionServices.String(ref state, Get(c).Memory, (ushort)v, (Translator.IR.RepeatPrefix)f));
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort InterruptHelper(IntPtr context, IntPtr state, uint vector, ushort form);
    private static readonly InterruptHelper _interrupt = (c, s, v, f) =>
    {
        StateOperation(c, s, (ref MachineState state) => Get(c).Dispatcher.Interrupt(ref state, Get(c).Memory, checked((byte)v)));
        return Get(c).Error is null
            ? (ushort)1
            : (ushort)0;
    };
    private static readonly OperationHelper _flow = (c, s, v, f) => StateOperation(c, s, (ref MachineState state) =>
    {
        var route = Get(c);
        var transfer = route.Dispatcher.ControlTransfer ?? throw new InvalidOperationException("No whole-program control context.");
        transfer(ref state, route.Memory, v, f);
    });
    private static readonly OperationHelper _divide = (c, s, v, f) => StateOperation(c, s, (ref MachineState state) => Win16InstructionServices.Divide(ref state, v, f));
    private static readonly OperationHelper _target = (c, s, v, f) => StateOperation(c, s, (ref MachineState state) => Get(c).Dispatcher.Call(ref state, Get(c).Memory, new Win16CodeAddress((ushort)(v >> 16), (ushort)v), f == 1
        ? Translator.IR.CallDistance.Far
        : Translator.IR.CallDistance.Near));
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FaultHelper(IntPtr context, IntPtr state);
    private static readonly FaultHelper _dispatchFault = (c, s) => Get(c).Error ??= new InvalidOperationException("Unapproved original indirect target or table index.");
    private static readonly TailHelper _tail = (c, address, pointer, farJump) =>
    {
        var route = Get(c);
        if (route.Error is not null)
        {
            return 0;
        }

        var state = Marshal.PtrToStructure<MachineState>(address);
        try
        {
            return route.Dispatcher.Tail(ref state, route.Memory, new((ushort)(pointer >> 16), (ushort)pointer), farJump != 0);
        }
        catch (Exception error)
        {
            route.Error = error;
            return 0;
        }
        finally
        {
            Marshal.StructureToPtr(state, address, false);
        }
    };
    private delegate void StateAction(ref MachineState state);

    private static void StateOperation(IntPtr context, IntPtr address, StateAction action)
    {
        if (Get(context).Error is not null)
        {
            return;
        }

        var state = Marshal.PtrToStructure<MachineState>(address);
        try
        {
            action(ref state);
        }
        catch (Exception error)
        {
            Get(context).Error = error;
        }
        finally
        {
            Marshal.StructureToPtr(state, address, false);
        }
    }

    private static uint GuardRead(IntPtr context, Func<uint> read)
    {
        if (Get(context).Error is not null)
        {
            return 0;
        }

        try
        {
            return read();
        }
        catch (Exception error)
        {
            Get(context).Error = error;
            return 0;
        }
    }

    private static void Guard(IntPtr context, Action write)
    {
        if (Get(context).Error is not null)
        {
            return;
        }

        try
        {
            write();
        }
        catch (Exception error)
        {
            Get(context).Error = error;
        }
    }

    private readonly IntPtr _address;
    private readonly int _size;
    private readonly Entry _entry;
    private readonly Dictionary<string, Entry> _namedEntries = new(StringComparer.Ordinal);
    private bool _disposed;
    private readonly object _sync = new();
    public NativeWin16Body(MachineCodeArtifact artifact, IReadOnlyDictionary<string, int>? entryPoints = null)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("Native Win16 bodies require an AMD64 process.");
        }

        if (artifact.Abi != X64Abi.Windows || artifact.Architecture != "AMD64")
        {
            throw new ArgumentException("Expected an AMD64 Windows ABI artifact.", nameof(artifact));
        }

        var bytes = new List<byte>();
        var wrappers = new List<(string Name, int WrapperOffset, int CallOffset, int TargetOffset)>();
        entryPoints ??= new Dictionary<string, int>
        {
            ["entry"] = 0
        };
        if (entryPoints.Count == 0)
        {
            throw new ArgumentException("At least one native entry is required.");
        }

        foreach (var point in entryPoints)
        {
            if (point.Value < 0 || point.Value >= artifact.Bytes.Length)
            {
                throw new ArgumentException("Native entry is outside the linked artifact.");
            }

            while (bytes.Count % 16 != 0)
            {
                bytes.Add(0x90);
            }

            int wrapper = bytes.Count;
            if (OperatingSystem.IsWindows())
            {
                bytes.AddRange(new byte[] { 0xE9, 0, 0, 0, 0 });
            }
            else
            {
                bytes.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x89, 0xF9, 0x48, 0x89, 0xF2, 0xE8, 0, 0, 0, 0, 0x48, 0x83, 0xC4, 0x28, 0xC3 });
            }

            wrappers.Add((point.Key, wrapper, wrapper + (OperatingSystem.IsWindows()
                ? 1
                : 11), point.Value));
        }

        while (bytes.Count % 16 != 0)
        {
            bytes.Add(0x90);
        }

        int bodyOffset = bytes.Count;
        bytes.AddRange(artifact.Bytes);
        var symbols = new Dictionary<string, ulong>();
        foreach (var (name, callback) in new (string, Delegate)[]
        {
            (Win16HelperSymbols.ReadWordHelperSymbol, _read),
            (Win16HelperSymbols.WriteWordHelperSymbol, _write),
            (Win16HelperSymbols.CallServiceHelperSymbol, _service),
            ("__win16_read_byte", _readByte),
            ("__win16_read_dword", _readDword),
            ("__win16_write_byte", _writeByte),
            ("__win16_write_dword", _writeDword),
            ("__win16_string", _stringOperation),
            ("__win16_divide", _divide),
            ("__win16_interrupt", _interrupt),
            ("__win16_flow_transfer", _flow),
            ("__win16_call_target", _target),
            ("__win16_dispatch_fault", _dispatchFault),
            ("__win16_tail_target", _tail)
        }

        )
        {
            symbols.Add(name, (ulong)bytes.Count);
            bytes.AddRange(HelperAdapter(Marshal.GetFunctionPointerForDelegate(callback)));
        }

        byte[] relocated = MachineCodeArtifactRelocator.Relocate(artifact, (ulong)bodyOffset, symbols);
        relocated.CopyTo(CollectionsMarshal.AsSpan(bytes).Slice(bodyOffset));
        foreach (var wrapper in wrappers)
        {
            BinaryPrimitives.WriteInt32LittleEndian(CollectionsMarshal.AsSpan(bytes).Slice(wrapper.CallOffset, 4), bodyOffset + wrapper.TargetOffset - wrapper.CallOffset - 4);
        }

        _size = bytes.Count;
        _address = ExecutableMemory.Allocate(bytes.ToArray());
        try
        {
            foreach (var wrapper in wrappers)
            {
                _namedEntries.Add(wrapper.Name, Marshal.GetDelegateForFunctionPointer<Entry>(_address + wrapper.WrapperOffset));
            }

            _entry = _namedEntries[wrappers[0].Name];
        }
        catch
        {
            ExecutableMemory.Free(_address, _size);
            throw;
        }
    }

    public ushort Invoke(ref MachineState state, SegmentMemory memory, Win16CallDispatcher dispatcher, string? nativeEntryName = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var route = new Route
            {
                Memory = memory,
                Dispatcher = dispatcher
            };
            var handle = GCHandle.Alloc(route);
            try
            {
                ushort cleanup = (nativeEntryName is null
                    ? _entry
                    : _namedEntries[nativeEntryName])(GCHandle.ToIntPtr(handle), ref state);
                if (route.Error is not null)
                {
                    throw new InvalidOperationException("Native Win16 runtime helper failed.", route.Error);
                }

                return cleanup;
            }
            finally
            {
                handle.Free();
            }
        }
    }

    public void Register(Win16CallDispatcher dispatcher, SegmentMemory memory, string name, Win16CodeAddress target, Translator.IR.CallDistance distance, string? nativeEntryName = null) => dispatcher.Register(name, target, distance, (ref MachineState state, SegmentMemory callMemory, Win16CallDispatcher calls, Win16CallContext _) => Invoke(ref state, callMemory, calls, nativeEntryName));

    private static Route Get(IntPtr context) => (Route)GCHandle.FromIntPtr(context).Target!;
    private static byte[] HelperAdapter(IntPtr callback)
    {
        var bytes = new List<byte>();
        if (!OperatingSystem.IsWindows())
        {
            // Win64 preserves RDI, RSI and XMM6..15; SysV callbacks may overwrite them.
            bytes.AddRange(new byte[] { 0x57, 0x56, 0x48, 0x81, 0xEC, 0xA8, 0, 0, 0 });
            for (int reg = 6; reg < 16; reg++)
            {
                SaveXmm(false, reg);
            }

            bytes.AddRange(new byte[] { 0x48, 0x89, 0xCF, 0x48, 0x89, 0xD6, 0x4C, 0x89, 0xC2, 0x4C, 0x89, 0xC9 });
        }

        bytes.AddRange(new byte[] { 0x48, 0xB8 });
        bytes.AddRange(BitConverter.GetBytes(callback.ToInt64()));
        if (OperatingSystem.IsWindows())
        {
            bytes.AddRange(new byte[] { 0xFF, 0xE0 });
        }
        else
        {
            bytes.AddRange(new byte[] { 0xFF, 0xD0 });
            for (int reg = 6; reg < 16; reg++)
            {
                SaveXmm(true, reg);
            }

            bytes.AddRange(new byte[] { 0x48, 0x81, 0xC4, 0xA8, 0, 0, 0, 0x5E, 0x5F, 0xC3 });
        }

        return bytes.ToArray();

        void SaveXmm(bool load, int reg)
        {
            bytes.Add(0xF3);
            if (reg >= 8)
            {
                bytes.Add(0x44);
            }

            bytes.AddRange(new byte[] { 0x0F, load
                ? (byte)0x6F
                : (byte)0x7F, (byte)(0x84 | ((reg & 7) << 3)), 0x24 });
            bytes.AddRange(BitConverter.GetBytes((reg - 6) * 16));
        }

    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ExecutableMemory.Free(_address, _size);
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    ~NativeWin16Body()
    {
        if (!_disposed)
        {
            ExecutableMemory.Free(_address, _size);
        }
    }
}
