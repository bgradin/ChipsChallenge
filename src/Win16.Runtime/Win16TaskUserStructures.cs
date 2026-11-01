using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using static Win16_Runtime.Win16NativeBuffers;

namespace Win16_Runtime;

/// <summary>USER pointer, rectangle, text and configuration contracts. No guest pointer is passed to Windows.</summary>
public sealed class Win16TaskUserStructures(SegmentMemory memory, IWin16GuiBackend backend, Win16GuiHandles handles, ushort instance) : IDisposable
{
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "USER.#1",
        "USER.#5",
        "USER.#6",
        "USER.#15",
        "USER.#33",
        "USER.#61",
        "USER.#78",
        "USER.#85",
        "USER.#88",
        "USER.#92",
        "USER.#93",
        "USER.#95",
        "USER.#102",
        "USER.#125",
        "USER.#127",
        "USER.#411",
        "USER.#420",
        "USER.#483"
    };

    private bool _disposed;
    private nint Call(string name, params nint[] args) => backend.Call(name == "GetTickCount"
        ? "KERNEL32"
        : "USER32", name, args);
    private nint Handle(uint id, Win16GuiHandleKind kind, bool nullable = false)
    {
        if (id == 0 && nullable)
        {
            return 0;
        }

        return id <= 65535 && handles.TryGet((ushort)id, kind, out var h)
            ? h!.Native
            : 0;
    }

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var c = Win16ExportContracts.Get(identity);
        var saved = state;
        uint[] a = Enumerable.Range(0, c.Arguments.Count).Select(i => c.Argument(i, memory, saved)).ToArray();
        uint result = 0;
        using var b = new Win16NativeBuffers(memory);
        switch (c.Ordinal)
        {
            case 1:
                {
                    nint owner = Handle(a[0], Win16GuiHandleKind.Window, true);
                    if (a[0] == 0 || owner != 0)
                    {
                        result = (uint)Call("MessageBoxW", owner, b.String(a[1]), b.String(a[2]), (ushort)a[3]);
                    }

                    break;
                }

            case 5:
                if (a[0] == instance)
                {
                    nint message = b.Alloc(48);
                    Call("PeekMessageW", message, 0, 0, 0, 0);
                    result = 1;
                }

                break;
            case 6:
                Call("PostQuitMessage", (short)a[0]);
                break;
            case 15:
                result = unchecked((uint)(nuint)Call("GetTickCount"));
                break;
            case 33:
                {
                    b.ValidateWrite(a[1], 8);
                    nint window = Handle(a[0], Win16GuiHandleKind.Window);
                    if (window != 0)
                    {
                        nint rect = b.Alloc(16);
                        if (Call("GetClientRect", window, rect) != 0)
                        {
                            b.WriteRect(a[1], rect);
                        }
                    }

                    break;
                }

            case 61:
                {
                    nint window = Handle(a[0], Win16GuiHandleKind.Window);
                    nint rect = b.Rect(a[3]), clip = b.Rect(a[4]);
                    if (window != 0)
                    {
                        Call("ScrollWindow", window, (short)a[1], (short)a[2], rect, clip);
                    }

                    break;
                }

            case 78:
                {
                    b.ValidateWrite(a[0], 8);
                    var rect = new Win16Marshaller(memory).ReadRect(new((ushort)a[0], (ushort)(a[0] >> 16)));
                    int x = (short)a[1], y = (short)a[2];
                    ushort seg = (ushort)(a[0] >> 16), off = (ushort)a[0];
                    memory.WriteWord(seg, off, unchecked((ushort)(rect.Left - x)));
                    memory.WriteWord(seg, (ushort)(off + 2), unchecked((ushort)(rect.Top - y)));
                    memory.WriteWord(seg, (ushort)(off + 4), unchecked((ushort)(rect.Right + x)));
                    memory.WriteWord(seg, (ushort)(off + 6), unchecked((ushort)(rect.Bottom + y)));
                    break;
                }

            case 85:
                {
                    b.ValidateWrite(a[3], 8);
                    nint dc = Handle(a[0], Win16GuiHandleKind.Dc);
                    int length = (short)a[2];
                    string text = length < 0
                        ? new Win16Marshaller(memory).ReadAnsi(new((ushort)a[1], (ushort)(a[1] >> 16)), 65536)
                        : Ansi.GetString(b.Read(a[1], length));
                    nint rect = b.Rect(a[3]);
                    if (dc != 0)
                    {
                        result = unchecked((uint)(nuint)Call("DrawTextW", dc, b.Wide(text), text.Length, rect, (ushort)a[4]));
                        b.WriteRect(a[3], rect);
                    }

                    break;
                }

            case 88:
                {
                    nint window = Handle(a[0], Win16GuiHandleKind.Window);
                    if (window != 0)
                    {
                        result = (uint)Call("EndDialog", window, (short)a[1]);
                    }

                    break;
                }

            case 92:
                {
                    nint window = Handle(a[0], Win16GuiHandleKind.Window);
                    nint text = b.String(a[2]);
                    if (window != 0)
                    {
                        Call("SetDlgItemTextW", window, (short)a[1], text);
                    }

                    break;
                }

            case 93:
                {
                    int capacity = (ushort)a[3];
                    if (capacity > 0)
                    {
                        b.ValidateWrite(a[2], capacity);
                    }

                    nint window = Handle(a[0], Win16GuiHandleKind.Window);
                    if (window != 0 && capacity > 0)
                    {
                        nint text = b.Alloc(capacity * 2);
                        int count = (int)Call("GetDlgItemTextW", window, (short)a[1], text, capacity);
                        if (count < 0 || count >= capacity)
                        {
                            throw new InvalidDataException("Native dialog text count exceeds its buffer.");
                        }

                        byte[] output = Ansi.GetBytes(Marshal.PtrToStringUni(text, count)!);
                        int n = Math.Min(output.Length, capacity - 1);
                        byte[] terminated = new byte[n + 1];
                        output.AsSpan(0, n).CopyTo(terminated);
                        b.Write(a[2], terminated);
                        result = (uint)n;
                    }

                    break;
                }

            case 95:
                {
                    if (a[2] != 0)
                    {
                        b.ValidateWrite(a[2], 2);
                    }

                    nint window = Handle(a[0], Win16GuiHandleKind.Window);
                    nint success = b.Alloc(4);
                    if (window != 0)
                    {
                        nint value = Call("GetDlgItemInt", window, (short)a[1], success, (ushort)a[3]);
                        long number = a[3] != 0
                            ? unchecked((int)(nuint)value)
                            : unchecked((uint)(nuint)value);
                        bool valid = I32(success, 0) != 0 && (a[3] != 0
                            ? number is >= short.MinValue and <= short.MaxValue
                            : number is >= 0 and <= ushort.MaxValue);
                        if (a[2] != 0)
                        {
                            b.Write(a[2], new byte[] { valid
                            ? (byte)1
                            : (byte)0, 0 });
                        }

                        if (valid)
                        {
                            result = unchecked((ushort)number);
                        }
                    }
                    else if (a[2] != 0)
                    {
                        b.Write(a[2], new byte[2]);
                    }

                    break;
                }

            case 102:
                {
                    b.ValidateWrite(a[0], 8);
                    nint rect = b.Rect(a[0]);
                    result = (uint)Call("AdjustWindowRect", rect, (nint)(nuint)a[1], (ushort)a[2]);
                    if (result != 0)
                    {
                        b.WriteRect(a[0], rect);
                    }

                    break;
                }

            case 125:
            case 127:
                {
                    nint window = Handle(a[0], Win16GuiHandleKind.Window, true);
                    nint rect = b.Rect(a[1]);
                    if (a[0] == 0 || window != 0)
                    {
                        if (c.Ordinal == 125)
                        {
                            Call("InvalidateRect", window, rect, (ushort)a[2]);
                        }
                        else
                        {
                            Call("ValidateRect", window, rect);
                        }
                    }

                    break;
                }

            case 411:
                {
                    nint menu = Handle(a[0], Win16GuiHandleKind.Menu);
                    nint item = (ushort)a[2], text;
                    if ((a[1] & 0x10) != 0)
                    {
                        item = Handle(a[2], Win16GuiHandleKind.Menu);
                    }

                    if ((a[1] & 4) != 0)
                    {
                        text = Handle(a[3] & 0xffff, Win16GuiHandleKind.GdiObject);
                    }
                    else if ((a[1] & 0x100) != 0)
                    {
                        text = (nint)(nuint)a[3];
                    }
                    else
                    {
                        text = b.String(a[3]);
                    }

                    if (menu != 0 && ((a[1] & 0x10) == 0 || item != 0))
                    {
                        result = (uint)Call("AppendMenuW", menu, (ushort)a[1], item, text);
                    }

                    break;
                }

            case 420:
                result = (uint)Format(saved, b, a[0], a[1]);
                break;
            case 483:
                {
                    if (a[0] == 0x16)
                    {
                        b.ValidateWrite(a[2], 2);
                        nint output = b.Alloc(4);
                        result = (uint)Call("SystemParametersInfoW", 0x16, (ushort)a[1], output, (ushort)a[3]);
                        if (result != 0)
                        {
                            int value = I32(output, 0);
                            if (value is < 0 or > ushort.MaxValue)
                            {
                                throw new InvalidDataException("Keyboard delay exceeds Win16 range.");
                            }

                            byte[] word = new byte[2];
                            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)value);
                            b.Write(a[2], word);
                        }
                    }
                    else if (a[0] == 0x17 && a[2] == 0)
                    {
                        result = (uint)Call("SystemParametersInfoW", 0x17, (ushort)a[1], 0, (ushort)a[3]);
                    }
                    else
                    {
                        throw new NotSupportedException("Unobserved SystemParametersInfo structure/action.");
                    }

                    break;
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

    private int Format(MachineState state, Win16NativeBuffers b, uint destination, uint formatPointer)
    {
        string format = new Win16Marshaller(memory).ReadAnsi(new((ushort)formatPointer, (ushort)(formatPointer >> 16)), 65536);
        int cursor = 12;
        var output = new StringBuilder();

        ushort Word()
        {
            ushort v = memory.ReadWord(state.SS, (ushort)(state.SP + cursor));
            cursor += 2;
            return v;
        }


        uint Dword()
        {
            uint lo = Word();
            return lo | (uint)Word() << 16;
        }

        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
            {
                output.Append(format[i]);
                continue;
            }

            if (++i >= format.Length)
            {
                throw new InvalidDataException("Incomplete wsprintf conversion.");
            }

            if (format[i] == '%')
            {
                output.Append('%');
                continue;
            }

            bool left = false, zero = false, alternate = false;
            while (i < format.Length && format[i] is '-' or '0' or '#')
            {
                left |= format[i] == '-';
                zero |= format[i] == '0';
                alternate |= format[i] == '#';
                i++;
            }

            int width = 0;
            while (i < format.Length && char.IsAsciiDigit(format[i]))
            {
                width = checked(width * 10 + format[i++] - '0');
                if (width > 1023)
                {
                    throw new InvalidDataException("wsprintf field exceeds its 1024-byte contract.");
                }
            }

            int precision = -1;
            if (i < format.Length && format[i] == '.')
            {
                precision = 0;
                i++;
                while (i < format.Length && char.IsAsciiDigit(format[i]))
                {
                    precision = checked(precision * 10 + format[i++] - '0');
                    if (precision > 1023)
                    {
                        throw new InvalidDataException("wsprintf precision exceeds its bound.");
                    }
                }
            }

            bool wide = false, near = false;
            if (i < format.Length && format[i] is 'l' or 'h')
            {
                wide = format[i] == 'l';
                near = format[i] == 'h';
                i++;
            }

            if (i >= format.Length)
            {
                throw new InvalidDataException("Incomplete wsprintf conversion.");
            }

            char conversion = format[i];
            string text;
            if (conversion is 's' or 'S')
            {
                uint pointer = near
                    ? (uint)state.DS << 16 | Word()
                    : Dword();
                text = pointer == 0
                    ? "(null)"
                    : new Win16Marshaller(memory).ReadAnsi(new((ushort)pointer, (ushort)(pointer >> 16)), 65536);
                if (precision >= 0 && text.Length > precision)
                {
                    text = text[..precision];
                }
            }
            else if (conversion is 'c' or 'C')
            {
                text = Ansi.GetString(new[] { (byte)Word() });
            }
            else if (conversion is 'd' or 'i' or 'u' or 'x' or 'X')
            {
                uint value = wide
                    ? Dword()
                    : Word();
                text = conversion is 'd' or 'i'
                    ? (wide
                        ? unchecked((int)value)
                        : (short)value).ToString(CultureInfo.InvariantCulture)
                    : value.ToString(conversion is 'x' or 'X'
                        ? conversion.ToString()
                        : "D", CultureInfo.InvariantCulture);
                if (precision >= 0)
                {
                    bool negative = text.StartsWith('-');
                    text = (negative
                        ? "-"
                        : "") + text[(negative
                        ? 1
                        : 0)..].PadLeft(precision, '0');
                }

                if (alternate && conversion is 'x' or 'X' && value != 0)
                {
                    text = (conversion == 'x'
                        ? "0x"
                        : "0X") + text;
                }
            }
            else
            {
                throw new NotSupportedException($"Unobserved Win16 wsprintf conversion %{conversion}.");
            }

            if (width > text.Length)
            {
                if (left)
                {
                    text = text.PadRight(width);
                }
                else if (zero && precision < 0 && text.StartsWith('-'))
                {
                    text = "-" + text[1..].PadLeft(width - 1, '0');
                }
                else
                {
                    text = text.PadLeft(width, zero && precision < 0
                        ? '0'
                        : ' ');
                }
            }

            output.Append(text);
            if (output.Length > 1023)
            {
                throw new InvalidDataException("wsprintf output exceeds its original 1024-byte buffer limit.");
            }
        }

        byte[] bytes = Ansi.GetBytes(output.ToString() + "\0");
        if (bytes.Length > 1024)
        {
            throw new InvalidDataException("wsprintf output exceeds its original buffer limit.");
        }

        b.Write(destination, bytes);
        return bytes.Length - 1;
    }

    public void Dispose() => _disposed = true;
}
