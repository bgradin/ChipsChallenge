using System.Runtime.InteropServices;
using Translator.Binary;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeStructureServiceTests
{
    private sealed class Backend : IWin16GuiBackend
    {
        public Func<string, nint[], nint> Run = (_, _) => 0;

        public nint Call(string module, string function, ReadOnlySpan<nint> args) => Run(function, args.ToArray());
    }

    private sealed class Fixture : IDisposable
    {
        public readonly SegmentMemory Memory = new();
        public readonly Win16Segment Data;
        public readonly Backend Backend = new();
        public readonly Win16GuiHandles Handles = new();
        public MachineState State = new()
        {
            DS = 1,
            SS = 1,
            SP = 4096,
            DX = 0x7777
        };

        public Fixture()
        {
            Data = Memory.CreateSegment(1, 8192);
            Memory.Map(Data);
        }

        public void Args(string id, params uint[] args)
        {
            var c = Win16ExportContracts.Get(id);
            for (int i = 0; i < c.Arguments.Count; i++)
            {
                var a = c.Arguments[i];
                ushort at = (ushort)(State.SP + 4 + a.StackWord * 2);
                if (a.Bytes == 4)
                {
                    Memory.WriteDword(1, at, args[i]);
                }
                else
                {
                    Memory.WriteWord(1, at, (ushort)args[i]);
                }
            }
        }

        public void Dispose() => Data.Dispose();
    }

    [Fact]
    public void LogFontAndTextMetricUseThePackedWin16FieldOrder()
    {
        using var f = new Fixture();
        using var g = new Win16TaskGraphics(f.Memory, f.Backend, f.Handles);
        f.Memory.WriteWord(1, 64, unchecked((ushort)-32));
        f.Memory.WriteWord(1, 72, 700);
        f.Memory.WriteBytes(1, 82, new byte[] { 0x80, 65, 0 });
        f.Backend.Run = (name, a) =>
        {
            Assert.Equal("CreateFontIndirectW", name);
            Assert.Equal(-32, Marshal.ReadInt32(a[0]));
            Assert.Equal(700, Marshal.ReadInt32(a[0], 16));
            Assert.Equal("€A", Marshal.PtrToStringUni(a[0] + 28));
            return unchecked((nint)0x123456789L);
        };
        f.Args("GDI.#57", 0x10040);
        Assert.Equal((ushort)4, g.Invoke("GDI.#57", ref f.State));
        Assert.True(f.Handles.TryGet(f.State.AX, Win16GuiHandleKind.GdiObject, out var font));
        Assert.True(font!.Owned);
        ushort dc = f.Handles.Add(unchecked((nint)0x223456789L), Win16GuiHandleKind.Dc);
        f.Backend.Run = (name, a) =>
        {
            Assert.Equal("GetTextMetricsA", name);
            for (int i = 0; i < 11; i++)
            {
                Marshal.WriteInt32(a[1], i * 4, 100 + i);
            }

            for (int i = 0; i < 9; i++)
            {
                Marshal.WriteByte(a[1], 44 + i, (byte)(20 + i));
            }

            return 1;
        };
        f.Args("GDI.#93", dc, 0x10100);
        Assert.Equal((ushort)6, g.Invoke("GDI.#93", ref f.State));
        Assert.Equal((ushort)100, f.Memory.ReadWord(1, 256));
        Assert.Equal((byte)24, f.Memory.ReadByte(1, 272));
        Assert.Equal((byte)20, f.Memory.ReadByte(1, 275));
        Assert.Equal((byte)27, f.Memory.ReadByte(1, 279));
        Assert.Equal((byte)28, f.Memory.ReadByte(1, 280));
        Assert.Equal((ushort)108, f.Memory.ReadWord(1, 281));
        Assert.Equal((ushort)110, f.Memory.ReadWord(1, 285));
        Assert.Equal((ushort)0x7777, f.State.DX);
        f.Args("GDI.#93", dc, 0x11fff);
        Assert.Throws<InvalidDataException>(() => g.Invoke("GDI.#93", ref f.State));
    }

    [Fact]
    public void BitmapObjectOmitsNativeBitsPointerAndHonorsCapacityAndFailure()
    {
        using var f = new Fixture();
        using var g = new Win16TaskGraphics(f.Memory, f.Backend, f.Handles);
        ushort bitmap = f.Handles.Add(unchecked((nint)0x123456789L), Win16GuiHandleKind.GdiObject, true);
        f.Backend.Run = (name, a) =>
        {
            if (name == "GetObjectType")
            {
                return 7;
            }

            Assert.Equal("GetObjectW", name);
            Marshal.WriteInt32(a[2], 4, 48);
            Marshal.WriteInt32(a[2], 8, 64);
            Marshal.WriteInt32(a[2], 12, 24);
            Marshal.WriteInt16(a[2], 16, 1);
            Marshal.WriteInt16(a[2], 18, 4);
            Marshal.WriteIntPtr(a[2], 24, unchecked((nint)0x765432109L));
            return 32;
        };
        f.Args("GDI.#82", bitmap, 14, 0x10040);
        g.Invoke("GDI.#82", ref f.State);
        Assert.Equal((ushort)14, f.State.AX);
        Assert.Equal((ushort)48, f.Memory.ReadWord(1, 66));
        Assert.Equal(0u, f.Memory.ReadDword(1, 74));
        f.Memory.WriteWord(1, 64, 0xabcd);
        f.Args("GDI.#82", bitmap, 13, 0x10040);
        g.Invoke("GDI.#82", ref f.State);
        Assert.Equal((ushort)0, f.State.AX);
        Assert.Equal((ushort)0xabcd, f.Memory.ReadWord(1, 64));
        f.Backend.Run = (_, _) => 0;
        f.Args("GDI.#82", bitmap, 14, 0x10040);
        g.Invoke("GDI.#82", ref f.State);
        Assert.Equal((ushort)0, f.State.AX);
    }

    [Fact]
    public void UserRectanglesTextAndKeyboardDelayConvertOutputWidths()
    {
        using var f = new Fixture();
        using var u = new Win16TaskUserStructures(f.Memory, f.Backend, f.Handles, 1);
        ushort window = f.Handles.Add(unchecked((nint)0x123456789L), Win16GuiHandleKind.Window);
        f.Backend.Run = (name, a) =>
        {
            switch (name)
            {
                case "GetClientRect":
                    for (int i = 0; i < 4; i++)
                    {
                        Marshal.WriteInt32(a[1], i * 4, i * 10 - 5);
                    }

                    return 1;
                case "GetDlgItemInt":
                    Marshal.WriteInt32(a[2], 1);
                    return 70000;
                case "GetDlgItemTextW":
                    Marshal.WriteInt16(a[2], 0, 0x20ac);
                    Marshal.WriteInt16(a[2], 2, 0);
                    return 1;
                case "SystemParametersInfoW":
                    Marshal.WriteInt32(a[2], 3);
                    return 1;
                default:
                    throw new Exception(name);
            }
        };
        f.Args("USER.#33", window, 0x10040);
        u.Invoke("USER.#33", ref f.State);
        Assert.Equal(unchecked((ushort)-5), f.Memory.ReadWord(1, 64));
        Assert.Equal((ushort)25, f.Memory.ReadWord(1, 70));
        f.Args("USER.#78", 0x10040, 2, 3);
        u.Invoke("USER.#78", ref f.State);
        Assert.Equal(unchecked((ushort)-7), f.Memory.ReadWord(1, 64));
        Assert.Equal((ushort)28, f.Memory.ReadWord(1, 70));
        f.Args("USER.#95", window, 100, 0x10060, 0);
        u.Invoke("USER.#95", ref f.State);
        Assert.Equal((ushort)0, f.State.AX);
        Assert.Equal((ushort)0, f.Memory.ReadWord(1, 96));
        f.Args("USER.#93", window, 100, 0x10080, 8);
        u.Invoke("USER.#93", ref f.State);
        Assert.Equal((ushort)1, f.State.AX);
        Assert.Equal((byte)0x80, f.Memory.ReadByte(1, 128));
        Assert.Equal((byte)0, f.Memory.ReadByte(1, 129));
        f.Args("USER.#483", 0x16, 0, 0x100a0, 0);
        u.Invoke("USER.#483", ref f.State);
        Assert.Equal((ushort)3, f.Memory.ReadWord(1, 160));
        Assert.Equal((ushort)1, f.State.AX);
        f.Args("USER.#483", 0xffff, 0, 0, 0);
        Assert.Throws<NotSupportedException>(() => u.Invoke("USER.#483", ref f.State));
    }

    [Fact]
    public void WsPrintfReadsCdeclWordAndDwordVarargsAndPreflightsItsOutput()
    {
        using var f = new Fixture();
        using var u = new Win16TaskUserStructures(f.Memory, f.Backend, f.Handles, 1);
        f.Memory.WriteBytes(1, 64, System.Text.Encoding.ASCII.GetBytes("%05d %lu %s %%\0"));
        f.Memory.WriteBytes(1, 96, System.Text.Encoding.ASCII.GetBytes("ok\0"));
        f.Args("USER.#420", 0x10100, 0x10040);
        f.Memory.WriteWord(1, (ushort)(f.State.SP + 12), unchecked((ushort)-7));
        f.Memory.WriteDword(1, (ushort)(f.State.SP + 14), 0xfedcba98);
        f.Memory.WriteDword(1, (ushort)(f.State.SP + 18), 0x10060);
        Assert.Equal((ushort)0, u.Invoke("USER.#420", ref f.State));
        Assert.Equal("-0007 4275878552 ok %", Win16TaskKernelServices.ReadString(f.Memory, 0x10100));
        Assert.Equal((ushort)21, f.State.AX);
        f.Args("USER.#420", 0x11fff, 0x10040);
        Assert.Throws<InvalidDataException>(() => u.Invoke("USER.#420", ref f.State));
    }

    [SuppliedNeFact]
    public void OriginalLargeBitmapsMenusAcceleratorsAndDialogsAreConvertedFromSourceResources()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        using var f = new Fixture();
        using var r = new Win16TaskGuiResources(bytes, f.Memory, f.Backend, f.Handles, 1);
        int menus = 0, items = 0, separators = 0;
        f.Backend.Run = (name, a) =>
        {
            if (name is "CreateMenu" or "CreatePopupMenu")
            {
                return unchecked((nint)(0x123456789L + menus++));
            }

            if (name == "AppendMenuW")
            {
                items++;
                if ((a[1] & 0x800) != 0)
                {
                    separators++;
                    Assert.Equal((nint)0, a[2]);
                }
                else
                {
                    Assert.False(a[2] == 0 && string.IsNullOrEmpty(Marshal.PtrToStringUni(a[3])), "Separator became a blank menu item.");
                }

                return 1;
            }

            if (name == "GetDC")
            {
                return 10;
            }

            if (name == "ReleaseDC")
            {
                return 1;
            }

            if (name == "CreateDIBitmap")
            {
                Assert.NotEqual(0, Marshal.ReadInt32(a[1], 4));
                Assert.True(a[3] > a[1]);
                return unchecked((nint)0x323456789L);
            }

            if (name == "CreateAcceleratorTableW")
            {
                Assert.True(a[1] > 0);
                return unchecked((nint)0x423456789L);
            }

            return 1;
        };
        using var catalog = new NeResourceCatalog(bytes);

        uint Name(NeResourceIdentity id)
        {
            if (id.Id is not null)
            {
                return (uint)id.Id.Value;
            }

            f.Memory.WriteBytes(1, 512, System.Text.Encoding.ASCII.GetBytes(id.Name + "\0"));
            return 0x10200;
        }


        f.Args("USER.#150", 1, Name(catalog.Resources.First(r => r.Type.Id == 4).Identity));
        r.Invoke("USER.#150", ref f.State);
        Assert.NotEqual((ushort)0, f.State.AX);
        Assert.True(menus > 1);
        Assert.True(items > 10);
        Assert.True(separators > 0);
        foreach (var row in catalog.Resources.Select((x, i) => (x, i)).Where(r => r.x.Type.Id == 2 && r.x.Length > 65536))
        {
            f.Args("USER.#175", 1, Name(row.x.Identity));
            r.Invoke("USER.#175", ref f.State);
            Assert.NotEqual((ushort)0, f.State.AX);
        }

        f.Args("USER.#177", 1, Name(catalog.Resources.First(r => r.Type.Id == 9).Identity));
        r.Invoke("USER.#177", ref f.State);
        Assert.NotEqual((ushort)0, f.State.AX);
        foreach (var row in catalog.Resources.Select((x, i) => (x, i)).Where(r => r.x.Type.Id == 5))
        {
            byte[] converted = Win16TaskGuiResources.ConvertDialogTemplate(catalog.Extract(row.i + 1).Bytes);
            Assert.True(converted.Length > 18);
            Assert.Equal(catalog.Extract(row.i + 1).Bytes.AsSpan(0, 4).ToArray(), converted.AsSpan(0, 4).ToArray());
        }

        Assert.Throws<InvalidDataException>(() => Win16TaskGuiResources.ConvertDialogTemplate(new byte[4]));
        f.Args("USER.#175", 0x7777, 256);
        r.Invoke("USER.#175", ref f.State);
        Assert.Equal((ushort)0, f.State.AX);
    }
}
