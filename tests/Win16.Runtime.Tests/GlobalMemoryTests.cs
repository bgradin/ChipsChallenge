using Win16_Runtime;
using Xunit;

namespace Win16.Runtime.Tests;

public sealed class GlobalMemoryTests
{
    private static (SegmentMemory Memory, Win16GlobalMemoryServices Services) Create(Win16GlobalMemoryLimits? limits = null)
    {
        var memory = new SegmentMemory();
        return (memory, new(memory, new Win16CallDispatcher(), limits: limits));
    }

    [Fact]
    public void DynamicTablesExceedEightAndDisposeOnlyOwnedMappings()
    {
        var (memory, services) = Create();
        memory.Map(new Win16Segment(0x5000, 2)); // Selector collision must be skipped.
        for (int i = 0; i < 32; i++)
        {
            ushort handle = services.Allocate(16);
            var pointer = services.Lock(handle);
            Assert.NotEqual((ushort)0, handle);
            Assert.NotEqual(handle, pointer.Segment);
            Assert.NotEqual((ushort)0x5000, pointer.Segment);
            Assert.True(services.TryGetAllocation(handle, out var block));
            Assert.Equal((ushort)1, block!.LockCount);
        }

        Assert.Equal(32, services.AllocationCount);
        Assert.Equal(512, services.AllocatedBytes);
        services.Dispose();
        services.Dispose();
        Assert.Equal(0, services.AllocationCount);
        Assert.Equal(0, services.AllocatedBytes);
        Assert.Equal((ushort)0x5000, Assert.Single(memory.Segments).Selector);
        Assert.Throws<ObjectDisposedException>(() => services.Allocate(1));
        Assert.Throws<ObjectDisposedException>(() => services.Lock(1));
    }

    [Fact]
    public void LocksCountAndFinalUnlockReturnsFalseWithoutInvalidatingPointer()
    {
        var (memory, services) = Create();
        using (services)
        {
            ushort handle = services.Allocate(4);
            var pointer = services.Lock(handle);
            Assert.Equal(pointer, services.Lock(handle));
            Assert.False(services.Free(handle));
            Assert.True(services.Unlock(handle));
            Assert.False(services.Unlock(handle));
            Assert.False(services.Unlock(handle));
            Assert.True(services.TryGetAllocation(handle, out var block));
            Assert.Equal((ushort)0, block!.LockCount);
            memory.Resolve(pointer).WriteByte(0xA5);
            Assert.True(services.Free(handle));
            Assert.False(services.Free(handle));
            Assert.False(services.TryGetAllocation(handle, out _));
            Assert.Throws<InvalidOperationException>(() => memory.Resolve(pointer));
            Assert.Equal(default, services.Lock(handle));
            Assert.False(services.Unlock(handle));
        }
    }

    [Fact]
    public void ResizePreservesDataAndInvalidatesUnlockedPointersButKeepsLockedSelectors()
    {
        var (memory, services) = Create();
        using (services)
        {
            ushort handle = services.Allocate(4);
            var old = services.Lock(handle);
            memory.Resolve(old).WriteWord(0xA55A);
            Assert.Equal(handle, services.Reallocate(handle, 8));
            Assert.True(services.TryGetAllocation(handle, out var locked));
            Assert.Equal(old.Segment, locked!.Selector);
            Assert.Equal((ushort)1, locked.LockCount);
            Assert.Equal((ushort)0xA55A, memory.Resolve(old).ReadWord());
            Assert.False(services.Unlock(handle));
            Assert.Equal(handle, services.Reallocate(handle, 12));
            Assert.Throws<InvalidOperationException>(() => memory.Resolve(old));
            var moved = services.Lock(handle);
            Assert.NotEqual(old, moved);
            Assert.Equal((ushort)0xA55A, memory.Resolve(moved).ReadWord());
            Assert.All(memory.ReadBytes(moved.Segment, 4, 8), value => Assert.Equal((byte)0, value));
            services.Unlock(handle);
            Assert.Equal(handle, services.Reallocate(handle, 2));
            var shrunk = services.Lock(handle);
            Assert.Equal((ushort)0xA55A, memory.Resolve(shrunk).ReadWord());
            Assert.Throws<InvalidOperationException>(() => memory.ReadByte(shrunk.Segment, 2));
        }
    }

    [Fact]
    public void ResourceAndInjectedFailuresLeaveOriginalAllocationUnchangedAndCapacityIsReclaimed()
    {
        var (memory, services) = Create(new(MaxAllocations: 2, MaxBytes: 16));
        using (services)
        {
            ushort handle = services.Allocate(8);
            var pointer = services.Lock(handle);
            memory.Resolve(pointer).WriteByte(0xA5);
            services.Unlock(handle);
            Assert.True(services.TryGetAllocation(handle, out var before));
            Assert.Equal((ushort)0, services.Reallocate(handle, 17));
            services.FailNextReallocation = true;
            Assert.Equal((ushort)0, services.Reallocate(handle, 12));
            Assert.True(services.TryGetAllocation(handle, out var after));
            Assert.Equal(before, after);
            Assert.Equal((byte)0xA5, memory.Resolve(pointer).ReadByte());
            services.FailNextAllocation = true;
            Assert.Equal((ushort)0, services.Allocate(1));
            ushort second = services.Allocate(8);
            Assert.NotEqual((ushort)0, second);
            Assert.Equal((ushort)0, services.Allocate(1));
            Assert.True(services.Free(second));
            Assert.NotEqual((ushort)0, services.Allocate(8));
            Assert.Equal(16, services.AllocatedBytes);
        }
    }

    [Fact]
    public void ZeroSizedMovableBlocksHaveHandlesAndCanBeReconstituted()
    {
        var (memory, services) = Create();
        using (services)
        {
            ushort handle = services.Allocate(0);
            Assert.NotEqual((ushort)0, handle);
            Assert.Equal(default, services.Lock(handle));
            Assert.Empty(memory.Segments);
            Assert.Equal(handle, services.Reallocate(handle, 4));
            var pointer = services.Lock(handle);
            Assert.False(pointer.IsNull);
            Assert.Equal((ushort)0, services.Reallocate(handle, 0)); // Cannot discard a locked block.
            services.Unlock(handle);
            Assert.Equal(handle, services.Reallocate(handle, 0));
            Assert.Equal(default, services.Lock(handle));
            Assert.Throws<InvalidOperationException>(() => memory.Resolve(pointer));
            Assert.Equal(0, services.AllocatedBytes);
        }
    }

    [Fact]
    public void SizeAndFlagBoundariesAndInvalidHandlesFailWithoutCreatingMappings()
    {
        var (memory, services) = Create();
        using (services)
        {
            Assert.Equal((ushort)0, services.Allocate(65537));
            Assert.Equal((ushort)0, services.Allocate(uint.MaxValue));
            Assert.Equal((ushort)0, services.Allocate(1, 0)); // Fixed/discardable policies are unsupported.
            Assert.Equal((ushort)0, services.Allocate(1, 0x102));
            Assert.Equal((ushort)0, services.Reallocate(0xFFFF, 1));
            Assert.False(services.Unlock(0xFFFF));
            Assert.Equal(default, services.Lock(0xFFFF));
            Assert.Empty(memory.Segments);
            ushort handle = services.Allocate(65536);
            var pointer = services.Lock(handle);
            memory.WriteWord(pointer.Segment, 65535, 0xA55A);
            Assert.Equal((ushort)0xA55A, memory.ReadWord(pointer.Segment, 65535));
        }
    }

    [Fact]
    public void LockCountNeverWrapsAndCleanupDoesNotRemoveAReplacementOwnedByTheHost()
    {
        var (memory, services) = Create();
        ushort handle = services.Allocate(1);
        FarPointer pointer = default;
        for (int i = 0; i < ushort.MaxValue; i++)
        {
            pointer = services.Lock(handle);
        }

        Assert.False(pointer.IsNull);
        Assert.Equal(default, services.Lock(handle));
        Assert.True(services.TryGetAllocation(handle, out var block));
        Assert.Equal(ushort.MaxValue, block!.LockCount);
        memory.Unmap(pointer.Segment);
        var external = new Win16Segment(pointer.Segment, 8);
        memory.Map(external);
        Assert.Equal(default, services.Lock(handle));
        Assert.Equal((ushort)0, services.Reallocate(handle, 4));
        services.Dispose();
        Assert.Same(external, Assert.Single(memory.Segments));
    }

    [Fact]
    public void RejectsInvalidLimitsAndHonorsZeroByteBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new(MaxAllocations: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new(MaxBytes: -1)));
        var (_, services) = Create(new(MaxBytes: 0));
        using (services)
        {
            Assert.Equal((ushort)0, services.Allocate(1));
            ushort empty = services.Allocate(0);
            Assert.NotEqual((ushort)0, empty);
            Assert.Equal((ushort)0, services.Reallocate(empty, 1));
            Assert.True(services.Free(empty));
        }
    }
}
