using System.Buffers.Binary;
using Translator.Binary;
using static Win16_Runtime.Win16NativeBuffers;

namespace Win16_Runtime;

/// <summary>NE resources become native objects without mapping large payloads into a fictitious 64KiB selector.</summary>
public sealed class Win16TaskGuiResources : IDisposable
{
    private readonly NeResourceCatalog _catalog;
    private readonly SegmentMemory _memory;
    private readonly IWin16GuiBackend _backend;
    private readonly Win16GuiHandles _handles;
    private readonly ushort _instance;
    private bool _disposed;
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "USER.#150",
        "USER.#173",
        "USER.#174",
        "USER.#175",
        "USER.#177"
    };

    public Win16TaskGuiResources(byte[] source, SegmentMemory memory, IWin16GuiBackend backend, Win16GuiHandles handles, ushort instance)
    {
        _catalog = new(source);
        _memory = memory;
        _backend = backend;
        _handles = handles;
        _instance = instance;
    }

    public byte[]? Find(int type, uint name)
    {
        string? text = (name >> 16) != 0
            ? new Win16Marshaller(_memory).ReadAnsi(new((ushort)name, (ushort)(name >> 16)), 65536)
            : null;
        var row = _catalog.Resources.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Type.Id == type && (text is null
            ? x.r.Identity.Id == (ushort)name
            : string.Equals(x.r.Identity.Name, text, StringComparison.OrdinalIgnoreCase)));
        return row.r is null
            ? null
            : _catalog.Extract(row.i + 1).Bytes;
    }

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var c = Win16ExportContracts.Get(identity);
        uint inst = c.Argument(0, _memory, state), name = c.Argument(1, _memory, state);
        ushort result = 0;
        using var b = new Win16NativeBuffers(_memory);
        nint native = 0;
        Win16GuiHandleKind kind = Win16GuiHandleKind.GdiObject;
        bool owned = true;

        nint Call(string module, string function, params nint[] a) => _backend.Call(module, function, a);

        if (inst == 0 && c.Ordinal is 173 or 174)
        {
            kind = c.Ordinal == 173
                ? Win16GuiHandleKind.Cursor
                : Win16GuiHandleKind.Icon;
            native = Call("USER32", c.Ordinal == 173
                ? "LoadCursorW"
                : "LoadIconW", 0, b.String(name, true));
            owned = false;
        }
        else if (inst == 0 || inst == _instance)
        {
            if (c.Ordinal == 150)
            {
                kind = Win16GuiHandleKind.Menu;
                byte[]? data = Find(4, name);
                if (data is not null)
                {
                    if (data.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0)
                    {
                        throw new InvalidDataException("Unsupported menu template version.");
                    }

                    int at = 4 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
                    native = BuildMenu(data, ref at, false, b, 0);
                }
            }
            else if (c.Ordinal == 175)
            {
                byte[]? data = Find(2, name);
                if (data is not null)
                {
                    var dib = NeBitmapDecoder.Decode(data);
                    nint info = b.Bytes(data);
                    int offset = dib.PixelDataOffset;
                    nint dc = Call("USER32", "GetDC", 0);
                    try
                    {
                        native = Call("GDI32", "CreateDIBitmap", dc, info, 4, info + offset, info, 0);
                    }
                    finally
                    {
                        if (dc != 0)
                        {
                            Call("USER32", "ReleaseDC", 0, dc);
                        }
                    }
                }
            }
            else if (c.Ordinal == 177)
            {
                kind = Win16GuiHandleKind.Accelerator;
                byte[]? data = Find(9, name);
                if (data is not null)
                {
                    var rows = new List<byte>();
                    for (int p = 0; p + 5 <= data.Length; p += 5)
                    {
                        byte flags = data[p];
                        rows.Add((byte)(flags & 0x7f));
                        rows.Add(0);
                        rows.AddRange(data.AsSpan(p + 1, 4).ToArray());
                        if ((flags & 0x80) != 0)
                        {
                            break;
                        }
                    }

                    if (rows.Count != 0)
                    {
                        native = Call("USER32", "CreateAcceleratorTableW", b.Bytes(rows.ToArray()), rows.Count / 6);
                    }
                }
            }
            else
            {
                bool icon = c.Ordinal == 174;
                kind = icon
                    ? Win16GuiHandleKind.Icon
                    : Win16GuiHandleKind.Cursor;
                byte[]? group = Find(icon
                    ? 14
                    : 12, name);
                if (group is not null && group.Length >= 20)
                {
                    ushort id = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(18));
                    byte[]? image = Find(icon
                        ? 3
                        : 1, id);
                    if (image is not null)
                    {
                        native = Call("USER32", "CreateIconFromResourceEx", b.Bytes(image), image.Length, icon
                            ? 1
                            : 0, 0x30000, 0, 0, 0);
                    }
                }
            }
        }

        if (native != 0)
        {
            result = _handles.Add(native, kind, owned);
            if (result == 0 && owned)
            {
                Call(kind == Win16GuiHandleKind.GdiObject
                    ? "GDI32"
                    : "USER32", kind switch
                    {
                        Win16GuiHandleKind.Menu => "DestroyMenu",
                        Win16GuiHandleKind.Accelerator => "DestroyAcceleratorTable",
                        Win16GuiHandleKind.Icon => "DestroyIcon",
                        Win16GuiHandleKind.Cursor => "DestroyCursor",
                        _ => "DeleteObject"
                    }, native);
            }
        }

        state.AX = result;
        return c.CleanupBytes;
    }

    private nint BuildMenu(byte[] data, ref int at, bool popup, Win16NativeBuffers b, int depth)
    {
        if (depth > 16)
        {
            throw new InvalidDataException("Menu nesting exceeds its bound.");
        }

        nint menu = _backend.Call("USER32", popup
            ? "CreatePopupMenu"
            : "CreateMenu", []);
        if (menu == 0)
        {
            return 0;
        }

        try
        {
            for (int items = 0; items < 4096; items++)
            {
                if (at > data.Length - 2)
                {
                    throw new InvalidDataException("Truncated Win16 menu.");
                }

                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
                at += 2;
                bool child = (flags & 0x10) != 0;
                ushort id = 0;
                if (!child)
                {
                    if (at > data.Length - 2)
                    {
                        throw new InvalidDataException("Truncated menu ID.");
                    }

                    id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
                    at += 2;
                }

                int end = Array.IndexOf(data, (byte)0, at);
                if (end < 0)
                {
                    throw new InvalidDataException("Unterminated menu text.");
                }

                string text = Ansi.GetString(data, at, end - at);
                at = end + 1;
                nint item = child
                    ? BuildMenu(data, ref at, true, b, depth + 1)
                    : id;
                if (child && item == 0)
                {
                    throw new InvalidOperationException("Native popup allocation failed.");
                }

                // Win16 also encodes separators as an empty string with ID zero.
                if (!child && id == 0 && text.Length == 0)
                {
                    flags |= 0x800;
                }

                // Remove only the template end marker; preserve MF_SEPARATOR (0x800).
                if (_backend.Call("USER32", "AppendMenuW", [menu, flags & ~0x80, item, b.Wide(text)]) == 0)
                {
                    if (child)
                    {
                        _backend.Call("USER32", "DestroyMenu", [item]);
                    }

                    throw new InvalidOperationException("Native menu insertion failed.");
                }

                if ((flags & 0x80) != 0)
                {
                    return menu;
                }
            }

            throw new InvalidDataException("Menu item count exceeds its bound.");
        }
        catch
        {
            _backend.Call("USER32", "DestroyMenu", [menu]);
            throw;
        }
    }

    public ushort LoadMenu(uint name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[]? data = Find(4, name);
        if (data is null)
        {
            return 0;
        }

        if (data.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0)
        {
            throw new InvalidDataException("Unsupported menu template version.");
        }

        int at = 4 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        using var b = new Win16NativeBuffers(_memory);
        nint native = BuildMenu(data, ref at, false, b, 0);
        ushort id = _handles.Add(native, Win16GuiHandleKind.Menu, true);
        if (id == 0 && native != 0)
        {
            _backend.Call("USER32", "DestroyMenu", [native]);
        }

        return id;
    }

    public static byte[] ConvertDialogTemplate(byte[] source)
    {
        int at = 0;
        using var stream = new MemoryStream();
        using var output = new BinaryWriter(stream);

        byte Byte()
        {
            if (at >= source.Length)
            {
                throw new InvalidDataException("Truncated Win16 dialog.");
            }

            return source[at++];
        }


        ushort Word()
        {
            byte lo = Byte();
            return (ushort)(lo | Byte() << 8);
        }

        uint Dword()
        {
            uint lo = Word();
            return lo | (uint)Word() << 16;
        }

        void Wide(string text)
        {
            foreach (char ch in text)
            {
                output.Write((ushort)ch);
            }

            output.Write((ushort)0);
        }

        string Text()
        {
            var bytes = new List<byte>();
            byte ch;
            while ((ch = Byte()) != 0)
            {
                bytes.Add(ch);
            }

            return Ansi.GetString(bytes.ToArray());
        }

        void Name(bool classByte = false)
        {
            if (at >= source.Length)
            {
                throw new InvalidDataException("Truncated dialog name.");
            }

            if (source[at] == 0xff)
            {
                at++;
                output.Write((ushort)0xffff);
                output.Write(Word());
            }
            else if (classByte && (source[at] & 0x80) != 0)
            {
                output.Write((ushort)0xffff);
                output.Write((ushort)Byte());
            }
            else
            {
                Wide(Text());
            }
        }

        void Align()
        {
            while ((stream.Position & 3) != 0)
            {
                output.Write((byte)0);
            }
        }

        uint style = Dword();
        byte count = Byte();
        output.Write(style);
        output.Write(0u);
        output.Write((ushort)count);
        for (int i = 0; i < 4; i++)
        {
            output.Write(Word());
        }

        Name();
        Name();
        Name();
        if ((style & 0x40) != 0)
        {
            output.Write(Word());
            Wide(Text());
        }

        for (int i = 0; i < count; i++)
        {
            ushort x = Word(), y = Word(), cx = Word(), cy = Word(), id = Word();
            uint itemStyle = Dword();
            Align();
            output.Write(itemStyle);
            output.Write(0u);
            output.Write(x);
            output.Write(y);
            output.Write(cx);
            output.Write(cy);
            output.Write(id);
            Name(true);
            Name();
            int extra = Byte();
            output.Write((ushort)(extra == 0
                ? 0
                : extra + 2));
            for (int j = 0; j < extra; j++)
            {
                output.Write(Byte());
            }
        }

        return stream.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _catalog.Dispose();
        _disposed = true;
    }
}
