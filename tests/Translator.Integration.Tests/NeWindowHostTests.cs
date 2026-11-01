using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeWindowHostTests
{
    private sealed class Backend : IWin16GuiBackend
    {
        public Win16TaskWindowHost.WindowProcedure? Procedure;
        public readonly nint Window = unchecked((nint)0x123456789L);
        public Func<string, nint[], nint>? Handler;

        public nint Call(string module, string function, ReadOnlySpan<nint> args)
        {
            if (Handler is not null)
            {
                return Handler(function, args.ToArray());
            }

            if (function == "GetModuleHandleW")
            {
                return 0x4444;
            }

            if (function == "RegisterClassW")
            {
                Procedure = Marshal.GetDelegateForFunctionPointer<Win16TaskWindowHost.WindowProcedure>(Marshal.ReadIntPtr(args[0], 8));
                return 1;
            }

            if (function == "CreateWindowExW")
            {
                Assert.NotNull(Procedure);
                Assert.NotEqual((nint)0, Procedure(Window, 0x81, 0, 0));
                return Window;
            }

            if (function == "DestroyWindow")
            {
                Procedure?.Invoke(args[0], 0x82, 0, 0);
                return 1;
            }

            return 1;
        }
    }

    [Fact]
    public void RegisteredCallbackUsesPascalWordsDxAxAndRestoresTheSuspendedCpu()
    {
        // Store five physical argument words to DS:80..88, return 1234:5678, RETF 10.
        byte[] code =
        {
            0x55,
            0x8b,
            0xec,
            0x8b,
            0x46,
            0x06,
            0xa3,
            0x80,
            0,
            0x8b,
            0x46,
            0x08,
            0xa3,
            0x82,
            0,
            0x8b,
            0x46,
            0x0a,
            0xa3,
            0x84,
            0,
            0x8b,
            0x46,
            0x0c,
            0xa3,
            0x86,
            0,
            0x8b,
            0x46,
            0x0e,
            0xa3,
            0x88,
            0,
            0xb8,
            0x78,
            0x56,
            0xba,
            0x34,
            0x12,
            0x5d,
            0xca,
            0x0a,
            0
        };
        byte[] bytes = NeDiscoveryTests.Fixture(code);
        NeLoaderTests.W(bytes, 0x94, 0);
        NeLoaderTests.W(bytes, 0x90, 512);
        using var p = new NeWholeProgramControlProgram(bytes);
        p.TaskMemory.GrowDataSegment(65536);
        var backend = new Backend();
        var handles = new Win16GuiHandles();
        using var resources = new Win16TaskGuiResources(bytes, p.Memory, backend, handles, p.InitialState.DS);
        using var host = new Win16TaskWindowHost(p.Memory, p.TaskMemory, p.Control, backend, handles, resources, p.InitialState);
        var state = p.InitialState;
        state.EAX = 0xabcd0000;
        state.EDX = 0xeeee0000;
        ushort ds = state.DS;

        uint Ptr(int at) => (uint)ds << 16 | (ushort)at;

        p.Memory.WriteBytes(ds, 512, System.Text.Encoding.ASCII.GetBytes("SyntheticGuestClass\0"));
        byte[] cls = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(cls.AsSpan(2), (uint)state.CS << 16);
        BinaryPrimitives.WriteUInt16LittleEndian(cls.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(cls.AsSpan(10), ds);
        BinaryPrimitives.WriteUInt32LittleEndian(cls.AsSpan(22), Ptr(512));
        p.Memory.WriteBytes(ds, 256, cls);

        ushort Call(string id, params uint[] args)
        {
            var c = Win16ExportContracts.Get(id);
            foreach (var pair in c.Arguments.Select((a, i) => (a, i)))
            {
                ushort at = (ushort)(state.SP + 4 + pair.a.StackWord * 2);
                if (pair.a.Bytes == 4)
                {
                    p.Memory.WriteDword(state.SS, at, args[pair.i]);
                }
                else
                {
                    p.Memory.WriteWord(state.SS, at, (ushort)args[pair.i]);
                }
            }

            return host.Invoke(id, ref state);
        }


        Assert.Equal((ushort)4, Call("USER.#57", Ptr(256)));
        Assert.Equal((ushort)1, state.AX);
        Assert.Equal((ushort)30, Call("USER.#41", Ptr(512), 0, 0, 1, 2, 40, 50, 0, 0, ds, 0xdeadbeef));
        ushort window = state.AX;
        Assert.NotEqual((ushort)0, window);
        var before = state;
        ushort child = handles.Add(unchecked((nint)0x223456789L), Win16GuiHandleKind.Window, false, window);
        nint result = backend.Procedure!(backend.Window, 0x111, (nuint)(100u | 7u << 16), unchecked((nint)0x223456789L));
        Assert.Equal(unchecked((nint)0x12345678), result);
        Assert.Equal(before, state);
        Assert.Equal(child, p.Memory.ReadWord(ds, 128));
        Assert.Equal((ushort)7, p.Memory.ReadWord(ds, 130));
        Assert.Equal((ushort)100, p.Memory.ReadWord(ds, 132));
        Assert.Equal((ushort)0x111, p.Memory.ReadWord(ds, 134));
        Assert.Equal(window, p.Memory.ReadWord(ds, 136));
        Assert.Equal(0, p.TaskMemory.Local.AllocationCount);
        Call("USER.#134", window, 0, 0xbeef);
        Call("USER.#133", window, 0);
        Assert.Equal((ushort)0xbeef, state.AX);
        Call("USER.#134", window, 4, 0xffff);
        Call("USER.#133", window, 4);
        Assert.Equal((ushort)0, state.AX);
        host.Dispose();
        Assert.False(handles.TryGet(window, Win16GuiHandleKind.Window, out _));
    }

    [Fact]
    public void PaintAndDcLeasesPreserveNativeStructuresAndRejectMismatchedOwners()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xcb);
        NeLoaderTests.W(bytes, 0x94, 0);
        NeLoaderTests.W(bytes, 0x90, 512);
        using var p = new NeWholeProgramControlProgram(bytes);
        p.TaskMemory.GrowDataSegment(65536);
        var backend = new Backend();
        var handles = new Win16GuiHandles();
        using var resources = new Win16TaskGuiResources(bytes, p.Memory, backend, handles, p.InitialState.DS);
        using var host = new Win16TaskWindowHost(p.Memory, p.TaskMemory, p.Control, backend, handles, resources, p.InitialState);
        var state = p.InitialState;
        ushort window = handles.Add(backend.Window, Win16GuiHandleKind.Window);
        uint pointer = (uint)state.DS << 16 | 256;
        int ended = 0;
        backend.Handler = (name, a) =>
        {
            if (name == "BeginPaint")
            {
                Marshal.WriteIntPtr(a[1], unchecked((nint)0x323456789L));
                Marshal.WriteInt32(a[1], 8, 1);
                Marshal.WriteInt32(a[1], 20, 40);
                Marshal.WriteInt32(a[1], 24, 50);
                Marshal.WriteByte(a[1], 40, 0xab);
                return unchecked((nint)0x323456789L);
            }

            if (name == "EndPaint")
            {
                Assert.Equal((byte)0xab, Marshal.ReadByte(a[1], 40));
                ended++;
                return 1;
            }

            if (name == "GetDC")
            {
                return unchecked((nint)0x423456789L);
            }

            return 1;
        };

        void Args(string id, params uint[] args)
        {
            var c = Win16ExportContracts.Get(id);
            for (int i = 0; i < c.Arguments.Count; i++)
            {
                var a = c.Arguments[i];
                ushort at = (ushort)(state.SP + 4 + a.StackWord * 2);
                if (a.Bytes == 4)
                {
                    p.Memory.WriteDword(state.SS, at, args[i]);
                }
                else
                {
                    p.Memory.WriteWord(state.SS, at, (ushort)args[i]);
                }
            }
        }


        Args("USER.#39", window, pointer);
        host.Invoke("USER.#39", ref state);
        ushort dc = state.AX;
        Assert.NotEqual((ushort)0, dc);
        Assert.Equal(dc, p.Memory.ReadWord(state.DS, 256));
        Assert.Equal((ushort)1, p.Memory.ReadWord(state.DS, 258));
        Assert.Equal((ushort)40, p.Memory.ReadWord(state.DS, 264));
        Assert.Throws<InvalidOperationException>(() => host.Invoke("USER.#39", ref state));
        Args("USER.#40", window, pointer);
        host.Invoke("USER.#40", ref state);
        Assert.Equal(1, ended);
        Assert.False(handles.TryGet(dc, Win16GuiHandleKind.Dc, out _));
        Args("USER.#66", window);
        host.Invoke("USER.#66", ref state);
        dc = state.AX;
        Args("USER.#68", window, dc);
        host.Invoke("USER.#68", ref state);
        Assert.Equal((ushort)1, state.AX);
        Assert.False(handles.TryGet(dc, Win16GuiHandleKind.Dc, out _));
    }
}
