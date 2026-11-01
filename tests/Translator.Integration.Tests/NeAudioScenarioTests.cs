using System.Text.Json;
using System.Runtime.InteropServices;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool sndPlaySoundW(string? filename, uint flags);

    private static float[] WaveWindow(float[] original, int originalRate, float[] observed, int observedRate)
    {
        Correlation(Envelope(original, originalRate, 1000), Envelope(observed, observedRate, 1000), out int shift);
        int start = Math.Max(0, (int)((long)shift * observedRate / 1000) - observedRate / 50);
        int count = Math.Min(observed.Length - start, (int)((long)original.Length * observedRate / originalRate) + observedRate / 25);
        return observed.AsSpan(start, count).ToArray();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static float[] ObserveOriginalWave(WindowsAudioLoopback capture, string path, string evidence)
    {
        var wave = ReadOriginalWave(File.ReadAllBytes(path));
        Assert.True(sndPlaySoundW(path, 3), "System could not play original reference WAV");
        float[] reference;
        try
        {
            reference = capture.ReadFor(TimeSpan.FromSeconds(2));
        }
        finally
        {
            sndPlaySoundW(null, 0);
        }

        File.WriteAllBytes(Path.Combine(evidence, "reference.f32"), reference.SelectMany(BitConverter.GetBytes).ToArray());
        capture.ReadFor(TimeSpan.FromMilliseconds(400));
        return WaveWindow(wave.Samples, wave.Rate, reference, capture.SampleRate);
    }

    private sealed record AudioScenario(string Wave, ParityScenarioLevel Level, uint[] SetupKeys, uint TriggerKey = 0x27, bool Death = false, bool Completion = false);

    private static IEnumerable<AudioScenario> AudioScenarios()
    {
        foreach (var (name, tile) in new[]
        {
            ("CLICK3.WAV", 2),
            ("OOF3.WAV", 1),
            ("POP2.WAV", 40),
            ("BUMMER.WAV", 4),
            ("DITTY1.WAV", 21)
        }

        )
        {
            var level = new ParityScenarioLevel();
            level.Tile(16, 15, (byte)tile);
            if (tile == 2)
            {
                level.Chips = 1;
            }

            if (tile == 35)
            {
                level.Tile(18, 15, 37);
            }

            yield return new(name, level, [], Death: tile == 4, Completion: tile == 21);
        }

        var door = new ParityScenarioLevel();
        door.Tile(16, 15, 100);
        door.Tile(17, 15, 22);
        yield return new("DOOR.WAV", door, [0x27]);
        var thief = new ParityScenarioLevel();
        thief.Tile(16, 15, 104);
        thief.Tile(17, 15, 33);
        yield return new("STRIKE.WAV", thief, [0x27]);
        var teleport = new ParityScenarioLevel();
        teleport.Tile(16, 15, 41);
        teleport.Tile(18, 15, 41);
        yield return new("TELEPORT.WAV", teleport, []);
        var water = new ParityScenarioLevel();
        water.Tile(16, 15, 10);
        water.Tile(17, 15, 3);
        yield return new("WATER2.WAV", water, []);
    }

    private static (float[] Samples, int Rate) ReadOriginalWave(byte[] bytes)
    {
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        int rate = 0, bits = 0, channels = 0;
        byte[]? pcm = null;
        int end = checked(BitConverter.ToInt32(bytes, 4) + 8);
        Assert.InRange(end, 12, bytes.Length);
        for (int at = 12; at + 8 <= end;)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int size = BitConverter.ToInt32(bytes, at + 4);
            int start = at + 8;
            Assert.InRange(size, 0, end - start);
            if (id == "fmt ")
            {
                Assert.Equal((ushort)1, BitConverter.ToUInt16(bytes, start));
                channels = BitConverter.ToUInt16(bytes, start + 2);
                rate = BitConverter.ToInt32(bytes, start + 4);
                bits = BitConverter.ToUInt16(bytes, start + 14);
            }

            if (id == "data")
            {
                pcm = bytes.AsSpan(start, size).ToArray();
            }

            at = start + size + (size & 1);
        }

        Assert.NotNull(pcm);
        Assert.True(bits is 8 or 16);
        Assert.True(channels > 0 && rate > 0);
        var samples = new float[pcm.Length / (channels * bits / 8)];
        for (int i = 0; i < samples.Length; i++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int at = (i * channels + ch) * bits / 8;
                samples[i] += (bits == 8
                    ? (pcm[at] - 128) / 128f
                    : BitConverter.ToInt16(pcm, at) / 32768f) / channels;
            }
        }

        return (samples, rate);
    }

    private static float[] Envelope(float[] samples, int rate, int binsPerSecond = 200)
    {
        int count = (int)((long)samples.Length * binsPerSecond / rate);
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            int first = (int)((long)i * rate / binsPerSecond), last = (int)((long)(i + 1) * rate / binsPerSecond);
            double sum = 0;
            for (int j = first; j < last; j++)
            {
                sum += (double)samples[j] * samples[j];
            }

            output[i] = (float)Math.Sqrt(sum / (last - first));
        }

        return output;
    }

    private static float[] Spectrum(float[] samples, int rate)
    {
        var output = new float[25];
        if (samples.Length < 2)
        {
            return output;
        }

        var windowed = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            windowed[i] = samples[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (samples.Length - 1)));
        }

        for (int band = 0; band < output.Length; band++)
        {
            double coefficient = 2 * Math.Cos(2 * Math.PI * (band + 1) * 200 / rate), previous = 0, beforePrevious = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double next = windowed[i] + coefficient * previous - beforePrevious;
                beforePrevious = previous;
                previous = next;
            }

            output[band] = (float)Math.Sqrt(Math.Max(0, previous * previous + beforePrevious * beforePrevious - coefficient * previous * beforePrevious));
        }

        return output;
    }

    private static double SpectralSimilarity(float[] reference, int referenceRate, float[] actual, int actualRate)
    {
        if (actual.Length == 0)
        {
            return 0;
        }

        Correlation(Envelope(reference, referenceRate, 1000), Envelope(actual, actualRate, 1000), out int envelopeShift);
        int onset = (int)((long)envelopeShift * actualRate / 1000);
        float[] expected = AverageSpectrum(reference, referenceRate, 50), full = Spectrum(reference, referenceRate);
        double best = 0;
        int first = Math.Max(0, onset - actualRate / 30), last = Math.Min(actual.Length - 1, onset + actualRate / 30);
        for (int offset = first; offset <= last; offset += Math.Max(1, actualRate / 1000))
        {
            int count = Math.Min(actual.Length - offset, (int)((long)reference.Length * actualRate / referenceRate));
            var window = actual.AsSpan(offset, count).ToArray();
            best = Math.Max(best, Math.Max(Correlation(full, Spectrum(window, actualRate)), Correlation(expected, AverageSpectrum(window, actualRate, 50))));
        }

        return best;
    }

    private static double Correlation(float[] expected, float[] actual) => Correlation(expected, actual, out _);

    [Fact]
    public void AudioAlignmentPrefersAudibleSignalOverPerfectlyShapedSilentTail()
    {
        float[] reference = [1, 0.7f, 0.4f, 0.1f, 0];
        float[] actual = [0.95f, 0.8f, 0.35f, 0.12f, 0, .. new float[20], .. reference.Select(v => v * 0.0001f)];
        double similarity = Correlation(reference, actual, out int alignment);
        Assert.Equal(0, alignment);
        Assert.InRange(similarity, 0.98, 1);
    }

    private static double Correlation(float[] expected, float[] actual, out int bestShift)
    {
        bestShift = 0;
        if (actual.Length < expected.Length || expected.Length == 0)
        {
            return 0;
        }

        double best = 0, referenceEnergy = expected.Sum(v => (double)v * v);
        var energy = new double[actual.Length + 1];
        for (int i = 0; i < actual.Length; i++)
        {
            energy[i + 1] = energy[i] + (double)actual[i] * actual[i];
        }

        // Normalized correlation must not select an almost-silent tail merely
        // because its noise happens to resemble the expected envelope. Align
        // against a meaningful-energy window of the actual captured signal.
        double maximumEnergy = 0;
        for (int i = 0; i + expected.Length <= actual.Length; i++)
        {
            maximumEnergy = Math.Max(maximumEnergy, energy[i + expected.Length] - energy[i]);
        }

        for (int shift = 0; shift + expected.Length <= actual.Length; shift++)
        {
            double e = energy[shift + expected.Length] - energy[shift];
            if (e < 1e-12 || e < maximumEnergy * 0.1)
            {
                continue;
            }

            double dot = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                dot += (double)expected[i] * actual[shift + i];
            }

            double value = dot / Math.Sqrt(referenceEnergy * e);
            if (value > best)
            {
                best = value;
                bestShift = shift;
            }
        }

        return best;
    }

    [WindowsSuppliedNeFact]
    public void NativePickupProducesOriginalAudioEnvelopeAndSpectrumAndSoundToggleSuppressesIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var level = new ParityScenarioLevel();
        level.Tile(16, 15, 100);
        using var session = new ParitySession("audio-wave", ParityScenarioLevel.Build([level, new ParityScenarioLevel()]), ParityScenarioLevel.Title(0));
        bool complete = false;
        double correlation = 0, spectralSimilarity = 0, activeEnergy = 0, mutedEnergy = 0, ambientMean = 0, mutedCorrelation = 0;
        bool wrongAssetRejected = false;
        try
        {
            if (Checked(session.Window, 0x75) != 0)
            {
                Command(session.Window, 0x75);
                Until(() => Checked(session.Window, 0x75) == 0, session.Process, "Music did not turn off before waveform check");
            }

            Assert.NotEqual(uint.MaxValue, GetMenuState(GetMenu(session.Window), 0x76, 0));
            if (Checked(session.Window, 0x76) == 0)
            {
                Command(session.Window, 0x76);
                Until(() => Checked(session.Window, 0x76) != 0, session.Process, "Sound did not enable");
            }

            Key(session.Window, 0x20);
            using var capture = new WindowsAudioLoopback();
            capture.ReadFor(TimeSpan.FromMilliseconds(400));
            string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
            string wavePath = Path.Combine(Path.GetDirectoryName(source)!, "BLIP2.WAV");
            float[] reference = ObserveOriginalWave(capture, wavePath, session.Evidence);
            float[] ambient = capture.ReadFor(TimeSpan.FromSeconds(1));
            ambientMean = ambient.Length == 0
                ? 0
                : ambient.Sum(v => (double)v * v) / ambient.Length;
            Key(session.Window, 0x27);
            float[] active = capture.ReadFor(TimeSpan.FromSeconds(2));
            correlation = Correlation(Envelope(reference, capture.SampleRate, 1000), Envelope(active, capture.SampleRate, 1000));
            spectralSimilarity = SpectralSimilarity(reference, capture.SampleRate, active, capture.SampleRate);
            activeEnergy = active.Sum(v => (double)v * v);
            File.WriteAllBytes(Path.Combine(session.Evidence, "pickup.f32"), active.SelectMany(BitConverter.GetBytes).ToArray());
            Assert.True(correlation > 0.90 && spectralSimilarity > 0.90, $"Original envelope correlation {correlation}, spectrum {spectralSimilarity}; energy {activeEnergy}; {active.Length} captured samples at {capture.SampleRate}Hz");
            Command(session.Window, 0x76);
            Until(() => Checked(session.Window, 0x76) == 0, session.Process, "Sound did not turn off");
            Command(session.Window, 0x71);
            Until(() => Observe(session).X == 15 && Observe(session).Inventory[0] == 0, session.Process, "Audio scenario restart failed");
            Key(session.Window, 0x20);
            capture.ReadFor(TimeSpan.FromMilliseconds(400));
            Key(session.Window, 0x27);
            float[] muted = capture.ReadFor(TimeSpan.FromSeconds(2));
            mutedEnergy = muted.Sum(v => (double)v * v);
            double mutedMean = muted.Length == 0
                ? 0
                : mutedEnergy / muted.Length;
            Assert.Equal((ushort)0, Observe(session).Global(0x13c8));
            mutedCorrelation = Correlation(reference, muted);
            Assert.True(mutedCorrelation < 0.4, $"Disabled sound mean {mutedMean} vs ambient {ambientMean}, template correlation {mutedCorrelation}");
            session.Close();
            // Causal negative control: the native game still executes normally,
            // but its private pickup asset is intentionally replaced. The same
            // acceptance thresholds must reject that wrong audible result.
            using (var mutant = new ParitySession("audio-wrong-asset", ParityScenarioLevel.Build([level, new ParityScenarioLevel()]), ParityScenarioLevel.Title(0)))
            {
                File.WriteAllBytes(Path.Combine(mutant.Directory, "BLIP2.WAV"), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "BUMMER.WAV")));
                if (Checked(mutant.Window, 0x76) == 0)
                {
                    Command(mutant.Window, 0x76);
                    Until(() => Checked(mutant.Window, 0x76) != 0, mutant.Process, "Mutant sound did not enable");
                }

                Key(mutant.Window, 0x20);
                using var negativeCapture = new WindowsAudioLoopback();
                negativeCapture.ReadFor(TimeSpan.FromMilliseconds(400));
                Key(mutant.Window, 0x27);
                float[] negative = negativeCapture.ReadFor(TimeSpan.FromSeconds(2));
                double negativeEnvelope = Correlation(Envelope(reference, capture.SampleRate, 1000), Envelope(negative, negativeCapture.SampleRate, 1000)), negativeSpectrum = SpectralSimilarity(reference, capture.SampleRate, negative, negativeCapture.SampleRate);
                wrongAssetRejected = negativeEnvelope <= 0.90 || negativeSpectrum <= 0.90;
                Assert.True(wrongAssetRejected, $"Wrong asset passed audio oracle: envelope {negativeEnvelope}, spectrum {negativeSpectrum}");
                mutant.Close();
            }

            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "audio.json"), JsonSerializer.Serialize(new
            {
                Format = "NeAudioWave/v2",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                OriginalWave = "BLIP2.WAV",
                NormalizedEnvelopeCorrelation = correlation,
                SpectralSimilarity = spectralSimilarity,
                EnabledEnergy = activeEnergy,
                DisabledEnergy = mutedEnergy,
                AmbientMeanEnergy = ambientMean,
                DisabledTemplateCorrelation = mutedCorrelation,
                WrongAssetMutationRejected = wrongAssetRejected,
                IndependentReference = "Original WAV played separately by system sndPlaySoundW",
                Observation = "WASAPI shared render-endpoint loopback, read only; original external input and native key/menu messages",
                Limitation = "Pickup waveform and sound toggle only; remaining effects and MIDI have separate acceptance requirements."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [WindowsSuppliedNeFact]
    public void NativeGameActionsProduceEveryOtherSuppliedOriginalSoundEffect()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var results = new List<object>();
        bool complete = false;
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath(), "audio-effects");
        Directory.CreateDirectory(evidence);
        try
        {
            foreach (var test in AudioScenarios())
            {
                using var session = new ParitySession("audio-" + test.Wave, ParityScenarioLevel.Build([test.Level, new ParityScenarioLevel()]), ParityScenarioLevel.Title(0));
                if (Checked(session.Window, 0x75) != 0)
                {
                    Command(session.Window, 0x75);
                    Until(() => Checked(session.Window, 0x75) == 0, session.Process, "Music did not disable before sound observation");
                }

                if (Checked(session.Window, 0x76) == 0)
                {
                    Command(session.Window, 0x76);
                    Until(() => Checked(session.Window, 0x76) != 0, session.Process, "Sound did not enable");
                }

                Key(session.Window, 0x20);
                using var capture = new WindowsAudioLoopback();
                foreach (uint key in test.SetupKeys)
                {
                    Key(session.Window, key);
                    capture.ReadFor(TimeSpan.FromMilliseconds(500));
                }

                capture.ReadFor(TimeSpan.FromMilliseconds(300));
                string wavePath = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!)!, test.Wave);
                byte[] bytes = File.ReadAllBytes(wavePath);
                float[] reference = ObserveOriginalWave(capture, wavePath, session.Evidence);
                Key(session.Window, test.TriggerKey);
                float[] samples = capture.ReadFor(TimeSpan.FromSeconds(2));
                // A 1ms RMS window follows individual carrier cycles in longer
                // effects, making independent Windows resampling sample offsets look
                // like different envelopes. Use 5ms for effects at least 100ms
                // long, retaining 1ms resolution for the short clicks. Both
                // envelope and independent spectrum must still exceed 0.90.
                var original = ReadOriginalWave(bytes);
                int envelopeBins = original.Samples.Length >= original.Rate / 10
                    ? 200
                    : 1000;
                double envelope = Correlation(Envelope(reference, capture.SampleRate, envelopeBins), Envelope(samples, capture.SampleRate, envelopeBins));
                double spectrum = SpectralSimilarity(reference, capture.SampleRate, samples, capture.SampleRate);
                File.WriteAllBytes(Path.Combine(session.Evidence, "effect.f32"), samples.SelectMany(BitConverter.GetBytes).ToArray());
                Assert.True(envelope > 0.90 && spectrum > 0.90, $"{test.Wave}: envelope={envelope}, spectrum={spectrum}, samples={samples.Length}");
                results.Add(new
                {
                    test.Wave,
                    OriginalWaveSha256 = Hash(bytes),
                    EnvelopeCorrelation = envelope,
                    EnvelopeBinsPerSecond = envelopeBins,
                    SpectralSimilarity = spectrum,
                    OutputSampleRate = capture.SampleRate,
                    CapturedSamples = samples.Length
                });
                File.WriteAllText(Path.Combine(evidence, "partial.json"), JsonSerializer.Serialize(results));
                if (test.Death || test.Completion)
                {
                    nint dialog = 0;
                    Until(() =>
                    {
                        dialog = Find(session.Process, "#32770");
                        return dialog != 0;
                    }, session.Process, "Original audio action dialog missing");
                    if (test.Completion)
                    {
                        Command(dialog, 106);
                    }
                    else
                    {
                        PostMessageW(dialog, 0x10, 0, 0);
                    }

                    Until(() => !IsWindow(dialog), session.Process, "Original audio action dialog did not close");
                }

                session.Close();
            }

            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "effects.json"), JsonSerializer.Serialize(new
            {
                Format = "NeAudioEffects/v2",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ExpectedEffects = 9,
                IndependentReference = "Each original WAV is played separately by system sndPlaySoundW and captured at the same endpoint format; original PCM is independently parsed",
                Observations = results,
                TimingPolicy = "Align normalized 5ms RMS envelopes (1ms for original effects shorter than 100ms) and 200Hz spectral samples; native Windows resampling/volume need not preserve individual sample timing. No audio settings are changed."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
