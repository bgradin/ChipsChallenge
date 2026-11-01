using Win16_Runtime;
using Xunit;

namespace Win16.Runtime.Tests;

public sealed class NativeMemoryTests
{
    [Fact]
    public void StorageScalesPastFixtureArenaAndDisposalInvalidatesBorrowedMemory()
    {
        using var allocator = new Win16NativeMemoryAllocator(new(MaximumBytes: 2 * 1024 * 1024));
        var memory = new SegmentMemory(allocator);
        var segments = Enumerable.Range(1, 20).Select(i => memory.CreateSegment((ushort)i)).ToArray();
        foreach (var segment in segments)
        {
            memory.Map(segment);
        }

        Assert.Equal(20 * 65536, allocator.Statistics.LiveBytes);
        Assert.All(segments, s => Assert.All(s.Memory.ToArray(), b => Assert.Equal(0, b)));
        var borrowed = segments[0].Memory;
        allocator.Dispose();
        allocator.Dispose();
        Assert.Equal(0, allocator.Statistics.LiveBytes);
        Assert.Equal(0, allocator.Statistics.LiveAllocations);
        Assert.Throws<ObjectDisposedException>(() => borrowed.ToArray());
        Assert.Throws<ObjectDisposedException>(() => allocator.Allocate(4));
        foreach (var segment in segments)
        {
            segment.Dispose();
        }
    }

    [Fact]
    public void BackendFailureDuringResizePreservesHandleSelectorLocksAndBytes()
    {
        using var allocator = new Win16NativeMemoryAllocator(new(MaximumBytes: 20));
        var memory = new SegmentMemory(allocator);
        using var services = new Win16GlobalMemoryServices(memory, new Win16CallDispatcher());
        ushort handle = services.Allocate(8);
        var pointer = services.Lock(handle);
        memory.Resolve(pointer).WriteWord(0xA55A);
        var oldStorage = memory.GetSegment(pointer.Segment).Memory;
        Assert.True(services.TryGetAllocation(handle, out var before));
        // The replacement must coexist with the old block until commit.
        Assert.Equal((ushort)0, services.Reallocate(handle, 16));
        allocator.FailNextAllocation = true;
        Assert.Equal((ushort)0, services.Reallocate(handle, 10));
        Assert.True(services.TryGetAllocation(handle, out var after));
        Assert.Equal(before, after);
        Assert.Equal(8, allocator.Statistics.LiveBytes);
        Assert.Equal((ushort)0xA55A, memory.Resolve(pointer).ReadWord());
        Assert.Equal(handle, services.Reallocate(handle, 12));
        Assert.Equal(pointer, services.Lock(handle));
        Assert.Throws<ObjectDisposedException>(() => oldStorage.ToArray());
        services.Unlock(handle);
        services.Unlock(handle);
        Assert.Equal(handle, services.Reallocate(handle, 4));
        var moved = services.Lock(handle);
        Assert.NotEqual(pointer.Segment, moved.Segment);
        Assert.Equal((ushort)0xA55A, memory.Resolve(moved).ReadWord());
        services.Dispose();
        Assert.Empty(memory.Segments);
        Assert.Equal(0, allocator.Statistics.LiveBytes);
    }

    [Fact]
    public void CountLimitsAndReplacementMappingsRespectOwnership()
    {
        using var allocator = new Win16NativeMemoryAllocator(new(MaximumAllocations: 1));
        var memory = new SegmentMemory(allocator);
        using var services = new Win16GlobalMemoryServices(memory, new Win16CallDispatcher());
        ushort handle = services.Allocate(4);
        var pointer = services.Lock(handle);
        Assert.Equal((ushort)0, services.Allocate(1));
        memory.Unmap(pointer.Segment);
        using var external = new Win16Segment(pointer.Segment, 8);
        memory.Map(external);
        services.Dispose();
        Assert.Same(external, Assert.Single(memory.Segments));
        Assert.False(external.IsDisposed);
        Assert.Equal(0, allocator.Statistics.LiveBytes);
    }
}
