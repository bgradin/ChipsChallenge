using System.Runtime.InteropServices;
using System.Text;

namespace Win16_Runtime;

/// <summary>Short-lived native buffers; all guest ranges are checked before host access.</summary>
internal sealed unsafe class Win16NativeBuffers(SegmentMemory memory) : IDisposable
{
    private readonly List<nint> _allocations = [];
    internal static readonly Encoding Ansi = CreateAnsi();

    private static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public nint Alloc(int bytes)
    {
        if (bytes < 0 || bytes > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Native buffer exceeds the adapter bound.");
        }

        nint p = Marshal.AllocHGlobal(Math.Max(bytes, 1));
        _allocations.Add(p);
        new Span<byte>((void*)p, Math.Max(bytes, 1)).Clear();
        return p;
    }

    public nint Bytes(ReadOnlySpan<byte> bytes)
    {
        nint p = Alloc(bytes.Length);
        bytes.CopyTo(new Span<byte>((void*)p, bytes.Length));
        return p;
    }

    public byte[] Read(uint pointer, int bytes)
    {
        return new Win16Marshaller(memory).CopyInput(new((ushort)pointer, (ushort)(pointer >> 16)), bytes);
    }

    public void ValidateWrite(uint pointer, int bytes) => memory.ValidateBufferWrite((ushort)(pointer >> 16), (ushort)pointer, bytes);

    public void Write(uint pointer, ReadOnlySpan<byte> bytes) => Win16TaskKernelServices.WriteBuffer(memory, pointer, bytes.ToArray());

    public nint String(uint pointer, bool identifier = false)
    {
        if (identifier && (pointer >> 16) == 0)
        {
            return (nint)pointer;
        }

        if (pointer == 0)
        {
            return 0;
        }

        string text = new Win16Marshaller(memory).ReadAnsi(new((ushort)pointer, (ushort)(pointer >> 16)), 65536);
        return Wide(text);
    }

    public nint Wide(string text)
    {
        nint p = Marshal.StringToHGlobalUni(text);
        _allocations.Add(p);
        return p;
    }

    public static short S16(nint p, int offset) => Marshal.ReadInt16(p, offset);

    public static ushort U16(nint p, int offset) => unchecked((ushort)S16(p, offset));

    public static int I32(nint p, int offset) => Marshal.ReadInt32(p, offset);

    public static uint U32(nint p, int offset) => unchecked((uint)I32(p, offset));

    public static nint Ptr(nint p, int offset) => Marshal.ReadIntPtr(p, offset);

    public static void W16(nint p, int offset, int value) => Marshal.WriteInt16(p, offset, unchecked((short)value));

    public static void W32(nint p, int offset, long value) => Marshal.WriteInt32(p, offset, unchecked((int)value));

    public static void WP(nint p, int offset, nint value) => Marshal.WriteIntPtr(p, offset, value);

    public nint Rect(uint pointer)
    {
        if (pointer == 0)
        {
            return 0;
        }

        var r = new Win16Marshaller(memory).ReadRect(new((ushort)pointer, (ushort)(pointer >> 16)));
        nint p = Alloc(16);
        W32(p, 0, r.Left);
        W32(p, 4, r.Top);
        W32(p, 8, r.Right);
        W32(p, 12, r.Bottom);
        return p;
    }

    public void WriteRect(uint pointer, nint rect)
    {
        ValidateWrite(pointer, 8);
        for (int i = 0; i < 4; i++)
        {
            int value = I32(rect, i * 4);
            if (value is < short.MinValue or > short.MaxValue)
            {
                throw new NotSupportedException("Native rectangle exceeds Win16 coordinates.");
            }
        }

        for (int i = 0; i < 4; i++)
        {
            memory.WriteWord((ushort)(pointer >> 16), (ushort)(pointer + i * 2), (ushort)I32(rect, i * 4));
        }
    }

    public void Dispose()
    {
        foreach (nint p in _allocations)
        {
            Marshal.FreeHGlobal(p);
        }

        _allocations.Clear();
    }
}
