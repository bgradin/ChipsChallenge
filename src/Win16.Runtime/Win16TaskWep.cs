using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Translator.Binary;
using static Win16_Runtime.Win16NativeBuffers;

namespace Win16_Runtime;

/// <summary>Compatibility for the five observed utility exports, pinned to WEP4UTIL.DLL.
/// This module supplies utility UI only; it contains no application/game initialization.</summary>
public sealed class Win16TaskWep : IDisposable
{
    private readonly NeResourceCatalog _utility, _application;
    private readonly SegmentMemory _memory;
    private readonly IWin16GuiBackend _backend;
    private readonly Win16GuiHandles _handles;
    private readonly ushort _instance;
    private readonly string _modulePath;
    private bool _disposed, _usesHtmlHelp;
    private Exception? _callbackFailure;
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "WEP4UTIL.#2",
        "WEP4UTIL.#4",
        "WEP4UTIL.#5",
        "WEP4UTIL.#103",
        "WEP4UTIL.#1202"
    };

    public Win16TaskWep(byte[] dll, byte[] exe, SegmentMemory memory, IWin16GuiBackend backend, Win16GuiHandles handles, ushort instance, string modulePath)
    {
        Win16ExportContracts.VerifyWepImage(dll);
        _utility = new(dll);
        _application = new(exe);
        _memory = memory;
        _backend = backend;
        _handles = handles;
        _instance = instance;
        _modulePath = modulePath;
    }

    private nint Call(string name, params nint[] a) => _backend.Call(name is "GetStockObject" or "SetBkColor" or "SetBkMode" or "SelectObject" or "PatBlt" or "DeleteObject"
        ? "GDI32"
        : name == "GetModuleHandleW"
            ? "KERNEL32"
            : "USER32", name, a);
    private nint Handle(uint id, Win16GuiHandleKind kind = Win16GuiHandleKind.Window) => id == 0
        ? 0
        : id <= 65535 && _handles.TryGet((ushort)id, kind, out var h)
            ? h!.Native
            : throw new InvalidDataException("Invalid utility handle.");
    private static byte[]? Resource(NeResourceCatalog catalog, int type, int id)
    {
        int i = catalog.Resources.ToList().FindIndex(r => r.Type.Id == type && r.Identity.Id == id);
        return i < 0
            ? null
            : catalog.Extract(i + 1).Bytes;
    }

    private static string String(NeResourceCatalog catalog, int id)
    {
        byte[]? data = Resource(catalog, 6, (id >> 4) + 1);
        if (data is null)
        {
            return "";
        }

        int at = 0;
        for (int i = 0; i <= id % 16; i++)
        {
            if (at >= data.Length)
            {
                throw new InvalidDataException("Truncated utility string table.");
            }

            int length = data[at++];
            if (at > data.Length - length)
            {
                throw new InvalidDataException("Truncated utility string.");
            }

            if (i == id % 16)
            {
                return Ansi.GetString(data, at, length);
            }

            at += length;
        }

        return "";
    }

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var c = Win16ExportContracts.Get(identity);
        var frame = state;
        uint[] a = Enumerable.Range(0, c.Arguments.Count).Select(i => c.Argument(i, _memory, frame)).ToArray();
        using var b = new Win16NativeBuffers(_memory);
        uint result = 0;
        switch (c.Ordinal)
        {
            case 2:
                result = (a[0] & 0xff00) <= 0x220
                    ? 1u
                    : 0u;
                if (result == 0 && (a[0] & 1) == 0)
                {
                    Call("MessageBoxW", 0, b.Wide(String(_utility, 911)), 0, 0x30);
                }

                break;
            case 103:
                Center(Handle(a[0]), b);
                break;
            case 1202:
                result = Gray(Handle(a[0]), (ushort)a[1], a[2], a[3], b);
                break;
            case 5:
                {
                    if (a[0] != _instance)
                    {
                        break;
                    }

                    string help = _modulePath.Length > 4 && _modulePath[^4] == '.'
                        ? _modulePath[..^4] + ".HLP"
                        : _modulePath + ".HLP";
                    string converted = Path.ChangeExtension(_modulePath, ".CHM");
                    if (File.Exists(converted))
                    {
                        nint owner = Handle(a[1]);
                        if (a[2] == 2)
                        {
                            _backend.Call("HHCTRL.OCX", "HtmlHelpW", [0, 0, 0x12, 0]);
                            _usesHtmlHelp = false;
                            result = 1;
                            break;
                        }

                        if (a[2] == 4)
                        {
                            Call("MessageBoxW", owner, b.Wide("Use Contents to browse topics, Index to find keywords, and Back to return.\nClick a link to open its topic. Use Print to print the current topic."), b.Wide("Using Help"), 0x40);
                            result = 1;
                            break;
                        }

                        if (a[2] is 0x101 or 0x102 or 0x105)
                        {
                            string keyword = Marshal.PtrToStringUni(b.String(a[3])) ?? "";
                            if (keyword.Length == 0)
                            {
                                result = _backend.Call("HHCTRL.OCX", "HtmlHelpW", [owner, b.Wide(converted), 2, 0]) != 0
                                    ? 1u
                                    : 0u;
                                _usesHtmlHelp |= result != 0;
                                break;
                            }

                            if (keyword.Equals("Contents", StringComparison.OrdinalIgnoreCase))
                            {
                                result = _backend.Call("HHCTRL.OCX", "HtmlHelpW", [owner, b.Wide(converted), 0, 0]) != 0
                                    ? 1u
                                    : 0u;
                                _usesHtmlHelp |= result != 0;
                                break;
                            }

                            result = _backend.Call("HHCTRL.OCX", "HtmlHelpW", [owner, b.Wide(converted + "::/keywords/" + Translator.Core.HelpKeywordRoute.File(keyword)), 0, 0]) != 0
                                ? 1u
                                : 0u;
                        }
                        else if (a[2] is 1 or 8)
                        {
                            result = _backend.Call("HHCTRL.OCX", "HtmlHelpW", [owner, b.Wide(converted + "::/html/topic" + a[3].ToString(System.Globalization.CultureInfo.InvariantCulture) + ".htm"), 0, 0]) != 0
                                ? 1u
                                : 0u;
                        }
                        else if (a[2] is 0 or 3 or 0xb)
                        {
                            result = _backend.Call("HHCTRL.OCX", "HtmlHelpW", [owner, b.Wide(converted), a[2] == 0xb
                                ? 1
                                : 0, 0]) != 0
                                ? 1u
                                : 0u;
                        }

                        _usesHtmlHelp |= result != 0;
                        break;
                    }

                    nint data = a[2] is 0x101 or 0x102
                        ? b.String(a[3])
                        : (nint)(nuint)a[3];
                    result = (uint)Call("WinHelpW", Handle(a[1]), b.Wide(help), (ushort)a[2], data);
                    break;
                }

            case 4:
                {
                    if (a[0] != _instance)
                    {
                        break;
                    }

                    string title = String(_application, 257), description = String(_application, 258), copyright = String(_application, 259);
                    if (copyright.Length == 0)
                    {
                        copyright = String(_utility, 259);
                    }

                    string caption = String(_utility, 400).Replace("@0", title, StringComparison.Ordinal);
                    byte[]? template = Resource(_utility, 5, 100);
                    if (template is null)
                    {
                        throw new InvalidDataException("Verified utility has no About dialog.");
                    }

                    nint icon = 0;
                    byte[]? group = Resource(_application, 14, 256);
                    if (group is not null && group.Length >= 20)
                    {
                        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(18));
                        byte[]? image = Resource(_application, 3, id);
                        if (image is not null)
                        {
                            icon = Call("CreateIconFromResourceEx", b.Bytes(image), image.Length, 1, 0x30000, 0, 0, 0);
                        }
                    }

                    Win16TaskWindowHost.WindowProcedure callback = (window, message, wp, lp) =>
                    {
                        try
                        {
                            using var local = new Win16NativeBuffers(_memory);
                            if (message == 0x110)
                            {
                                Call("SetWindowTextW", window, local.Wide(caption));
                                Call("SetDlgItemTextW", window, 702, local.Wide(title));
                                Call("SetDlgItemTextW", window, 703, local.Wide(description));
                                Call("SetDlgItemTextW", window, 704, local.Wide(copyright));
                                if (icon != 0)
                                {
                                    Call("SendDlgItemMessageW", window, 701, 0x170, icon, 0);
                                }

                                Center(window, local);
                                return 1;
                            }

                            if (message == 0x111 && ((ushort)wp is 1 or 2))
                            {
                                Call("EndDialog", window, (ushort)wp);
                                return 1;
                            }

                            if (message == 0x10)
                            {
                                Call("EndDialog", window, 2);
                                return 1;
                            }

                            if (message is >= 0x132 and <= 0x138)
                            {
                                return Handle(Gray(window, 0x19, _handles.Add((nint)wp, Win16GuiHandleKind.Dc), (uint)(message - 0x132) << 16, local), Win16GuiHandleKind.GdiObject);
                            }

                            if (message == 0xf)
                            {
                                Gray(window, 0xf, 0, 0, local);
                            }

                            return 0;
                        }
                        catch (Exception error)
                        {
                            _callbackFailure ??= error;
                            Call("EndDialog", window, -1);
                            return 0;
                        }
                    };
                    try
                    {
                        Call("DialogBoxIndirectParamW", Call("GetModuleHandleW", 0), b.Bytes(Win16TaskGuiResources.ConvertDialogTemplate(template)), Handle(a[1]), Marshal.GetFunctionPointerForDelegate(callback), 0);
                        GC.KeepAlive(callback);
                    }
                    finally
                    {
                        if (icon != 0)
                        {
                            Call("DestroyIcon", icon);
                        }
                    }

                    if (_callbackFailure is not null)
                    {
                        var error = _callbackFailure;
                        _callbackFailure = null;
                        throw new InvalidOperationException("Utility dialog callback failed.", error);
                    }

                    break;
                }
        }

        if (c.Ordinal is 2 or 5 or 1202)
        {
            state.AX = (ushort)result;
        }

        return c.CleanupBytes;
    }

    private void Center(nint window, Win16NativeBuffers b)
    {
        nint rect = b.Alloc(16);
        if (Call("GetWindowRect", window, rect) == 0)
        {
            return;
        }

        short width = unchecked((short)((int)Call("GetSystemMetrics", 0) - I32(rect, 8) + I32(rect, 0)));
        width = unchecked((short)((ushort)width & 0xfff1));
        short height = unchecked((short)((int)Call("GetSystemMetrics", 1) - I32(rect, 12) + I32(rect, 4)));
        Call("SetWindowPos", window, 0, width >> 1, height / 3, 0, 0, 0x11);
    }

    private uint Gray(nint window, ushort message, uint dcId, uint lParam, Win16NativeBuffers b)
    {
        if (message == 0x19)
        {
            ushort type = (ushort)(lParam >> 16);
            if (type is 1 or 6)
            {
                nint dc = Handle(dcId, Win16GuiHandleKind.Dc);
                Call("SetBkColor", dc, 0xc0c0c0);
                Call("SetBkMode", dc, 1);
            }

            return type is 0 or 1 or 4 or 6
                ? _handles.Add(Call("GetStockObject", 1), Win16GuiHandleKind.GdiObject)
                : 0u;
        }

        if (message != 0xf)
        {
            return 0;
        }

        nint dcNative = Call("GetDC", window);
        if (dcNative == 0)
        {
            return 0;
        }

        nint old = 0;
        try
        {
            nint rect = b.Alloc(16);
            if (Call("GetClientRect", window, rect) == 0)
            {
                return 0;
            }

            int left = I32(rect, 0), top = I32(rect, 4), right = I32(rect, 8), bottom = I32(rect, 12);
            old = Call("SelectObject", dcNative, Call("GetStockObject", 0));
            Call("PatBlt", dcNative, left, top, left + 2, bottom - 1, 0xf00021);
            Call("SelectObject", dcNative, Call("GetStockObject", 2));
            Call("PatBlt", dcNative, left + 1, bottom - 2, right, bottom - 1, 0xf00021);
            Call("PatBlt", dcNative, left, bottom - 2, right, bottom, 0xf00021);
            Call("PatBlt", dcNative, right - 2, top, right, bottom, 0xf00021);

            void Validate(int l, int t, int r, int d)
            {
                W32(rect, 0, l);
                W32(rect, 4, t);
                W32(rect, 8, r);
                W32(rect, 12, d);
                Call("ValidateRect", window, rect);
            }


            Validate(left, bottom - 2, right, bottom);
            Validate(right - 1, 0, right, bottom);
            Validate(0, 0, 1, bottom);
        }
        finally
        {
            if (old != 0)
            {
                Call("SelectObject", dcNative, old);
            }

            Call("ReleaseDC", window, dcNative);
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_usesHtmlHelp)
        {
            _backend.Call("HHCTRL.OCX", "HtmlHelpW", [0, 0, 0x12, 0]);
        }

        _utility.Dispose();
        _application.Dispose();
        _disposed = true;
    }
}
