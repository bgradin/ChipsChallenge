using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Translator.Binary;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeParserTests
{
    // Small, independent Windows NE: one code segment, fixed/movable/unused entries,
    // ordinal names, an imported module and one named bitmap resource.
    internal static byte[] Fixture()
    {
        var b = new byte[0x400];

        void W(int p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), (ushort)v);


        void D(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), v);


        void S(int p, string s)
        {
            b[p] = (byte)s.Length;
            Encoding.ASCII.GetBytes(s).CopyTo(b, p + 1);
        }


        W(0, 0x5a4d);
        W(8, 4);
        D(0x3c, 0x80);
        W(0x80, 0x454e);
        W(0x84, 0xa0);
        W(0x86, 16);
        W(0x96, 1);
        W(0x9c, 1);
        W(0x9e, 1);
        W(0xa0, 7);
        W(0xa2, 0x40);
        W(0xa4, 0x50);
        W(0xa6, 0x80);
        W(0xa8, 0x90);
        W(0xaa, 0x92);
        D(0xac, 0x140);
        W(0xb0, 1);
        W(0xb2, 4);
        b[0xb6] = 2;
        W(0xc0, 0x20);
        W(0xc2, 16);
        W(0xc4, 0);
        W(0xc6, 32);
        W(0xd0, 4);
        W(0xd2, 0x8002);
        W(0xd4, 1);
        W(0xda, 0x30);
        W(0xdc, 1);
        W(0xde, 0x30);
        W(0xe0, 0x18);
        W(0xe6, 0);
        S(0xe8, "PIC");
        S(0x100, "MOD");
        W(0x104, 0);
        b[0x106] = 0;
        W(0x110, 0);
        S(0x112, "KERNEL");
        b[0x120] = 1;
        b[0x121] = 1;
        b[0x122] = 1;
        W(0x123, 0);
        b[0x125] = 1;
        b[0x126] = 0;
        b[0x127] = 1;
        b[0x128] = 255;
        b[0x129] = 1;
        W(0x12a, 0x3fcd);
        b[0x12c] = 1;
        W(0x12d, 4);
        b[0x12f] = 0;
        S(0x140, "EXP");
        W(0x144, 3);
        b[0x146] = 0;
        b[0x200] = 0xcb;
        b[0x300] = 0x28;
        return b;
    }

    [Fact]
    public void ParsesTablesWithoutLosingOrdinalOrRawIdentity()
    {
        var reader = new NeExecutableReader();
        var model = reader.Read(Fixture());
        Assert.Equal(1, reader.ReadHeader(Fixture()).SegmentCount);
        Assert.Equal(0x200, model.Segments.Single().FileOffset);
        Assert.Equal(32, model.Segments.Single().MemoryLength);
        Assert.Equal(new[] { "Fixed", "Unused", "Movable" }, model.EntryPoints.Select(e => e.Kind));
        Assert.Equal(3, model.NonResidentNames.Single().Ordinal);
        Assert.Equal("KERNEL", model.ModuleReferences.Single().Name);
        Assert.Equal(2, model.Resources.Single().Type.Id);
        Assert.Equal("PIC", model.Resources.Single().Identity.Name);
        Assert.Equal(0x300, model.Resources.Single().FileOffset);
        Assert.Equal(JsonSerializer.Serialize(model), JsonSerializer.Serialize(reader.Read(Fixture())));
    }

    [Theory]
    [InlineData(0x3c, 0xffff)] // NE header offset
    [InlineData(0xa2, 0xffff)] // segment table offset
    [InlineData(0x9c, 0xffff)] // segment count
    [InlineData(0xb2, 32)] // alignment
    [InlineData(0xc0, 0xffff)] // segment file offset
    [InlineData(0x123, 32)] // entry offset outside allocation
    [InlineData(0x12a, 0)] // movable signature
    [InlineData(0x110, 1)] // module reference points inside name
    [InlineData(0xdc, 0xffff)] // resource length
    [InlineData(0xe0, 0x7fff)] // resource name offset
    [InlineData(0x144, 99)] // name references nonexistent ordinal
    public void RejectsMutatedMetadataWithControlledDiagnostic(int offset, ushort value)
    {
        var bytes = Fixture();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        var error = Assert.Throws<InvalidDataException>(() => new NeExecutableReader().Read(bytes));
        Assert.Contains("file 0x", error.Message);
    }

    [Fact]
    public void EveryTruncationEitherParsesOrProducesAFormatDiagnostic()
    {
        byte[] bytes = Fixture();
        for (int i = 0; i < bytes.Length; i++)
        {
            try
            {
                new NeExecutableReader().Read(bytes.AsSpan(0, i));
            }
            catch (InvalidDataException)
            {
                continue;
            }

            Assert.True(i >= 0x310, $"Unexpected successful truncation at {i:X}");
        }
    }

    [Fact]
    public void HandlesUnbackedAndEncoded64KSegments()
    {
        var bytes = Fixture();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0xc0), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0xc2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0xc6), 0);
        var segment = new NeExecutableReader().Read(bytes).Segments.Single();
        Assert.Equal(0, segment.FileLength);
        Assert.Equal(65536, segment.MemoryLength);
        Array.Resize(ref bytes, 0x10200);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0xc0), 0x20);
        Assert.Equal(65536, new NeExecutableReader().Read(bytes).Segments.Single().FileLength);
    }

    [Fact]
    public void RandomMutationsNeverEscapeAsUncheckedParserExceptions()
    {
        var random = new Random(41);
        for (int i = 0; i < 2000; i++)
        {
            byte[] bytes = Fixture();
            bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            try
            {
                new NeExecutableReader().Read(bytes);
            }
            catch (InvalidDataException)
            {
            }
        }
    }
}
