using System.Text.Json;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [WindowsSuppliedNeFact]
    public void OriginalRepeatedRealAttemptsOfferSkipAndHonorNoThenYes()
    {
        using var session = new ParitySession("skip", ParityScenarioLevel.Build([new ParityScenarioLevel(), new ParityScenarioLevel()]), ParityScenarioLevel.Title(0));
        bool complete = false;
        try
        {
            foreach (uint answer in new[]
            {
                7u,
                6u
            }

            )
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    Key(session.Window, 0x20);
                    // Original Melinda logic requires >30 real moves on each
                    // of ten attempts. Neither the counters nor time are set
                    // by the test; wait for the original one-move buffer.
                    for (int step = 0; step < 31; step++)
                    {
                        int x = step % 2 == 0
                            ? 16
                            : 15;
                        Key(session.Window, step % 2 == 0
                            ? 0x27u
                            : 0x25u);
                        Until(() =>
                        {
                            var s = Observe(session);
                            return s.X == x && s.Word(0xa34) >= step + 1 && s.Word(0x80e) == 0;
                        }, session.Process, "Real skip attempt movement was lost");
                    }

                    Command(session.Window, 0x71);
                    if (attempt < 9)
                    {
                        Until(() => Observe(session).Word(0x810) != 0 && Observe(session).Word(0xa34) == 0, session.Process, "Original restart did not finish a skip attempt");
                    }
                }

                nint prompt = 0;
                Until(() =>
                {
                    prompt = DialogContaining(session.Process, "Would you like to skip to the next level?");
                    return prompt != 0;
                }, session.Process, "Original skip offer did not appear after ten qualified attempts");
                Command(prompt, answer);
                Until(() => !IsWindow(prompt) && Observe(session).Word(0x810) != 0, session.Process, "Skip answer did not return to a playable level");
                Assert.Contains(ParityScenarioLevel.Title(answer == 7
                    ? 0
                    : 1), Text(session.Window));
                Assert.Equal((ushort)0, Observe(session).Word(0xa32));
            }

            Assert.Equal(0, BitConverter.ToInt32(Observe(session).Data, 0x1696));
            session.Close();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "skip.json"), JsonSerializer.Serialize(new
            {
                Format = "NeSkip/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                QualifiedAttemptsPerOffer = 10,
                RealMovesPerAttempt = 31,
                Answers = "NO preserves level, YES advances without awarding completion points",
                FixtureInitialization = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [WindowsSuppliedNeFact]
    public void OriginalAllSixDeathMessagesReloadBoardAndPermitNormalRetry()
    {
        string[] messages = ["Don't step in the fire without fire boots!", "Chip can't swim without flippers!", "Don't touch the bombs!", "Watch out for moving blocks!", "Look out for creatures!", "Out of time!"];
        var results = new List<object>();
        bool complete = false;
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath(), "deaths");
        Directory.CreateDirectory(evidence);
        try
        {
            for (int cause = 1; cause <= 6; cause++)
            {
                var level = new ParityScenarioLevel();
                if (cause <= 3)
                {
                    level.Tile(16, 15, cause == 1
                        ? (byte)4
                        : cause == 2
                            ? (byte)3
                            : (byte)42);
                }

                if (cause == 4)
                {
                    level.Tile(16, 15, 10);
                    level.Tile(17, 15, 20);
                }

                if (cause == 5)
                {
                    level.Tile(15, 14, 0x4e);
                    level.Fields.Add((10, new byte[] { 15, 14 }));
                }

                if (cause == 6)
                {
                    level.TimeLimit = 1;
                }

                using var session = new ParitySession("death-" + cause, ParityScenarioLevel.Build([level, new ParityScenarioLevel()]), ParityScenarioLevel.Title(0));
                Key(session.Window, 0x20);
                if (cause <= 4)
                {
                    Key(session.Window, 0x27);
                }

                nint dialog = 0;
                Until(() =>
                {
                    dialog = DialogContaining(session.Process, messages[cause - 1]);
                    return dialog != 0;
                }, session.Process, "Original death message missing: " + cause);
                Assert.Equal(cause, Observe(session).Death);
                PostMessageW(dialog, 0x10, 0, 0);
                Until(() =>
                {
                    var s = Observe(session);
                    return !IsWindow(dialog) && s.Death == 0 && s.X == 15 && s.Y == 15 && s.Word(0x810) != 0;
                }, session.Process, "Original death did not reload initial board: " + cause);
                Assert.Equal((ushort)1, Observe(session).Word(0xa30));
                // A fresh input dismisses the new placard and moves to a safe
                // tile, proving the retry can actually play in the same process.
                Key(session.Window, 0x20);
                Key(session.Window, 0x25);
                Until(() => Observe(session).X == 14 && Observe(session).Y == 15, session.Process, "Retry did not accept ordinary movement");
                results.Add(new
                {
                    Cause = cause,
                    OriginalMessage = messages[cause - 1],
                    RestartCount = 1,
                    RetryX = 14,
                    RetryY = 15
                });
                session.Close();
            }

            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "deaths.json"), JsonSerializer.Serialize(new
            {
                Format = "NeDeaths/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                ExternalScenarioInput = true,
                FixtureInitialization = false,
                Observations = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
