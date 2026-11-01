using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static Win16_Runtime.Win16NativeBuffers;

namespace Win16_Runtime;

/// <summary>Packed Win16 GDI structures, independent of Win64 alignment and handle width.</summary>
public sealed class Win16TaskGraphics(SegmentMemory memory, IWin16GuiBackend backend, Win16GuiHandles handles) : IDisposable
{
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "GDI.#57",
        "GDI.#82",
        "GDI.#93",
        "GDI.#443"
    };

    private bool _disposed;

    private nint Call(string name, params nint[] args) => backend.Call("GDI32", name, args);
    private nint Handle(uint id, Win16GuiHandleKind kind) => id <= 65535 && handles.TryGet((ushort)id, kind, out var h)
        ? h!.Native
        : 0;
    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var contract = Win16ExportContracts.Get(identity);
        var snapshot = state;
        uint[] a = Enumerable.Range(0, contract.Arguments.Count).Select(i => contract.Argument(i, memory, snapshot)).ToArray();
        using var b = new Win16NativeBuffers(memory);
        uint result = 0;
        if (identity == "GDI.#57")
        {
            byte[] source = b.Read(a[0], 50);
            nint font = b.Alloc(92);
            for (int i = 0; i < 5; i++)
            {
                W32(font, i * 4, BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(i * 2)));
            }

            for (int i = 0; i < 8; i++)
            {
                Marshal.WriteByte(font, 20 + i, source[10 + i]);
            }

            int end = Array.IndexOf(source, (byte)0, 18, 32);
            if (end < 0)
            {
                end = 50;
            }

            string face = Ansi.GetString(source, 18, end - 18);
            for (int i = 0; i < Math.Min(face.Length, 31); i++)
            {
                W16(font, 28 + i * 2, face[i]);
            }

            nint native = Call("CreateFontIndirectW", font);
            result = handles.Add(native, Win16GuiHandleKind.GdiObject, true);
            if (result == 0 && native != 0)
            {
                Call("DeleteObject", native);
            }
        }
        else if (identity == "GDI.#82")
        {
            int capacity = unchecked((short)a[1]);
            if (capacity > 0)
            {
                b.ValidateWrite(a[2], capacity);
            }

            nint native = Handle(a[0], Win16GuiHandleKind.GdiObject);
            if (native != 0 && capacity >= 14 && Call("GetObjectType", native) == 7) // OBJ_BITMAP; bmBits of a DDB is null.
            {
                nint bitmap = b.Alloc(32);
                if (Call("GetObjectW", native, 32, bitmap) != 0)
                {
                    byte[] output = new byte[14];
                    for (int i = 0; i < 4; i++)
                    {
                        int value = I32(bitmap, i * 4);
                        if (value is < short.MinValue or > short.MaxValue)
                        {
                            throw new NotSupportedException("BITMAP dimension exceeds Win16.");
                        }

                        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), (short)value);
                    }

                    output[8] = (byte)U16(bitmap, 16);
                    output[9] = (byte)U16(bitmap, 18);
                    b.Write(a[2], output);
                    result = 14;
                }
            }
            else if (native != 0 && capacity >= 50 && Call("GetObjectType", native) == 6)
            {
                nint font = b.Alloc(92);
                if (Call("GetObjectW", native, 92, font) != 0)
                {
                    byte[] output = new byte[50];
                    for (int i = 0; i < 5; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), checked((short)I32(font, i * 4)));
                    }

                    for (int i = 0; i < 8; i++)
                    {
                        output[10 + i] = Marshal.ReadByte(font, 20 + i);
                    }

                    string face = Marshal.PtrToStringUni(font + 28, 32)!.Split('\0')[0];
                    byte[] text = Ansi.GetBytes(face);
                    text.AsSpan(0, Math.Min(31, text.Length)).CopyTo(output.AsSpan(18));
                    b.Write(a[2], output);
                    result = 50;
                }
            }
        }
        else if (identity == "GDI.#93")
        {
            b.ValidateWrite(a[1], 31);
            nint dc = Handle(a[0], Win16GuiHandleKind.Dc);
            if (dc != 0)
            {
                nint metrics = b.Alloc(56);
                if (Call("GetTextMetricsA", dc, metrics) != 0)
                {
                    byte[] output = new byte[31];
                    for (int i = 0; i < 8; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), checked((short)I32(metrics, i * 4))); // bytes precede overhang in packed TEXTMETRIC16.
                    }

                    for (int i = 0; i < 3; i++)
                    {
                        output[16 + i] = Marshal.ReadByte(metrics, 48 + i);
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        output[19 + i] = Marshal.ReadByte(metrics, 44 + i);
                    }

                    output[23] = Marshal.ReadByte(metrics, 51);
                    output[24] = Marshal.ReadByte(metrics, 52);
                    for (int i = 0; i < 3; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(25 + i * 2), checked((short)I32(metrics, 32 + i * 4)));
                    }

                    b.Write(a[1], output);
                    result = 1;
                }
            }
        }
        else
        {
            nint dc = Handle(a[0], Win16GuiHandleKind.Dc);
            if (dc != 0)
            {
                byte[] header = b.Read(a[10], 40);
                int size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size != 40)
                {
                    throw new NotSupportedException("SetDIBitsToDevice requires a BITMAPINFOHEADER.");
                }

                int width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)), depth = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
                uint compression = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)), used = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
                if (width <= 0 || planes != 1 || depth is not (1 or 4 or 8 or 16 or 24 or 32) || compression > 3)
                {
                    throw new NotSupportedException("Unsupported DIB format.");
                }

                if (compression is not (0 or 3))
                {
                    throw new NotSupportedException("Compressed DIB scan transfer is not a supported original path.");
                }

                int colors = checked((int)(used != 0
                    ? used
                    : depth <= 8
                        ? 1u << depth
                        : 0));
                if (colors > 256)
                {
                    throw new InvalidDataException("Invalid DIB palette.");
                }

                int infoBytes = checked(40 + (compression == 3
                    ? 12
                    : 0) + colors * (a[11] == 1
                    ? 2
                    : 4));
                int stride = checked((int)(((long)width * depth + 31) / 32 * 4));
                int bitsBytes = checked(stride * (ushort)a[8]);
                nint info = b.Bytes(b.Read(a[10], infoBytes)), bits = b.Bytes(b.Read(a[9], bitsBytes));
                result = unchecked((uint)(nuint)Call("SetDIBitsToDevice", dc, (short)a[1], (short)a[2], (ushort)a[3], (ushort)a[4], (short)a[5], (short)a[6], (ushort)a[7], (ushort)a[8], bits, info, (ushort)a[11]));
            }
        }

        state.AX = (ushort)result;
        return contract.CleanupBytes;
    }

    public void Dispose() => _disposed = true;
}
