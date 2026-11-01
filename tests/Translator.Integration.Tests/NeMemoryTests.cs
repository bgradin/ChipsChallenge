using Translator.Analysis;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeMemoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AliasesShareBackingLimitsAndResizesWithoutDanglingNativeStorage(bool native)
    {
        using var allocator = native
            ? new Win16NativeMemoryAllocator()
            : null;
        var m = new SegmentMemory(allocator);
        using var owner = m.CreateSegment(0x1000, 32);
        m.Map(owner);
        using var full = m.CreateAlias(0x6000, owner.Selector);
        using var slice = m.CreateAlias(0x6001, owner.Selector, 8, 8);
        m.WriteWord(full.Selector, 8, 0x1234);
        Assert.Equal((ushort)0x1234, m.ReadWord(slice.Selector, 0));
        m.ResizeDataSegment(owner.Selector, 64);
        Assert.Equal(64, full.Size);
        Assert.Equal(8, slice.Size);
        Assert.Equal((ushort)0x1234, m.ReadWord(slice.Selector, 0));
        m.WriteWord(full.Selector, 60, 0xabcd);
        Assert.Equal((ushort)0xabcd, m.ReadWord(owner.Selector, 60));
        m.ResizeDataSegment(owner.Selector, 12);
        Assert.Equal(4, slice.Size);
        Assert.Throws<InvalidOperationException>(() => m.WriteWord(slice.Selector, 3, 0xffff));
        Assert.Equal((byte)0, m.ReadByte(slice.Selector, 3));
        owner.Dispose();
        Assert.Equal((ushort)0x1234, m.ReadWord(slice.Selector, 0));
        slice.Dispose();
        full.Dispose();
        if (allocator is not null)
        {
            Assert.Equal(0, allocator.Statistics.LiveBytes);
        }
    }

    [Fact]
    public void CodeWritesThroughAliasesFailBeforeMutationAndRecordExactFault()
    {
        var m = new SegmentMemory();
        using var code = m.CreateSegment(0x1000, 8, true, false, new byte[] { 0xcb }, executable: true);
        m.Map(code);
        using var alias = m.CreateAlias(0x6000, 0x1000);
        Assert.Throws<ArgumentException>(() => m.CreateAlias(0x6001, 0x1000, canWrite: true));
        Assert.Throws<InvalidOperationException>(() => m.WriteDword(alias.Selector, 0, 0));
        Assert.Equal((byte)0xcb, m.ReadByte(code.Selector, 0));
        Assert.Equal(new Win16ExecutableWriteFault(alias.Selector, 0, 4, true), Assert.Single(m.ExecutableWriteFaults));
    }

    [Fact]
    public void FarPointerWritesAreAtomicAtSegmentLimitAndWrapAt64KiB()
    {
        var m = new SegmentMemory();
        using var small = m.CreateSegment(1, 8);
        m.Map(small);
        m.WriteWord(1, 6, 0x1111);
        Assert.Throws<InvalidOperationException>(() => m.WriteFarPointer(1, 6, new(2, 3)));
        Assert.Equal((ushort)0x1111, m.ReadWord(1, 6));
        using var full = m.CreateSegment(2);
        m.Map(full);
        m.WriteFarPointer(2, 0xfffe, new(0x1234, 0xabcd));
        Assert.Equal(new FarPointer(0x1234, 0xabcd), m.ReadFarPointer(2, 0xfffe));
    }

    [Fact]
    public void LockedGlobalAliasesFollowResizeAndUnlockedResizeRetiresEveryView()
    {
        var m = new SegmentMemory();
        using var g = new Win16GlobalMemoryServices(m, new());
        ushort h = g.Allocate(8);
        var p = g.Lock(h);
        using var a = m.CreateAlias(m.AllocateSelector(), p.Segment);
        m.WriteWord(a.Selector, 0, 0x1234);
        Assert.Equal(h, g.Reallocate(h, 32));
        Assert.Equal(32, a.Size);
        Assert.Equal((ushort)0x1234, m.ReadWord(a.Selector, 0));
        Assert.False(g.Unlock(h));
        Assert.Equal(h, g.Reallocate(h, 16));
        Assert.True(a.IsDisposed);
        Assert.False(m.TryGetSegment(a.Selector, out _));
        Assert.False(m.TryGetSegment(p.Segment, out _));
        var fresh = g.Lock(h);
        Assert.NotEqual(p.Segment, fresh.Segment);
        g.Unlock(h);
        Assert.True(g.Free(h));
        Assert.False(m.TryGetSegment(fresh.Segment, out _));
        Assert.Empty(m.Segments);
    }

    [Fact]
    public void FixedGlobalMemoryKeepsItsHandleSelectorAndFailureLeavesDataIntact()
    {
        var m = new SegmentMemory();
        using var g = new Win16GlobalMemoryServices(m, new(), wholeProgram: true);
        ushort h = g.Allocate(16, 0x20);
        Assert.NotEqual((ushort)0, h);
        var p = g.Lock(h);
        Assert.Equal(h, p.Segment);
        m.WriteWord(h, 0, 0xbeef);
        g.FailNextReallocation = true;
        Assert.Equal((ushort)0, g.Reallocate(h, 32, 0x20));
        Assert.Equal((ushort)0xbeef, m.ReadWord(h, 0));
        Assert.Equal(h, g.Reallocate(h, 32, 0x20));
        Assert.Equal(32, m.GetSegment(h).Size);
        g.Unlock(h);
        Assert.True(g.Free(h));
        using var empty = new Win16GlobalMemoryServices(m, new(), limits: new(MaxBytes: 0), wholeProgram: true);
        Assert.Equal((ushort)0, empty.Allocate(0, 0));
        var other = new SegmentMemory();
        using var reserved = new Win16GlobalMemoryServices(other, new(), serviceSegment: 0x5000);
        Assert.NotEqual((ushort)0x5000, reserved.Lock(reserved.Allocate(8)).Segment);
    }

    [Fact]
    public void LocalNearPointersMoveOnlyWhenPermittedAndDoNotCorruptNeighbours()
    {
        var m = new SegmentMemory();
        using var s = m.CreateSegment(1, 512);
        m.Map(s);
        var h = new Win16LocalHeap(m, 1, 64, 256);
        ushort a = h.Allocate(7, 0x20), b = h.Allocate(8);
        Assert.Equal(8, h.Size(a));
        m.WriteWord(1, a, 0xbeef);
        m.WriteWord(1, b, 0x9999);
        Assert.Equal((ushort)0, h.Reallocate(a, 32));
        Assert.Equal((ushort)0xbeef, m.ReadWord(1, a));
        ushort c = h.Reallocate(a, 32, 0x62);
        Assert.NotEqual(a, c);
        Assert.Equal((ushort)0xbeef, m.ReadWord(1, c));
        Assert.Equal((ushort)0, m.ReadWord(1, (ushort)(c + 8)));
        Assert.Equal((ushort)0x9999, m.ReadWord(1, b));
        Assert.False(h.Free(a));
        Assert.True(h.Free(c));
        Assert.True(h.Free(b));
        Assert.Equal(0, h.AllocationCount);
        Assert.Equal((ushort)0, h.Allocate(70000));
        Assert.Equal((ushort)0, h.Allocate(4, 2));
    }

    [Theory]
    [InlineData(Win16CallbackStack.Caller)]
    [InlineData(Win16CallbackStack.DataSegment)]
    [InlineData(Win16CallbackStack.SeparateSegment)]
    public void CallbackRestoresAllCpuBytesAndPersistsOriginalMemory(Win16CallbackStack policy)
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xc7, 0x06, 0, 0, 0x34, 0x12, 0xb8, 7, 0, 0xcb);
        NeLoaderTests.W(bytes, 0x94, 0);
        NeLoaderTests.W(bytes, 0x90, 512);
        using var p = new NeWholeProgramControlProgram(bytes);
        using var separate = p.Memory.CreateSegment(0x7000, 2048);
        p.Memory.Map(separate);
        var state = p.InitialState;
        state.AXHigh = 0xabcd;
        state.BXHigh = 0xffff;
        state.ES = 0x7777;
        var saved = state;
        uint result = p.TaskMemory.Callback(p.Control, ref state, new(state.CS, 0), stack: policy, stackBytes: 128, separateSelector: separate.Selector, separateTop: 1024);
        Assert.Equal(0xabcd0007u, result);
        Assert.Equal(saved, state);
        Assert.Equal((ushort)0x1234, p.Memory.ReadWord(state.DS, 0));
        Assert.Equal(0, p.TaskMemory.Local.AllocationCount);
    }

    [Fact]
    public void CallbackDataStackPreservesStackLocalNearPointersAndDataGrowth()
    {
        // PUSH BP; MOV BP,SP; SUB SP,2; MOV word [BP-2],beef; MOV AX,[BP-2]; MOV SP,BP; POP BP; RETF
        byte[] bytes = NeDiscoveryTests.Fixture(0x55, 0x8b, 0xec, 0x83, 0xec, 2, 0xc7, 0x46, 0xfe, 0xef, 0xbe, 0x8b, 0x46, 0xfe, 0x8b, 0xe5, 0x5d, 0xcb);
        NeLoaderTests.W(bytes, 0x94, 0);
        NeLoaderTests.W(bytes, 0x90, 512);
        using var p = new NeWholeProgramControlProgram(bytes);
        var state = p.InitialState;
        var before = state;
        Assert.Equal(0xbeefu, p.TaskMemory.Callback(p.Control, ref state, new(state.CS, 0), stack: Win16CallbackStack.DataSegment, stackBytes: 128));
        Assert.Equal(before, state);
        // A DS alias remains valid as the module's data segment grows.
        using var alias = p.Memory.CreateAlias(0x6500, state.DS);
        int old = alias.Size;
        p.TaskMemory.GrowDataSegment(65536);
        Assert.Equal(65536, alias.Size);
        Assert.Equal(old, p.Memory.GetSegment(state.DS).Memory.Span[..old].Length);
    }

    [Fact]
    public void ExecutableAliasSelectsCompiledOriginalEntriesAndRejectsInteriorPointers()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0x0e, 0x58, 0xcb);
        NeLoaderTests.W(bytes, 0x94, 0);
        using var p = new NeWholeProgramControlProgram(bytes);
        var state = p.InitialState;
        using var alias = p.Memory.CreateAlias(0x6500, state.CS);
        p.Control.Invoke(ref state, new(alias.Selector, 0));
        Assert.Equal(alias.Selector, state.AX);
        Assert.Throws<InvalidOperationException>(() => p.Control.Resolve(new(alias.Selector, 4)));
    }

    [Theory]
    [InlineData(new byte[] { 0x2e, 0xc7, 0x06, 0, 0, 1, 0, 0xcb })]
    [InlineData(new byte[] { 0x0e, 0x07, 0x26, 0xc7, 0x06, 0, 0, 1, 0, 0xcb })]
    public void AuditDetectsDirectAndPropagatedExecutableSelectorWrites(byte[] code)
    {
        byte[] bytes = NeDiscoveryTests.Fixture(code);
        NeLoaderTests.W(bytes, 0x94, 0);
        var d = NeWholeProgramDiscoveryBuilder.Build(bytes);
        var a = NeMemoryAuditBuilder.Build(bytes, d);
        Assert.True(a.StaticResolutionRequired);
        Assert.True(a.ProvenExecutableWriteAccesses > 0);
        Assert.Throws<NotSupportedException>(() => NeWholeProgramControlCompiler.Compile(bytes));
    }

    [Fact]
    public void AuditPreservesDynamicWriteUncertaintyAndRejectsMismatchedImage()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0x26, 0x89, 0x05, 0xcb);
        NeLoaderTests.W(bytes, 0x94, 0);
        var d = NeWholeProgramDiscoveryBuilder.Build(bytes);
        var a = NeMemoryAuditBuilder.Build(bytes, d);
        Assert.False(a.StaticResolutionRequired);
        Assert.False(a.ProvesAbsenceOfAllExecutableWrites);
        Assert.True(a.RuntimeGuardedWriteAccesses > 0);
        bytes[0x201] ^= 1;
        Assert.Throws<InvalidDataException>(() => NeMemoryAuditBuilder.Build(bytes, d));
    }

    [Fact]
    public void MemoryAuditCliIsDeterministicAndProtectsItsAuthoritativeInput()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), output = file + ".json";
        byte[] bytes = NeDiscoveryTests.Fixture(0x26, 0x89, 0x05, 0xcb);
        NeLoaderTests.W(bytes, 0x94, 0);
        File.WriteAllBytes(file, bytes);
        string[] args = ["analyze", "--input", file, "--whole-program", "--memory-audit", "--output", output];
        try
        {
            Assert.Equal(0, CliIntegrationTests.Run(args).Code);
            var report = File.ReadAllBytes(output);
            Assert.Equal(0, CliIntegrationTests.Run(args).Code);
            Assert.Equal(report, File.ReadAllBytes(output));
            Assert.NotEqual(0, CliIntegrationTests.Run("analyze", "--input", file, "--memory-audit").Code);
            Assert.NotEqual(0, CliIntegrationTests.Run(args.Concat(new[] { "--control-flow" }).ToArray()).Code);
            Assert.NotEqual(0, CliIntegrationTests.Run(args.Concat(new[] { "--instruction-coverage" }).ToArray()).Code);
            Assert.NotEqual(0, CliIntegrationTests.Run("analyze", "--input", file, "--whole-program", "--memory-audit", "--output", file).Code);
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }
        finally
        {
            File.Delete(file);
            File.Delete(output);
        }
    }

    [SuppliedNeFact]
    public void OriginalMemoryAuditHasNoProvenCodeWritesAndRetainsMutablePointerSources()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var discovery = NeWholeProgramDiscoveryBuilder.Build(bytes);
        var audit = NeMemoryAuditBuilder.Build(bytes, discovery);
        Assert.Equal(discovery.Instructions.Count, audit.InstructionsAudited);
        Assert.NotEmpty(audit.Writes);
        Assert.Equal(audit.Writes.Select(w => w.Site).Distinct().Count(), audit.MemoryWriteInstructions);
        Assert.Equal(audit.Writes.Count, audit.MemoryWriteAccesses);
        var sites = discovery.Instructions.Select(i => i.Original.Address).ToHashSet();
        Assert.All(audit.Writes, write => Assert.Contains(write.Site, sites));
        Assert.Equal(0, audit.ProvenExecutableWriteAccesses);
        Assert.False(audit.StaticResolutionRequired);
        Assert.NotEmpty(audit.ReadonlyCodeTables);
        Assert.Equal(discovery.IndirectTransfers.Count, audit.ProcedurePointerSources.Count);
        Assert.Equal(audit.Writes.Count(w => w.RuntimeSelectorOrOffset), audit.RuntimeGuardedWriteAccesses);
        Assert.False(audit.ProvesAbsenceOfAllExecutableWrites);
    }
}
