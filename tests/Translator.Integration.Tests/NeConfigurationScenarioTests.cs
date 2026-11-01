using System.Text.Json;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [WindowsSuppliedNeFact]
    public void OriginalDefaultsAndExternalProfileRestoreLevelScoreAudioAndDisplayPreferences()
    {
        byte[] data = ParityScenarioLevel.Build([new ParityScenarioLevel(), new ParityScenarioLevel(), new ParityScenarioLevel()]);
        var observations = new List<object>();
        bool complete = false;
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath(), "configuration");
        Directory.CreateDirectory(evidence);
        try
        {
            using (var session = new ParitySession("profile-defaults", data, ParityScenarioLevel.Title(0)))
            {
                var state = Observe(session);
                Assert.Equal(1, state.Word(0x800));
                Assert.Equal(0, state.Global(0x13c6));
                Assert.Equal(0, state.Global(0x13c8));
                Assert.Equal(0, BitConverter.ToInt32(state.Data, 0x1696));
                Assert.Equal(0u, Checked(session.Window, 0x75));
                Assert.Equal(0u, Checked(session.Window, 0x76));
                observations.Add(new
                {
                    Profile = "Absent",
                    Level = 1,
                    Score = 0,
                    MidiEnabled = 0,
                    SoundEnabled = 0
                });
                session.Close();
            }

            string profile = "[Chip's Challenge]\r\nCurrent Level=2\r\nHighest Level=2\r\nCurrent Score=123456\r\nMIDI=0\r\nSounds=0\r\nColor=0\r\nNumber of Midi Files=0\r\nLevel2=TAAB,100,4567\r\n";
            using (var session = new ParitySession("profile-restored", data, ParityScenarioLevel.Title(1), profile))
            {
                var state = Observe(session);
                Assert.Equal(2, state.Word(0x800));
                Assert.Equal(123456, BitConverter.ToInt32(state.Data, 0x1696));
                Assert.Equal(0, state.Global(0x13dc));
                Assert.Equal(0u, Checked(session.Window, 0x7a));
                Key(session.Window, 0x20);
                Key(session.Window, 0x27);
                Until(() => Observe(session).X == 16, session.Process, "Restored profile did not permit gameplay");
                session.Close();
                string saved = File.ReadAllText(Path.Combine(session.Directory, "entpack.ini"));
                Assert.Contains("Current Level=2", saved);
                Assert.Contains("Current Score=123456", saved);
                Assert.Contains("Level2=TAAB,100,4567", saved);
                observations.Add(new
                {
                    Profile = "Restored",
                    Level = 2,
                    Score = 123456,
                    MidiFiles = 0,
                    ColorEnabled = 0,
                    OriginalBestResultPreserved = true
                });
            }

            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "configuration.json"), JsonSerializer.Serialize(new
            {
                Format = "NeConfiguration/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ExternalProfileInput = true,
                FixtureInitialization = false,
                Observations = observations
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [WindowsSuppliedNeFact]
    public void OriginalMissingMidiProfileShowsItsErrorDisablesMusicAndLeavesGamePlayable()
    {
        string profile = "[Chip's Challenge]\r\nMIDI=0\r\nSounds=0\r\nNumber of Midi Files=1\r\nMidiFile0=missing-reference.mid\r\n";
        using var session = new ParitySession("missing-midi", ParityScenarioLevel.Build([new ParityScenarioLevel()]), ParityScenarioLevel.Title(0), profile);
        bool complete = false;
        try
        {
            Key(session.Window, 0x20);
            Command(session.Window, 0x75);
            nint error = 0;
            try
            {
                Until(() =>
                {
                    error = DialogContaining(session.Process, "None of the MIDI files specified");
                    return error != 0;
                }, session.Process, "Original missing-MIDI error did not appear");
            }
            catch (TimeoutException)
            {
                var state = Observe(session);
                throw new InvalidOperationException(Describe(session.Process) + $" MIDI={state.Global(0x13c6)} count={state.Global(0x13dc)} device={state.Global(0x1736)} menu={Checked(session.Window, 0x75)}");
            }

            PostMessageW(error, 0x10, 0, 0);
            Until(() => !IsWindow(error), session.Process, "Missing-MIDI error did not close");
            Assert.Equal(0u, Checked(session.Window, 0x75));
            Assert.NotEqual(0u, GetMenuState(GetMenu(session.Window), 0x75, 0) & 3);
            Key(session.Window, 0x20);
            Key(session.Window, 0x27);
            Until(() => Observe(session).X == 16, session.Process, "Game did not recover after missing MIDI");
            session.Close();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "missing-midi.json"), JsonSerializer.Serialize(new
            {
                Format = "NeMissingMidi/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                OriginalErrorAndRecovery = complete
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
