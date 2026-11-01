using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Win16.Runtime.Tests;

public sealed class Win16CallTests
{
    [Fact]
    public void DispatchesNestedNearCallsAndAppliesWin16RegisterPreservation()
    {
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        var calls = new Win16CallDispatcher();
        calls.Register("Inner", new(0x1000, 0x0200), CallDistance.Near, (ref MachineState state, SegmentMemory _, Win16CallDispatcher _, Win16CallContext context) =>
        {
            Assert.Equal((ushort)0x0110, context.Caller.Offset);
            Assert.Equal((ushort)0x00FA, context.EntryStackPointer);
            state.AX = 0xFACE;
            state.BX = 0xBEEF;
            state.CX = 0xCCCC;
            state.DX = 0xDDDD;
            state.ES = 0xEEEE;
            state.BP = 1;
            state.DS = 2;
            state.SI = 3;
            state.DI = 4;
            state.Flags = 0;
            return 0;
        });
        calls.Register("Outer", new(0x1000, 0x0100), CallDistance.Near, (ref MachineState state, SegmentMemory segmentMemory, Win16CallDispatcher dispatcher, Win16CallContext context) =>
        {
            Assert.Equal((ushort)0xCAFE, Win16StackArguments.ReadWord(segmentMemory, state.SS, context.EntryStackPointer, context.Distance, 0));
            state.IP = 0x0110; // Native translated body resumes after its nested CALL here.
            dispatcher.Call(ref state, segmentMemory, new Win16CodeAddress(0x1000, 0x0200), CallDistance.Near);
            Assert.Equal((ushort)0x0110, state.IP);
            Assert.Equal((ushort)0xCAFE, state.BP);
            Assert.Equal((ushort)0x1000, state.DS);
            Assert.Equal((ushort)0x1111, state.SI);
            Assert.Equal((ushort)0x2222, state.DI);
            return 2; // RET 2 removes the caller's word argument.
        });
        var state = new MachineState
        {
            AX = 7,
            BX = 8,
            CX = 9,
            DX = 10,
            BP = 0xCAFE,
            SI = 0x1111,
            DI = 0x2222,
            SP = 0x0100,
            IP = 0x0050,
            CS = 0x1000,
            DS = 0x1000,
            SS = 0x1000
        };
        Win16Stack.Push(ref state, memory, 0xCAFE);
        Assert.True(calls.TryResolve("Outer", out var address));
        Assert.Equal(new Win16CodeAddress(0x1000, 0x0100), address);
        calls.Call(ref state, memory, "Outer", CallDistance.Near);
        Assert.Equal((ushort)0x0050, state.IP);
        Assert.Equal((ushort)0x1000, state.CS);
        Assert.Equal((ushort)0x0100, state.SP);
        Assert.Equal((ushort)0xFACE, state.AX);
        Assert.Equal((ushort)0xBEEF, state.BX);
        Assert.Equal((ushort)0xCCCC, state.CX);
        Assert.Equal((ushort)0xDDDD, state.DX);
        Assert.Equal((ushort)0xEEEE, state.ES);
        Assert.Equal((ushort)0xCAFE, state.BP);
        Assert.Equal((ushort)0x1000, state.DS);
        Assert.Equal((ushort)0x1111, state.SI);
        Assert.Equal((ushort)0x2222, state.DI);
        Assert.Equal((ushort)0xCAFE, Win16StackArguments.ReadWord(memory, state.SS, 0x00FC, CallDistance.Near, 0));
    }

    [Fact]
    public void MarshalsFarPointerStackArgumentsAndDispatchesFarEntries()
    {
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        memory.Map(new Win16Segment(0x2000));
        memory.Map(new Win16Segment(0x3000));
        var dispatcher = new Win16CallDispatcher();
        var expected = new FarPointer(0x4567, 0x3000);
        dispatcher.Register("FarService", new(0x2000, 0x0100), CallDistance.Far, (ref MachineState state, SegmentMemory segmentMemory, Win16CallDispatcher _, Win16CallContext context) =>
        {
            Assert.Equal((ushort)0x00F8, context.EntryStackPointer);
            Assert.Equal(expected, Win16StackArguments.ReadFarPointer(segmentMemory, state.SS, context.EntryStackPointer, context.Distance));
            Win16StackArguments.WriteFarPointer(segmentMemory, state.SS, context.EntryStackPointer, context.Distance, new FarPointer(0x1111, 0x2222));
            state.AX = 1;
            state.SI = 0;
            state.DS = 0x2222;
            state.SS = 0x2000;
            return 4;
        });
        var state = new MachineState
        {
            SP = 0x0100,
            IP = 0x0050,
            CS = 0x1000,
            DS = 0x1000,
            SS = 0x1000,
            SI = 0x7777
        };
        Win16Stack.PushFarPointer(ref state, memory, expected);
        ushort preCallArgumentSp = state.SP;
        dispatcher.Call(ref state, memory, new Win16CodeAddress(0x2000, 0x0100), CallDistance.Far);
        Assert.Equal(new FarPointer(0x1111, 0x2222), Win16StackArguments.ReadFarPointer(memory, state.SS, (ushort)0x00F8, CallDistance.Far));
        Assert.Equal((ushort)0x1000, state.CS);
        Assert.Equal((ushort)0x0050, state.IP);
        Assert.Equal(unchecked((ushort)(preCallArgumentSp + 4)), state.SP);
        Assert.Equal((ushort)0x7777, state.SI);
        Assert.Equal((ushort)0x1000, state.DS);
        Assert.Equal((ushort)0x1000, state.SS);
    }

    [Fact]
    public void RejectsMismatchedDistanceAndCrossSegmentNearCallsBeforeChangingState()
    {
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        var dispatcher = new Win16CallDispatcher();
        dispatcher.Register("Near", new(0x1000, 0x0020), CallDistance.Near, (ref MachineState _, SegmentMemory _, Win16CallDispatcher _, Win16CallContext _) => 0);
        var state = new MachineState
        {
            SP = 0x0100,
            IP = 0x0010,
            CS = 0x1000,
            SS = 0x1000
        };
        Assert.Throws<InvalidOperationException>(() => dispatcher.Call(ref state, memory, "Near", CallDistance.Far));
        dispatcher.Register("OtherSegment", new(0x2000, 0x0040), CallDistance.Near, (ref MachineState _, SegmentMemory _, Win16CallDispatcher _, Win16CallContext _) => 0);
        Assert.Throws<InvalidOperationException>(() => dispatcher.Call(ref state, memory, "OtherSegment", CallDistance.Near));
        Assert.Equal((ushort)0x0100, state.SP);
        Assert.Equal((ushort)0x0010, state.IP);
    }

    [Fact]
    public void GlobalMemoryServicesRouteFarCallsAndKeepHandlesDistinctFromLockedPointers()
    {
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        var calls = new Win16CallDispatcher();
        var services = new Win16GlobalMemoryServices(memory, calls);
        var state = new MachineState
        {
            SP = 0x2000,
            IP = 0x0050,
            CS = 0x1000,
            DS = 0x1000,
            SS = 0x1000
        };
        // The source pushes flags, size high word, then size low word before GlobalAlloc.
        Win16Stack.Push(ref state, memory, 0x0042);
        Win16Stack.Push(ref state, memory, 0);
        Win16Stack.Push(ref state, memory, 24);
        calls.Call(ref state, memory, "KERNEL.GlobalAlloc", CallDistance.Far);
        ushort handle = state.AX;
        Assert.NotEqual((ushort)0, handle);
        Assert.Equal((ushort)0x2000, state.SP); // RETF 6 removes the argument words.
        Assert.Equal((ushort)0x0050, state.IP);
        Assert.Equal((ushort)0x1000, state.CS);
        Win16Stack.Push(ref state, memory, handle);
        calls.Call(ref state, memory, "KERNEL.GlobalLock", CallDistance.Far);
        var pointer = new FarPointer(state.AX, state.DX);
        Assert.Equal((ushort)0, pointer.Offset);
        Assert.NotEqual(handle, pointer.Segment);
        Assert.Equal(24, memory.GetSegment(pointer.Segment).Size); // word order decoded as AX low, DX high.
        memory.WriteByte(pointer.Segment, pointer.Offset, 0x7A);
        Assert.Equal((byte)0x7A, memory.ReadByte(pointer.Segment, pointer.Offset));
        // GlobalReAlloc is invoked with flags, a 32-bit size, then the handle on the stack.
        Win16Stack.Push(ref state, memory, handle);
        Win16Stack.Push(ref state, memory, 0);
        Win16Stack.Push(ref state, memory, 40);
        Win16Stack.Push(ref state, memory, 2);
        calls.Call(ref state, memory, "KERNEL.GlobalReAlloc", CallDistance.Far);
        Assert.Equal(handle, state.AX);
        Assert.Equal(1, services.AllocationCount);
        Win16Stack.Push(ref state, memory, handle);
        calls.Call(ref state, memory, "KERNEL.GlobalLock", CallDistance.Far);
        var movedPointer = new FarPointer(state.AX, state.DX);
        Assert.Equal(40, memory.GetSegment(movedPointer.Segment).Size);
        Assert.Equal((byte)0x7A, memory.ReadByte(movedPointer.Segment, movedPointer.Offset)); // reallocation copied contents
    }

    [Fact]
    public void GlobalAllocationServicesCanFailDeterministically()
    {
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        var calls = new Win16CallDispatcher();
        var services = new Win16GlobalMemoryServices(memory, calls);
        var state = new MachineState
        {
            SP = 0x2000,
            IP = 0x0050,
            CS = 0x1000,
            SS = 0x1000
        };
        services.FailNextAllocation = true;
        Win16Stack.Push(ref state, memory, 0x0042);
        Win16Stack.Push(ref state, memory, 0);
        Win16Stack.Push(ref state, memory, 8);
        calls.Call(ref state, memory, "KERNEL.GlobalAlloc", CallDistance.Far);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal(0, services.AllocationCount);
        Assert.Equal((ushort)0x2000, state.SP);
        Assert.Equal((ushort)0x0050, state.IP);
    }
}
