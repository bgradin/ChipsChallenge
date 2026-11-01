using System.Runtime.InteropServices;

namespace Win16_Runtime;

/// <summary>Known Win32 integer/pointer signatures only. Win16 marshalling is
/// completed before entry; native pointer results remain inside typed handle maps.</summary>
public sealed unsafe class WindowsTaskGuiBackend : IWin16GuiBackend, IDisposable
{
    private readonly Dictionary<string, nint> _libraries = new(StringComparer.Ordinal);
    private bool _disposed;

    public WindowsTaskGuiBackend()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException("Native GUI services require Windows.");
        }
    }

    public nint Call(string module, string function, ReadOnlySpan<nint> args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (module is not ("USER32" or "GDI32" or "KERNEL32" or "WINMM" or "HHCTRL.OCX"))
        {
            throw new NotSupportedException(module);
        }

        if (!_libraries.TryGetValue(module, out nint library))
        {
            library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, module == "HHCTRL.OCX"
                ? module
                : module + ".dll"));
            _libraries.Add(module, library);
        }

        nint entry = NativeLibrary.GetExport(library, function);
        return args.Length switch
        {
            0 => ((delegate* unmanaged<nint>)entry)(),
            1 => ((delegate* unmanaged<nint, nint>)entry)(args[0]),
            2 => ((delegate* unmanaged<nint, nint, nint>)entry)(args[0], args[1]),
            3 => ((delegate* unmanaged<nint, nint, nint, nint>)entry)(args[0], args[1], args[2]),
            4 => ((delegate* unmanaged<nint, nint, nint, nint, nint>)entry)(args[0], args[1], args[2], args[3]),
            5 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)entry)(args[0], args[1], args[2], args[3], args[4]),
            6 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)entry)(args[0], args[1], args[2], args[3], args[4], args[5]),
            7 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint>)entry)(args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
            8 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint>)entry)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
            9 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)entry)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
            10 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)entry)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
            11 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)entry)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
            12 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)entry)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
            _ => throw new NotSupportedException("Win32 call arity exceeds the declared adapter.")
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (nint handle in _libraries.Values)
        {
            NativeLibrary.Free(handle);
        }

        _libraries.Clear();
        _disposed = true;
    }
}
