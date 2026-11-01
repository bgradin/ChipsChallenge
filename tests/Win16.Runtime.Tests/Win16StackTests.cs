using Win16_Runtime;
using Xunit;

namespace Win16.Runtime.Tests;

public sealed class Win16StackTests
{
    [Fact]
    public void AppliesNearAndFarCallReturnStackEffectsAndCleanup()
    {
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        var state = new MachineState
        {
            SS = 0x1000,
            SP = 0x100,
            CS = 0x2222,
            IP = 0x3333
        };
        Win16Stack.CallNear(ref state, memory, 0x4444);
        Assert.Equal((ushort)0xFE, state.SP);
        Assert.Equal((ushort)0x4444, state.IP);
        Win16Stack.ReturnNear(ref state, memory, 4);
        Assert.Equal((ushort)0x3333, state.IP);
        Assert.Equal((ushort)0x104, state.SP);
        Win16Stack.CallFar(ref state, memory, 0xAAAA, 0xBBBB);
        Assert.Equal((ushort)0x100, state.SP);
        Assert.Equal((ushort)0xAAAA, state.CS);
        Assert.Equal((ushort)0xBBBB, state.IP);
        Win16Stack.ReturnFar(ref state, memory, 2);
        Assert.Equal((ushort)0x2222, state.CS);
        Assert.Equal((ushort)0x3333, state.IP);
        Assert.Equal((ushort)0x106, state.SP);
    }

    [Fact]
    public void WrapsStackPointerAndMarshalsPushedFarPointerWordOrder()
    {
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        var state = new MachineState
        {
            SS = 0x1000,
            SP = 0x0001
        };
        var pointer = new FarPointer(0x1234, 0x5678);
        Win16Stack.PushFarPointer(ref state, memory, pointer);
        Assert.Equal((ushort)0xFFFD, state.SP);
        Assert.Equal((ushort)0x1234, memory.ReadWord(0x1000, 0xFFFD));
        Assert.Equal((ushort)0x5678, memory.ReadWord(0x1000, 0xFFFF));
        Assert.Equal(pointer, Win16Stack.PopFarPointer(ref state, memory));
        Assert.Equal((ushort)0x0001, state.SP);
    }
}
