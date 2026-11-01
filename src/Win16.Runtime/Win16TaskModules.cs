using System.Runtime.InteropServices;
using static Win16_Runtime.Win16NativeBuffers;

namespace Win16_Runtime;

public interface IWin16MultimediaBackend : IDisposable
{
    bool Load();

    bool HasExport(string name);

    nint Call(string name, ReadOnlySpan<nint> arguments);

    void Unload();
}

public sealed class WindowsTaskMultimediaBackend : IWin16MultimediaBackend
{
    private nint _library;
    private readonly WindowsTaskGuiBackend _calls = new();

    public bool Load() => _library != 0 || NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "winmm.dll"), out _library);

    public bool HasExport(string name) => _library != 0 && NativeLibrary.TryGetExport(_library, name, out _);

    public nint Call(string name, ReadOnlySpan<nint> arguments)
    {
        if (_library == 0)
        {
            throw new InvalidOperationException("Multimedia module is unloaded.");
        }

        return _calls.Call("WINMM", name, arguments);
    }

    public void Unload()
    {
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }

    public void Dispose()
    {
        Unload();
        _calls.Dispose();
    }
}

/// <summary>Only source-evidenced Win16 MMSYSTEM exports acquire callable guest addresses.
/// WinMM procedure pointers remain in the provider; guest calls select fixed service entries.</summary>
public sealed class Win16TaskModules : IDisposable
{
    private readonly SegmentMemory _memory;
    private readonly IWin16MultimediaBackend _backend;
    private readonly Win16GuiHandles _handles;
    private readonly string _directory;
    private bool _disposed;
    private int _references;
    private readonly Dictionary<ushort, uint> _devices = [];
    private int _nextDevice = 1;
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "KERNEL.#50",
        "KERNEL.#95",
        "KERNEL.#96"
    };
    public static IReadOnlyList<(string Name, ushort Ordinal, string Native, ushort Cleanup)> Procedures { get; } = Array.AsReadOnly(new[] { ("sndPlaySound", (ushort)2, "sndPlaySoundW", (ushort)6), ("midiOutGetNumDevs", (ushort)201, "midiOutGetNumDevs", (ushort)0), ("waveOutGetNumDevs", (ushort)401, "waveOutGetNumDevs", (ushort)0), ("mciSendCommand", (ushort)701, "mciSendCommandW", (ushort)12), ("mciGetErrorString", (ushort)706, "mciGetErrorStringW", (ushort)10) });

    public Win16TaskModules(SegmentMemory memory, Win16ProgramControl control, IWin16MultimediaBackend backend, Win16GuiHandles handles, string directory)
    {
        _memory = memory;
        _backend = backend;
        _handles = handles;
        _directory = Path.GetFullPath(directory);
        foreach (var p in Procedures)
        {
            string name = p.Name;
            control.RegisterModuleProcedure(new(0xf100, p.Ordinal), "MMSYSTEM." + name, (ref MachineState state, SegmentMemory _) => InvokeProcedure(name, ref state));
        }
    }

    public ushort GuestDevice(uint native) => _devices.FirstOrDefault(p => p.Value == native).Key;
    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var c = Win16ExportContracts.Get(identity);
        var saved = state;

        uint Arg(int i) => c.Argument(i, _memory, saved);

        uint result = 0;
        if (c.Ordinal == 95)
        {
            string path = Win16TaskKernelServices.ReadString(_memory, Arg(0));
            string file = Path.GetFileName(path.Replace('\\', '/'));
            if (file.Equals("MMSYSTEM.DLL", StringComparison.OrdinalIgnoreCase) || file.Equals("MMSYSTEM", StringComparison.OrdinalIgnoreCase))
            {
                if (_backend.Load())
                {
                    _references++;
                    result = 0x600;
                }
            }
            else
            {
                result = 2;
            }
        }
        else if (c.Ordinal == 96)
        {
            if (Arg(0) == 0x600 && _references > 0 && --_references == 0)
            {
                CloseDevices();
                _backend.Unload();
            }
        }
        else if (Arg(0) == 0x600 && _references > 0)
        {
            uint name = Arg(1);
            var p = (name >> 16) == 0
                ? Procedures.FirstOrDefault(p => p.Ordinal == name)
                : Procedures.FirstOrDefault(p => p.Name.Equals(Win16TaskKernelServices.ReadString(_memory, name), StringComparison.Ordinal));
            if (p.Name is not null && _backend.HasExport(p.Native))
            {
                result = 0xf1000000u | p.Ordinal;
            }
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

    public ushort InvokeProcedure(string name, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_references == 0)
        {
            throw new InvalidOperationException("Call through an unloaded module procedure.");
        }

        var p = Procedures.Single(p => p.Name == name);
        using var b = new Win16NativeBuffers(_memory);
        uint result = 0;
        var frame = state;

        ushort Word(int n) => _memory.ReadWord(frame.SS, (ushort)(frame.SP + 4 + n * 2));


        uint Dword(int n) => _memory.ReadDword(frame.SS, (ushort)(frame.SP + 4 + n * 2));


        nint Call(params nint[] args) => _backend.Call(p.Native, args);

        if (name is "midiOutGetNumDevs" or "waveOutGetNumDevs")
        {
            result = unchecked((ushort)Call());
        }
        else if (name == "sndPlaySound")
        {
            uint sound = Dword(1);
            ushort flags = Word(0);
            nint text = 0;
            if (sound != 0)
            {
                string path = Win16TaskKernelServices.ReadString(_memory, sound);
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(_directory, path.Replace('\\', Path.DirectorySeparatorChar));
                }

                text = b.Wide(path);
            }

            if ((flags & 4) != 0)
            {
                throw new NotSupportedException("SND_MEMORY requires an owned WAVE resource lifetime.");
            }

            result = (uint)Call(text, flags);
        }
        else if (name == "mciGetErrorString")
        {
            int capacity = Word(0);
            uint destination = Dword(1), error = Dword(3);
            if (capacity > 0)
            {
                b.ValidateWrite(destination, capacity);
                nint text = b.Alloc(capacity * 2);
                result = (uint)Call((nint)(nuint)error, text, capacity);
                if (result != 0)
                {
                    string value = Marshal.PtrToStringUni(text) ?? "";
                    byte[] output = Ansi.GetBytes(value);
                    int n = Math.Min(output.Length, capacity - 1);
                    byte[] terminated = new byte[n + 1];
                    output.AsSpan(0, n).CopyTo(terminated);
                    b.Write(destination, terminated);
                }
            }
        }
        else
        {
            result = Mci(Word(5), Word(4), Dword(2), Dword(0), b);
        }

        state.AX = (ushort)result;
        if (name == "mciSendCommand")
        {
            state.DX = (ushort)(result >> 16);
        }

        return p.Cleanup;
    }

    private uint Mci(ushort device, ushort command, uint flags, uint pointer, Win16NativeBuffers b)
    {
        nint Call(params nint[] args) => _backend.Call("mciSendCommandW", args);

        if (command == 0x803)
        {
            byte[] source = b.Read(pointer, 20);
            b.ValidateWrite(pointer, 20);

            uint D(int n) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(n));

            nint data = b.Alloc(36);
            nint callback = 0;
            if ((flags & 1) != 0)
            {
                callback = Window((ushort)D(0));
            }

            WP(data, 0, callback);
            uint type = D(8), element = D(12), alias = D(16);
            WP(data, 12, (flags & 0x1000) != 0
                ? (nint)type
                : b.String(type));
            if ((flags & 0x200) != 0)
            {
                string path = Win16TaskKernelServices.ReadString(_memory, element);
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(_directory, path.Replace('\\', Path.DirectorySeparatorChar));
                }

                WP(data, 20, b.Wide(path));
            }

            if ((flags & 0x400) != 0)
            {
                WP(data, 28, b.String(alias));
            }

            uint error = unchecked((uint)(nuint)Call(device, command, (nint)(nuint)flags, data));
            if (error != 0)
            {
                return error;
            }

            uint native = U32(data, 8);
            if (_nextDevice >= 65535)
            {
                Call((nint)native, 0x804, 0, 0);
                return 264;
            }

            ushort id = (ushort)_nextDevice++;
            _devices.Add(id, native);
            _memory.WriteWord((ushort)(pointer >> 16), (ushort)(pointer + 4), id);
            return 0;
        }

        if (!_devices.TryGetValue(device, out uint nativeDevice))
        {
            return 257;
        }

        if (command == 0x804)
        {
            uint result = unchecked((uint)(nuint)Call((nint)(nuint)nativeDevice, command, (nint)(nuint)flags, 0));
            if (result == 0)
            {
                _devices.Remove(device);
            }

            return result;
        }

        if (command == 0x814)
        {
            byte[] source = b.Read(pointer, 16);
            b.ValidateWrite(pointer, 16);
            nint data = b.Alloc(24);
            uint callback = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source);
            if ((flags & 1) != 0)
            {
                WP(data, 0, Window((ushort)callback));
            }

            W32(data, 16, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(8)));
            W32(data, 20, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(12)));
            uint result = unchecked((uint)(nuint)Call((nint)(nuint)nativeDevice, command, (nint)(nuint)flags, data));
            if (result == 0)
            {
                nuint value = (nuint)Ptr(data, 8);
                if (value > uint.MaxValue)
                {
                    throw new NotSupportedException("MCI status result exceeds the original DWORD.");
                }

                _memory.WriteDword((ushort)(pointer >> 16), (ushort)(pointer + 4), (uint)value);
            }

            return result;
        }

        if (command == 0x806)
        {
            byte[] source = b.Read(pointer, 12);
            nint data = b.Alloc(16);
            uint callback = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source);
            if ((flags & 1) != 0)
            {
                WP(data, 0, Window((ushort)callback));
            }

            W32(data, 8, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4)));
            W32(data, 12, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(8)));
            return unchecked((uint)(nuint)Call((nint)(nuint)nativeDevice, command, (nint)(nuint)flags, data));
        }

        return 261; // MCIERR_UNRECOGNIZED_COMMAND, never fabricated success.
    }

    private nint Window(ushort id) => id == 0
        ? 0
        : _handles.TryGet(id, Win16GuiHandleKind.Window, out var h)
            ? h!.Native
            : throw new InvalidDataException("Invalid multimedia callback window.");

    private void CloseDevices()
    {
        foreach (uint native in _devices.Values)
        {
            _backend.Call("mciSendCommandW", [(nint)native, 0x804, 0, 0]);
        }

        _devices.Clear();
        if (_references >= 0 && _backend.HasExport("sndPlaySoundW"))
        {
            _backend.Call("sndPlaySoundW", [0, 0]);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_references > 0)
        {
            CloseDevices();
            _backend.Unload();
        }

        _references = 0;
        _disposed = true;
    }
}
