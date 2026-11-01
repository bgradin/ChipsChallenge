using System.Diagnostics;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class CliIntegrationTests
{
    internal static (int Code, string Output) Run(params string[] arguments)
    {
        string cli = Path.GetFullPath("../../../../../src/Translator.Cli/bin/Debug/net10.0/Translator.Cli.dll");
        // Use the running test host's framework installation (works with dotnet test and vstest).
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows()
            ? "dotnet.exe"
            : "dotnet");
        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(cli);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("CLI timed out.");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    [SuppliedNeFact]
    public void CliBuildsOriginalMessageWindowWithExplicitDeferredPainting()
    {
        string original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        string path = Path.Combine(Path.GetTempPath(), "window-cli-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var result = Run("translate", "--input", original, "--application", "--startup", "window-check", "--output", path);
            Assert.True(result.Code == 0, result.Output);
            Assert.Contains("original-message window", result.Output);
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path + ".json"));
            Assert.Equal("NE window/message PE32+ v1", json.RootElement.GetProperty("Format").GetString());
            Assert.False(json.RootElement.GetProperty("Translation").GetProperty("Graph").GetProperty("ClosureComplete").GetBoolean());
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(0, NeApplicationTests.Execute(File.ReadAllBytes(path)));
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".json");
        }
    }

    [SuppliedNeFact]
    public void CliBuildsExplicitGuiLifecycleCheckpointAndRejectsUnsupportedStartupModes()
    {
        string input = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        string path = Path.Combine(Path.GetTempPath(), $"lifecycle-cli-{Guid.NewGuid():N}.exe");
        try
        {
            string[] args = ["translate", "--input", input, "--application", "--startup", "lifecycle-check", "--output", path];
            var first = Run(args);
            Assert.True(first.Code == 0, first.Output);
            byte[] expected = File.ReadAllBytes(path);
            string manifest = File.ReadAllText(path + ".json");
            var repeat = Run(args);
            Assert.True(repeat.Code == 0, repeat.Output);
            Assert.Equal(expected, File.ReadAllBytes(path));
            Assert.Equal(manifest, File.ReadAllText(path + ".json"));
            Assert.Contains("no game window", first.Output);
            Assert.Contains("0009:0028", manifest);
            Assert.Contains("WinMain", manifest);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(0, NeApplicationTests.Execute(expected));
            }

            Assert.NotEqual(0, Run("translate", "--input", input, "--application", "--output", path).Code);
            Assert.NotEqual(0, Run(args.Concat(new[] { "--root", "WinMain" }).ToArray()).Code);
            Assert.NotEqual(0, Run(args.Concat(new[] { "--assets", "external" }).ToArray()).Code);
            Assert.NotEqual(0, Run(args.Concat(new[] { "--memory-backend", "Managed" }).ToArray()).Code);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".json");
        }
    }

    [Fact]
    public void CliPackagesNeDrivenStandalonePeAndRequiresIndependentExpectedResults()
    {
        string input = Path.Combine(Path.GetTempPath(), $"ne-pe-cli-{Guid.NewGuid():N}.exe");
        string state = input + ".state.json", output = input + ".standalone.exe";
        try
        {
            File.WriteAllBytes(input, NeDiscoveryTests.Fixture(0xb8, 7, 0, 0xcb));
            File.WriteAllText(state, """{"Arguments":[],"ExpectedRegisters":{"AX":7}}""");
            Assert.NotEqual(0, Run("translate", "--input", input, "--root", "1:0", "--pe", "--output", output).Code);
            var result = Run("translate", "--input", input, "--root", "1:0", "--pe", "--state", state, "--output", output);
            Assert.True(result.Code == 0, result.Output);
            Assert.True(Pe32PlusWriter.IsAmd64Pe32Plus(File.ReadAllBytes(output)));
            Assert.Equal(0, Run("validate", "--input", output).Code);
            string manifest = File.ReadAllText(output + ".json");
            Assert.Contains("PeSha256", manifest);
            Assert.Contains("NativeHelperSha256", manifest);
            Assert.Contains("HeapCreate", manifest);
            Assert.Contains("OriginalOffset", manifest);
            byte[] pe = File.ReadAllBytes(output);
            Assert.Equal(0, Run("translate", "--input", input, "--root", "1:0", "--pe", "--state", state, "--output", output).Code);
            Assert.Equal(pe, File.ReadAllBytes(output));
            Assert.Equal(manifest, File.ReadAllText(output + ".json"));
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(0, NeStandaloneTests.Execute(pe));
            }
        }
        finally
        {
            File.Delete(input);
            File.Delete(state);
            File.Delete(output);
            File.Delete(output + ".json");
        }
    }

    [Fact]
    public void CliInspectsExtractsResourcesAndReportsNativeInitialization()
    {
        string input = Path.Combine(Path.GetTempPath(), $"resource-cli-{Guid.NewGuid():N}.exe");
        string output = input + ".raw";
        try
        {
            File.WriteAllBytes(input, NeParserTests.Fixture());
            var inspect = Run("inspect", "--input", input, "--resources");
            Assert.Equal(1, inspect.Code);
            Assert.Contains("Invalid", inspect.Output);
            var extract = Run("extract-resource", "--input", input, "--resource-index", "1", "--output", output);
            Assert.True(extract.Code == 0, extract.Output);
            Assert.Equal(NeParserTests.Fixture().AsSpan(0x300, 16).ToArray(), File.ReadAllBytes(output));
            Assert.Contains("ExecutableSha256", File.ReadAllText(output + ".json"));
            Assert.Contains("PayloadSha256", File.ReadAllText(output + ".json"));
            Assert.NotEqual(0, Run("extract-resource", "--input", input, "--resource-index", "2", "--output", output).Code);
            File.WriteAllBytes(input, NeDiscoveryTests.Fixture(0xcb));
            foreach (string backend in new[]
            {
                "Native",
                "Managed"
            }

            )
            {
                var initialized = Run("inspect", "--input", input, "--load", "--initialization", "--memory-backend", backend);
                Assert.True(initialized.Code == 0, initialized.Output);
                Assert.Contains("MachineStateAbiSize", initialized.Output);
                Assert.Contains($"\"Backend\": \"{backend}\"", initialized.Output);
            }

            Assert.NotEqual(0, Run("inspect", "--input", input, "--load", "--memory-backend", "invalid").Code);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
            File.Delete(output + ".json");
        }
    }

    [Fact]
    public void CliInspectsNeDeterministicallyAndRejectsTruncation()
    {
        string input = Path.Combine(Path.GetTempPath(), $"ne-cli-{Guid.NewGuid():N}.exe");
        string output = input + ".json";
        try
        {
            File.WriteAllBytes(input, NeParserTests.Fixture());
            var first = Run("inspect", "--input", input);
            Assert.True(first.Code == 0, first.Output);
            Assert.Contains("KERNEL", first.Output);
            Assert.Contains("Movable", first.Output);
            Assert.Equal(first.Output, Run("inspect", "--input", input).Output);
            Assert.Equal(0, Run("inspect", "--input", input, "--output", output).Code);
            Assert.Equal(first.Output.TrimEnd(), File.ReadAllText(output));
            File.WriteAllBytes(input, new byte[] { 77, 90 });
            var invalid = Run("inspect", "--input", input);
            Assert.NotEqual(0, invalid.Code);
            Assert.Contains("NE format at file", invalid.Output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void CliLoadsNeWithExplicitBindingsAndReportsIncompleteInspection()
    {
        string input = Path.Combine(Path.GetTempPath(), $"ne-load-{Guid.NewGuid():N}.exe");
        string bindings = input + ".json";
        try
        {
            File.WriteAllBytes(input, NeLoaderTests.Fixture((3, 1, 4, 1, 77)));
            var strict = Run("inspect", "--input", input, "--load");
            Assert.NotEqual(0, strict.Code);
            Assert.Contains("Unresolved import KERNEL.#77", strict.Output);
            var partial = Run("inspect", "--input", input, "--load", "--allow-incomplete");
            Assert.Equal(0, partial.Code);
            Assert.Contains("\"FullyRelocated\": false", partial.Output);
            File.WriteAllText(bindings, """[{"Module":"KERNEL","Ordinal":77,"Selector":28672,"Offset":4660}]""");
            var complete = Run("inspect", "--input", input, "--load", "--import-bindings", bindings);
            Assert.True(complete.Code == 0, complete.Output);
            Assert.Contains("\"FullyRelocated\": true", complete.Output);
            Assert.Contains("34120070", complete.Output);
            Assert.Equal(complete.Output, Run("inspect", "--input", input, "--load", "--import-bindings", bindings).Output);
            File.WriteAllText(bindings, "[] invalid");
            Assert.Contains("Invalid import binding JSON", Run("inspect", "--input", input, "--load", "--import-bindings", bindings).Output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(bindings);
        }
    }

    [Fact]
    public void CliDiscoversAndAutomaticallyLoadsSupportedNeImports()
    {
        string input = Path.Combine(Path.GetTempPath(), $"ne-import-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(input, NeImportTests.MemoryImportDataFixture());
            var imports = Run("inspect", "--input", input, "--imports");
            Assert.True(imports.Code == 0, imports.Output);
            Assert.Contains("KERNEL.GlobalAlloc", imports.Output);
            Assert.Contains("SupportedRuntimeService", imports.Output);
            Assert.Equal(imports.Output, Run("inspect", "--input", input, "--imports").Output);
            var loaded = Run("inspect", "--input", input, "--load");
            Assert.True(loaded.Code == 0, loaded.Output);
            Assert.Contains("\"FullyRelocated\": true", loaded.Output);
            Assert.Contains("\"BindingStatus\": \"Bound\"", loaded.Output);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public void CliExportsBinaryClosuresAndReturnsFailureForUnresolvedTargets()
    {
        string input = Path.Combine(Path.GetTempPath(), $"ne-graph-{Guid.NewGuid():N}.exe");
        string output = input + ".json";
        try
        {
            File.WriteAllBytes(input, NeDiscoveryTests.Fixture(0xcb));
            var complete = Run("analyze", "--input", input, "--root", "1:0", "--output", output);
            Assert.True(complete.Code == 0, complete.Output);
            Assert.Contains("\"ClosureComplete\": true", File.ReadAllText(output));
            string first = File.ReadAllText(output);
            Assert.Equal(0, Run("analyze", "--input", input, "--root", "1:0", "--output", output).Code);
            Assert.Equal(first, File.ReadAllText(output));
            File.WriteAllBytes(input, NeDiscoveryTests.Fixture(0xff, 0xd0, 0xcb));
            var incomplete = Run("analyze", "--input", input, "--root", "1:0");
            Assert.Equal(1, incomplete.Code);
            Assert.Contains("UnresolvedDynamicTarget", incomplete.Output);
            var unknown = Run("analyze", "--input", input, "--root", "NoVerifiedSymbol");
            Assert.NotEqual(0, unknown.Code);
            Assert.Contains("verified matches", unknown.Output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void CliLowersComparesLinksAndExecutesBinaryFunctionsWithReviewableInputs()
    {
        string input = Path.Combine(Path.GetTempPath(), $"ne-binary-{Guid.NewGuid():N}.exe");
        string assembly = input + ".asm", state = input + ".state.json", output = input + ".bin";
        try
        {
            byte[] code = Convert.FromHexString("8CD89045558BEC1E8ED8B807008D66FE1F5D4DCB");
            File.WriteAllBytes(input, NeDiscoveryTests.Fixture(code));
            File.WriteAllText(assembly, "func sub_0001_0000\nmov ax,7\nendfunc");
            File.WriteAllText(state, """{"Arguments":[],"ExpectedRegisters":{"AX":7}}""");
            var ir = Run("dump-ir", "--input", input, "--root", "1:0", "--compare-assembly", assembly);
            Assert.True(ir.Code == 0, ir.Output);
            Assert.Contains("BinarySource", ir.Output);
            Assert.Contains("8CD8", ir.Output);
            var compile = Run("translate", "--input", input, "--root", "1:0", "--compare-assembly", assembly, "--output", output);
            Assert.True(compile.Code == 0, compile.Output);
            Assert.True(new FileInfo(output).Length > 0);
            string manifest = File.ReadAllText(output + ".json");
            Assert.Contains("\"Equivalent\": true", manifest);
            Assert.Contains("NativeCompiled", manifest);
            var execute = Run("test", "--input", input, "--root", "1:0", "--state", state);
            Assert.True(execute.Code == 0, execute.Output);
            Assert.Contains("\"Passed\": true", execute.Output);
            File.WriteAllText(state, """{"Arguments":[],"ExpectedRegisters":{"AX":8}}""");
            var wrong = Run("test", "--input", input, "--root", "1:0", "--state", state);
            Assert.Equal(1, wrong.Code);
            Assert.Contains("\"Passed\": false", wrong.Output);
            File.Delete(output);
            File.WriteAllText(assembly, "func sub_0001_0000\nmov ax,8\nendfunc");
            var mismatch = Run("translate", "--input", input, "--root", "1:0", "--compare-assembly", assembly, "--output", output);
            Assert.NotEqual(0, mismatch.Code);
            Assert.Contains("Reconstruction/binary mismatch", mismatch.Output);
            Assert.False(File.Exists(output));
        }
        finally
        {
            foreach (string file in new[]
            {
                input,
                assembly,
                state,
                output,
                output + ".json"
            }

            )
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void CliDumpsTranslatesAndBehaviorallyChecksSyntheticTurnLeft()
    {
        string sample = Path.GetFullPath("../../../../../tests/Fixtures/TurnLeft/TurnLeft.asm");
        string input = Path.Combine(Path.GetTempPath(), $"synthetic-turn-{Guid.NewGuid():N}.asm");
        string output = input + ".bin";
        try
        {
            string text = File.ReadAllText(sample);
            File.WriteAllText(input, text);
            var dump = Run("dump-ir", "--input", input);
            Assert.True(dump.Code == 0, dump.Output);
            Assert.Contains("CMP", dump.Output);
            var valid = Run("test", "--input", input);
            Assert.True(valid.Code == 0, valid.Output);
            // The source sequence is translated as supplied; the behavioral oracle
            // must catch an incorrect result even when its instructions are supported.
            File.WriteAllText(input, text.Replace("mov word [bx],-1", "mov word [bx],-2"));
            var translated = Run("translate", "--input", input, "--output", output);
            Assert.True(translated.Code == 0, translated.Output);
            var wrong = Run("test", "--input", input);
            Assert.Equal(1, wrong.Code);
            Assert.Contains("Differential mismatch", wrong.Output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void CliBuildsAndValidatesTheAcceptanceExecutable()
    {
        string input = Path.GetFullPath("../../../../../tests/Fixtures/Compiler/Acceptance.asm");
        string output = Path.Combine(Path.GetTempPath(), $"compiler-cli-{Guid.NewGuid():N}.exe");
        try
        {
            var result = Run("translate", "--input", input, "--acceptance", "--pe", "--output", output);
            Assert.True(result.Code == 0, result.Output);
            Assert.True(Pe32PlusWriter.IsAmd64Pe32Plus(File.ReadAllBytes(output)));
            var validate = Run("validate", "--input", output);
            Assert.True(validate.Code == 0, validate.Output);
            var execute = Run("test", "--input", input, "--acceptance");
            Assert.True(execute.Code == 0, execute.Output);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [Theory]
    [InlineData("FindMonster")]
    [InlineData("GrowArray")]
    public void CliSelectsAndEmitsRelocatableTargetsWithAnAbiManifest(string target)
    {
        string input = Path.GetFullPath("../../../../../tests/Fixtures/Compiler/Acceptance.asm");
        string output = Path.Combine(Path.GetTempPath(), $"target-{Guid.NewGuid():N}.bin");
        try
        {
            var result = Run("translate", "--input", input, "--function", target, "--output", output);
            Assert.True(result.Code == 0, result.Output);
            Assert.True(new FileInfo(output).Length > 0);
            string manifest = File.ReadAllText(output + ".json");
            Assert.Contains("Relocations", manifest);
            Assert.Contains("__win16_read_word", manifest);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }

            if (File.Exists(output + ".json"))
            {
                File.Delete(output + ".json");
            }
        }
    }

    [Theory]
    [InlineData("IsPointVisible")]
    [InlineData("InvertViewportTile")]
    public void CliTranslatesSyntheticGraphicsInstructionTargets(string target)
    {
        string input = Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Ui.asm");
        string output = Path.Combine(Path.GetTempPath(), $"graphics-{Guid.NewGuid():N}.bin");
        try
        {
            var result = Run("translate", "--input", input, "--function", target, "--output", output);
            Assert.True(result.Code == 0, result.Output);
            Assert.True(new FileInfo(output).Length > 0);
            string manifest = File.ReadAllText(output + ".json");
            Assert.Contains("Relocations", manifest);
            if (target == "InvertViewportTile")
            {
                Assert.Contains("__win16_call_target", manifest);
            }

            var inspect = Run("inspect", "--input", input, "--function", target);
            Assert.True(inspect.Code == 0, inspect.Output);
            Assert.Contains("Runtime helpers", inspect.Output);
            if (target == "InvertViewportTile")
            {
                Assert.Contains("configured translated procedure 4000:0100", inspect.Output);
            }
        }
        finally
        {
            File.Delete(output);
            File.Delete(output + ".json");
        }
    }

    [Fact]
    public void CliEmitsTheCompleteSyntheticCallbackBodyAndDescribesPartialCoverage()
    {
        string input = Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Ui.asm"), output = Path.Combine(Path.GetTempPath(), $"callback-{Guid.NewGuid():N}.bin");
        try
        {
            var inspect = Run("inspect", "--input", input, "--function", "SAMPLEWNDPROC");
            Assert.True(inspect.Code == 0, inspect.Output);
            Assert.Contains("Callback reference: 4000:0300", inspect.Output);
            Assert.Contains("non-paint integration only", inspect.Output);
            var translated = Run("translate", "--input", input, "--function", "SAMPLEWNDPROC", "--output", output);
            Assert.True(translated.Code == 0, translated.Output);
            Assert.True(new FileInfo(output).Length > 0);
            Assert.Contains("__win16_divide", File.ReadAllText(output + ".json"));
            if (!OperatingSystem.IsWindows())
            {
                string fixture = Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Callbacks.asm");
                var run = Run("test", "--input", fixture, "--callbacks", "--callback-input", input);
                Assert.Equal(0, run.Code);
                Assert.Contains("SKIPPED", run.Output);
            }
        }
        finally
        {
            File.Delete(output);
            File.Delete(output + ".json");
        }
    }

    [Fact]
    public void CliPackagesTheCompleteNativeWindowsCallbackAndReportsPlatformSkips()
    {
        string baseline = Path.GetFullPath("../../../../../tests/Fixtures/Compiler/Acceptance.asm"), ui = Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Ui.asm"), fixtures = Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Callbacks.asm");
        string output = Path.Combine(Path.GetTempPath(), $"windows_callback-cli-{Guid.NewGuid():N}.exe");
        try
        {
            var result = Run("translate", "--input", baseline, "--acceptance", "--graphics-input", ui, "--callbacks", "--callback-fixtures", fixtures, "--pe", "--output", output);
            Assert.True(result.Code == 0, result.Output);
            Assert.True(Pe32PlusWriter.IsAmd64Pe32Plus(File.ReadAllBytes(output)));
            Assert.Contains("Windows API/graphics/callback", result.Output);
            var test = Run("test", "--input", baseline, "--acceptance", "--graphics-input", ui, "--callbacks", "--callback-fixtures", fixtures);
            Assert.True(test.Code == 0, test.Output);
            Assert.Contains(OperatingSystem.IsWindows()
                ? "exit status: 0"
                : "SKIPPED", test.Output);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
