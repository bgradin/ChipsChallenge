using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Translator.Analysis;
using Translator.Binary;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeWindowTests
{
    private static byte[] Fixture(bool changeReturn = false)
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 1, 19, 1, 107));
        bytes.AsSpan(0x200, 64).Fill(0x90);
        byte[] code = [0x55, 0x8b, 0xec, 0xff, 0x76, 14, 0xff, 0x76, 12, 0xff, 0x76, 10, 0xff, 0x76, 8, 0xff, 0x76, 6, 0x9a, 0xff, 0xff, 0, 0, 0x5d, 0xca, 10, 0];
        if (changeReturn)
        {
            code = code[..23].Concat(new byte[] { 0x40, 0x5d, 0xca, 10, 0 }).ToArray();
        }

        code.CopyTo(bytes, 0x200);
        bytes[0x230] = 0xcb;
        NeLoaderTests.W(bytes, 0x12d, 48);
        NeLoaderTests.W(bytes, 0x92, 1024);
        bytes.AsSpan(0x112, 7).Clear();
        bytes[0x112] = 4;
        Encoding.ASCII.GetBytes("USER").CopyTo(bytes, 0x113);
        return bytes;
    }

    private static NeApplicationProgram Build(byte[] bytes, string root = "1:0") => NeApplicationProgram.Build(bytes, new(root, new ushort[5], 0, WindowMessages: true));

    [Fact]
    public void NativeWindowContractsPreserveOrdinalsAndPascalStackWidthsWithoutClaimingPaintingSupport()
    {
        var apis = Win16WindowContracts.All;
        Assert.Equal(30, apis.Single(a => a.Name == "CreateWindow").CleanupBytes);
        Assert.Equal(4, apis.Single(a => a.Name == "RegisterClass").CleanupBytes);
        Assert.Equal((ushort?)133, apis.Single(a => a.Name == "GetWindowWord").Ordinal);
        Assert.All(apis.Where(a => a.Name is "BeginPaint" or "EndPaint" or "SetDIBitsToDevice"), a => Assert.False(a.Supported));
        Assert.Equal(6, Win16ApiRegistry.All.Count); // existing general runtime registry is unchanged
    }

    [Fact]
    public void WindowPackagingIsDeterministicAndRetainsConservativeClosureStatus()
    {
        var first = Build(Fixture());
        var second = Build(Fixture());
        Assert.Equal(first.Executable, second.Executable);
        Assert.Equal(JsonSerializer.Serialize(first.Manifest), JsonSerializer.Serialize(second.Manifest));
        Assert.NotNull(first.Manifest.WindowCompatibility);
        using var pe = new PEReader(new MemoryStream(first.Executable));
        Assert.Equal(Subsystem.WindowsGui, pe.PEHeaders.PEHeader!.Subsystem);
        Assert.Null(pe.PEHeaders.CorHeader);
        Assert.Contains(first.Imports, api => api.Name == "CreateWindowExW");
        Assert.Contains(first.Manifest.DeferredCapabilities, text => text.Contains("painting"));
        // Import policies cannot erase unrelated unknown control-flow targets.
        var dynamic = Fixture();
        dynamic[0x200] = 0xff;
        dynamic[0x201] = 0xd0;
        Assert.Throws<InvalidDataException>(() => Build(dynamic));
    }

    [WindowsFact]
    public void SyntheticOriginalProcedureHandlesLiveMessagesAndReturnMutationFails()
    {
        Assert.Equal(0, NeApplicationTests.Execute(Build(Fixture()).Executable));
        Assert.Equal(40, NeApplicationTests.Execute(Build(Fixture(changeReturn: true)).Executable));
    }

    [WindowsSuppliedNeFact]
    public void OriginalCounterDefaultPathReceivesLiveWindowMessagesAndPaintingStillFailsClosed()
    {
        byte[] original = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var program = Build(original, "2:296a");
        Assert.Equal(2, program.Manifest.Translation.Functions.Count);
        Assert.False(program.Manifest.Translation.Graph.ClosureComplete);
        Assert.Equal(0, NeApplicationTests.Execute(program.Executable));
        var graph = new NeProgramAnalyzer().Analyze(original, "2:296a");
        var sub = graph.Functions.First(f => f.Address == graph.Root).Instructions.First(i => i.Mnemonic == "Sub" && i.Text.StartsWith("sub ax", StringComparison.Ordinal));
        var changed = original.ToArray();
        var segment = new NeExecutableReader().Read(original).Segments.Single(s => s.Index == 2);
        Assert.Contains(sub.BytesHex, new[] { "2D0F00", "83E80F" });
        int immediate = segment.FileOffset + sub.Address.Offset + (sub.BytesHex.StartsWith("2D", StringComparison.Ordinal)
            ? 1
            : 2);
        Assert.Equal(15, changed[immediate]);
        changed[immediate] = 1; // WM_CREATE now enters original paint path -> explicit rejection endpoint
        Assert.Equal(30, NeApplicationTests.Execute(Build(changed, "2:296a").Executable));
    }
}
