using System.Text.Json;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [WindowsSuppliedNeFact]
    public void OriginalCharacterAcceleratorsRestartRealStateAndActivationFreezesThenResumesTime()
    {
        var level = new ParityScenarioLevel
        {
            TimeLimit = 100,
            Chips = 1
        };
        level.Tile(16, 15, 2);
        using var session = new ParitySession("input-focus", ParityScenarioLevel.Build([new ParityScenarioLevel(), level, new ParityScenarioLevel()]), ParityScenarioLevel.Title(0));
        bool complete = false;
        try
        {
            nint go = Dialog(session.Process, session.Window, 0x77);
            SetField(go, 100, "2");
            SetField(go, 101, ParityScenarioLevel.Password(1));
            Command(go, 1);
            Until(() => !IsWindow(go) && Text(session.Window).Contains(ParityScenarioLevel.Title(1), StringComparison.Ordinal), session.Process, "Input scenario selection failed");
            // The original resource uses character accelerators for Ctrl-P/N/R,
            // rather than virtual-key accelerator entries. Supply their normal
            // WM_CHAR messages to the original TranslateAccelerator loop.
            PostMessageW(session.Window, 0x102, 0x10, 1);
            Until(() => Text(session.Window).Contains(ParityScenarioLevel.Title(0), StringComparison.Ordinal), session.Process, "Ctrl-P did not select previous level");
            PostMessageW(session.Window, 0x102, 0x0e, 1);
            Until(() => Text(session.Window).Contains(ParityScenarioLevel.Title(1), StringComparison.Ordinal), session.Process, "Ctrl-N did not select known next level");
            Key(session.Window, 0x20);
            Key(session.Window, 0x27);
            Until(() => Observe(session).X == 16 && Observe(session).Global(0x1692) == 0, session.Process, "Input did not collect original chip");
            PostMessageW(session.Window, 0x102, 0x12, 1);
            Until(() =>
            {
                var s = Observe(session);
                return s.X == 15 && s.Global(0x1692) == 1 && s.Game[15 * 32 + 16] == 2;
            }, session.Process, "Ctrl-R did not restore original board and counters");
            Key(session.Window, 0x20);
            Assert.True(PostMessageW(session.Window, 0x1c, 0, 0));
            Until(() => Observe(session).Global(0x24) != 0, session.Process, "Native deactivation did not pause original game");
            ushort frozen = Observe(session).Global(0x1694);
            Thread.Sleep(1200);
            Assert.Equal(frozen, Observe(session).Global(0x1694));
            Assert.True(PostMessageW(session.Window, 0x1c, 1, 0));
            Until(() => Observe(session).Global(0x24) == 0, session.Process, "Native activation did not resume original game");
            Until(() => Observe(session).Global(0x1694) < frozen, session.Process, "Time did not advance after native activation", 5000);
            // A held-key repeat is queued through the real message loop; the
            // original one-move buffer must retain a bounded legal board state.
            for (int i = 0; i < 20; i++)
            {
                Assert.True(PostMessageW(session.Window, 0x100, 0x27, unchecked((nint)0x40000001)));
            }

            PostMessageW(session.Window, 0x101, 0x27, unchecked((nint)(int)0xc0000001));
            Thread.Sleep(800);
            var after = Observe(session);
            Assert.InRange(after.X, 16, 17);
            Assert.Equal(15, after.Y);
            Assert.Equal(0, after.Death);
            Key(session.Window, 0x72);
            Until(() => Checked(session.Window, 0x74) != 0, session.Process, "F3 did not pause after input repeat");
            session.Close();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "input.json"), JsonSerializer.Serialize(new
            {
                Format = "NeInput/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ExternalScenarioInput = true,
                FixtureInitialization = false,
                CtrlPreviousNextRestart = complete,
                ActivationMessagePauseResume = complete,
                BoundedRepeat = complete,
                TimingPolicy = "Observe original state transitions, 1.2 seconds frozen; eventual timer advance within 5 seconds. Activation events injected through native window messages; no guest-state writes."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
