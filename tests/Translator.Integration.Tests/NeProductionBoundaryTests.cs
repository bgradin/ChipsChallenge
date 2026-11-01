using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Translator.ChipsChallenge;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeProductionBoundaryTests
{
    private static string ProductionDll => Path.GetFullPath("../../../../../src/Translator.Production/bin/Debug/net10.0/Translator.Production.dll");

    private static int Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(ProductionDll);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(true);
            throw new TimeoutException("Production diagnostic command timed out.");
        }

        Task.WaitAll(stdout, stderr);
        return process.ExitCode;
    }

    [Fact]
    public void ProductionDependencyClosureExcludesFixturesAndAssemblyParser()
    {
        string deps = File.ReadAllText(Path.ChangeExtension(ProductionDll, ".deps.json"));
        Assert.DoesNotContain("Translator.TestSupport", deps);
        Assert.DoesNotContain("Translator.Assembly/", deps);
        Assert.DoesNotContain("Translator.Cli/", deps);
        Assert.DoesNotContain(typeof(NeProductionCompiler).Assembly.GetReferencedAssemblies(), a => a.Name == "Translator.ChipsChallenge");
        Assert.NotEqual(typeof(NeProductionCompiler).Assembly, typeof(NeApplicationProgram).Assembly);
        Assert.Equal(typeof(NeApplicationProgram).Assembly, typeof(ChipsLessonOneRoute).Assembly);
    }

    [Fact]
    public void SourceIdentityRejectsChangedOrUnsupportedInputs()
    {
        Assert.Throws<InvalidDataException>(() => SupportedChipsSource.Validate(new byte[SupportedChipsSource.Size]));
        Assert.Throws<InvalidDataException>(() => SupportedChipsSource.Validate([]));
    }

    [SuppliedNeFact]
    public void ProductionDiagnosticsAreDeterministicAndNeverReplaceAnExecutable()
    {
        byte[] original = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        SupportedChipsSource.Validate(original);
        var first = NeProductionCompiler.Compile(original, new("first.exe"));
        var second = NeProductionCompiler.Compile(original, new("other-location.exe"));
        Assert.Null(first.Executable);
        Assert.False(first.Manifest.ExecutableEmitted);
        Assert.False(first.Manifest.ApplicationAccepted);
        Assert.Equal(JsonSerializer.Serialize(first.Manifest), JsonSerializer.Serialize(second.Manifest));
        Assert.Contains(first.Manifest.Diagnostics, d => d.Code == "IncompletePackaging");
        Assert.Contains(first.Manifest.Diagnostics, d => d.Code == "OriginalStartupPending");
        string directory = Path.Combine(Path.GetTempPath(), "p7r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "CHIPS.EXE"), output = Path.Combine(directory, "CHIPS64.EXE");
            File.WriteAllBytes(input, original);
            File.WriteAllText(output, "existing-output");
            Assert.Equal(3, Run("--input", input, "--output", output));
            Assert.Equal("existing-output", File.ReadAllText(output));
            Assert.Equal(SHA256.HashData(original), SHA256.HashData(File.ReadAllBytes(input)));
            using var manifest = JsonDocument.Parse(File.ReadAllText(output + ".json"));
            Assert.False(manifest.RootElement.GetProperty("ExecutableEmitted").GetBoolean());
            Assert.Equal(2, Run("--input", input, "--output", input));
            Assert.Equal(2, Run("--input", input, "--manifest", input));
            Assert.Equal(2, Run("--input", input, "--startup", "level-check"));
            File.Delete(output);
            Assert.Equal(3, Run("--input", input, "--output", output));
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
