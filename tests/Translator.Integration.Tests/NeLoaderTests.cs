using System.Buffers.Binary;
using System.Text;
using Translator.Binary;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeLoaderTests
{
    internal static void W(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    internal static byte[] Fixture(params (byte Type, byte Flags, ushort Source, ushort A, ushort B)[] records)
    {
        byte[] bytes = NeParserTests.Fixture();
        Array.Resize(ref bytes, 0x600);
        W(bytes, 0x9c, 2);
        W(bytes, 0x8e, 2);
        W(bytes, 0x9a, 2);
        W(bytes, 0x90, 8);
        W(bytes, 0x92, 16);
        W(bytes, 0xc2, 64);
        W(bytes, 0xc4, records.Length == 0
            ? (ushort)0
            : (ushort)0x100);
        W(bytes, 0xc6, 64);
        W(bytes, 0xc8, 0x40);
        W(bytes, 0xca, 16);
        W(bytes, 0xcc, 1);
        W(bytes, 0xce, 32);
        bytes[0x119] = 3;
        Encoding.ASCII.GetBytes("API").CopyTo(bytes, 0x11a);
        for (int i = 0; i < 16; i++)
        {
            bytes[0x400 + i] = (byte)(0x80 + i);
        }

        W(bytes, 0x240, (ushort)records.Length);
        for (int i = 0; i < records.Length; i++)
        {
            var r = records[i];
            int p = 0x242 + i * 8;
            bytes[p] = r.Type;
            bytes[p + 1] = r.Flags;
            W(bytes, p + 2, r.Source);
            W(bytes, p + 4, r.A);
            W(bytes, p + 6, r.B);
            W(bytes, 0x200 + r.Source, 0xffff);
        }

        return bytes;
    }

    [Fact]
    public void InitializesOriginalBytesBssSelectorsStackHeapAndPermissions()
    {
        byte[] bytes = Fixture();
        using var program = new NeProgramLoader().Load(bytes);
        ushort code = program.SelectorFor(1), data = program.SelectorFor(2);
        Assert.Equal(0x1000, code);
        Assert.Equal(0x1008, data);
        Assert.Equal(2, program.SegmentIndexFor(data));
        Assert.Equal(new Win16CodeAddress(code, 4), program.Resolve(1, 4));
        Assert.Equal(bytes.AsSpan(0x400, 16).ToArray(), program.Memory.ReadBytes(data, 0, 16));
        Assert.All(program.Memory.ReadBytes(data, 16, 40), b => Assert.Equal(0, b));
        Assert.Equal(56, program.Memory.GetSegment(data).Size);
        Assert.Equal(code, program.InitialState.CS);
        Assert.Equal(data, program.InitialState.SS);
        Assert.Equal(data, program.InitialState.DS);
        Assert.Equal(48, program.InitialState.SP);
        Assert.Equal(32, program.Report.InitialRegisters.StackBottom);
        Assert.Equal(48, program.Report.InitialRegisters.HeapOffset);
        Assert.Throws<InvalidOperationException>(() => program.Memory.WriteByte(code, 0, 1));
        program.Memory.WriteByte(data, 0, 7);
        Assert.Equal(7, program.Memory.ReadByte(data, 0));
        Assert.Equal(MachineState.AbiVersion, program.Report.MachineStateAbiVersion);
        Assert.Equal(44, System.Runtime.InteropServices.Marshal.SizeOf<MachineState>());
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(2, 0x1000)]
    [InlineData(5, 4)]
    public void AppliesInternalByteSelectorAndOffsetChains(byte type, int expected)
    {
        byte[] bytes = Fixture((type, 0, 4, 1, 4));
        W(bytes, 0x204, 12);
        W(bytes, 0x20c, 0xffff);
        using var program = new NeProgramLoader().Load(bytes);
        var audit = Assert.Single(program.Report.Relocations);
        Assert.Equal(2, audit.Patches.Count);
        Assert.Equal(new ushort[] { 4, 12 }, audit.Relocation.SourceOffsets);
        foreach (ushort offset in new ushort[]
        {
            4,
            12
        }

        )
        {
            Assert.Equal(expected, type == 0
                ? program.Memory.ReadByte(program.SelectorFor(1), offset)
                : program.Memory.ReadWord(program.SelectorFor(1), offset));
        }

        Assert.Equal(12, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x204))); // original remains a chain
        Assert.True(program.Report.FullyRelocated);
    }

    [Fact]
    public void ResolvesMovableEntryAndWritesInternalFarPointers()
    {
        byte[] bytes = Fixture((3, 0, 4, 255, 3));
        using var program = new NeProgramLoader().Load(bytes);
        Assert.Equal(new FarPointer(4, program.SelectorFor(1)), program.Memory.ReadFarPointer(program.SelectorFor(1), 4));
        Assert.Equal(NeRelocationTargetKind.InternalEntry, Assert.Single(program.Report.Relocations).Relocation.TargetKind);
    }

    [Theory]
    [InlineData(1, 77, null)]
    [InlineData(2, 7, "API")]
    public void ResolvesOrdinalAndNameImportsThroughExplicitFarProcedureBindings(byte flags, ushort value, string? name)
    {
        byte[] bytes = Fixture((3, flags, 4, 1, value));
        NeImportReference? seen = null;
        using var program = new NeProgramLoader().Load(bytes, importResolver: import =>
        {
            seen = import;
            return new(0x7000, 0x1234);
        });
        Assert.Equal("KERNEL", seen!.Module);
        Assert.Equal(name, seen.Name);
        Assert.Equal(name is null
            ? (ushort?)77
            : null, seen.Ordinal);
        Assert.Equal(new FarPointer(0x1234, 0x7000), program.Memory.ReadFarPointer(program.SelectorFor(1), 4));
        Assert.Equal("FFFF0000", Assert.Single(Assert.Single(program.Report.Relocations).Patches).OriginalBytesHex);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(5, 2)]
    [InlineData(3, 2)]
    public void AdditiveRelocationsWrapOffsetWithoutFollowingAnAddendAsAChain(byte type, ushort expected)
    {
        byte[] bytes = Fixture((type, 4, 4, 1, 4));
        W(bytes, 0x204, 0xfffe);
        W(bytes, 0x206, 0xbeef);
        using var program = new NeProgramLoader().Load(bytes);
        ushort code = program.SelectorFor(1);
        Assert.Equal(expected, type == 0
            ? program.Memory.ReadByte(code, 4)
            : program.Memory.ReadWord(code, 4));
        Assert.Single(Assert.Single(program.Report.Relocations).Patches);
        if (type == 3)
        {
            Assert.Equal(code, program.Memory.ReadWord(code, 6));
        }
    }

    [Fact]
    public void AdditiveSelectorRequiresZeroAddend()
    {
        byte[] bytes = Fixture((2, 4, 4, 2, 0));
        W(bytes, 0x204, 0);
        using var program = new NeProgramLoader().Load(bytes);
        Assert.Equal(program.SelectorFor(2), program.Memory.ReadWord(program.SelectorFor(1), 4));
        W(bytes, 0x204, 1);
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(bytes));
    }

    [Fact]
    public void UnresolvedOrUnsupportedFixupsCannotClaimCompleteLoading()
    {
        foreach (var bytes in new[]
        {
            Fixture((3, 1, 4, 1, 77)),
            Fixture((13, 0, 4, 1, 0)),
            Fixture((5, 3, 4, 0, 0))
        }

        )
        {
            var memory = new SegmentMemory();
            Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(bytes, memory: memory));
            Assert.Empty(memory.Segments);
            using var partial = new NeProgramLoader().Load(bytes, new(AllowIncompleteRelocations: true));
            Assert.False(partial.Report.FullyRelocated);
            Assert.NotNull(Assert.Single(partial.Report.Relocations).Diagnostic);
            Assert.Equal(0xffff, partial.Memory.ReadWord(partial.SelectorFor(1), 4));
        }
    }

    [Theory]
    [InlineData(0x204, 4)] // self-cycle
    [InlineData(0x204, 63)] // chain crosses segment end
    [InlineData(0x244, 64)] // source outside segment
    [InlineData(0x246, 3)] // nonexistent target segment
    [InlineData(0x248, 64)] // target offset beyond allocation
    public void RejectsMalformedSourcesAndTargets(int offset, ushort value)
    {
        byte[] bytes = Fixture((3, 0, 4, 1, 0));
        W(bytes, offset, value);
        Assert.Throws<InvalidDataException>(() => new NeExecutableReader().Read(bytes));
    }

    [Fact]
    public void RejectsMultiNodeCyclesOverlapsAndInvalidImportOrEntryReferences()
    {
        byte[] bytes = Fixture((5, 0, 4, 1, 0));
        W(bytes, 0x204, 8);
        W(bytes, 0x208, 4);
        Assert.Throws<InvalidDataException>(() => new NeExecutableReader().Read(bytes));
        foreach (var invalid in new[]
        {
            Fixture((3, 0, 4, 1, 0), (5, 0, 6, 1, 0)),
            Fixture((3, 1, 4, 2, 77)),
            Fixture((3, 2, 4, 1, 1)),
            Fixture((3, 0, 4, 255, 2))
        }

        )
        {
            Assert.Throws<InvalidDataException>(() => new NeExecutableReader().Read(invalid));
        }
    }

    [Fact]
    public void FailureAndDisposalPreserveBorrowedMappingsAndClearOwnedStorage()
    {
        var memory = new SegmentMemory();
        var borrowed = new Win16Segment(0x6000, 32);
        memory.Map(borrowed);
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(Fixture(), new(MaximumBytes: 1), memory: memory));
        Assert.Same(borrowed, Assert.Single(memory.Segments));
        var program = new NeProgramLoader().Load(Fixture(), memory: memory);
        var owned = memory.GetSegment(program.SelectorFor(2));
        program.Dispose();
        program.Dispose();
        Assert.Same(borrowed, Assert.Single(memory.Segments));
        Assert.All(owned.Memory.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => program.SelectorFor(1));
        Assert.Throws<ObjectDisposedException>(() => program.Memory);
        memory.Map(new Win16Segment(0x1000, 32));
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(Fixture(), memory: memory));
        Assert.Equal(2, memory.Segments.Count);
    }

    [Fact]
    public void RejectsSelectorOverflowOversizedHeapStackAndUnsupportedStorage()
    {
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(Fixture(), new(FirstSelector: 0xffff)));
        byte[] bytes = Fixture();
        W(bytes, 0x90, 0xffff);
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(bytes));
        bytes = Fixture();
        W(bytes, 0xc4, 0x2000);
        Assert.Throws<NotSupportedException>(() => new NeProgramLoader().Load(bytes));
        bytes = Fixture();
        W(bytes, 0x98, 8);
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(bytes));
        bytes = Fixture((3, 1, 4, 1, 77));
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(bytes, importResolver: _ => new(0x1000, 0)));
    }

    [Fact]
    public void ExplicitStackPointerAndDataSegmentConventionsRemainInspectable()
    {
        byte[] bytes = Fixture();
        W(bytes, 0x98, 64);
        using var program = new NeProgramLoader().Load(bytes);
        Assert.Equal(64, program.InitialState.SP);
        Assert.Equal(72, program.Memory.GetSegment(program.InitialState.DS).Size);
        Assert.Equal(64, program.Report.InitialRegisters.HeapOffset);
        bytes = NeParserTests.Fixture();
        using var noData = new NeProgramLoader().Load(bytes);
        Assert.Equal(0, noData.InitialState.DS);
        Assert.Equal(0, noData.InitialState.SS);
        bytes = Fixture();
        W(bytes, 0x9a, 0);
        W(bytes, 0x92, 0);
        W(bytes, 0x98, 1);
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(bytes));
        bytes = Fixture();
        W(bytes, 0xcc, 0x81);
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(bytes));
    }

    [Fact]
    public void ImportOrdinalAndTargetSegmentMutationsChangeResolutionRatherThanBeingIgnored()
    {
        byte[] bytes = Fixture((3, 1, 4, 1, 77));

        Win16CodeAddress? Resolve(NeImportReference import) => import.Ordinal == 77
            ? new(0x7000, 10)
            : null;

        using var bound = new NeProgramLoader().Load(bytes, importResolver: Resolve);
        W(bytes, 0x248, 78);
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(bytes, importResolver: Resolve));
        bytes = Fixture((2, 0, 4, 1, 0));
        using var first = new NeProgramLoader().Load(bytes);
        W(bytes, 0x246, 2);
        using var second = new NeProgramLoader().Load(bytes);
        Assert.NotEqual(first.Memory.ReadWord(first.SelectorFor(1), 4), second.Memory.ReadWord(second.SelectorFor(1), 4));
    }

    [Fact]
    public void SupportsMoreThanFixtureSegmentLimitsAndAnUnbacked64KSegment()
    {
        byte[] bytes = Fixture();
        W(bytes, 0x9c, 20);
        W(bytes, 0xa2, 0x480); // table at 0x500
        for (int i = 0; i < 20; i++)
        {
            int p = 0x500 + i * 8;
            W(bytes, p, 0);
            W(bytes, p + 2, 0);
            W(bytes, p + 4, i == 0
                ? (ushort)0
                : (ushort)1);
            W(bytes, p + 6, 32);
        }

        W(bytes, 0x506, 0); // first code segment uses encoded 64K minimum allocation
        using var program = new NeProgramLoader().Load(bytes);
        Assert.Equal(20, program.Memory.Segments.Count);
        Assert.Equal(65536, program.Memory.GetSegment(program.SelectorFor(1)).Size);
        Assert.All(program.Memory.ReadBytes(program.SelectorFor(1), 0xfff0, 16), b => Assert.Equal(0, b));
    }
}
