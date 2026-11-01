using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeDistributionTests
{
    private static string Original => Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;

    private static NeApplicationProgram Build() => NeApplicationProgram.Build(File.ReadAllBytes(Original), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true, KeyboardInput: true, StandaloneDistribution: true));

    private static string DirectoryPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "distribution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [SuppliedNeFact]
    public void DefaultApplicationExportHasDeterministicAssetFreeDistributionSidecars()
    {
        string root = DirectoryPath();
        try
        {
            string first = Path.Combine(root, "one"), second = Path.Combine(root, "two");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            foreach (string directory in new[]
            {
                first,
                second
            }

            )
            {
                var result = CliIntegrationTests.Run("translate", "--input", Original, "--application", "--assets", "external", "--output", Path.Combine(directory, "ChipChallenge64.exe"));
                Assert.True(result.Code == 0, result.Output);
            }

            Assert.Equal(5, Directory.GetFiles(first).Length);
            foreach (string file in Directory.GetFiles(first))
            {
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(second, Path.GetFileName(file))));
            }

            using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "ChipChallenge64.exe.package.json")));
            Assert.False(package.RootElement.GetProperty("IncludesOriginalAssets").GetBoolean());
            Assert.False(package.RootElement.GetProperty("RuntimeRequiresClr").GetBoolean());
            foreach (var file in package.RootElement.GetProperty("Files").EnumerateArray())
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(first, file.GetProperty("Name").GetString()!));
                Assert.Equal(file.GetProperty("Sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }

            string readme = File.ReadAllText(Path.Combine(first, "ChipChallenge64.exe.README.txt"));
            Assert.Contains("THE PLAYER DOES NOT MOVE", readme);
            Assert.Contains("--diagnostics", readme);
        }
        finally
        {
            NeApplicationTests.DeleteProcessDirectory(root);
        }
    }

    [WindowsSuppliedNeFact]
    public void NativeDiagnosticFilesExplainSuccessMissingAssetsIdentityAndRuntimeFailures()
    {
        var program = Build();
        string root = DirectoryPath();
        try
        {
            int Run(byte[] pe, string? original, string name, params string[] extra)
            {
                string log = Path.Combine(root, name);
                var args = new[]
                {
                    "--diagnostics",
                    log
                }.Concat(extra).ToArray();
                return NeApplicationTests.Execute(pe, originalPath: original, additionalArguments: args);
            }

            Assert.Equal(0, Run(program.Executable, Original, "success ü.log"));
            Assert.Contains("Status: success", File.ReadAllText(Path.Combine(root, "success ü.log")));
            Assert.Equal(80, Run(program.Executable, null, "missing.log"));
            Assert.Contains("Original executable missing", File.ReadAllText(Path.Combine(root, "missing.log")));
            string changed = Path.Combine(root, "changed.exe");
            byte[] source = File.ReadAllBytes(Original);
            source[^1] ^= 1;
            File.WriteAllBytes(changed, source);
            Assert.Equal(81, Run(program.Executable, changed, "identity.log"));
            Assert.Contains("identity mismatch", File.ReadAllText(Path.Combine(root, "identity.log")));
            Assert.Equal(70, Run(program.Executable, Original, "arguments.log", "--unknown"));
            Assert.Contains("Stage: arguments", File.ReadAllText(Path.Combine(root, "arguments.log")));
            byte[] pe = program.Executable.ToArray();
            int image = pe.AsSpan().IndexOf(program.Descriptor);
            Assert.True(image >= 0);
            byte[] state = program.Manifest.Scene!.Writes.Single(w => w.Offset == 0x4000).Bytes;
            int fixture = program.Descriptor.AsSpan().IndexOf(state);
            Assert.True(fixture >= 0);
            pe[image + fixture + 0xa36] = 1;
            Assert.Equal(30, Run(pe, Original, "runtime.log"));
            string runtime = File.ReadAllText(Path.Combine(root, "runtime.log"));
            Assert.Contains("RuntimeError: 8", runtime);
            Assert.Contains("fatal boundary", runtime);
            Assert.Contains("CleanupCode: 0", runtime);
            string existing = Path.Combine(root, "existing.log");
            File.WriteAllText(existing, "preserve me");
            Assert.Equal(83, Run(program.Executable, Original, "existing.log"));
            Assert.Equal("preserve me", File.ReadAllText(existing));
        }
        finally
        {
            NeApplicationTests.DeleteProcessDirectory(root);
        }
    }

    [WindowsSuppliedNeFact]
    public void MissingAssetsShowAnOwnedNativeDialogWithoutAutomation()
    {
        string root = DirectoryPath();
        Process? process = null;
        try
        {
            string pe = Path.Combine(root, "standalone.exe");
            File.WriteAllBytes(pe, Build().Executable);
            process = Process.Start(new ProcessStartInfo(pe) { UseShellExecute = false, WorkingDirectory = root })!;
            IntPtr dialog = IntPtr.Zero;
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 7000 && !process.HasExited && dialog == IntPtr.Zero)
            {
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out uint id);
                    var text = new StringBuilder(128);
                    GetClassNameW(hwnd, text, text.Capacity);
                    if (id == process.Id && text.ToString() == "#32770")
                    {
                        dialog = hwnd;
                    }

                    return true;
                }, IntPtr.Zero);
                if (dialog == IntPtr.Zero)
                {
                    Thread.Sleep(25);
                }
            }

            Assert.NotEqual(IntPtr.Zero, dialog);
            var messages = new StringBuilder();
            EnumChildWindows(dialog, (hwnd, _) =>
            {
                var text = new StringBuilder(1024);
                GetWindowTextW(hwnd, text, text.Capacity);
                messages.Append(text);
                return true;
            }, IntPtr.Zero);
            Assert.Contains("Original executable missing", messages.ToString());
            Assert.True(PostMessageW(dialog, 0x10, UIntPtr.Zero, IntPtr.Zero));
            Assert.True(process.WaitForExit(5000));
            Assert.Equal(80, process.ExitCode);
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }

                process.Dispose();
            }

            NeApplicationTests.DeleteProcessDirectory(root);
        }
    }

    private delegate bool Enumerate(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(Enumerate callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, Enumerate callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int length);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int length);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr wp, IntPtr lp);
}
