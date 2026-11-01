using System.Text.Json;
using Translator.Binary;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [WindowsSuppliedNeFact]
    public void OriginalLevels144And149RunEndingAnimationMessagesAndFinalEmbeddedArtwork()
    {
        var levels = Enumerable.Range(0, 149).Select(_ => new ParityScenarioLevel()).ToArray();
        levels[143].Tile(16, 15, 21);
        levels[148].Tile(16, 15, 21);
        var results = new List<object>();
        bool complete = false;
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath(), "endings");
        Directory.CreateDirectory(evidence);
        using var catalog = new NeResourceCatalog(File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!));
        int imageIndex = catalog.Resources.ToList().FindIndex(r => r.Type.Id == 2 && r.Identity.Name == "CHIPEND") + 1;
        var artwork = NeBitmapDecoder.Decode(catalog.Extract(imageIndex).Bytes);
        var expected = new byte[288 * 288 * 4];
        for (int i = 0; i < 288 * 288; i++)
        {
            expected[i * 4] = artwork.Rgba32[i * 4 + 2];
            expected[i * 4 + 1] = artwork.Rgba32[i * 4 + 1];
            expected[i * 4 + 2] = artwork.Rgba32[i * 4];
        }

        try
        {
            foreach (int number in new[]
            {
                144,
                149
            }

            )
            {
                using var session = new ParitySession("ending-" + number, ParityScenarioLevel.Build(levels), ParityScenarioLevel.Title(0));
                nint go = Dialog(session.Process, session.Window, 0x77);
                SetField(go, 100, number.ToString());
                SetField(go, 101, ParityScenarioLevel.Password(number - 1));
                Command(go, 1);
                Until(() => !IsWindow(go) && Text(session.Window).Contains(ParityScenarioLevel.Title(number - 1), StringComparison.Ordinal), session.Process, "Ending scenario selection failed");
                Key(session.Window, 0x20);
                Key(session.Window, 0x27);
                nint score = 0;
                Until(() =>
                {
                    score = DialogContaining(session.Process, "Level Bonus:");
                    return score != 0;
                }, session.Process, "Final-level score dialog missing");
                Assert.Equal("Level Bonus:  " + 500 * number, Text(GetDlgItem(score, 103)));
                Command(score, 106);
                var ticks = new HashSet<int>();
                nint message = 0;
                Until(() =>
                {
                    ticks.Add(Observe(session).Word(0xa36));
                    message = DialogContaining(session.Process, "Great Job, Chip!");
                    return message != 0;
                }, session.Process, "Ending animation did not reach original congratulations", 40000);
                Assert.Contains(104, ticks);
                Assert.True(ticks.Count >= 90, "Original ending animation skipped too many observable stages");
                PostMessageW(message, 0x10, 0, 0);
                Until(() => !IsWindow(message), session.Process, "Congratulations did not close");
                Until(() =>
                {
                    message = DialogContaining(session.Process, "Melinda herself offers Chip membership");
                    return message != 0;
                }, session.Process, "Original Melinda message missing");
                PostMessageW(message, 0x10, 0, 0);
                Until(() => !IsWindow(message), session.Process, "Melinda message did not close");
                Until(() =>
                {
                    message = DialogContaining(session.Process, "You completed 1 levels");
                    return message != 0;
                }, session.Process, "Original ending score summary missing");
                PostMessageW(message, 0x10, 0, 0);
                Until(() => !IsWindow(message), session.Process, "Ending summary did not close");
                nint board = 0;
                EnumChildWindows(session.Window, (w, _) =>
                {
                    if (Class(w) == "BoardClass")
                    {
                        board = w;
                    }

                    return true;
                }, 0);
                string actual = "";
                Until(() =>
                {
                    actual = Hash(CaptureBoard(board));
                    return actual == Hash(expected);
                }, session.Process, "Final embedded CHIPEND artwork differs");
                results.Add(new
                {
                    Level = number,
                    LevelBonus = 500 * number,
                    ObservedEndingTicks = ticks.Order().ToArray(),
                    OriginalMessages = 3,
                    FinalArtworkSha256 = actual,
                    ExpectedArtworkSha256 = Hash(expected)
                });
                session.Close();
                File.WriteAllText(Path.Combine(evidence, "partial.json"), JsonSerializer.Serialize(results));
            }

            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "endings.json"), JsonSerializer.Serialize(new
            {
                Format = "NeEndings/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ExternalScenarioInput = true,
                FixtureInitialization = false,
                Observations = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
