using System.Buffers.Binary;
using System.Text;
using Translator.ChipsChallenge;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeHelpResourceTests
{
    internal static Dictionary<string, byte[]> ReadUncompressedChm(byte[] bytes)
    {
        Assert.Equal("ITSF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        int directory = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(72))), data = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(88)));
        Assert.Equal("ITSP", Encoding.ASCII.GetString(bytes, directory, 4));
        int first = directory + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(directory + 8)), size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(directory + 16));
        var files = new Dictionary<string, byte[]>();
        var visited = new HashSet<int>();
        int page = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(directory + 32));
        int previous = -1;
        while (page != -1)
        {
            Assert.True(visited.Add(page));
            int block = first + page * size;
            Assert.Equal("PMGL", Encoding.ASCII.GetString(bytes, block, 4));
            Assert.Equal(previous, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(block + 12)));
            int end = block + size - BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(block + 4));
            int at = block + 20;

            int CompressedInteger()
            {
                int value = 0;
                byte part;
                do
                {
                    part = bytes[at++];
                    value = checked(value * 128 + (part & 127));
                }
                while ((part & 128) != 0);
                return value;
            }


            while (at < end)
            {
                int length = CompressedInteger();
                string name = Encoding.UTF8.GetString(bytes, at, length);
                at += length;
                Assert.Equal(0, CompressedInteger());
                int offset = CompressedInteger(), count = CompressedInteger();
                files.Add(name, bytes.AsSpan(data + offset, count).ToArray());
            }

            Assert.Equal(end, at);
            previous = page;
            page = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(block + 16));
        }

        Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(directory + 44)), visited.Count);
        return files;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AuthoredHlpConvertsTextFormattingLinksKeywordsAndBitmap(bool phrases, bool packedBitmap)
    {
        byte[] input = SampleWinHelp.Create(phrases: phrases, packedBitmap: packedBitmap);
        byte[] converted = SupportedChipsHelp.Convert(input);
        Assert.Equal(converted, SupportedChipsHelp.Convert(input));
        var files = ReadUncompressedChm(converted);
        string html = Encoding.UTF8.GetString(files["/html/topic23.htm"]);
        Assert.Contains("Sample contents", html);
        Assert.Contains("Repeated phrase &amp; sample &lt;text&gt;", html);
        Assert.Contains("font-weight:bold", html);
        Assert.Contains("font-style:italic", html);
        Assert.Contains("font-size:12pt", html);
        Assert.Contains("<BR>", html);
        Assert.Contains("href=\"topic41.htm\"", html);
        Assert.Contains("../images/bm7.bmp", html);
        Assert.Contains("A separate authored topic.", Encoding.UTF8.GetString(files["/html/topic41.htm"]));
        byte[] bitmap = files["/images/bm7.bmp"];
        Assert.Equal("BM", Encoding.ASCII.GetString(bitmap, 0, 2));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(18)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(22)));
        Assert.Equal(packedBitmap
            ? Enumerable.Repeat((byte)0xaa, 8).ToArray()
            : new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 }, bitmap[^8..]);
        Assert.Contains("sample-key", Encoding.UTF8.GetString(files["/index.hhk"]));
        Assert.Contains("html/topic41.htm", Encoding.UTF8.GetString(files["/index.hhk"]));
        Assert.Contains("html/topic23.htm", Encoding.UTF8.GetString(files["/#SYSTEM"]));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public void PalettedPicturesRetainTheirBitDepthPaletteAndDecodedPixels(int bits, bool packed)
    {
        var files = ReadUncompressedChm(SupportedChipsHelp.Convert(SampleWinHelp.Create(packedBitmap: packed, bitmapBits: bits)));
        byte[] bitmap = files["/images/bm7.bmp"];
        int offset = BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(10));
        Assert.Equal((ushort)bits, BinaryPrimitives.ReadUInt16LittleEndian(bitmap.AsSpan(28)));
        Assert.Equal(54 + (1 << bits) * 4, offset);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, bitmap[54..58]);
        byte second = (byte)(bits == 1
            ? 255
            : 1);
        Assert.Equal(new byte[] { second, second, second, 0 }, bitmap[58..62]);
        Assert.Equal(offset + 4, bitmap.Length);
        Assert.Equal((byte)(packed
            ? 0xaa
            : bits == 1
                ? 0x80
                : bits == 4
                    ? 0x12
                    : 1), bitmap[offset]);
    }

    [Fact]
    public void ContextIdentifiersAndContentsAreTakenFromTheInputMap()
    {
        var files = ReadUncompressedChm(SupportedChipsHelp.Convert(SampleWinHelp.Create(contentsId: 1, detailsId: 2)));
        Assert.Contains("html/topic1.htm", Encoding.UTF8.GetString(files["/#SYSTEM"]));
        Assert.Contains("href=\"topic2.htm\"", Encoding.UTF8.GetString(files["/html/topic1.htm"]));
        Assert.Contains("/html/topic2.htm", files.Keys);
        string index = Encoding.UTF8.GetString(files["/index.hhk"]);
        Assert.Contains("sample-key", index);
        string route = "/keywords/" + Translator.Core.HelpKeywordRoute.File("SaMpLe-KeY");
        Assert.Contains("url=../html/topic2.htm", Encoding.UTF8.GetString(files[route]));
        Assert.Contains("Sample details", index);
    }

    [Fact]
    public void TopicBlocksAndMultiPageKeywordDirectoriesPreserveAllAuthoredEntries()
    {
        string body = new('x', 6000);
        var files = ReadUncompressedChm(SupportedChipsHelp.Convert(SampleWinHelp.Create(body, extraKeywords: 180)));
        Assert.Contains(body, Encoding.UTF8.GetString(files["/html/topic23.htm"]));
        for (int i = 0; i < 180; i++)
        {
            Assert.Contains("/keywords/" + Translator.Core.HelpKeywordRoute.File("Authored-keyword-" + i.ToString("D4")), files.Keys);
        }
    }

    [Fact]
    public void AmbiguousKeywordsOfferAllTargetsAndCaseVariantsUseTheSameRoute()
    {
        var files = ReadUncompressedChm(SupportedChipsHelp.Convert(SampleWinHelp.Create(keyword: "caf\u00e9", ambiguousKeyword: true)));
        string html = Encoding.UTF8.GetString(files["/keywords/" + Translator.Core.HelpKeywordRoute.File("CAF\u00c9")]);
        Assert.Contains("../html/topic23.htm", html);
        Assert.Contains("../html/topic41.htm", html);
        Assert.DoesNotContain("http-equiv=\"refresh\"", html);
    }

    [Fact]
    public void ConversionUsesSuppliedContentRatherThanPreparedHelp()
    {
        var first = ReadUncompressedChm(SupportedChipsHelp.Convert(SampleWinHelp.Create("Authored version one")));
        var second = ReadUncompressedChm(SupportedChipsHelp.Convert(SampleWinHelp.Create("Authored version two")));
        Assert.Contains("Authored version one", Encoding.UTF8.GetString(first["/html/topic23.htm"]));
        Assert.Contains("Authored version two", Encoding.UTF8.GetString(second["/html/topic23.htm"]));
        Assert.NotEqual(first["/html/topic23.htm"], second["/html/topic23.htm"]);
    }

    [Fact]
    public void ProductionAssemblyDoesNotEmbedPreparedHelp()
    {
        Assert.DoesNotContain(typeof(SupportedChipsHelp).Assembly.GetManifestResourceNames(), name => name.Contains("Help", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("|Phrases", 4)]
    [InlineData("|TOPIC", 4)]
    [InlineData("|TOPIC", 24)]
    [InlineData("|FONT", 4)]
    [InlineData("bm7", 8)]
    [InlineData("|SYSTEM", 2)]
    [InlineData("|TOMAP", 0)]
    [InlineData("|KWDATA", 0)]
    public void MalformedAuthoredHelpInternalsAreRejected(string name, int field)
    {
        byte[] source = SampleWinHelp.Create();
        int directory = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(4)) + 9;
        int at = directory + 38 + 8;
        while (at < source.Length)
        {
            int end = Array.IndexOf(source, (byte)0, at);
            string entry = Encoding.ASCII.GetString(source, at, end - at);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(end + 1));
            if (entry == name)
            {
                BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(offset + 9 + field), int.MaxValue);
                Assert.Throws<InvalidDataException>(() => SupportedChipsHelp.Convert(source));
                return;
            }

            at = end + 5;
        }

        Assert.Fail("Missing fixture internal file: " + name);
    }

    [Fact]
    public void InvalidHeadersTruncationAndUnsupportedVersionsAreRejected()
    {
        byte[] input = SampleWinHelp.Create();
        foreach (int offset in new[]
        {
            0,
            4,
            12
        }

        )
        {
            byte[] bad = input.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(offset), int.MaxValue);
            Assert.Throws<InvalidDataException>(() => SupportedChipsHelp.Convert(bad));
        }

        Assert.Throws<InvalidDataException>(() => SupportedChipsHelp.Convert(input.AsSpan(0, input.Length - 1)));
        Assert.Throws<InvalidDataException>(() => SupportedChipsHelp.Convert([]));
    }
}
