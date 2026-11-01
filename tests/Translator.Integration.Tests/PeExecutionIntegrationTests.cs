using System.Diagnostics;
using Translator.Assembly;
using Translator.CodeGen;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public class PeExecutionIntegrationTests
{
    [WindowsFact]
    public void GeneratedPeRunsTurnLeftSmokeCaseOnWindows()
    {
        string sample = Path.GetFullPath("../../../../../tests/Fixtures/TurnLeft/TurnLeft.asm");
        var parsed = new AssemblyParser().Parse(sample);
        Assert.Empty(parsed.Diagnostics);
        var function = Assert.Single(parsed.Module.Functions);
        byte[] body = new TurnLeftCodeGenerator().Generate(function, X64Abi.Windows).Bytes;
        string executable = Path.Combine(Path.GetTempPath(), $"turnleft-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(executable, new Pe32PlusWriter().WriteExecutable(body));
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
            Assert.NotNull(process);
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("TurnLeft PE timed out.");
            }

            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (File.Exists(executable))
            {
                File.Delete(executable);
            }
        }
    }

    [WindowsFact]
    public void GeneratedPeRunsRealCompilerAcceptanceCasesOnWindows()
    {
        string executable = Path.Combine(Path.GetTempPath(), $"compiler-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(executable, CompilerAcceptanceTests.Build().WriteExecutable());
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
            Assert.NotNull(process);
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("compiler acceptance PE timed out.");
            }

            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (File.Exists(executable))
            {
                File.Delete(executable);
            }
        }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires execution on 64-bit Windows.";
        }
    }
}
