using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeLevelInitializationTests
{
    private static string DataPath => Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"))!, "CHIPS.DAT");

    [SuppliedNeFact]
    public void OriginalLoaderDecodesAuthenticLessonOneAndInitializesPlayerAndMetadata()
    {
        var state = NeLevelInitialization.Load(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!), File.ReadAllBytes(DataPath));
        Assert.Equal("57fde52f2e19823f7c6fea53b2e19827646f96352b9810434aa7408e55629b31", state.BoardSha256);
        Assert.Equal(15, state.PlayerX);
        Assert.Equal(14, state.PlayerY);
        Assert.Equal(100, state.TimeLimit);
        Assert.Equal(11, state.Chips);
        Assert.All(state.Inventory, b => Assert.Equal(0, b));
        Assert.Equal("LESSON 1", state.Title);
        Assert.Equal("BDHP", state.Password);
        Assert.Equal(1, BitConverter.ToUInt16(state.State, 0x800));
        Assert.Equal(149, BitConverter.ToUInt16(state.State, 0x802));
        foreach (int offset in new[]
        {
            0x81c,
            0x91e,
            0x928,
            0x932,
            0x93c,
            0x946,
            0x950,
            0x816,
            0xa36
        }

        )
        {
            Assert.Equal(0, BitConverter.ToUInt16(state.State, offset));
        }

        Assert.Equal(11, state.State.Take(1024).Count(t => t == 2));
        Assert.Equal(0x6e, state.State[14 * 32 + 15]);
        Assert.All(state.State.Skip(1024).Take(1024), t => Assert.Equal(0, t));
        var repeat = NeLevelInitialization.Load(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!), File.ReadAllBytes(DataPath));
        Assert.Equal(state.State, repeat.State);
        byte[] changed = File.ReadAllBytes(DataPath);
        changed[20] ^= 1;
        Assert.Throws<InvalidDataException>(() => NeLevelInitialization.ValidateData(changed));
    }
}

public sealed class NeAuthenticLevelGuiTests
{
    [WindowsSuppliedNeFact]
    public void NativeGuiLoadsOriginalLevelPaintsIndependentPixelsAndRejectsChangedAssets()
    {
        string original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        string data = Path.Combine(Path.GetDirectoryName(original)!, "CHIPS.DAT");
        var program = NeApplicationProgram.Build(File.ReadAllBytes(original), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true, AuthenticLevel: true), levelData: File.ReadAllBytes(data));
        string root = Path.Combine(Path.GetTempPath(), "level-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string exe = Path.Combine(root, "OriginalLevel64.exe");
            File.WriteAllBytes(exe, program.Executable);
            string lastLog = "";

            int Run(string path)
            {
                var info = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = false
                };
                string diagnostic = Path.Combine(root, Guid.NewGuid().ToString("N") + ".txt");
                foreach (string arg in new[]
                {
                    "--original",
                    original,
                    "--levels",
                    path,
                    "--lifecycle-test",
                    "--diagnostics",
                    diagnostic
                }

                )
                {
                    info.ArgumentList.Add(arg);
                }

                using var process = System.Diagnostics.Process.Start(info)!;
                if (!process.WaitForExit(30000))
                {
                    process.Kill(true);
                    Assert.Fail("Original level GUI timed out.");
                }

                lastLog = File.ReadAllText(diagnostic);
                return process.ExitCode;
            }


            int code = Run(data);
            Assert.True(code == 0, lastLog);
            Assert.Equal(84, Run(Path.Combine(root, "missing.dat")));
            byte[] changed = File.ReadAllBytes(data);
            changed[20] ^= 1;
            string bad = Path.Combine(root, "changed.dat");
            File.WriteAllBytes(bad, changed);
            Assert.Equal(85, Run(bad));
            foreach (string hash in new[]
            {
                "57fde52f2e19823f7c6fea53b2e19827646f96352b9810434aa7408e55629b31",
                program.Manifest.Scene!.Pixels[0].Sha256
            }

            )
            {
                byte[] damaged = program.Executable.ToArray(), pattern = Convert.FromHexString(hash);
                int at = Enumerable.Range(0, damaged.Length - pattern.Length + 1).First(i => damaged.AsSpan(i, pattern.Length).SequenceEqual(pattern));
                damaged[at] ^= 1;
                File.WriteAllBytes(exe, damaged);
                Assert.Equal(40, Run(data));
            }

            File.WriteAllBytes(exe, program.Executable);
            var repeat = NeApplicationProgram.Build(File.ReadAllBytes(original), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true, AuthenticLevel: true), levelData: File.ReadAllBytes(data));
            Assert.Equal(program.Executable, repeat.Executable);
            Assert.DoesNotContain(program.Manifest.Scene!.Writes, w => w.Offset == 0x4000);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
