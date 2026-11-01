using System.Text;
using System.Text.Json;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [WindowsSuppliedNeFact]
    public void OriginalHintAppearsWithExternalTextAndRandomForceUsesOnlyFourLegalDirections()
    {
        const string hint = "Collect the keys before opening the doors.";
        var level = new ParityScenarioLevel();
        level.Tile(16, 15, 47);
        level.Fields.Add((7, Encoding.ASCII.GetBytes(hint + "\0")));
        var force = new ParityScenarioLevel();
        force.Tile(16, 15, 50);
        using var session = new ParitySession("hint-force", ParityScenarioLevel.Build([level, force]), ParityScenarioLevel.Title(0));
        bool complete = false;
        var exits = new HashSet<string>();
        string hintPixels = "";
        try
        {
            Key(session.Window, 0x20);
            Key(session.Window, 0x27);
            nint window = 0;
            Until(() =>
            {
                EnumChildWindows(session.Window, (w, _) =>
                {
                    if (Class(w) == "HintClass")
                    {
                        window = w;
                    }

                    return true;
                }, 0);
                return window != 0;
            }, session.Process, "Original hint window did not appear");
            var state = Observe(session);
            int length = Array.IndexOf(state.Game, (byte)0, 0x99a, 128) - 0x99a;
            Assert.Equal(hint, Encoding.ASCII.GetString(state.Game, 0x99a, length));
            Assert.True(GetClientRect(window, out var bounds));
            byte[] pixels = CaptureCenter(window, bounds.Right - 10, bounds.Bottom - 10);
            hintPixels = Hash(pixels);
            Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4] != 0 || pixels[i * 4 + 1] != 0 || pixels[i * 4 + 2] != 0);
            Key(session.Window, 0x25);
            Until(() => !IsWindow(window), session.Process, "Hint did not disappear after leaving its tile");
            nint go = Dialog(session.Process, session.Window, 0x77);
            SetField(go, 100, "2");
            SetField(go, 101, ParityScenarioLevel.Password(1));
            Command(go, 1);
            Until(() => !IsWindow(go) && Text(session.Window).Contains(ParityScenarioLevel.Title(1), StringComparison.Ordinal), session.Process, "Random force selection failed");
            for (int attempt = 0; attempt < 64 && exits.Count < 4; attempt++)
            {
                Key(session.Window, 0x20);
                Key(session.Window, 0x27);
                GuestSnapshot? actual = null;
                Until(() =>
                {
                    actual = Observe(session);
                    return actual.Word(0xa34) >= 2 && actual.Word(0x80c) == 0;
                }, session.Process, "Random force did not finish an automatic move");
                string exit = actual!.X + "," + actual.Y;
                Assert.Contains(exit, new[] { "15,15", "17,15", "16,14", "16,16" });
                Assert.Equal(0, actual.Death);
                exits.Add(exit);
                Command(session.Window, 0x71);
                Until(() => Observe(session).Word(0x810) != 0 && Observe(session).X == 15, session.Process, "Random force restart failed");
            }

            Assert.Equal(4, exits.Count);
            session.Close();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "hint-force.json"), JsonSerializer.Serialize(new
            {
                Format = "NeHintForce/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                OriginalHint = hint,
                RenderedHintPixelsSha256 = hintPixels,
                RandomForceExits = exits.Order().ToArray(),
                ExternalScenarioInput = true,
                FixtureInitialization = false,
                TimingPolicy = "No RNG seed or guest state is written. Poll completed original moves, with at most 64 independent ordinary restarts."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
