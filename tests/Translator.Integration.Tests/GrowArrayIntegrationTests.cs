using Translator.Assembly;
using Translator.CodeGen;
using Translator.IR;
using Translator.Linker;
using Win16_Runtime;
using System.Buffers.Binary;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class GrowArrayIntegrationTests
{
    private static string Source => File.ReadAllText(Path.GetFullPath("../../../../../tests/Fixtures/Compiler/Acceptance.asm"));

    [Fact]
    public void NativeGrowArrayAllocatesStoresFarPointerAndCleansItsFarArguments()
    {
        var (memory, dispatcher, services, native) = CreateRuntime();
        PrepareArguments(memory, 0x0100, 0x0102, 0x0106, length: 4);
        var state = InitialState();
        PushGrowArgs(ref state, memory, 0x0100, 0x0102, 0x0106, 3, 2);
        dispatcher.Call(ref state, memory, "GrowArray", CallDistance.Far);
        ushort handle = memory.ReadWord(0x1000, 0x0100);
        var pointer = memory.ReadFarPointer(0x1000, 0x0102);
        Assert.Equal((ushort)1, state.AX);
        Assert.NotEqual((ushort)0, handle);
        Assert.NotEqual(handle, pointer.Segment);
        Assert.Equal((ushort)0, pointer.Offset);
        Assert.Equal(6, memory.GetSegment(pointer.Segment).Size);
        Assert.Equal((ushort)7, memory.ReadWord(0x1000, 0x0106));
        Assert.Equal((ushort)0x3000, state.SP);
        Assert.Equal((ushort)0xAAAA, state.SI);
        Assert.Equal(1, services.AllocationCount);
        Assert.NotNull(native);
    }

    [Fact]
    public void NativeGrowArrayReallocatesAndPreservesExistingBytes()
    {
        var (memory, dispatcher, _, _) = CreateRuntime();
        var state = InitialState();
        ushort handle = Allocate(memory, dispatcher, ref state, 8);
        memory.WriteWord(0x1000, 0x0100, handle);
        Win16Stack.Push(ref state, memory, handle);
        dispatcher.Call(ref state, memory, "KERNEL.GlobalLock", CallDistance.Far);
        var oldPointer = new FarPointer(state.AX, state.DX);
        memory.WriteFarPointer(0x1000, 0x0102, oldPointer);
        memory.WriteByte(oldPointer.Segment, oldPointer.Offset, 0xA5);
        memory.WriteWord(0x1000, 0x0106, 2);
        PushGrowArgs(ref state, memory, 0x0100, 0x0102, 0x0106, 2, 3);
        dispatcher.Call(ref state, memory, "GrowArray", CallDistance.Far);
        Assert.Equal((ushort)1, state.AX);
        Assert.Equal(handle, memory.ReadWord(0x1000, 0x0100));
        var grownPointer = memory.ReadFarPointer(0x1000, 0x0102);
        Assert.Equal((byte)0xA5, memory.ReadByte(grownPointer.Segment, grownPointer.Offset));
        Assert.Equal(12, memory.GetSegment(grownPointer.Segment).Size);
        Assert.Equal((ushort)4, memory.ReadWord(0x1000, 0x0106));
        Assert.Equal((ushort)0x3000, state.SP);
    }

    [Fact]
    public void NativeGrowArrayHandlesAllocationFailureWithoutIncreasingLength()
    {
        var (memory, dispatcher, services, _) = CreateRuntime();
        services.FailNextAllocation = true;
        memory.WriteWord(0x1000, 0x0100, 0);
        memory.WriteFarPointer(0x1000, 0x0102, new(0x1111, 0x2222));
        memory.WriteWord(0x1000, 0x0106, 4);
        var state = InitialState();
        PushGrowArgs(ref state, memory, 0x0100, 0x0102, 0x0106, 3, 2);
        dispatcher.Call(ref state, memory, "GrowArray", CallDistance.Far);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal((ushort)0, memory.ReadWord(0x1000, 0x0100));
        Assert.Equal(new FarPointer(0, 0), memory.ReadFarPointer(0x1000, 0x0102));
        Assert.Equal((ushort)4, memory.ReadWord(0x1000, 0x0106));
        Assert.Equal((ushort)0x3000, state.SP);
    }

    [Fact]
    public void NativeGrowArrayHandlesReallocationFailureAndRetainsOldHandleAndPointer()
    {
        var (memory, dispatcher, services, _) = CreateRuntime();
        var state = InitialState();
        ushort handle = Allocate(memory, dispatcher, ref state, 8);
        memory.WriteWord(0x1000, 0x0100, handle);
        Win16Stack.Push(ref state, memory, handle);
        dispatcher.Call(ref state, memory, "KERNEL.GlobalLock", CallDistance.Far);
        var oldPointer = new FarPointer(state.AX, state.DX);
        memory.WriteFarPointer(0x1000, 0x0102, oldPointer);
        services.FailNextReallocation = true;
        memory.WriteWord(0x1000, 0x0106, 2);
        PushGrowArgs(ref state, memory, 0x0100, 0x0102, 0x0106, 2, 3);
        dispatcher.Call(ref state, memory, "GrowArray", CallDistance.Far);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal(handle, memory.ReadWord(0x1000, 0x0100));
        Assert.Equal(oldPointer, memory.ReadFarPointer(0x1000, 0x0102));
        Assert.Equal((ushort)2, memory.ReadWord(0x1000, 0x0106));
        Assert.Equal((ushort)0x3000, state.SP);
    }

    [Fact]
    public void NativeGrowArrayUsesDynamicTablesAndHitsTheConfiguredAllocationLimit()
    {
        var (memory, dispatcher, services, _) = CreateRuntime(new(MaxAllocations: 32, MaxBytes: 1024));
        using (services)
        {
            for (int i = 0; i < 33; i++)
            {
                PrepareArguments(memory, 0x100, 0x102, 0x106, length: 4);
                var state = InitialState();
                PushGrowArgs(ref state, memory, 0x100, 0x102, 0x106, 3, 2);
                dispatcher.Call(ref state, memory, "GrowArray", CallDistance.Far);
                Assert.Equal((ushort)(i < 32
                    ? 1
                    : 0), state.AX);
                Assert.Equal((ushort)(i < 32
                    ? 7
                    : 4), memory.ReadWord(0x1000, 0x106));
                Assert.Equal((ushort)0x3000, state.SP);
            }

            Assert.Equal(32, services.AllocationCount);
            Assert.Equal(192, services.AllocatedBytes);
        }

        Assert.Equal(2, memory.Segments.Count); // Only the host DS and SS mappings remain.
    }

    [Fact]
    public void NativeGrowArrayRetainsExistingDataWhenByteBudgetRejectsReallocation()
    {
        var (memory, dispatcher, services, _) = CreateRuntime(new(MaxBytes: 8));
        using (services)
        {
            var state = InitialState();
            ushort handle = Allocate(memory, dispatcher, ref state, 8);
            var pointer = services.Lock(handle);
            memory.Resolve(pointer).WriteWord(0xA55A);
            memory.WriteWord(0x1000, 0x100, handle);
            memory.WriteFarPointer(0x1000, 0x102, pointer);
            memory.WriteWord(0x1000, 0x106, 2);
            PushGrowArgs(ref state, memory, 0x100, 0x102, 0x106, 2, 3);
            dispatcher.Call(ref state, memory, "GrowArray", CallDistance.Far);
            Assert.Equal((ushort)0, state.AX);
            Assert.Equal(handle, memory.ReadWord(0x1000, 0x100));
            Assert.Equal(pointer, memory.ReadFarPointer(0x1000, 0x102));
            Assert.Equal((ushort)2, memory.ReadWord(0x1000, 0x106));
            Assert.Equal((ushort)0xA55A, memory.Resolve(pointer).ReadWord());
            Assert.Equal(8, services.AllocatedBytes);
        }
    }

    [Fact]
    public void GrowArrayArtifactCarriesAResolvableRuntimeCallRelocation()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Source);
            var function = Assert.Single(new AssemblyParser().ParseSelected(path, new[] { "GrowArray" }).Module.Functions);
            var artifact = GrowArrayCodeGenerator.GenerateArtifact(function, X64Abi.Windows);
            Assert.NotEmpty(artifact.Relocations!);
            Assert.True(artifact.Bytes.Length > 600); // artifact contains the allocation/reallocation control flow
            var relocation = artifact.Relocations![0];
            Assert.Equal(GrowArrayCodeGenerator.WriteWordHelperSymbol, relocation.Symbol);
            Assert.Equal(MachineCodeRelocationKind.Relative32Call, relocation.Kind);
            byte[] code = MachineCodeArtifactRelocator.Relocate(artifact, 0x200000, new Dictionary<string, ulong> { [GrowArrayCodeGenerator.ReadWordHelperSymbol] = 0x200200, [GrowArrayCodeGenerator.WriteWordHelperSymbol] = 0x200300, [GrowArrayCodeGenerator.CallServiceHelperSymbol] = 0x200500 });
            Assert.Equal(0x200300L - (0x200000L + relocation.Offset + 4), BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(relocation.Offset, 4)));
            byte[] image = new Pe32PlusWriter().WriteExecutable(PeFunctionBody.FromArtifact("GrowArray", artifact), new[] { new PeFunctionBody(GrowArrayCodeGenerator.ReadWordHelperSymbol, new byte[] { 0xC3 }), new PeFunctionBody(GrowArrayCodeGenerator.WriteWordHelperSymbol, new byte[] { 0xC3 }), new PeFunctionBody(GrowArrayCodeGenerator.CallServiceHelperSymbol, new byte[] { 0xC3 }) });
            Assert.True(Pe32PlusWriter.IsAmd64Pe32Plus(image));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (SegmentMemory Memory, Win16CallDispatcher Dispatcher, Win16GlobalMemoryServices Services, NativeGrowArray Native) CreateRuntime(Win16GlobalMemoryLimits? limits = null)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Source);
            var parsed = new AssemblyParser().ParseSelected(path, new[] { "GrowArray" });
            Assert.Empty(parsed.Diagnostics);
            NativeGrowArray native = GrowArrayCodeGenerator.Generate(Assert.Single(parsed.Module.Functions));
            var memory = new SegmentMemory();
            memory.Map(new Win16Segment(0x1000));
            memory.Map(new Win16Segment(0x2000));
            var dispatcher = new Win16CallDispatcher();
            var services = new Win16GlobalMemoryServices(memory, dispatcher, limits: limits);
            dispatcher.Register("GrowArray", new(0x3000, 0x0100), CallDistance.Far, (ref MachineState state, SegmentMemory callMemory, Win16CallDispatcher _, Win16CallContext _) => native(new GrowArrayContext(dispatcher), ref state, callMemory));
            return (memory, dispatcher, services, native);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ushort Allocate(SegmentMemory memory, Win16CallDispatcher dispatcher, ref MachineState state, ushort bytes)
    {
        Win16Stack.Push(ref state, memory, 0x0042);
        Win16Stack.Push(ref state, memory, 0);
        Win16Stack.Push(ref state, memory, bytes);
        dispatcher.Call(ref state, memory, "KERNEL.GlobalAlloc", CallDistance.Far);
        return state.AX;
    }

    private static void PrepareArguments(SegmentMemory memory, ushort hMem, ushort ptr, ushort lenPtr, ushort length)
    {
        memory.WriteWord(0x1000, hMem, 0);
        memory.WriteWord(0x1000, lenPtr, length);
        memory.WriteFarPointer(0x1000, ptr, default);
    }

    private static void PushGrowArgs(ref MachineState state, SegmentMemory memory, ushort hMem, ushort ptr, ushort lenPtr, ushort num, ushort size)
    {
        Win16Stack.Push(ref state, memory, size);
        Win16Stack.Push(ref state, memory, num);
        Win16Stack.Push(ref state, memory, lenPtr);
        Win16Stack.Push(ref state, memory, ptr);
        Win16Stack.Push(ref state, memory, hMem);
    }

    private static MachineState InitialState() => new()
    {
        SP = 0x3000,
        IP = 0x0050,
        CS = 0x1000,
        DS = 0x1000,
        SS = 0x2000,
        SI = 0xAAAA
    };
}
