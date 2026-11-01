using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint mciSendStringW(string command, StringBuilder? result, uint length, nint callback);

    private static float[] AverageSpectrum(float[] samples, int rate, int windowsPerSecond = 20)
    {
        int size = rate / windowsPerSecond, count = samples.Length / size;
        if (count == 0)
        {
            return Spectrum(samples, rate);
        }

        var sums = new double[25];
        for (int frame = 0; frame < count; frame++)
        {
            var spectrum = Spectrum(samples.AsSpan(frame * size, size).ToArray(), rate);
            for (int i = 0; i < sums.Length; i++)
            {
                sums[i] += (double)spectrum[i] * spectrum[i];
            }
        }

        return sums.Select(v => (float)Math.Sqrt(v / Math.Max(count, 1))).ToArray();
    }

    [WindowsSuppliedNeFact]
    public void OriginalLevelMusicMatchesBothIndependentMidiFilesAndHonorsPauseResumeAndToggle()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var results = new List<object>();
        bool complete = false;
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath(), "music");
        Directory.CreateDirectory(evidence);
        try
        {
            foreach (int number in new[]
            {
                1,
                2
            }

            )
            {
                using var session = new ParitySession("music-" + number, ParityScenarioLevel.Build([new ParityScenarioLevel(), new ParityScenarioLevel(), new ParityScenarioLevel()]), ParityScenarioLevel.Title(0));
                if (number == 2)
                {
                    nint go = Dialog(session.Process, session.Window, 0x77);
                    SetField(go, 100, "2");
                    SetField(go, 101, ParityScenarioLevel.Password(1));
                    Command(go, 1);
                    Until(() => !IsWindow(go) && Text(session.Window).Contains(ParityScenarioLevel.Title(1), StringComparison.Ordinal), session.Process, "Music level selection failed");
                }

                Key(session.Window, 0x20);
                if (Checked(session.Window, 0x75) != 0)
                {
                    Command(session.Window, 0x75);
                    Until(() => Checked(session.Window, 0x75) == 0, session.Process, "Original music did not disable");
                }

                using var capture = new WindowsAudioLoopback();
                capture.ReadFor(TimeSpan.FromSeconds(1));
                string filename = number == 1
                    ? "CHIP02.MID"
                    : "CHIP01.MID", path = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!)!, filename), alias = "Reference" + Guid.NewGuid().ToString("N");
                float[] reference;
                Assert.Equal(0u, mciSendStringW($"open \"{path}\" type sequencer alias {alias}", null, 0, 0));
                try
                {
                    Assert.Equal(0u, mciSendStringW("play " + alias, null, 0, 0));
                    reference = capture.ReadFor(TimeSpan.FromSeconds(3));
                }
                finally
                {
                    mciSendStringW("stop " + alias, null, 0, 0);
                    mciSendStringW("close " + alias, null, 0, 0);
                }

                capture.ReadFor(TimeSpan.FromSeconds(1));
                Command(session.Window, 0x75);
                Until(() => Checked(session.Window, 0x75) != 0, session.Process, "Original music did not enable");
                float[] native = capture.ReadFor(TimeSpan.FromSeconds(3));
                float[] referenceWindow = reference.Take(Math.Min(reference.Length, (int)(capture.SampleRate * 2.5))).ToArray();
                double envelope = Correlation(Envelope(referenceWindow, capture.SampleRate), Envelope(native, capture.SampleRate));
                double spectrum = Correlation(AverageSpectrum(referenceWindow, capture.SampleRate), AverageSpectrum(native.Take(Math.Min(native.Length, referenceWindow.Length)).ToArray(), capture.SampleRate));
                double energy = native.Sum(v => (double)v * v);
                File.WriteAllBytes(Path.Combine(session.Evidence, "reference.f32"), reference.SelectMany(BitConverter.GetBytes).ToArray());
                File.WriteAllBytes(Path.Combine(session.Evidence, "native.f32"), native.SelectMany(BitConverter.GetBytes).ToArray());
                Assert.True(energy > 0.01 && envelope > 0.85 && spectrum > 0.85, $"{filename}: native energy {energy}, envelope {envelope}, spectrum {spectrum}");
                Key(session.Window, 0x72);
                Until(() => Checked(session.Window, 0x74) != 0, session.Process, "F3 did not pause music");
                capture.ReadFor(TimeSpan.FromSeconds(1));
                float[] paused = capture.ReadFor(TimeSpan.FromMilliseconds(700));
                double pausedEnergy = paused.Sum(v => (double)v * v);
                Assert.True(pausedEnergy < energy * 0.01, $"MIDI did not pause: {pausedEnergy} vs {energy}");
                Key(session.Window, 0x72);
                Until(() => Checked(session.Window, 0x74) == 0, session.Process, "F3 did not resume music");
                float[] resumed = capture.ReadFor(TimeSpan.FromSeconds(1));
                Assert.True(resumed.Sum(v => (double)v * v) > 0.01, "MIDI did not resume audible output");
                Command(session.Window, 0x75);
                Until(() => Checked(session.Window, 0x75) == 0, session.Process, "Music toggle did not stop MIDI");
                capture.ReadFor(TimeSpan.FromSeconds(1));
                float[] stopped = capture.ReadFor(TimeSpan.FromMilliseconds(700));
                double stoppedEnergy = stopped.Sum(v => (double)v * v);
                Assert.True(stoppedEnergy < energy * 0.01, "MIDI did not stop after toggle");
                results.Add(new
                {
                    Level = number,
                    OriginalMidi = filename,
                    OriginalMidiSha256 = Hash(File.ReadAllBytes(path)),
                    EnvelopeCorrelation = envelope,
                    SpectralSimilarity = spectrum,
                    NativeEnergy = energy,
                    PausedEnergy = pausedEnergy,
                    StoppedEnergy = stoppedEnergy,
                    OutputSampleRate = capture.SampleRate
                });
                session.Close();
                File.WriteAllText(Path.Combine(evidence, "partial.json"), JsonSerializer.Serialize(results));
            }

            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "music.json"), JsonSerializer.Serialize(new
            {
                Format = "NeNativeMusic/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                IndependentReference = "Original MIDI files played separately by the Windows system sequencer; no game logic or translator is used for reference synthesis",
                Observation = "Read-only WASAPI render-endpoint capture",
                Observations = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
