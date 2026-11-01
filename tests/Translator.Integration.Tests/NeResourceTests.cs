using System.Buffers.Binary;
using System.Security.Cryptography;
using Translator.Binary;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeResourceTests
{
    private static void W(byte[] b, int p, int n) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), (ushort)n);

    private static void D(byte[] b, int p, int n) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p), n);
    private static byte[] Dib(int width, int height, int bits, int compression, params byte[] pixels)
    {
        int colors = bits <= 8
            ? 1 << bits
            : 0;
        var b = new byte[40 + colors * 4 + pixels.Length];
        D(b, 0, 40);
        D(b, 4, width);
        D(b, 8, height);
        W(b, 12, 1);
        W(b, 14, bits);
        D(b, 16, compression);
        D(b, 20, pixels.Length);
        // Index n is red n, green 0, blue 0; verify channel ordering independently.
        for (int i = 0; i < colors; i++)
        {
            b[40 + i * 4 + 2] = (byte)i;
        }

        pixels.CopyTo(b, 40 + colors * 4);
        return b;
    }

    private static byte[] Resource(int type, byte[] payload, int id = 1)
    {
        var b = NeParserTests.Fixture();
        Array.Resize(ref b, 0x300 + Math.Max(payload.Length, 16));
        W(b, 0xd0, 0);
        W(b, 0xd2, 0x8000 | type);
        W(b, 0xda, 0x300);
        W(b, 0xdc, payload.Length);
        W(b, 0xe0, 0x8000 | id);
        payload.CopyTo(b, 0x300);
        return b;
    }

    [Fact]
    public void IndexedRowsRespectPaddingNibbleOrderAndOrientation()
    {
        byte[] bytes = Dib(3, 2, 4, 0, 0x12, 0x30, 0, 0, 0x45, 0x60, 0, 0);
        var bitmap = NeBitmapDecoder.Decode(bytes);
        Assert.Equal(new byte[] { 4, 5, 6, 1, 2, 3 }, bitmap.Rgba32.Where((_, i) => i % 4 == 0));
        Assert.All(bitmap.Rgba32.Where((_, i) => i % 4 == 3), b => Assert.Equal(255, b));
        D(bytes, 8, -2);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, NeBitmapDecoder.Decode(bytes).Rgba32.Where((_, i) => i % 4 == 0));
        Assert.Equal(new byte[] { 1, 0, 1 }, NeBitmapDecoder.Decode(Dib(3, 1, 1, 0, 0xa0, 0, 0, 0)).Rgba32.Where((_, i) => i % 4 == 0));
        Assert.Equal(new byte[] { 3, 2, 1, 255 }, NeBitmapDecoder.Decode(Dib(1, 1, 24, 0, 1, 2, 3, 0)).Rgba32);
    }

    [Fact]
    public void Rle4HandlesEncodedAbsolutePaddedAndDeltaCommands()
    {
        var bitmap = NeBitmapDecoder.Decode(Dib(8, 2, 4, 2, 3, 0x12, 0, 2, 2, 0, 0, 3, 0x34, 0x50, 0, 0, 0, 5, 0x67, 0x89, 0xa0, 0, 3, 0xbc, 0, 1));
        Assert.Equal(new byte[] { 6, 7, 8, 9, 10, 11, 12, 11, 1, 2, 1, 0, 0, 3, 4, 5 }, bitmap.Rgba32.Where((_, i) => i % 4 == 0));
        foreach (var stream in new[]
        {
            new byte[]
            {
                9,
                0x12,
                0,
                1
            },
            new byte[]
            {
                0,
                2,
                0,
                2,
                0,
                1
            },
            new byte[]
            {
                0,
                3,
                0x12
            },
            new byte[]
            {
                3,
                0x12
            }
        }

        )
        {
            Assert.Throws<InvalidDataException>(() => NeBitmapDecoder.Decode(Dib(8, 2, 4, 2, stream)));
        }
    }

    [Fact]
    public void TruncationDimensionsAndUnsupportedCompressionAreControlled()
    {
        var bytes = Dib(3, 2, 4, 0, new byte[8]);
        for (int i = 0; i < bytes.Length; i++)
        {
            Assert.Throws<InvalidDataException>(() => NeBitmapDecoder.Decode(bytes.AsSpan(0, i)));
        }

        D(bytes, 4, int.MaxValue);
        Assert.Throws<InvalidDataException>(() => NeBitmapDecoder.Decode(bytes));
        D(bytes, 4, 3);
        D(bytes, 16, 99);
        Assert.Throws<NotSupportedException>(() => NeBitmapDecoder.Decode(bytes));
    }

    [Fact]
    public void CatalogExtractionIsIsolatedAndSourceAssociatedAndReportsUnsupportedOrInvalid()
    {
        byte[] bytes = Resource(6, new byte[] { 2, 0x41, 0x80 }.Concat(new byte[15]).ToArray(), 17);
        using var catalog = new NeResourceCatalog(bytes);
        var extracted = catalog.Extract(1);
        var strings = Assert.IsType<NeStringTable>(catalog.Inspect(1).Decoded);
        Assert.Equal(256, strings.Strings[0].Id);
        Assert.Equal("A€", strings.Strings[0].Value);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), extracted.ExecutableSha256);
        Assert.Equal(bytes.AsSpan(0x300).ToArray(), extracted.Bytes);
        bytes[0x300] = 255;
        extracted.Bytes[0] = 255;
        Assert.Equal("Decoded", catalog.Inspect(1).DecodeStatus);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Extract(0));
        catalog.Dispose();
        Assert.Throws<ObjectDisposedException>(() => catalog.Extract(1));
        using var unsupported = new NeResourceCatalog(Resource(4, new byte[16]));
        Assert.Equal("Unsupported", unsupported.Inspect(1).DecodeStatus);
        using var invalid = new NeResourceCatalog(Resource(6, new byte[] { 255 }));
        Assert.Equal("Invalid", invalid.Inspect(1).DecodeStatus);
        using var badGroup = new NeResourceCatalog(Resource(14, new byte[] { 0, 0, 1, 0, 1, 0 }.Concat(new byte[14]).ToArray()));
        Assert.Equal("Invalid", badGroup.Inspect(1).DecodeStatus);
    }

    [Theory]
    [InlineData(NeMemoryBackend.Native)]
    [InlineData(NeMemoryBackend.Managed)]
    public void InitializationUsesOriginalBytesAndProcedureMappingsAndRollsBackOnFailure(NeMemoryBackend backend)
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xb8, 7, 0, 0xcb);
        using var program = NeNativeProgram.Create(bytes, "1:0", runtimeOptions: new(backend));
        var initialization = program.Initialization;
        Assert.Equal(backend.ToString(), initialization.Runtime.Storage.Backend);
        Assert.Equal(44, initialization.Runtime.MachineStateAbiSize);
        Assert.Equal(2, initialization.Runtime.MachineStateAbiVersion);
        Assert.Single(initialization.Procedures);
        var state = program.CreateState();
        program.Invoke(ref state, Array.Empty<ushort>());
        Assert.Equal(7, state.AX);
        Assert.Throws<InvalidDataException>(() => NeRuntimeProgram.Load(bytes, runtimeOptions: new(NativeLimits: new(MaximumBytes: 1))));
        using var allocator = new Win16NativeMemoryAllocator(new(MaximumBytes: 64));
        var memory = new SegmentMemory(allocator);
        // Failure on a subsequent segment must release every already-published allocation.
        Assert.Throws<InvalidDataException>(() => new NeProgramLoader().Load(bytes, memory: memory));
        Assert.Empty(memory.Segments);
        Assert.Equal(0, allocator.Statistics.LiveBytes);
    }

    [SuppliedNeFact]
    public void OriginalResourcesDecodeWithoutShippingAssetsAndNativeImageMatchesManaged()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        using var catalog = new NeResourceCatalog(bytes);
        var report = catalog.InspectAll();
        Assert.Equal(20, report.Resources.Count);
        Assert.Equal(9, report.Resources.Count(r => r.DecodeStatus == "Decoded"));
        Assert.DoesNotContain(report.Resources, r => r.DecodeStatus == "Invalid");
        Assert.Equal(7, report.Resources.Count(r => r.Decoded is NeDecodedBitmap));
        var bitmap = Assert.IsType<NeDecodedBitmap>(report.Resources[1].Decoded);
        Assert.Equal(416, bitmap.Width);
        Assert.Equal(512, bitmap.Height);
        Assert.Equal(2u, bitmap.Compression);
        var icon = Assert.IsType<NeIconGroup>(report.Resources[0].Decoded);
        Assert.Equal(1, Assert.Single(icon.Images).ImageResourceId);
        using var native = NeRuntimeProgram.Load(bytes, new(AllowIncompleteRelocations: true));
        using var managed = NeRuntimeProgram.Load(bytes, new(AllowIncompleteRelocations: true), new(NeMemoryBackend.Managed));
        foreach (var segment in native.Image.Memory.Segments)
        {
            Assert.Equal(managed.Image.Memory.GetSegment(segment.Selector).Memory.ToArray(), segment.Memory.ToArray());
        }

        var memory = native.Image.Memory;
        var stale = memory.Segments.First().Memory;
        native.Dispose();
        Assert.Empty(memory.Segments);
        Assert.Equal(0, native.StorageStatistics.LiveBytes);
        Assert.Throws<ObjectDisposedException>(() => stale.ToArray());
    }
}
