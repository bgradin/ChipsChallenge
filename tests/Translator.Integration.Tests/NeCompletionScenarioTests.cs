using System.Text.Json;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [WindowsSuppliedNeFact]
    public void OriginalCompletionScoresProgressesPersistsAndNewGameHonorsBothAnswers()
    {
        var first = new ParityScenarioLevel();
        first.Tile(16, 15, 21);
        var second = new ParityScenarioLevel();
        second.Tile(16, 15, 21);
        byte[] data = ParityScenarioLevel.Build([first, second, new ParityScenarioLevel()]);
        using var session = new ParitySession("completion-new-game", data, ParityScenarioLevel.Title(0));
        bool complete = false;
        var observations = new List<object>();
        try
        {
            for (int number = 1; number <= 2; number++)
            {
                Key(session.Window, 0x20);
                Key(session.Window, 0x27);
                nint dialog = 0;
                Until(() =>
                {
                    dialog = DialogContaining(session.Process, "Level Bonus:");
                    return dialog != 0;
                }, session.Process, "Original completion dialog missing");
                int bonus = 500 * number;
                Assert.Equal("Time Bonus:  0", Text(GetDlgItem(dialog, 102)));
                Assert.Equal("Level Bonus:  " + bonus, Text(GetDlgItem(dialog, 103)));
                Assert.Equal("Level Score:  " + bonus, Text(GetDlgItem(dialog, 108)));
                observations.Add(new
                {
                    Level = number,
                    TimeBonus = 0,
                    LevelBonus = bonus,
                    Score = bonus,
                    OriginalDialog = true
                });
                Command(dialog, 106);
                Until(() => !IsWindow(dialog) && Text(session.Window).Contains(ParityScenarioLevel.Title(number), StringComparison.Ordinal), session.Process, "Original Onward did not advance");
            }

            string saved = File.ReadAllText(Path.Combine(session.Directory, "entpack.ini"));
            Assert.Contains("Highest Level=3", saved);
            Assert.Contains("Current Score=1500", saved);
            Assert.Contains("Level1=TAAA,0,500", saved);
            Assert.Contains("Level2=TAAB,0,1000", saved);
            // New Game asks only after nontrivial progress. The NO answer must
            // preserve both the running level and durable best results.
            Key(session.Window, 0x71);
            nint confirm = 0;
            Until(() =>
            {
                confirm = Find(session.Process, "#32770");
                return confirm != 0;
            }, session.Process, "F2 did not request New Game confirmation");
            Command(confirm, 7);
            Until(() => !IsWindow(confirm), session.Process, "New Game cancel did not return");
            Assert.Contains(ParityScenarioLevel.Title(2), Text(session.Window));
            Assert.Equal(saved, File.ReadAllText(Path.Combine(session.Directory, "entpack.ini")));
            Key(session.Window, 0x71);
            Until(() =>
            {
                confirm = Find(session.Process, "#32770");
                return confirm != 0;
            }, session.Process, "F2 did not request New Game confirmation");
            Command(confirm, 6);
            Until(() => !IsWindow(confirm) && Text(session.Window).Contains(ParityScenarioLevel.Title(0), StringComparison.Ordinal), session.Process, "New Game confirm did not reset level");
            Assert.Equal((ushort)0, Observe(session).Global(0x1696));
            saved = File.ReadAllText(Path.Combine(session.Directory, "entpack.ini"));
            Assert.Contains("Highest Level=1", saved);
            Assert.Contains("Current Score=0", saved);
            Assert.DoesNotContain("Level2=", saved);
            session.Close();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "completion.json"), JsonSerializer.Serialize(new
            {
                Format = "NeCompletion/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ExternalScenarioInput = true,
                FixtureInitialization = false,
                InputDataSha256 = Hash(data),
                Observations = observations,
                NewGameCancelAndConfirm = complete
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
