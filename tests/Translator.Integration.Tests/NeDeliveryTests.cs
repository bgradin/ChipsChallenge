using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Translator.ChipsChallenge;
using Translator.Delivery;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeDeliveryTests
{
    private sealed class InlineProgress(Action<TranslationProgress> action) : IProgress<TranslationProgress>
    {
        public void Report(TranslationProgress value) => action(value);
    }

    private sealed class Inputs : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "delivery-" + Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Directory, "CHIPS.EXE");
        public string Output => Path.Combine(Directory, "CHIPS64.EXE");

        private readonly Dictionary<string, string> _original;

        public Inputs()
        {
            System.IO.Directory.CreateDirectory(Directory);
            string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
            foreach (string name in TranslationService.RequiredAssets.Concat(new[] { "CHIPS.EXE", "WEP4UTIL.DLL" }))
            {
                File.Copy(Path.Combine(Path.GetDirectoryName(source)!, name), Path.Combine(Directory, name));
            }

            File.WriteAllText(Path.Combine(Directory, "entpack.ini"), "[Chip's Challenge]\r\nCurrent Level=2\r\nColor=1\r\n");
            File.WriteAllBytes(Path.Combine(Directory, "CHIPS.HLP"), SampleWinHelp.Create());
            _original = Snapshot();
        }

        private Dictionary<string, string> Snapshot() => System.IO.Directory.GetFiles(Directory).ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

        public void AssertPreserved()
        {
            var current = Snapshot();
            foreach (var item in _original)
            {
                Assert.Equal(item.Value, current[item.Key]);
            }

            Assert.Empty(System.IO.Directory.GetFiles(Directory, ".chips64-*.tmp"));
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    [SuppliedNeFact]
    public async Task AdjacentOutputIsNativeAndPreservesOriginalAssetsAndSaves()
    {
        using var files = new Inputs();
        var stages = new List<string>();
        var result = await new TranslationService().TranslateAsync(new(files.Input), new InlineProgress(p => stages.Add(p.Stage)));
        Assert.Equal(files.Output, result.Output);
        Assert.Equal(new[] { Path.Combine(files.Directory, "CHIPS.CHM"), Path.Combine(files.Directory, "CHIPS64.HLP"), Path.Combine(files.Directory, "CHIPS64.CHM") }, result.CopiedAssets);
        Assert.Equal(File.ReadAllBytes(Path.Combine(files.Directory, "CHIPS.HLP")), File.ReadAllBytes(Path.Combine(files.Directory, "CHIPS64.HLP")));
        TranslationService.ValidateExecutable(File.ReadAllBytes(result.Output));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.Output))).ToLowerInvariant(), result.Sha256);
        Assert.Equal(new[] { "Validating", "Translating", "ValidatingOutput", "Publishing" }, stages);
        Assert.False(File.Exists(result.Output + ".json"));
        files.AssertPreserved();
    }

    [SuppliedNeFact]
    public async Task AlternateFolderReceivesOnlyRequiredAssetsAndExecutable()
    {
        using var files = new Inputs();
        string target = Path.Combine(files.Directory, "destination with spaces");
        System.IO.Directory.CreateDirectory(target);
        var result = await new TranslationService().TranslateAsync(new(files.Input, Path.Combine(target, "Custom.exe")));
        Assert.Equal(TranslationService.RequiredAssets.Count + 4, result.CopiedAssets.Count);
        Assert.Equal(TranslationService.RequiredAssets.Count + 5, System.IO.Directory.GetFiles(target).Length);
        Assert.Equal(File.ReadAllBytes(Path.Combine(files.Directory, "CHIPS.HLP")), File.ReadAllBytes(Path.Combine(target, "Custom.HLP")));
        foreach (string name in TranslationService.RequiredAssets)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(files.Directory, name)), File.ReadAllBytes(Path.Combine(target, name)));
        }

        Assert.False(File.Exists(Path.Combine(target, "entpack.ini")));
        files.AssertPreserved();
    }

    [SuppliedNeFact]
    public async Task UnsupportedInputAndMissingCompanionsNeverPublish()
    {
        using var files = new Inputs();
        byte[] original = File.ReadAllBytes(files.Input);
        File.WriteAllBytes(files.Input, new byte[] { 77, 90 });
        await Assert.ThrowsAsync<InvalidDataException>(() => new TranslationService().TranslateAsync(new(files.Input)));
        File.WriteAllBytes(files.Input, original);
        foreach (string name in TranslationService.RequiredAssets.Concat(new[] { "WEP4UTIL.DLL" }))
        {
            string path = Path.Combine(files.Directory, name);
            File.Move(path, path + ".saved");
            try
            {
                await Assert.ThrowsAsync<FileNotFoundException>(() => new TranslationService().TranslateAsync(new(files.Input)));
            }
            finally
            {
                File.Move(path + ".saved", path);
            }
        }

        byte[] utility = File.ReadAllBytes(Path.Combine(files.Directory, "WEP4UTIL.DLL"));
        var wrong = utility.ToArray();
        wrong[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(files.Directory, "WEP4UTIL.DLL"), wrong);
        await Assert.ThrowsAsync<InvalidDataException>(() => new TranslationService().TranslateAsync(new(files.Input)));
        File.WriteAllBytes(Path.Combine(files.Directory, "WEP4UTIL.DLL"), utility);
        Assert.False(File.Exists(files.Output));
        files.AssertPreserved();
    }

    [SuppliedNeFact]
    public async Task ExistingOutputAndCompanionConflictsArePreserved()
    {
        using var files = new Inputs();
        File.WriteAllText(files.Output, "keep existing output");
        await Assert.ThrowsAsync<IOException>(() => new TranslationService().TranslateAsync(new(files.Input)));
        Assert.Equal("keep existing output", File.ReadAllText(files.Output));
        string target = Path.Combine(files.Directory, "other");
        System.IO.Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "CHIPS.DAT"), "keep existing data");
        await Assert.ThrowsAsync<IOException>(() => new TranslationService().TranslateAsync(new(files.Input, Path.Combine(target, "CHIPS64.EXE"))));
        Assert.Equal("keep existing data", File.ReadAllText(Path.Combine(target, "CHIPS.DAT")));
        Assert.Single(System.IO.Directory.GetFiles(target));
        files.AssertPreserved();
        File.WriteAllText(Path.Combine(files.Directory, "CHIPS64.HLP"), "preserve existing help");
        File.Delete(files.Output);
        await Assert.ThrowsAsync<IOException>(() => new TranslationService().TranslateAsync(new(files.Input)));
        Assert.Equal("preserve existing help", File.ReadAllText(Path.Combine(files.Directory, "CHIPS64.HLP")));
        Assert.False(File.Exists(files.Output));
        files.AssertPreserved();
    }

    [SuppliedNeFact]
    public async Task MissingReferencedHelpFailsBeforeCompilationAndPublication()
    {
        using var files = new Inputs();
        string help = Path.Combine(files.Directory, "CHIPS.HLP");
        byte[] original = File.ReadAllBytes(help);
        File.Delete(help);
        var stages = new List<string>();
        try
        {
            var error = await Assert.ThrowsAsync<FileNotFoundException>(() => new TranslationService().TranslateAsync(new(files.Input), new InlineProgress(stage => stages.Add(stage.Stage))));
            Assert.Contains(".HLP", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Translating", stages);
            Assert.False(File.Exists(files.Output));
        }
        finally
        {
            File.WriteAllBytes(help, original);
        }

        files.AssertPreserved();
    }

    [SuppliedNeFact]
    public async Task ExistingConvertedHelpIsPreservedAndConflictsStopBeforeTranslation()
    {
        using var files = new Inputs();
        string converted = Path.Combine(files.Directory, "CHIPS64.CHM");
        File.WriteAllText(converted, "existing help must survive");
        var stages = new List<string>();
        await Assert.ThrowsAsync<IOException>(() => new TranslationService().TranslateAsync(new(files.Input), new InlineProgress(p => stages.Add(p.Stage))));
        Assert.Equal("existing help must survive", File.ReadAllText(converted));
        Assert.DoesNotContain("Translating", stages);
        Assert.False(File.Exists(files.Output));
        files.AssertPreserved();
    }

    [SuppliedNeFact]
    public async Task AdjacentHelpIsConvertedFromItsContentAndMalformedHelpNeverPublishes()
    {
        using var files = new Inputs();
        string path = Path.Combine(files.Directory, "CHIPS.HLP");
        byte[] original = File.ReadAllBytes(path);
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            await Assert.ThrowsAsync<InvalidDataException>(() => new TranslationService().TranslateAsync(new(files.Input)));
            Assert.False(File.Exists(files.Output));
            Assert.False(File.Exists(Path.Combine(files.Directory, "CHIPS64.CHM")));
            byte[] modified = SampleWinHelp.Create("Authored replacement help");
            File.WriteAllBytes(path, modified);
            await new TranslationService().TranslateAsync(new(files.Input));
            Assert.Equal(modified, File.ReadAllBytes(path));
            Assert.Equal(modified, File.ReadAllBytes(Path.Combine(files.Directory, "CHIPS64.HLP")));
            byte[] converted = File.ReadAllBytes(Path.Combine(files.Directory, "CHIPS64.CHM"));
            Assert.NotEqual(Convert.ToHexString(SHA256.HashData(SupportedChipsHelp.Convert(original))), Convert.ToHexString(SHA256.HashData(converted)));
            Assert.Contains(NeHelpResourceTests.ReadUncompressedChm(converted).Where(p => p.Key.StartsWith("/html/", StringComparison.Ordinal)).Select(p => System.Text.Encoding.UTF8.GetString(p.Value)), s => s.Contains("Authored replacement help", StringComparison.Ordinal));
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }

        files.AssertPreserved();
    }

    [SuppliedNeFact]
    public async Task CancellationBeforeWorkAfterCompilationAndBeforePublishingRemovesStaging()
    {
        using var files = new Inputs();
        foreach (string stage in new[]
        {
            "Validating",
            "ValidatingOutput",
            "Publishing"
        }

        )
        {
            using var stop = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TranslationService().TranslateAsync(new(files.Input), new InlineProgress(p =>
            {
                if (p.Stage == stage)
                {
                    stop.Cancel();
                }
            }), stop.Token));
            Assert.False(File.Exists(files.Output));
            files.AssertPreserved();
        }
    }

    [SuppliedNeFact]
    public async Task ConcurrentOutputCreationRollsBackOnlyNewAssets()
    {
        using var files = new Inputs();
        string target = Path.Combine(files.Directory, "race");
        System.IO.Directory.CreateDirectory(target);
        string output = Path.Combine(target, "CHIPS64.EXE");
        await Assert.ThrowsAsync<IOException>(() => new TranslationService().TranslateAsync(new(files.Input, output), new InlineProgress(p =>
        {
            if (p.Stage == "Publishing")
            {
                File.WriteAllText(output, "other publisher");
            }
        })));
        Assert.Equal("other publisher", File.ReadAllText(output));
        Assert.Single(System.IO.Directory.GetFiles(target));
        files.AssertPreserved();
    }

    [WindowsSuppliedNeFact]
    public async Task UnwritableDirectoryFailsBeforeTranslationAndLeavesNoPartialOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new Inputs();
        var directory = new DirectoryInfo(Path.Combine(files.Directory, "denied"));
        directory.Create();
        var original = directory.GetAccessControl();
        var denied = directory.GetAccessControl();
        var identity = WindowsIdentity.GetCurrent().User!;
        denied.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.CreateFiles, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            var stages = new List<string>();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new TranslationService().TranslateAsync(new(files.Input, Path.Combine(directory.FullName, "CHIPS64.EXE")), new InlineProgress(p => stages.Add(p.Stage))));
            Assert.DoesNotContain("Translating", stages);
            Assert.Empty(directory.GetFiles());
        }
        finally
        {
            directory.SetAccessControl(original);
        }

        files.AssertPreserved();
    }

    [Fact]
    public async Task CommandRejectsUnknownOrDuplicateOptionsAndSupportsCancellation()
    {
        foreach (var args in new[]
        {
            new[]
            {
                "translate"
            },
            new[]
            {
                "translate",
                "--input",
                "x",
                "--input",
                "y"
            },
            new[]
            {
                "translate",
                "--fixture",
                "x"
            }
        }

        )
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, await TranslationCommand.RunAsync(args, output, error));
            Assert.Contains("--input", error.ToString());
        }

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Equal(130, await TranslationCommand.RunAsync(new[] { "translate", "--input", "unused" }, TextWriter.Null, TextWriter.Null, stop.Token));
        Assert.ThrowsAny<Exception>(() => TranslationService.ValidateExecutable(new byte[] { 77, 90 }));
    }
}
