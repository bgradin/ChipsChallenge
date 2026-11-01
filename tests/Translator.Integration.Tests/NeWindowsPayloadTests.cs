using System.Diagnostics;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeWindowsPayloadTests
{
    [WindowsFact]
    public void ProductionNativePayloadRunsRealWindowCallbackGdiLifecycleAndMultimediaServices() => Run(false);

    [WindowsFact]
    public void ProductionPayloadServicesAlsoWorkWithSeparatedProductionSections() => Run(true);
    private static void Run(bool productionSections)
    {
        string directory = Path.Combine(Path.GetTempPath(), "payload-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "service-probe.exe");
            File.WriteAllBytes(path, NativeWholeTaskProbeProgram.Build(productionSections));
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = directory })!;
            if (!process.WaitForExit(30000))
            {
                process.Kill(true);
                throw new TimeoutException("Whole-task Windows service probe timed out.");
            }

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Value=17", File.ReadAllText(Path.Combine(directory, "service-probe.ini")));
            Assert.Contains("Value=17", File.ReadAllText(Path.Combine(directory, "service-relative.ini")));
            string? evidence = Environment.GetEnvironmentVariable("CHIPS_SERVICE_EVIDENCE");
            if (evidence is not null)
            {
                Directory.CreateDirectory(evidence);
                var report = new
                {
                    WindowsHost = true,
                    SeparatedProductionSections = productionSections,
                    ProductionNativePayload = true,
                    ExitCode = process.ExitCode,
                    NativePayloadSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Win16_Runtime.NativeWholeTaskRuntime.Bytes)).ToLowerInvariant(),
                    StaticImports = new[]
                    {
                        "USER.#95",
                        "USER.#93",
                        "USER.#57",
                        "USER.#41",
                        "USER.#66",
                        "USER.#68",
                        "USER.#53",
                        "USER.#107",
                        "GDI.#52",
                        "GDI.#57",
                        "GDI.#45",
                        "GDI.#93",
                        "GDI.#69",
                        "GDI.#68",
                        "KERNEL.#102",
                        "KERNEL.#95",
                        "KERNEL.#50",
                        "KERNEL.#96",
                        "KERNEL.#3",
                        "KERNEL.#49",
                        "KERNEL.#134",
                        "KERNEL.#74",
                        "KERNEL.#81",
                        "KERNEL.#82",
                        "KERNEL.#84",
                        "KERNEL.#127",
                        "KERNEL.#128",
                        "KERNEL.#129"
                    },
                    DynamicProcedures = new[]
                    {
                        "MMSYSTEM.midiOutGetNumDevs",
                        "MMSYSTEM.waveOutGetNumDevs",
                        "MMSYSTEM.mciSendCommand",
                        "MMSYSTEM.mciGetErrorString",
                        "MMSYSTEM.sndPlaySound"
                    },
                    ApplicationAccepted = false
                };
                File.WriteAllText(Path.Combine(evidence, productionSections
                    ? "native-production-layout-probe.json"
                    : "native-payload-probe.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            NeApplicationTests.DeleteProcessDirectory(directory);
        }
    }
}
