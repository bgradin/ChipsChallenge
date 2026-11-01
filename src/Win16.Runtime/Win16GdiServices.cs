using System.Runtime.InteropServices;

namespace Win16_Runtime;

/// <summary>Focused Windows GDI adapter. Registered DCs are borrowed; their owner controls lifetime.</summary>
public sealed class Win16GdiServices : IDisposable
{
    public const uint DstInvert = 0x00550009;
    private readonly Win16HandleTable<nint> _dcs = new();
    private bool _disposed;
    public int NativeCallCount
    {
        get; private set;
    }

    public Win16GdiServices(Win16CallDispatcher dispatcher)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("GDI services require Windows.");
        }

        var api = Win16ApiRegistry.Find("GDI.PatBlt")!;
        dispatcher.RegisterApi(api, 0xF000, (ref MachineState state, SegmentMemory memory, Win16CallDispatcher _, Win16CallContext call) =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var copy = state;

            uint Arg(string name) => api.ReadArgument(name, memory, copy, call);

            state.AX = 0;
            if (Arg("rop") != DstInvert)
            {
                return api.CleanupBytes;
            }

            nint dc;
            try
            {
                dc = _dcs.Get((ushort)Arg("hdc"));
            }
            catch (KeyNotFoundException)
            {
                return api.CleanupBytes;
            }

            NativeCallCount++;
            state.AX = Win16Marshaller.Bool(WindowsGdi.PatBlt(dc, Win16Marshaller.SignedWord(Arg("x")), Win16Marshaller.SignedWord(Arg("y")), Win16Marshaller.SignedWord(Arg("width")), Win16Marshaller.SignedWord(Arg("height")), Arg("rop")) != 0);
            return api.CleanupBytes;
        });
    }

    public ushort RegisterDc(nint dc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dc == 0 || dc == -1)
        {
            throw new ArgumentException("Invalid native DC.");
        }

        return _dcs.Add(dc);
    }

    public bool UnregisterDc(ushort handle) => _dcs.Remove(handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dcs.Dispose();
    }
}

internal static class WindowsGdi
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPels, YPels;
        public uint ColorsUsed, ColorsImportant, Color;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int DeleteObject(nint obj);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int DeleteDC(nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int GdiFlush();

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int SetViewportOrgEx(nint dc, int x, int y, nint previous);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int PatBlt(nint dc, int x, int y, int width, int height, uint rop);
}

/// <summary>Host-owned top-down 32-bit DIB used for deterministic GDI integration; not a Win16 API.</summary>
public sealed class WindowsOffscreenBitmap : IDisposable
{
    private nint _dc, _bitmap, _previous, _bits;
    public nint DeviceContext => _dc;
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }

    public WindowsOffscreenBitmap(int width, int height)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Offscreen GDI requires Windows.");
        }

        if (width < 1 || height < 1 || checked((long)width * height) > int.MaxValue / 4)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Width = width;
        Height = height;
        try
        {
            _dc = WindowsGdi.CreateCompatibleDC(0);
            if (_dc == 0)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed.");
            }

            var info = new WindowsGdi.BitmapInfo
            {
                Size = 40,
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32
            };
            _bitmap = WindowsGdi.CreateDIBSection(_dc, ref info, 0, out _bits, 0, 0);
            if (_bitmap == 0 || _bits == 0)
            {
                throw new InvalidOperationException("CreateDIBSection failed.");
            }

            _previous = WindowsGdi.SelectObject(_dc, _bitmap);
            if (_previous == 0 || _previous == -1)
            {
                throw new InvalidOperationException("SelectObject failed.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SetOrigin(int x, int y)
    {
        Check();
        if (WindowsGdi.SetViewportOrgEx(_dc, x, y, 0) == 0)
        {
            throw new InvalidOperationException("SetViewportOrgEx failed.");
        }
    }

    public void SetPixels(int[] pixels)
    {
        Check();
        if (pixels.Length != Width * Height)
        {
            throw new ArgumentException("Pixel count mismatch.");
        }

        Flush();
        Marshal.Copy(pixels, 0, _bits, pixels.Length);
    }

    public int[] GetPixels()
    {
        Check();
        Flush();
        var values = new int[Width * Height];
        Marshal.Copy(_bits, values, 0, values.Length);
        return values;
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(_dc == 0, this);
    }

    private static void Flush()
    {
        if (WindowsGdi.GdiFlush() == 0)
        {
            throw new InvalidOperationException("GdiFlush failed.");
        }
    }

    public void Dispose()
    {
        bool failed = false;
        if (_dc != 0)
        {
            if (_previous != 0 && _previous != -1)
            {
                nint selected = WindowsGdi.SelectObject(_dc, _previous);
                failed |= selected == 0 || selected == -1;
            }

            failed |= WindowsGdi.DeleteDC(_dc) == 0;
            _dc = 0;
        }

        if (_bitmap != 0)
        {
            failed |= WindowsGdi.DeleteObject(_bitmap) == 0;
            _bitmap = 0;
        }

        _bits = 0;
        _previous = 0;
        if (failed)
        {
            throw new InvalidOperationException("GDI offscreen resource cleanup failed.");
        }
    }
}
