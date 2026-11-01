using System.Security.Cryptography;
using System.Text.Json;
using Translator.Binary;
using Translator.ChipsChallenge;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeCounterPaintingTests
{
    private static byte[] Original() => File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);

    private static NeApplicationProgram Build() => NeApplicationProgram.Build(Original(), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true));

    private static int Run(byte[] pe, string? path = null) => NeApplicationTests.Execute(pe, originalPath: path ?? Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"));

    [SuppliedNeFact]
    public void PaintingLinksOriginalInitializationAndShutdownWithoutEmbeddingGraphics()
    {
        var first = Build();
        var second = Build();
        Assert.Equal(first.Executable, second.Executable);
        Assert.Equal(JsonSerializer.Serialize(first.Manifest), JsonSerializer.Serialize(second.Manifest));
        Assert.Equal(6, first.Manifest.Translation.Functions.Count);
        Assert.Contains(first.Manifest.Translation.Functions, f => f.Address.Identity == "0009:004E");
        Assert.Contains(first.Manifest.Translation.Functions, f => f.Address.Identity == "0009:00BC");
        Assert.Equal(7, first.Manifest.Graphics!.Cases.Count);
        using var manifest = JsonDocument.Parse(JsonSerializer.Serialize(first.Manifest));
        Assert.Contains("WM_PAINT executes original COUNTERWNDPROC", manifest.RootElement.GetProperty("WindowCompatibility").GetProperty("AllowedMessages").GetString());
        Assert.True(first.Manifest.ExitCodes.ContainsKey(80));
        Assert.True(first.Manifest.ExitCodes.ContainsKey(81));
        Assert.True(first.Manifest.ExitCodes.ContainsKey(82));
        using var resources = new NeResourceCatalog(Original());
        var raw = resources.Extract(6);
        Assert.Equal(-1, first.Descriptor.AsSpan().IndexOf(raw.Bytes));
        Assert.Equal(-1, first.Executable.AsSpan().IndexOf(raw.Bytes));
        Assert.Contains(first.Imports, i => i.Name == "SetDIBitsToDevice");
        Assert.Contains(first.Imports, i => i.Name == "BCryptHash");
    }

    [SuppliedNeFact]
    public void CounterReferencesCoverDimensionsPaletteAlignmentBlanksAndClipping()
    {
        var plan = ChipsCounterGraphics.Inspect(Original());
        using var resources = new NeResourceCatalog(Original());
        var raw = resources.Extract(6);
        var bitmap = NeBitmapDecoder.Decode(raw.Bytes);
        Assert.Equal(17, bitmap.Width);
        Assert.Equal(552, bitmap.Height);
        Assert.Equal("b65eecbaf91bcbcb6b61a6c9498b9f54137d71ebbe54c991cf020e19ba3fb2e4", plan.Cases[0].BgrxSha256);
        Assert.Equal(7, plan.Cases.Select(c => c.BgrxSha256).Distinct().Count());
        foreach (var fixture in plan.Cases)
        {
            var pixels = ChipsCounterGraphics.Expected(bitmap, fixture);
            Assert.Equal(fixture.Width * fixture.Height * 4, pixels.Length);
            Assert.Equal(fixture.BgrxSha256, Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant());
            if (fixture.Width >= 51 && fixture.Height >= 23)
            {
                Assert.Equal(new byte[] { 0x66, 0x44, 0x22, 0 }, pixels[..4]);
            }
        }
    }

    [WindowsSuppliedNeFact]
    public void OriginalCounterPaintingMatchesIndependentPixelCasesAndRepeatedRepaints()
    {
        Assert.Equal(0, Run(Build().Executable));
    }

    [WindowsSuppliedNeFact]
    public void RejectsMissingChangedOrTruncatedExternalAssets()
    {
        var program = Build();
        Assert.Equal(80, NeApplicationTests.Execute(program.Executable));
        Assert.Equal(80, Run(program.Executable, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
        string directory = Path.Combine(Path.GetTempPath(), "counter-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "original assets.exe");
            byte[] bytes = Original();
            File.WriteAllBytes(path, bytes);
            Assert.Equal(0, Run(program.Executable, path)); // quoted runtime paths
            bytes[^1] ^= 1;
            File.WriteAllBytes(path, bytes);
            Assert.Equal(81, Run(program.Executable, path));
            File.WriteAllBytes(path, bytes[..100]);
            Assert.Equal(81, Run(program.Executable, path));
        }
        finally
        {
            NeApplicationTests.DeleteProcessDirectory(directory);
        }
    }

    [WindowsSuppliedNeFact]
    public void PixelResourceDimensionAndOriginalEntryMutationsAreDetected()
    {
        var program = Build();
        int header = program.Descriptor.Length - 100 - program.Manifest.Graphics!.Cases.Count * 54;

        byte[] Mutate(int offset)
        {
            var result = program.Executable.ToArray();
            int at = result.AsSpan().IndexOf(program.Descriptor);
            Assert.True(at >= 0);
            result[at + header + offset] ^= 1;
            return result;
        }


        Assert.Equal(40, Run(Mutate(100 + 22))); // independent expected pixel digest
        Assert.Equal(40, Run(Mutate(100 + 6))); // rendered target width
        Assert.Equal(30, Run(Mutate(94))); // required resource ordinal no longer matches original FindBitmap immediate
        Assert.Equal(82, Run(Mutate(40))); // resource span/hash mismatch
        Assert.Equal(30, Run(Mutate(76))); // original LoadDigits binding missing
    }
}
