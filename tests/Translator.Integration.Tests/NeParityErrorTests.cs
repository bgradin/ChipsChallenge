using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    private static nint DialogContaining(Process process, string expected)
    {
        nint result = 0;
        EnumWindows((w, _) =>
        {
            GetWindowThreadProcessId(w, out uint pid);
            if (pid != process.Id || Class(w) != "#32770")
            {
                return true;
            }

            bool contains = Text(w).Contains(expected, StringComparison.Ordinal);
            EnumChildWindows(w, (child, _) =>
            {
                contains |= Text(child).Contains(expected, StringComparison.Ordinal);
                return true;
            }, 0);
            if (contains)
            {
                result = w;
            }

            return true;
        }, 0);
        return result;
    }

    [WindowsSuppliedNeFact]
    public void OriginalGotoRejectsInvalidPasswordAndNextPreviousUseStoredPasswords()
    {
        using var session = new ParitySession("password-navigation");
        bool complete = false;
        try
        {
            nint go = Dialog(session.Process, session.Window, 0x77);
            SetField(go, 100, "3");
            SetField(go, 101, "ZZZZ");
            Command(go, 1);
            nint error = 0;
            Until(() =>
            {
                error = DialogContaining(session.Process, "valid password");
                return error != 0;
            }, session.Process, "Invalid password did not show the original error");
            // Native MessageBox is owned by USER32, rather than an original
            // dialog procedure. A normal close acknowledges its MB_OK message.
            Assert.True(PostMessageW(error, 0x10, 0, 0));
            Until(() => !IsWindow(error), session.Process, "Invalid-password error did not close");
            Assert.True(IsWindow(go));
            Assert.Contains("LESSON 1", Text(session.Window));
            CloseDialog(session.Process, go);
            go = Dialog(session.Process, session.Window, 0x77);
            SetField(go, 100, "2");
            SetField(go, 101, "JXMJ");
            Command(go, 1);
            Until(() => !IsWindow(go) && Text(session.Window).Contains("LESSON 2", StringComparison.Ordinal), session.Process, "Valid Goto failed");
            Command(session.Window, 0x6f);
            Until(() => Text(session.Window).Contains("LESSON 1", StringComparison.Ordinal), session.Process, "Previous failed");
            Command(session.Window, 0x6e);
            Until(() => Text(session.Window).Contains("LESSON 2", StringComparison.Ordinal), session.Process, "Next failed with saved password");
            session.Close();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "navigation.json"), JsonSerializer.Serialize(new
            {
                Format = "NeNavigation/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                InvalidPasswordRejected = complete,
                NextPreviousStoredPassword = complete
            }));
        }
    }

    [WindowsSuppliedNeFact]
    public void OriginalMissingAndTruncatedDataShowTheirErrorAndExitNormally()
    {
        string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        string evidence = Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Path.GetTempPath();
        var results = new List<object>();
        Assert.True(KeyboardGet(0x16, 0, out uint delay, 0));
        try
        {
            foreach (string variant in new[]
            {
                "missing",
                "truncated"
            }

            )
            {
                string directory = Path.Combine(Path.GetTempPath(), "native-parity-error-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string exe = Path.Combine(directory, "CHIPS64.EXE");
                File.WriteAllBytes(exe, _package.Value.Executable!);
                foreach (string name in Translator.Delivery.TranslationService.RequiredAssets.Where(n => n != "CHIPS.DAT"))
                {
                    File.Copy(Path.Combine(Path.GetDirectoryName(source)!, name), Path.Combine(directory, name));
                }

                if (variant == "truncated")
                {
                    File.WriteAllBytes(Path.Combine(directory, "CHIPS.DAT"), new byte[] { 0xac, 0xaa });
                }

                using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = directory })!;
                try
                {
                    nint error = 0;
                    Until(() =>
                    {
                        error = DialogContaining(process, "Corrupt or inaccessible CHIPS.DAT file.");
                        return error != 0;
                    }, process, "Original data error did not appear: " + variant);
                    Assert.True(PostMessageW(error, 0x10, 0, 0));
                    Assert.True(process.WaitForExit(15000), "Original data-error shutdown timed out");
                    Assert.Equal(0, process.ExitCode);
                    results.Add(new
                    {
                        Variant = variant,
                        OriginalError = "Corrupt or inaccessible CHIPS.DAT file.",
                        ExitCode = process.ExitCode
                    });
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit();
                    }

                    NeApplicationTests.DeleteProcessDirectory(directory);
                }
            }
        }
        finally
        {
            KeyboardSet(0x17, delay, 0, 0);
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "data-errors.json"), JsonSerializer.Serialize(new
            {
                Format = "NeDataErrors/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = results.Count == 2,
                Observations = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
