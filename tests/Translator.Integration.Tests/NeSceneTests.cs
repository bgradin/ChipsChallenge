using System.Security.Cryptography;
using System.Text.Json;
using Translator.Binary;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeSceneTests
{
    private static byte[] Original() => File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);

    private static NeApplicationProgram Build() => NeApplicationProgram.Build(Original(), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true));

    private static int Run(byte[] pe) => NeApplicationTests.Execute(pe, originalPath: Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"));

    [Theory]
    [InlineData(0x0ed6)]
    [InlineData(0x0ed7)]
    public void ComplementCarryChangesOnlyTheCarryFlag(int flags)
    {
        using var program = NeNativeProgram.Create(NeDiscoveryTests.Fixture(0xf5, 0xcb), "1:0");
        var state = program.CreateState();
        state.Flags = (ushort)flags;
        program.Invoke(ref state, Array.Empty<ushort>());
        Assert.Equal((ushort)(flags ^ 1), state.Flags);
    }

    [SuppliedNeFact]
    public void ScenePreservesOriginalBodiesHierarchyFixtureAndExternalResourceProvenance()
    {
        var first = Build();
        var second = Build();
        var scene = first.Manifest.Scene!;
        Assert.Equal(first.Executable, second.Executable);
        Assert.Equal(JsonSerializer.Serialize(first.Manifest), JsonSerializer.Serialize(second.Manifest));
        Assert.Equal(15, first.Manifest.Translation.Functions.Count);
        Assert.False(first.Manifest.Translation.Graph.ClosureComplete);
        Assert.NotEmpty(first.Manifest.Translation.FatalBoundaries);
        Assert.Contains(first.Manifest.Translation.FatalBoundaries, b => b.Contains("0007:0A74"));
        Assert.Equal(7, scene.Windows.Count);
        Assert.Equal(-1, scene.Windows[0].Parent);
        Assert.Equal((short)288, scene.Windows[1].Width);
        Assert.Equal((short)288, scene.Windows[1].Height);
        Assert.Equal(0, scene.Windows[1].Parent);
        Assert.Equal(2, scene.Windows[6].Parent);
        Assert.Equal("0002:2A9A", scene.Windows[6].Procedure);
        Assert.Equal(4, scene.Pixels.Count);
        Assert.Contains(scene.Writes, w => w.Offset == 0x1680);
        using var resources = new NeResourceCatalog(Original());
        foreach (var resource in scene.Resources)
        {
            var entry = resources.Resources.Single(r => r.Identity.Name == resource.Name);
            Assert.Equal(resource.Offset, entry.FileOffset);
            Assert.Equal(resource.Length, entry.Length);
            byte[] raw = Original().AsSpan(entry.FileOffset, entry.Length).ToArray();
            Assert.Equal(resource.Sha256, Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant());
            Assert.Equal(-1, first.Executable.AsSpan().IndexOf(raw));
        }

        Assert.Contains(first.Manifest.Translation.Module.Functions.Single(f => f.OriginalOffset == 0x2a9a).Instructions, i => i.Operation == "CMC");
    }

    [WindowsSuppliedNeFact]
    public void OriginalScenePaintsBoardInventoryInfoAndCountersAcrossRepaintsAndCloses() => Assert.Equal(0, Run(Build().Executable));

    [WindowsSuppliedNeFact]
    public void ScenePixelResourceAndUnsafeGameStateMutationsFailDeterministically()
    {
        var program = Build();
        var scene = program.Manifest.Scene!;

        byte[] Mutate(int descriptorOffset)
        {
            var result = program.Executable.ToArray();
            int image = result.AsSpan().IndexOf(program.Descriptor);
            Assert.True(image >= 0);
            result[image + descriptorOffset] ^= 1;
            return result;
        }


        byte[] state = scene.Writes.Single(w => w.Offset == 0x4000).Bytes;
        int at = program.Descriptor.AsSpan().IndexOf(state);
        Assert.True(at >= 0);
        Assert.Equal(30, Run(Mutate(at + 0xa36))); // Original PaintBoardWindow reaches the fatal EndGame boundary.
        Assert.Equal(40, Run(Mutate(at + 2 * 32 + 2))); // Changing one original tile breaks the independent board oracle.
        byte[] digest = Convert.FromHexString(scene.Pixels[0].Sha256);
        int pixels = program.Descriptor.AsSpan().IndexOf(digest);
        Assert.True(pixels >= 0);
        Assert.Equal(40, Run(Mutate(pixels))); // An altered expected pixel buffer is detected.
        byte[] resourceHash = Convert.FromHexString(scene.Resources[0].Sha256);
        int resource = program.Descriptor.AsSpan().IndexOf(resourceHash);
        Assert.True(resource >= 0);
        byte[] inventoryWrite = [0x82, 0x16, 2, 0, 1, 0];
        int inventory = program.Descriptor.AsSpan().IndexOf(inventoryWrite);
        Assert.True(inventory >= 0);
        Assert.Equal(40, Run(Mutate(inventory + 4))); // Original CMC/SBB chooses the absent-key tile.
        // Node records start after the explicit fixture writes; original procedure bindings remain verified.
        int nodes = resource - 8 + 2 * 60 + 4 + scene.Writes.Sum(w => 4 + w.Bytes.Length) + 4;
        Assert.Equal(10, Run(Mutate(nodes + 24 + 4))); // Board HWND cannot bind an unregistered original entry.
        Assert.Equal(82, Run(Mutate(resource))); // External scene-resource identity is checked separately.
    }
}
