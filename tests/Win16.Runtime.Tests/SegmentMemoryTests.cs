using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Win16.Runtime.Tests;

public sealed class SegmentMemoryTests
{
    [Fact]
    public void MapsSelectorsAndChecksPermissionsAndBounds()
    {
        var memory = new SegmentMemory();
        var readOnly = new Win16Segment(0x1234, size: 8, canWrite: false);
        memory.Map(readOnly);
        Assert.Same(readOnly, memory.GetSegment(0x1234));
        Assert.True(memory.TryGetSegment(0x1234, out var found));
        Assert.Same(readOnly, found);
        Assert.False(memory.TryGetSegment(0x9999, out _));
        Assert.Throws<InvalidOperationException>(() => memory.Map(new Win16Segment(0x1234)));
        Assert.Throws<InvalidOperationException>(() => memory.ReadByte(0x9999, 0));
        Assert.Throws<InvalidOperationException>(() => memory.WriteByte(0x1234, 0, 1));
        Assert.Throws<InvalidOperationException>(() => memory.ReadByte(0x1234, 8));
        memory.Map(new Win16Segment(0x2345, size: 8, canRead: false, canWrite: true));
        Assert.Throws<InvalidOperationException>(() => memory.ReadByte(0x2345, 0));
        memory.WriteByte(0x2345, 0, 0xAA);
        Assert.True(memory.Unmap(0x1234));
        Assert.False(memory.Unmap(0x1234));
    }

    [Fact]
    public void ReadsAndWritesLittleEndianValuesIncludingOffsetWraparound()
    {
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x1000));
        memory.WriteByte(0x1000, 3, 0xA5);
        memory.WriteWord(0x1000, 4, 0xBEEF);
        memory.WriteDword(0x1000, 0xFFFE, 0x78563412);
        Assert.Equal((byte)0xA5, memory.ReadByte(0x1000, 3));
        Assert.Equal((ushort)0xBEEF, memory.ReadWord(0x1000, 4));
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, memory.ReadBytes(0x1000, 0xFFFE, 4));
        Assert.Equal((uint)0x78563412, memory.ReadDword(0x1000, 0xFFFE));
        Assert.Equal((byte)0x56, memory.ReadByte(0x1000, 0));
        var small = new SegmentMemory();
        small.Map(new Win16Segment(7, size: 8));
        Assert.Throws<InvalidOperationException>(() => small.ReadWord(7, 7));
        Assert.Throws<InvalidOperationException>(() => small.WriteWord(7, 7, 0xBEEF));
        Assert.Equal((byte)0, small.ReadByte(7, 7));
    }

    [Fact]
    public void ResolvesDefaultAndOverriddenSegmentsAndLegalEffectiveAddresses()
    {
        var memory = NewMappedMemory();
        var state = new MachineState
        {
            AX = 1,
            BX = 0x10,
            CX = 2,
            DX = 3,
            SI = 4,
            DI = 5,
            BP = 0x20,
            SP = 0x30,
            CS = 0x4000,
            DS = 0x1000,
            ES = 0x3000,
            SS = 0x2000
        };
        var ds = EffectiveAddress.Resolve(memory, state, new MemoryReference("bx+si+2", RegisterWidth.Word, Base: X86Register.BX, Index: X86Register.SI, Displacement: 2));
        Assert.Equal((ushort)0x1000, ds.Segment);
        Assert.Equal((ushort)0x16, ds.Offset);
        ds.WriteValue(0xBEEF);
        Assert.Equal(0xBEEFu, ds.ReadValue());
        Assert.Throws<ArgumentOutOfRangeException>(() => ds.WriteValue(0x1_0000));
        var ss = EffectiveAddress.Resolve(memory, state, new MemoryReference("bp+di", RegisterWidth.Word, Base: X86Register.BP, Index: X86Register.DI));
        Assert.Equal((ushort)0x2000, ss.Segment);
        Assert.Equal((ushort)0x25, ss.Offset);
        foreach (var (overrideName, selector) in new[]
        {
            ("CS", (ushort)0x4000),
            ("DS", (ushort)0x1000),
            ("ES", (ushort)0x3000),
            ("SS", (ushort)0x2000)
        }

        )
        {
            var reference = EffectiveAddress.Resolve(memory, state, new MemoryReference("[bx]", RegisterWidth.Word, overrideName, X86Register.BX, HasExplicitSegment: true));
            Assert.Equal(selector, reference.Segment);
        }

        Assert.Equal((ushort)1, EffectiveAddress.Calculate(0xFFFF, null, 2));
        Assert.True(EffectiveAddress.IsLegal8086Addressing(X86Register.BX, X86Register.SI));
        Assert.True(EffectiveAddress.IsLegal8086Addressing(X86Register.BX, X86Register.DI));
        Assert.True(EffectiveAddress.IsLegal8086Addressing(X86Register.BP, X86Register.SI));
        Assert.True(EffectiveAddress.IsLegal8086Addressing(X86Register.BP, X86Register.DI));
        Assert.True(EffectiveAddress.IsLegal8086Addressing(X86Register.SI, null));
        Assert.True(EffectiveAddress.IsLegal8086Addressing(null, null));
        Assert.False(EffectiveAddress.IsLegal8086Addressing(X86Register.BX, X86Register.BP));
        var wrapped = EffectiveAddress.Resolve(memory, state with
        {
            BX = 0xFFFE
        }, new MemoryReference("bx+3", RegisterWidth.Byte, Base: X86Register.BX, Displacement: 3));
        Assert.Equal((ushort)1, wrapped.Offset);
        Assert.Throws<ArgumentException>(() => EffectiveAddress.Resolve(memory, state, new MemoryReference("bx+bp", RegisterWidth.Word, Base: X86Register.BX, Index: X86Register.BP)));
        Assert.Throws<InvalidOperationException>(() => EffectiveAddress.Resolve(memory, state, new MemoryReference("Thing+bx", RegisterWidth.Word, Base: X86Register.BX, Symbol: "Thing")));
        var resolvedSymbol = EffectiveAddress.Resolve(memory, state, new MemoryReference("Thing+bx", RegisterWidth.Word, Base: X86Register.BX, Symbol: "Thing"), symbolResolver: _ => 0x10);
        Assert.Equal((ushort)0x20, resolvedSymbol.Offset);
    }

    [Fact]
    public void ReadsFarPointerFieldsIndependentlyAndResolvesFindMonsterShapedRecords()
    {
        var memory = NewMappedMemory();
        const ushort dataSegment = 0x1000, monsterSegment = 0x3000, pointerOffset = 0x0100, listOffset = 0x2000;
        memory.WriteFarPointer(dataSegment, pointerOffset, new FarPointer(listOffset, monsterSegment));
        Assert.Equal(listOffset, memory.ReadFarPointerOffset(dataSegment, pointerOffset));
        Assert.Equal(monsterSegment, memory.ReadFarPointerSegment(dataSegment, pointerOffset));
        memory.WriteFarPointerOffset(dataSegment, pointerOffset, (ushort)(listOffset + 1));
        Assert.Equal((ushort)(listOffset + 1), memory.ReadFarPointerOffset(dataSegment, pointerOffset));
        memory.WriteFarPointerOffset(dataSegment, pointerOffset, listOffset);
        // Two { x, y, type } word records, matching FindMonster's segmented list walk.
        memory.WriteWord(monsterSegment, listOffset, 4);
        memory.WriteWord(monsterSegment, (ushort)(listOffset + 2), 7);
        memory.WriteWord(monsterSegment, (ushort)(listOffset + 6), 9);
        memory.WriteWord(monsterSegment, (ushort)(listOffset + 8), 11);
        var state = new MachineState
        {
            DS = dataSegment,
            ES = monsterSegment,
            SI = pointerOffset,
            BX = listOffset
        };
        var pointerFields = EffectiveAddress.Resolve(memory, state, new MemoryReference("[si]", RegisterWidth.Word, Base: X86Register.SI));
        var pointer = memory.ReadFarPointer(pointerFields.Segment, pointerFields.Offset);
        var monsterX = EffectiveAddress.Resolve(memory, state with
        {
            BX = pointer.Offset,
            ES = pointer.Segment
        }, new MemoryReference("[es:bx]", RegisterWidth.Word, "ES", X86Register.BX, HasExplicitSegment: true));
        var monsterY = EffectiveAddress.Resolve(memory, state with
        {
            BX = pointer.Offset,
            ES = pointer.Segment
        }, new MemoryReference("[es:bx+2]", RegisterWidth.Word, "ES", X86Register.BX, Displacement: 2, HasExplicitSegment: true));
        Assert.Equal((ushort)4, monsterX.ReadWord());
        Assert.Equal((ushort)7, monsterY.ReadWord());
        var farReference = memory.Resolve(pointer.Add(6));
        Assert.Equal((ushort)9, farReference.ReadWord());
        farReference.WriteWord(13);
        Assert.Equal((ushort)13, memory.ReadWord(monsterSegment, (ushort)(listOffset + 6)));
        Assert.True(new FarPointer(0, 0).IsNull);
        Assert.False(new FarPointer(0, 1).IsNull);
        Assert.False(new FarPointer(1, 0).IsNull);
        Assert.Equal((ushort)1, new FarPointer(0xFFFF, 1).Add(2).Offset);
        Assert.Equal((ushort)1, new FarPointer(0xFFFF, 1).Add(2).Segment);
    }

    private static SegmentMemory NewMappedMemory()
    {
        var memory = new SegmentMemory();
        foreach (ushort selector in new ushort[]
        {
            0x1000,
            0x2000,
            0x3000,
            0x4000
        }

        )
        {
            memory.Map(new Win16Segment(selector));
        }

        return memory;
    }
}
