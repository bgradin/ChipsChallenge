using Win16_Runtime;
using Xunit;

namespace Win16.Runtime.Tests;

public sealed class MarshallingTests
{
    private static (SegmentMemory Memory, Win16Marshaller Marshaller) Create()
    {
        var m = new SegmentMemory();
        m.Map(new(0x1000));
        return (m, new(m));
    }

    [Fact]
    public void PrimitiveConversionsPreserveSignednessBoolAndPackedReturns()
    {
        Assert.Equal((short)-1, Win16Marshaller.SignedWord(65535));
        Assert.Equal(-1, Win16Marshaller.SignedDword(uint.MaxValue));
        Assert.True(Win16Marshaller.Bool(0x8000));
        Assert.False(Win16Marshaller.Bool((ushort)0));
        Assert.Equal((ushort)1, Win16Marshaller.Bool(true));
        var state = new MachineState
        {
            AXHigh = 0xABCD,
            DXHigh = 0x1234
        };
        Win16Marshaller.WriteDwordReturn(ref state, 0xFFEEABCD);
        Assert.Equal((ushort)0xABCD, state.AX);
        Assert.Equal((ushort)0xFFEE, state.DX);
        Assert.Equal((ushort)0xABCD, state.AXHigh);
    }

    [Fact]
    public void AnsiStringsUseExplicitWindows1252AndBoundedOutput()
    {
        var (m, b) = Create();
        m.WriteBytes(0x1000, 0x100, new byte[] { 0x80, 0xE9, 0 });
        Assert.Equal("€é", b.ReadAnsi(new(0x100, 0x1000), 3));
        Assert.Throws<InvalidDataException>(() => b.ReadAnsi(new(0x100, 0x1000), 2));
        b.WriteAnsi(new(0x200, 0x1000), "€é", 3);
        Assert.Equal(new byte[] { 0x80, 0xE9, 0 }, m.ReadBytes(0x1000, 0x200, 3));
        Assert.Throws<ArgumentException>(() => b.WriteAnsi(new(0x200, 0x1000), "long", 3));
        Assert.Equal(new byte[] { 0x80, 0xE9, 0 }, m.ReadBytes(0x1000, 0x200, 3));
    }

    [Fact]
    public void ApiBuffersValidateNullPermissionsAndBoundsWithoutCpuWrap()
    {
        var (m, b) = Create();
        m.WriteByte(0x1000, 65535, 9);
        Assert.Empty(b.CopyInput(default, 0));
        var nullNear = new MachineState
        {
            DS = 0x1000
        };
        Assert.Throws<ArgumentException>(() => b.CopyNearInput(nullNear, 0, 1));
        Assert.Throws<ArgumentException>(() => b.CopyInput(default, 1));
        Assert.Throws<ArgumentException>(() => b.CopyInput(new(65535, 0x1000), 2));
        Assert.Throws<ArgumentException>(() => b.CopyOutput(new(65535, 0x1000), new byte[] { 1, 2 }));
        Assert.Equal((byte)9, m.ReadByte(0x1000, 65535));
        m.Map(new(0x2000, 8, canWrite: false));
        Assert.Throws<InvalidOperationException>(() => b.CopyOutput(new(0, 0x2000), new byte[] { 1 }));
        var state = new MachineState
        {
            DS = 0x1000
        };
        Assert.Equal(new byte[] { 9 }, b.CopyNearInput(state, 65535, 1));
    }

    [Fact]
    public void PointAndRectHaveActualPackedWin16Layouts()
    {
        var (m, b) = Create();
        var rect = new Win16Rect(-1, -32768, 32767, 2);
        b.WriteRect(new(0x100, 0x1000), rect);
        Assert.Equal(new byte[] { 255, 255, 0, 128, 255, 127, 2, 0 }, m.ReadBytes(0x1000, 0x100, 8));
        Assert.Equal(rect, b.ReadRect(new(0x100, 0x1000)));
        var point = new Win16Point(-3, 4);
        b.WritePoint(new(0x200, 0x1000), point);
        Assert.Equal(point, b.ReadPoint(new(0x200, 0x1000)));
    }

    [Fact]
    public void TypedHandlesLookupNativeValuesAndReleaseEachOwnedEntryOnce()
    {
        var released = new List<nint>();
        var table = new Win16HandleTable<nint>(released.Add);
        ushort id = table.Add(unchecked((nint)0x1234567890L));
        Assert.Equal(unchecked((nint)0x1234567890L), table.Get(id));
        Assert.NotEqual((nint)id, table.Get(id));
        Assert.Throws<KeyNotFoundException>(() => table.Get(0));
        Assert.True(table.Remove(id));
        Assert.False(table.Remove(id));
        Assert.Throws<KeyNotFoundException>(() => table.Get(id));
        table.Add((nint)7);
        table.Dispose();
        table.Dispose();
        Assert.Equal(2, released.Count);
        Assert.Throws<ObjectDisposedException>(() => table.Get(id));
    }

    [Fact]
    public void DisposalAttemptsEveryReleaseAndReportsFailures()
    {
        var released = new List<int>();
        var failure = new InvalidOperationException("Release failed.");
        var table = new Win16HandleTable<int>(value =>
        {
            released.Add(value);
            if (value == 1)
            {
                throw failure;
            }
        });
        table.Add(1);
        table.Add(2);
        var error = Assert.Throws<AggregateException>(table.Dispose);
        Assert.Same(failure, Assert.Single(error.InnerExceptions));
        Assert.Equal(new[] { 1, 2 }, released);
        table.Dispose();
        Assert.Equal(2, released.Count);
    }
}
