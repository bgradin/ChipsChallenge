using System.Runtime.InteropServices;
using System.Text.Json;
using Translator.ChipsChallenge;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("oleacc.dll")]
    private static extern int ObjectFromLresult(nint result, ref Guid iid, nint parameter, [MarshalAs(UnmanagedType.Interface)] out object document);
    private static dynamic HelpDocument(nint help)
    {
        nint server = 0;
        EnumChildWindows(help, (w, _) =>
        {
            if (Class(w) == "Internet Explorer_Server" || Class(w) == "InternetExplorer_Server")
            {
                server = w;
            }

            return true;
        }, 0);
        if (server == 0)
        {
            return null!;
        }

        uint message = RegisterWindowMessageW("WM_HTML_GETOBJECT");
        if (SendMessageTimeoutW(server, message, 0, 0, 2, 5000, out var result) == 0 || result == 0)
        {
            return null!;
        }

        var iid = new Guid("332C4425-26CB-11D0-B483-00C04FD90119");
        return ObjectFromLresult(result, ref iid, 0, out var document) == 0
            ? document
            : null!;
    }

    [WindowsSuppliedNeFact]
    public void NativeHelpRequestsOpenAuthoredConvertedContentAndNavigation()
    {
        using var session = new ParitySession("help");
        // Keep the original executable's Help menu wiring, but exercise our own document.
        File.WriteAllBytes(Path.Combine(session.Directory, "CHIPS64.CHM"), SupportedChipsHelp.Convert(SampleWinHelp.Create(keyword: "How to Play")));
        bool complete = false;
        try
        {
            string CurrentBody()
            {
                nint help = Find(session.Process, "HH Parent");
                if (help == 0)
                {
                    return "";
                }

                try
                {
                    dynamic doc = HelpDocument(help);
                    return doc is null
                        ? ""
                        : (string)doc.body.innerText;
                }
                catch (Exception error) when (error is COMException or UnauthorizedAccessException or MissingMemberException)
                {
                    return "";
                }
            }

            Key(session.Window, 0x70);
            Until(() => CurrentBody().Contains("Repeated phrase & sample <text>", StringComparison.Ordinal), session.Process, "Sample contents did not open in the native help viewer");
            dynamic document = HelpDocument(Find(session.Process, "HH Parent"));
            Assert.Equal(1, (int)document.images.length);
            Until(() =>
            {
                dynamic image = document.images.item(0);
                return (bool)image.complete && (int)image.width == 2 && (int)image.height == 1;
            }, session.Process, "Sample bitmap did not render");
            bool clicked = false;
            for (int i = 0; i < (int)document.links.length; i++)
            {
                dynamic link = document.links.item(i);
                if (!(Convert.ToString(link.getAttribute("href", 2)) ?? "").EndsWith("topic41.htm", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                link.click();
                clicked = true;
                break;
            }

            Assert.True(clicked);
            Until(() => CurrentBody().Contains("A separate authored topic.", StringComparison.Ordinal), session.Process, "Sample topic link did not navigate");
            Key(session.Window, 0x70);
            Until(() => CurrentBody().Contains("Repeated phrase", StringComparison.Ordinal), session.Process, "Contents request did not return to the default topic");
            Command(session.Window, 0x78u);
            Until(() => CurrentBody().Contains("A separate authored topic.", StringComparison.Ordinal), session.Process, "Keyword help request did not select the sample keyword target");
            session.Close();
            Assert.Equal((nint)0, Find(session.Process, "HH Parent"));
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "help.json"), JsonSerializer.Serialize(new
            {
                Format = "NeNativeHelp/v2",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                Scope = "Sample conversion and native help request wiring; no game-content comparison",
                AuthoredSample = true,
                NativeSystemViewer = "HHCTRL.OCX HtmlHelpW",
                Checks = new[] { "Contents", "Link navigation", "Keyword request", "Image rendering", "Shutdown" }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
