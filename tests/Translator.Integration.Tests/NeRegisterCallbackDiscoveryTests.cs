using Translator.Analysis;
using Translator.Binary;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeRegisterCallbackDiscoveryTests
{
    private static byte[] Fixture(byte[] prefix, ushort api = 102)
    {
        var code = prefix.Concat(new byte[] { 0xb8, 0, 0x25, 0xba, 0x18, 0, 0x9a, 0xff, 0xff, 0, 0, 0xcb }).ToArray();
        byte[] bytes = NeDiscoveryTests.Fixture(code);
        bytes[0x218] = 0xcb;
        NeLoaderTests.W(bytes, 0x94, 0);
        NeLoaderTests.W(bytes, 0xc4, 0x100);
        NeLoaderTests.W(bytes, 0x240, 1);
        bytes[0x242] = 3;
        bytes[0x243] = 1;
        NeLoaderTests.W(bytes, 0x244, (ushort)(prefix.Length + 7));
        NeLoaderTests.W(bytes, 0x246, 1);
        NeLoaderTests.W(bytes, 0x248, api);
        return bytes;
    }

    [Theory]
    [InlineData(new byte[] { 0x0e, 0x1f })]
    [InlineData(new byte[] { 0x8c, 0xc8, 0x8e, 0xd8 })]
    public void VerifiedRegisterApiContractDiscoversCodeWithoutAnOpcodePatternOrSymbolRoot(byte[] prefix)
    {
        var report = NeWholeProgramDiscoveryBuilder.Build(Fixture(prefix));
        var entry = Assert.Single(report.Roots, r => r.Address == new NeBinaryAddress(1, 0x18));
        Assert.Contains(entry.Evidence, e => e.StartsWith("ApiCallback:DOSSetVector:"));
        Assert.True(entry.Visited);
        Assert.Contains(report.Instructions, i => i.Original.Address == entry.Address);
    }

    [Fact]
    public void UnknownSelectorsAndDifferentApiIdentitiesNeverInventARegisteredProcedure()
    {
        foreach (var bytes in new[]
        {
            Fixture(new byte[] { 0x90, 0x90 }),
            Fixture(new byte[] { 0x0e, 0x1f }, 103)
        }

        )
        {
            Assert.DoesNotContain(NeWholeProgramDiscoveryBuilder.Build(bytes).Roots, r => r.Address == new NeBinaryAddress(1, 0x18));
        }
    }
}
