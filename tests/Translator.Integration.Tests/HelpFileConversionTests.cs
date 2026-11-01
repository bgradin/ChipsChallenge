using System.Text;
using Translator.Delivery;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class HelpFileConversionTests
{
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sample-help-" + Guid.NewGuid().ToString("N"));

        public Folder() => Directory.CreateDirectory(Path);

        public void Write(string name, byte[] bytes)
        {
            string path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void Dispose() => Directory.Delete(Path, true);
    }

    private static byte[] Image(bool derived = false, params string[] references)
    {
        byte[] image = derived
            ? NeLoaderTests.Fixture((3, 1, 4, 1, 5))
            : NeLoaderTests.Fixture();
        Array.Resize(ref image, 0x800);
        NeLoaderTests.W(image, 0xca, 512);
        NeLoaderTests.W(image, 0xce, 512);
        image.AsSpan(0x400, 512).Clear();
        if (derived)
        {
            image.AsSpan(0x112, 14).Clear();
            image[0x112] = 8;
            Encoding.ASCII.GetBytes("WEP4UTIL").CopyTo(image, 0x113);
            image[0x11b] = 0;
        }

        int at = 0x400;
        foreach (string reference in references)
        {
            byte[] text = Encoding.Latin1.GetBytes(reference + "\0");
            text.CopyTo(image, at);
            at += text.Length;
        }

        return image;
    }

    [Fact]
    public void ConvertsMultipleLiteralReferencesPreservingRelativePathsAndInputFiles()
    {
        using var folder = new Folder();
        byte[] first = SampleWinHelp.Create("First authored document"), second = SampleWinHelp.Create("Second authored document");
        folder.Write("manual.hlp", first);
        folder.Write("docs/other.hlp", second);
        var result = HelpFileConversion.Plan(Image(false, "MANUAL.HLP", "docs\\other.hlp>secondary"), Image(), System.IO.Path.Combine(folder.Path, "tool.exe"), System.IO.Path.Combine(folder.Path, "new.exe"));
        Assert.Equal(4, result.Count);
        foreach (var pair in new[]
        {
            ("MANUAL", "First authored document"),
            ("docs/other", "Second authored document")
        }

        )
        {
            string name = System.IO.Path.ChangeExtension(pair.Item1.Replace('/', System.IO.Path.DirectorySeparatorChar), ".CHM");
            var chm = Assert.Single(result, r => r.RelativePath == name);
            var files = NeHelpResourceTests.ReadUncompressedChm(chm.Bytes);
            Assert.Contains(pair.Item2, Encoding.UTF8.GetString(files["/html/topic23.htm"]));
        }

        Assert.Equal(first, File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "manual.hlp")));
        Assert.False(File.Exists(System.IO.Path.Combine(folder.Path, "MANUAL.CHM")));
    }

    [Theory]
    [InlineData(".\\manual.hlp", "MANUAL.HLP")]
    [InlineData("caf\u00e9.hlp", "CAF\u00c9.HLP")]
    public void RelativePrefixesAndAnsiFilenamesResolveCaseInsensitively(string reference, string actual)
    {
        using var folder = new Folder();
        folder.Write(actual, SampleWinHelp.Create());
        var result = HelpFileConversion.Plan(Image(false, reference), Image(), System.IO.Path.Combine(folder.Path, "tool.exe"), "out.exe");
        Assert.Equal(2, result.Count);
        Assert.Single(result, r => r.RelativePath.EndsWith(".CHM", StringComparison.Ordinal));
    }

    [Fact]
    public void OutputBasenameAliasCannotReplaceADifferentReferencedDocument()
    {
        using var folder = new Folder();
        folder.Write("source.HLP", SampleWinHelp.Create("First"));
        folder.Write("renamed.HLP", SampleWinHelp.Create("Second"));
        Assert.Throws<InvalidDataException>(() => HelpFileConversion.Plan(Image(true, "renamed.HLP"), Image(), System.IO.Path.Combine(folder.Path, "source.exe"), System.IO.Path.Combine(folder.Path, "renamed.exe")));
    }

    [Fact]
    public void ModuleDerivedHelpProducesOriginalAndOutputBasenameAliases()
    {
        using var folder = new Folder();
        folder.Write("source.HLP", SampleWinHelp.Create());
        var result = HelpFileConversion.Plan(Image(true), Image(), System.IO.Path.Combine(folder.Path, "source.exe"), System.IO.Path.Combine(folder.Path, "renamed.exe"));
        Assert.Equal(new[] { "renamed.CHM", "renamed.HLP", "source.CHM", "source.HLP" }, result.Select(r => r.RelativePath).Order().ToArray());
        Assert.Equal(result.Single(r => r.RelativePath == "source.CHM").Bytes, result.Single(r => r.RelativePath == "renamed.CHM").Bytes);
    }

    [Theory]
    [InlineData("../outside.hlp")]
    [InlineData("C:\\external.hlp")]
    [InlineData("%s.hlp")]
    [InlineData(".HLP")]
    public void UnsafeOrUnresolvedReferencesAreReported(string reference)
    {
        using var folder = new Folder();
        Assert.Throws<NotSupportedException>(() => HelpFileConversion.Plan(Image(false, reference), Image(), System.IO.Path.Combine(folder.Path, "tool.exe"), System.IO.Path.Combine(folder.Path, "new.exe")));
    }

    [Fact]
    public void MissingOrUnsupportedReferencedFilesFailWithTheirFilename()
    {
        using var folder = new Folder();
        string input = System.IO.Path.Combine(folder.Path, "tool.exe");
        var missing = Assert.Throws<FileNotFoundException>(() => HelpFileConversion.Plan(Image(false, "missing.hlp"), Image(), input, input));
        Assert.Contains("missing.hlp", missing.Message);
        folder.Write("bad.hlp", [1, 2, 3]);
        var invalid = Assert.Throws<InvalidDataException>(() => HelpFileConversion.Plan(Image(false, "bad.hlp"), Image(), input, input));
        Assert.Contains("bad.hlp", invalid.Message);
    }

    [Fact]
    public void NoHelpReferencesRequireNoHelpCompanions()
    {
        using var folder = new Folder();
        Assert.Empty(HelpFileConversion.Plan(Image(), Image(), System.IO.Path.Combine(folder.Path, "tool.exe"), "out.exe"));
    }
}
