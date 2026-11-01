using System.Runtime.InteropServices;

namespace Win16_Runtime;

/// <summary>Value-only default-message forwarding for the partial original window-procedure integration.</summary>
public sealed class Win16UserServices
{
    public Win16UserServices(Win16CallDispatcher dispatcher, Win16WindowHandles handles)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("USER services require Windows.");
        }

        var api = Win16ApiRegistry.Find("USER.DefWindowProc")!;
        dispatcher.RegisterApi(api, 0xF000, (ref MachineState state, SegmentMemory memory, Win16CallDispatcher _, Win16CallContext call) =>
        {
            uint message = api.ReadArgument("message", memory, state, call);
            if (message is not (0 or 0x87))
            {
                throw new NotSupportedException("DefWindowProc supports only WM_NULL and WM_GETDLGCODE in this milestone.");
            }

            if (message == 0x87 && api.ReadArgument("lParam", memory, state, call) != 0)
            {
                throw new NotSupportedException("WM_GETDLGCODE supports only a null MSG pointer; structure pointer marshalling is not implemented.");
            }

            nint hwnd = handles.GetNative((ushort)api.ReadArgument("hwnd", memory, state, call));
            nint result = WindowsCallbackHost.DefaultWindowProcedure(hwnd, message, (nuint)api.ReadArgument("wParam", memory, state, call), (nint)unchecked((int)api.ReadArgument("lParam", memory, state, call)));
            if (result.ToInt64() < int.MinValue || result.ToInt64() > int.MaxValue)
            {
                throw new NotSupportedException("Native LRESULT does not fit Win16 LONG.");
            }

            Win16Marshaller.WriteDwordReturn(ref state, unchecked((uint)result.ToInt64()));
            return api.CleanupBytes;
        });
    }
}

/// <summary>Windows host operations for synchronous callback acceptance; these are not Win16 imports.</summary>
public static class WindowsCallbackHost
{
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int EnumChildWindows(nint parent, nint callback, nint user);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CallWindowProcW(nint callback, nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    public static nint DefaultWindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    public static void EnumerateChildren(nint parent, Win16CallbackBinding callback, int user)
    {
        if (callback.Reference.Signature != Win16CallbackSignature.EnumWindows)
        {
            throw new ArgumentException("Enumeration callback required.");
        }

        try
        {
            EnumChildWindows(parent, callback.NativePointer, user);
        }
        finally
        {
            GC.KeepAlive(callback);
        }
    }

    public static nint CallWindowProcedure(Win16CallbackBinding callback, nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (callback.Reference.Signature != Win16CallbackSignature.WindowProcedure)
        {
            throw new ArgumentException("Window procedure callback required.");
        }

        try
        {
            return CallWindowProcW(callback.NativePointer, hwnd, message, wParam, lParam);
        }
        finally
        {
            GC.KeepAlive(callback);
        }
    }
}

public sealed class WindowsCallbackTestWindow : IDisposable
{
    private nint _hwnd;
    public nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_hwnd == 0, this);
            return _hwnd;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameters);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DestroyWindow(nint hwnd);
    public WindowsCallbackTestWindow(nint parent = 0)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows callback fixtures require Windows.");
        }

        _hwnd = CreateWindowExW(0, "STATIC", "Win16 callback acceptance", parent == 0
            ? 0u
            : 0x40000000u, 0, 0, 8, 8, parent, 0, 0, 0);
        if (_hwnd == 0)
        {
            throw new InvalidOperationException("Cannot create hidden callback fixture window.");
        }
    }

    public void Dispose()
    {
        if (_hwnd == 0)
        {
            return;
        }

        nint old = _hwnd;
        _hwnd = 0;
        if (DestroyWindow(old) == 0)
        {
            throw new InvalidOperationException("Callback window cleanup failed.");
        }
    }
}
