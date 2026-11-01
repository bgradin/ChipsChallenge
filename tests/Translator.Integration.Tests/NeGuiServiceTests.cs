using System.Runtime.InteropServices;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeGuiServiceTests
{
    private sealed class Backend : IWin16GuiBackend
    {
        public Func<string, string, nint[], nint> Handler = (_, _, _) => 1;
        public List<(string Module, string Function, nint[] Arguments)> Calls { get; } = new();

        public nint Call(string module, string function, ReadOnlySpan<nint> arguments)
        {
            var args = arguments.ToArray();
            Calls.Add((module, function, args));
            return Handler(module, function, args);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly SegmentMemory Memory = new();
        private readonly Win16Segment _data;
        public readonly Backend Backend = new();
        public readonly Win16TaskGui Gui;
        public MachineState State = new()
        {
            SS = 1,
            DS = 1,
            SP = 1024,
            DX = 0x7777,
            BP = 0x1234
        };

        public Fixture()
        {
            _data = Memory.CreateSegment(1, 2048);
            Memory.Map(_data);
            Gui = new(Memory, Backend);
        }

        public ushort Call(string id, params uint[] args)
        {
            var c = Win16ExportContracts.Get(id);
            for (int i = 0; i < c.Arguments.Count; i++)
            {
                var a = c.Arguments[i];
                ushort offset = (ushort)(State.SP + 4 + a.StackWord * 2);
                if (a.Bytes == 4)
                {
                    Memory.WriteDword(1, offset, args[i]);
                }
                else
                {
                    Memory.WriteWord(1, offset, (ushort)args[i]);
                }
            }

            return Gui.Invoke(id, ref State);
        }

        public void Dispose()
        {
            Gui.Dispose();
            _data.Dispose();
        }
    }

    [Theory]
    [InlineData(24, -1, 32767)]
    [InlineData(24, 2, 2)]
    [InlineData(24, 256, 256)]
    [InlineData(12, -1, 65535)]
    public void DeviceColorCountSupportsSignedWin16Comparisons(int index, int host, int expected)
    {
        using var f = new Fixture();
        ushort dc = f.Gui.Handles.Add(123, Win16GuiHandleKind.Dc);
        f.Backend.Handler = (_, _, _) => host;
        f.Call("GDI.#80", dc, (uint)index);
        Assert.Equal((ushort)expected, f.State.AX);
    }

    public static IEnumerable<object[]> Plans => Win16TaskGui.Plans.Select(p => new object[] { p.Identity });

    [Theory]
    [MemberData(nameof(Plans))]
    public void EveryScalarPlanUsesPhysicalPascalArgumentsAndTypedNativeHandles(string identity)
    {
        using var f = new Fixture();
        var plan = Win16TaskGui.Plans.Single(p => p.Identity == identity);
        uint[] words = new uint[plan.Arguments.Count];
        nint[] expected = new nint[words.Length];
        for (int i = 0; i < words.Length; i++)
        {
            string type = plan.Arguments[i];
            if (type == "s")
            {
                words[i] = unchecked((ushort)(-20 - i));
                expected[i] = -20 - i;
            }
            else if (type == "u")
            {
                words[i] = (uint)(0xf000 + i);
                expected[i] = (nint)words[i];
            }
            else if (type == "l")
            {
                words[i] = 0xfedcba98;
                expected[i] = (nint)(nuint)words[i];
            }
            else if (type == "text")
            {
                words[i] = 0x10040;
                f.Memory.WriteBytes(1, 64, new byte[] { 0x80, (byte)'A', 0 });
            }
            else
            {
                var kind = type.TrimEnd('?').Replace("owned-", "") switch
                {
                    "h" => Win16GuiHandleKind.Window,
                    "d" => Win16GuiHandleKind.Dc,
                    "g" => Win16GuiHandleKind.GdiObject,
                    "m" => Win16GuiHandleKind.Menu,
                    "c" => Win16GuiHandleKind.Cursor,
                    _ => throw new Exception()
                };
                expected[i] = (nint)(0x123456780000L + i + 1);
                words[i] = f.Gui.Handles.Add(expected[i], kind, type.StartsWith("owned-"));
            }
        }

        nint nativeResult = plan.Result is "h" or "d" or "g" or "m" or "c" or "owned-d"
            ? unchecked((nint)0x765432109876L)
            : plan.Result == "muldiv"
                ? unchecked((nint)(uint)-21)
                : unchecked((nint)0x89abcdef);
        f.Backend.Handler = (module, function, args) =>
        {
            Assert.Equal(plan.NativeName, function);
            Assert.Equal(identity == "GDI.#128"
                ? "KERNEL32"
                : plan.Module == "GDI"
                    ? "GDI32"
                    : "USER32", module);
            Assert.Equal(expected.Length, args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                if (plan.Arguments[i] == "text")
                {
                    Assert.Equal("€A", Marshal.PtrToStringUni(args[i]));
                }
                else
                {
                    Assert.Equal(expected[i], args[i]);
                }
            }

            return nativeResult;
        };
        Assert.Equal(Win16ExportContracts.Get(identity).CleanupBytes, f.Call(identity, words));
        Assert.Equal((ushort)0x1234, f.State.BP);
        Assert.Equal((ushort)1024, f.State.SP);
        if (plan.Result == "long")
        {
            Assert.Equal((ushort)0xcdef, f.State.AX);
            Assert.Equal((ushort)0x89ab, f.State.DX);
        }
        else
        {
            Assert.Equal((ushort)0x7777, f.State.DX);
            if (plan.Result == "muldiv")
            {
                Assert.Equal(unchecked((ushort)-21), f.State.AX);
            }
            else if (plan.Result == "word")
            {
                Assert.Equal((ushort)0xcdef, f.State.AX);
            }
            else if (plan.Result != "void")
            {
                Assert.NotEqual((ushort)0, f.State.AX);
            }
        }

        if (plan.Result == "owned-d")
        {
            Assert.Contains(f.Gui.Handles.Entries, h => h.Id == f.State.AX && h.Owned && h.Native == nativeResult);
        }

        if (plan.ReleaseArgument >= 0)
        {
            Assert.DoesNotContain(f.Gui.Handles.Entries, h => h.Id == words[plan.ReleaseArgument]);
        }

        // Cleanup uses the same backend, but must not repeat this call's assertions.
        f.Backend.Handler = (_, _, _) => 1;
    }

    [Fact]
    public void InvalidAndBorrowedHandlesFailWithoutCallingTheHostAndStaleIdsAreNotRecycled()
    {
        using var f = new Fixture();
        ushort borrowed = f.Gui.Handles.Add(unchecked((nint)0x123456789L), Win16GuiHandleKind.Dc);
        f.Call("GDI.#68", borrowed);
        Assert.Equal((ushort)0, f.State.AX);
        Assert.Empty(f.Backend.Calls);
        f.Call("USER.#154", borrowed, 1, 0);
        Assert.Equal(ushort.MaxValue, f.State.AX);
        Assert.Empty(f.Backend.Calls);
        f.Call("GDI.#1", 0xffff, 0);
        Assert.Equal(ushort.MaxValue, f.State.AX);
        Assert.Equal(ushort.MaxValue, f.State.DX);
        ushort owned = f.Gui.Handles.Add(unchecked((nint)0x987654321L), Win16GuiHandleKind.Dc, true);
        f.Backend.Handler = (_, _, _) => 0;
        f.Call("GDI.#68", owned);
        Assert.True(f.Gui.Handles.TryGet(owned, Win16GuiHandleKind.Dc, out _));
        f.Backend.Handler = (_, _, _) => 1;
        f.Call("GDI.#68", owned);
        Assert.False(f.Gui.Handles.TryGet(owned, Win16GuiHandleKind.Dc, out _));
        Assert.NotEqual(owned, f.Gui.Handles.Add(unchecked((nint)0x987654321L), Win16GuiHandleKind.Dc, true));
    }

    [Fact]
    public void TextIsBoundedNullHandlesAreExplicitAndMulDivRejectsWideResults()
    {
        using var f = new Fixture();
        ushort window = f.Gui.Handles.Add(unchecked((nint)0x123456789L), Win16GuiHandleKind.Window);
        f.Memory.WriteByte(1, 2047, 65);
        Assert.Throws<InvalidDataException>(() => f.Call("USER.#37", window, 0x107ff));
        Assert.Empty(f.Backend.Calls);
        f.Backend.Handler = (_, _, args) =>
        {
            Assert.Equal((nint)0, args[0]);
            return 0;
        };
        f.Call("GDI.#52", 0);
        Assert.Equal((ushort)0, f.State.AX);
        f.Backend.Handler = (_, _, _) => 32768;
        f.Call("GDI.#128", 32767, 2, 1);
        Assert.Equal(ushort.MaxValue, f.State.AX);
        f.Backend.Handler = (_, _, _) => unchecked((nint)(uint)-1);
        f.Call("GDI.#128", 1, 1, 0);
        Assert.Equal(ushort.MaxValue, f.State.AX);
    }

    [Fact]
    public void ParentDestructionInvalidatesBorrowedChildrenAndDisposeReleasesOnlyOwnedObjectsOnce()
    {
        using var f = new Fixture();
        ushort parent = f.Gui.Handles.Add(unchecked((nint)0x123456789L), Win16GuiHandleKind.Window, true);
        f.Backend.Handler = (_, _, _) => unchecked((nint)0x223456789L);
        f.Call("USER.#91", parent, 12);
        ushort child = f.State.AX;
        f.Backend.Handler = (_, _, _) => 1;
        f.Call("USER.#53", parent);
        Assert.False(f.Gui.Handles.TryGet(child, Win16GuiHandleKind.Window, out _));
        f.Gui.Handles.Add(unchecked((nint)0x323456789L), Win16GuiHandleKind.Dc, true);
        f.Gui.Handles.Add(unchecked((nint)0x423456789L), Win16GuiHandleKind.GdiObject);
        f.Backend.Calls.Clear();
        f.Gui.Dispose();
        f.Gui.Dispose();
        Assert.Single(f.Backend.Calls);
        Assert.Equal("DeleteDC", f.Backend.Calls[0].Function);
        Assert.Throws<ObjectDisposedException>(() => f.Call("USER.#19"));
    }

    [WindowsFact]
    public void WindowsBackendCreatesAndDeletesARealCompatibleDc()
    {
        using var backend = new WindowsTaskGuiBackend();
        var memory = new SegmentMemory();
        using var segment = memory.CreateSegment(1, 2048);
        memory.Map(segment);
        using var gui = new Win16TaskGui(memory, backend);
        var state = new MachineState
        {
            SS = 1,
            SP = 1024
        };
        memory.WriteWord(1, 1028, 0);
        gui.Invoke("GDI.#52", ref state);
        ushort id = state.AX;
        Assert.NotEqual((ushort)0, id);
        Assert.True(gui.Handles.TryGet(id, Win16GuiHandleKind.Dc, out var handle));
        Assert.NotEqual((nint)0, handle!.Native);
        memory.WriteWord(1, 1028, id);
        gui.Invoke("GDI.#68", ref state);
        Assert.NotEqual((ushort)0, state.AX);
        Assert.Empty(gui.Handles.Entries);
    }
}
