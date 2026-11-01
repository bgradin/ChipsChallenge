using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

[Collection("Native modal desktop")]
public sealed class NePlayableDistributionTests
{
    private static string Original => Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;

    internal static NeApplicationProgram Build() => NeApplicationProgram.Build(File.ReadAllBytes(Original), new("0002:296A", new ushort[5], 0, Gameplay: true), levelData: File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Original)!, "CHIPS.DAT")));

    static string DirectoryPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "playable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [SuppliedNeFact]
    public void GameplayExportProducesDeterministicClrFreePackageWithTwoOriginalLaunchProfiles()
    {
        string root = DirectoryPath();
        try
        {
            string first = Path.Combine(root, "first"), second = Path.Combine(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            foreach (string dir in new[]
            {
                first,
                second
            }

            )
            {
                var result = CliIntegrationTests.Run("translate", "--input", Original, "--application", "--gameplay", "--assets", "external", "--output", Path.Combine(dir, "Game.exe"));
                Assert.True(result.Code == 0, result.Output);
            }

            Assert.Equal(6, Directory.GetFiles(first).Length);
            foreach (string file in Directory.GetFiles(first))
            {
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(second, Path.GetFileName(file))));
            }

            using var pe = new PEReader(File.OpenRead(Path.Combine(first, "Game.exe")));
            Assert.Null(pe.PEHeaders.CorHeader);
            Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
            Assert.Equal(PEMagic.PE32Plus, pe.PEHeaders.PEHeader!.Magic);
            Assert.Equal(Subsystem.WindowsGui, pe.PEHeaders.PEHeader.Subsystem);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "Game.exe.json")));
            var game = manifest.RootElement.GetProperty("Gameplay");
            Assert.Equal(new[] { 1, 2 }, game.GetProperty("SupportedInitialLevels").EnumerateArray().Select(e => e.GetInt32()));
            Assert.Equal(99, game.GetProperty("Profiles")[0].GetProperty("AcceptanceCases").GetInt32());
            Assert.Equal(21, game.GetProperty("Profiles")[1].GetProperty("AcceptanceCases").GetInt32());
            Assert.Equal(80, manifest.RootElement.GetProperty("Translation").GetProperty("Functions").GetArrayLength());
            Assert.Equal(25, manifest.RootElement.GetProperty("Clock").GetProperty("Snapshot").GetArrayLength());
            Assert.DoesNotContain("until 6.8", manifest.RootElement.GetProperty("Hazards").GetProperty("EndGameAudit").GetString()!);
            using var matrix = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "Game.exe.tiles.json")));
            Assert.True(JsonElement.DeepEquals(manifest.RootElement.GetProperty("Tiles"), matrix.RootElement));
            var tiles = matrix.RootElement.GetProperty("Tiles");
            foreach (int id in new[]
            {
                3,
                4
            }

            )
            {
                Assert.Equal("OriginalDeathAndRestart", tiles[id].GetProperty("TranslationStatus").GetString());
                Assert.Contains("ShowDeathMessage 0002:0B9A", Strings(tiles[id], "RuntimeDependencies"));
                Assert.Contains("LoadAndStartLevel 0004:0356", Strings(tiles[id], "RuntimeDependencies"));
                Assert.Contains("HaveBootsForTile 0003:187C", Strings(tiles[id], "RuntimeDependencies"));
                Assert.Contains("NeTileInteractionTests.OriginalTilesChangeBoardInventoryCountersBootEffectsAndHintPixels", Strings(tiles[id], "Tests"));
            }

            foreach (int id in new[]
            {
                64,
                65,
                66,
                67
            }

            )
            {
                Assert.Equal("OriginalBugSubset", tiles[id].GetProperty("TranslationStatus").GetString());
                Assert.Contains("NeHazardRestartTests.OriginalBugsWaterFireTimeoutDeathsRestartWithOwnedListsAndPixels", Strings(tiles[id], "Tests"));
                Assert.Contains("MonsterLoop 0003:074E", Strings(tiles[id], "RuntimeDependencies"));
                Assert.Contains("MoveMonster 0007:18DA", Strings(tiles[id], "RuntimeDependencies"));
            }

            Assert.Contains("AuthenticLesson1Step003_U_13_13", Strings(tiles[100], "Tests"));
            Assert.Contains("AuthenticLesson1Step005_L_12_12", Strings(tiles[22], "Tests"));
            Assert.Contains("EndLevel 0007:0CCA", Strings(tiles[21], "RuntimeDependencies"));
            Assert.Equal("Uncertified", tiles[10].GetProperty("TranslationStatus").GetString());
            Assert.Empty(Strings(tiles[10], "Tests"));
            var caseNames = manifest.RootElement.GetProperty("Input").GetProperty("Cases").EnumerateArray().Select(c => c.GetProperty("Name").GetString()).ToHashSet();
            foreach (var tile in tiles.EnumerateArray())
            {
                var tests = Strings(tile, "Tests");
                if (tile.GetProperty("TranslationStatus").GetString() != "Uncertified")
                {
                    Assert.NotEmpty(tests);
                }

                foreach (string reference in tests)
                {
                    if (caseNames.Contains(reference))
                    {
                        continue;
                    }

                    string[] parts = reference.Split('.');
                    Assert.Equal(2, parts.Length);
                    var type = typeof(NePlayableDistributionTests).Assembly.GetType("Translator.Integration.Tests." + parts[0]);
                    Assert.NotNull(type);
                    Assert.NotNull(type.GetMethod(parts[1]));
                }
            }

            using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "Game.exe.package.json")));
            Assert.False(package.RootElement.GetProperty("RuntimeRequiresClr").GetBoolean());
            Assert.False(package.RootElement.GetProperty("IncludesOriginalAssets").GetBoolean());
            Assert.False(package.RootElement.GetProperty("RuntimeRequiresSidecars").GetBoolean());
            foreach (var file in package.RootElement.GetProperty("Files").EnumerateArray())
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(first, file.GetProperty("Name").GetString()!));
                Assert.Equal(file.GetProperty("Sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }

            string readme = File.ReadAllText(Path.Combine(first, "Game.exe.README.txt"));
            Assert.Contains("--level 2", readme);
            Assert.Contains("CHIPS.DAT", readme);
            Assert.Contains("process-local", readme);
            Assert.DoesNotContain("THE PLAYER DOES NOT MOVE", readme);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string[] Strings(JsonElement row, string property) => row.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    [SuppliedNeFact]
    public void GameplayCliRejectsConflictingModesAndMissingUnpinnedLevelData()
    {
        string dir = DirectoryPath();
        try
        {
            string exe = Path.Combine(dir, "Game.exe");
            foreach (var options in new[]
            {
                new[]
                {
                    "--gameplay"
                },
                new[]
                {
                    "--application",
                    "--gameplay",
                    "--startup",
                    "completion-check",
                    "--assets",
                    "external"
                },
                new[]
                {
                    "--application",
                    "--gameplay"
                }
            }

            )
            {
                var result = CliIntegrationTests.Run(new[] { "translate", "--input", Original, "--output", exe }.Concat(options).ToArray());
                Assert.NotEqual(0, result.Code);
                Assert.False(File.Exists(exe));
            }

            string assembly = Path.Combine(dir, "source.asm");
            File.WriteAllText(assembly, "func_nods F\nmov ax,1\nendfunc\n");
            var wrongSource = CliIntegrationTests.Run("translate", "--input", assembly, "--function", "F", "--application", "--gameplay", "--assets", "external", "--output", exe);
            Assert.NotEqual(0, wrongSource.Code);
            Assert.False(File.Exists(exe));
            string source = Path.Combine(dir, "CHIPS.EXE");
            File.Copy(Original, source);
            var missing = CliIntegrationTests.Run("translate", "--input", source, "--application", "--gameplay", "--assets", "external", "--output", exe);
            Assert.NotEqual(0, missing.Code);
            Assert.False(File.Exists(exe));
            string data = Path.Combine(dir, "CHIPS.DAT");
            File.WriteAllBytes(data, [1, 2, 3]);
            var changed = CliIntegrationTests.Run("translate", "--input", source, "--application", "--gameplay", "--assets", "external", "--output", exe);
            Assert.NotEqual(0, changed.Code);
            Assert.False(File.Exists(exe));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [WindowsSuppliedNeFact]
    public void IsolatedPeUsesExternalAdjacentAssetsAndVerifiesBothSelectedLevels()
    {
        var p = Build();
        string dir = DirectoryPath();
        try
        {
            string assets = Path.Combine(dir, "assets ü"), binary = Path.Combine(dir, "binary only");
            Directory.CreateDirectory(assets);
            Directory.CreateDirectory(binary);
            string original = Path.Combine(assets, "CHIPS.EXE"), data = Path.Combine(assets, "CHIPS.DAT");
            File.Copy(Original, original);
            File.Copy(Path.Combine(Path.GetDirectoryName(Original)!, "CHIPS.DAT"), data);
            byte[] beforeExe = SHA256.HashData(File.ReadAllBytes(original)), beforeDat = SHA256.HashData(File.ReadAllBytes(data));
            string exe = Path.Combine(binary, "Game.exe");
            File.WriteAllBytes(exe, p.Executable);
            Assert.Single(Directory.GetFiles(binary));
            var first = Run(exe, dir, original);
            Assert.True(first.Exit == 0, first.Log);
            Assert.Contains("ClockCase: 99", first.Log);
            Assert.Contains("CompletionDialogs: 1", first.Log);
            Assert.Contains("ScoreLow: 1310", first.Log);
            Assert.Contains("Level: 2", first.Log);
            Assert.Contains("PlayerY: 11", first.Log);
            string renamed = Path.Combine(assets, "level data ü.dat");
            File.Move(data, renamed);
            Assert.Equal(84, Run(exe, dir, original).Exit);
            var second = Run(exe, dir, original, "--level", "2", "--levels", renamed);
            Assert.True(second.Exit == 0, second.Log);
            Assert.Contains("ClockCase: 21", second.Log);
            Assert.Contains("DeathDialogs: 5", second.Log);
            Assert.Contains("Restarts: 7", second.Log);
            Assert.Contains("CompletionDialogs: 0", second.Log);
            Assert.Contains("ListCleanup: 1", second.Log);
            Assert.Equal(beforeExe, SHA256.HashData(File.ReadAllBytes(original)));
            Assert.Equal(beforeDat, SHA256.HashData(File.ReadAllBytes(renamed)));
            Assert.Single(Directory.GetFiles(binary));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [WindowsSuppliedNeFact]
    public void GameplayRejectsBadAssetsArgumentsAndMalformedProfileWithoutLeaking()
    {
        var p = Build();
        string dir = DirectoryPath();
        try
        {
            string exe = Path.Combine(dir, "Game.exe");
            File.WriteAllBytes(exe, p.Executable);
            foreach (var options in new[]
            {
                new[]
                {
                    "--level",
                    "0"
                },
                new[]
                {
                    "--level",
                    "3"
                },
                new[]
                {
                    "--level",
                    "-1"
                },
                new[]
                {
                    "--level",
                    "one"
                },
                new[]
                {
                    "--level",
                    "1",
                    "--level",
                    "2"
                },
                new[]
                {
                    "--unknown"
                },
                new[]
                {
                    "--level"
                }
            }

            )
            {
                Assert.Equal(70, Run(exe, dir, Original, options).Exit);
            }

            Assert.Equal(80, Run(exe, dir, null).Exit);
            Assert.Equal(84, Run(exe, dir, Original, "--levels", Path.Combine(dir, "missing.dat")).Exit);
            byte[] changed = File.ReadAllBytes(Original);
            changed[^1] ^= 1;
            string badExe = Path.Combine(dir, "changed.exe");
            File.WriteAllBytes(badExe, changed);
            Assert.Equal(81, Run(exe, dir, badExe).Exit);
            string data = Path.Combine(dir, "changed.dat");
            File.WriteAllBytes(data, [0]);
            Assert.Equal(85, Run(exe, dir, Original, "--levels", data).Exit);
            byte[] malformed = p.Executable.ToArray();
            int at = malformed.AsSpan().IndexOf(p.Descriptor);
            Assert.True(at >= 0);
            malformed.AsSpan(at + 16, 4).Fill(0xff);
            File.WriteAllBytes(exe, malformed);
            var invalid = Run(exe, dir, Original);
            Assert.Equal(10, invalid.Exit);
            Assert.Contains("CleanupCode: 0", invalid.Log);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [WindowsSuppliedNeFact]
    public void PlayablePackageShowsCompletionAndContinuesUsingOriginalGameplay() => NeCompletionProgressionTests.VerifyLiveProgression(Build());
    static (int Exit, string Log) Run(string exe, string dir, string? original, params string[] options)
    {
        string log = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".txt");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        start.Environment["DOTNET_ROOT"] = Path.Combine(dir, "absent-runtime");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(dir, "absent-runtime");
        foreach (string arg in new[]
        {
            "--diagnostics",
            log,
            "--lifecycle-test"
        }

        )
        {
            start.ArgumentList.Add(arg);
        }

        if (original is not null)
        {
            start.ArgumentList.Add("--original");
            start.ArgumentList.Add(original);
        }

        foreach (string arg in options)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        if (!process.WaitForExit(30000))
        {
            process.Kill(true);
            Assert.Fail("Standalone gameplay timed out.");
        }

        return (process.ExitCode, File.Exists(log)
            ? File.ReadAllText(log)
            : "Missing native diagnostic");
    }
}
