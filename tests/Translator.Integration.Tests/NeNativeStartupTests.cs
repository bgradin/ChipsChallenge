using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

[Collection("Native modal desktop")]
public sealed partial class NeNativeStartupTests
{
    private delegate bool Enumerate(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(Enumerate callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, Enumerate callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int length);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, StringBuilder name, int length);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nuint wparam, nint lparam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint window, int id);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetMenu(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(nint menu, uint id, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint bitmap);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint bitmap);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint dc, int index);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint dc, int x, int y);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool KeyboardGet(uint a, uint b, out uint c, uint d);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool KeyboardSet(uint a, uint b, nint c, uint d);
    private static readonly Lazy<NeProductionCompilation> _package = new(() =>
    {
        string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        byte[] bytes = File.ReadAllBytes(source);
        return NeProductionCompiler.Compile(bytes, new("CHIPS64.EXE", File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "WEP4UTIL.DLL")), true, true));
    });

    private static string Text(nint window)
    {
        var s = new StringBuilder(256);
        GetWindowTextW(window, s, s.Capacity);
        return s.ToString();
    }

    private static string Class(nint window)
    {
        var s = new StringBuilder(100);
        GetClassNameW(window, s, s.Capacity);
        return s.ToString();
    }

    private static void Until(Func<bool> condition, Process process, string reason, int milliseconds = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            if (condition())
            {
                return;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException($"{reason}: native process exited {process.ExitCode}");
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException(reason);
    }

    private static nint Find(Process process, string name)
    {
        nint result = 0;
        EnumWindows((w, _) =>
        {
            GetWindowThreadProcessId(w, out uint id);
            if (id == process.Id && Class(w) == name)
            {
                result = w;
            }

            return true;
        }, 0);
        return result;
    }

    private static string Describe(Process process)
    {
        var text = new StringBuilder();
        EnumWindows((w, _) =>
        {
            GetWindowThreadProcessId(w, out uint id);
            if (id == process.Id)
            {
                text.Append(w.ToString("X") + " " + Class(w) + ":" + Text(w) + " [");
                EnumChildWindows(w, (child, _) =>
                {
                    text.Append(Class(child) + ":" + Text(child) + ";");
                    return true;
                }, 0);
                text.Append("]");
            }

            return true;
        }, 0);
        return text.ToString();
    }

    private static void SetField(nint dialog, int id, string value)
    {
        nint control = GetDlgItem(dialog, id), text = Marshal.StringToHGlobalUni(value), read = Marshal.AllocHGlobal(80);
        try
        {
            Assert.NotEqual(0, SendMessageTimeoutW(control, 0xc, 0, text, 2, 5000, out nint answer));
            Assert.NotEqual(0, answer);
            Assert.NotEqual(0, SendMessageTimeoutW(control, 0xd, 40, read, 2, 5000, out _));
            Assert.Equal(value, Marshal.PtrToStringUni(read));
        }
        finally
        {
            Marshal.FreeHGlobal(read);
            Marshal.FreeHGlobal(text);
        }
    }

    private static void Command(nint window, uint id) => Assert.True(PostMessageW(window, 0x111, id, 0));

    private static nint Dialog(Process process, nint window, uint command)
    {
        Command(window, command);
        nint dialog = 0;
        Until(() =>
        {
            dialog = Find(process, "#32770");
            return dialog != 0;
        }, process, "Original dialog did not open");
        return dialog;
    }

    private static void CloseDialog(Process process, nint dialog)
    {
        Command(dialog, 2);
        Until(() => !IsWindow(dialog), process, "Original dialog did not close");
    }

    private static uint Checked(nint window, uint id)
    {
        uint state = GetMenuState(GetMenu(window), id, 0);
        Assert.NotEqual(uint.MaxValue, state);
        return state & 8;
    }

    private static void Key(nint window, uint key)
    {
        Assert.True(PostMessageW(window, 0x100, key, 1));
        Assert.True(PostMessageW(window, 0x101, key, unchecked((nint)(int)0xc0000001)));
        Thread.Sleep(150);
    }

    [WindowsSuppliedNeFact]
    public void OriginalNativeStartupDialogsInputCloseReopenAndDurableStateWorkWithoutSourceOrSidecars()
    {
        string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!, folder = Path.GetDirectoryName(source)!;
        string directory = Path.Combine(Path.GetTempPath(), "native-startup-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? evidence = Environment.GetEnvironmentVariable("CHIPS_STARTUP_EVIDENCE");
        Assert.True(KeyboardGet(0x16, 0, out uint delay, 0));
        try
        {
            var compilation = _package.Value;
            Assert.True(compilation.Manifest.Package!.OriginalStartupConfigured);
            Assert.False(compilation.Manifest.Package.OriginalStartupEntered);
            Assert.False(compilation.Manifest.ApplicationAccepted);
            string executable = Path.Combine(directory, "CHIPS64.EXE");
            File.WriteAllBytes(executable, compilation.Executable!);
            foreach (string asset in Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p).ToUpperInvariant() is ".DAT" or ".WAV" or ".MID" or ".HLP"))
            {
                File.Copy(asset, Path.Combine(directory, Path.GetFileName(asset)));
            }

            Assert.False(File.Exists(Path.Combine(directory, "CHIPS.EXE")));
            Assert.False(File.Exists(Path.Combine(directory, "WEP4UTIL.DLL")));
            Assert.Empty(Directory.GetFiles(directory, "*.json"));
            Assert.False(File.Exists(Path.Combine(directory, "entpack.ini")));
            var observations = new List<object>();
            uint savedColor = 0;
            nint display = GetDC(0);
            int hostNumColors;
            try
            {
                hostNumColors = GetDeviceCaps(display, 24);
            }
            finally
            {
                ReleaseDC(0, display);
            }

            for (int run = 0; run < 2; run++)
            {
                string trace = Path.Combine(directory, $"startup-{run}.bin");
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath()
                };
                start.Environment["WIN16_TRACE_FILE"] = trace;
                using var process = Process.Start(start)!;
                try
                {
                    nint window = 0;
                    Until(() =>
                    {
                        window = Find(process, "MainClass");
                        return window != 0 && Text(window).Contains(run == 0
                            ? "LESSON 1"
                            : "LESSON 2", StringComparison.Ordinal);
                    }, process, "Original initial window/level did not appear");
                    var children = new List<(nint Window, string Class)>();
                    EnumChildWindows(window, (w, _) =>
                    {
                        children.Add((w, Class(w)));
                        return true;
                    }, 0);
                    // The original ShowHint creates HintClass lazily on hint tiles;
                    // it is not part of the initial window hierarchy.
                    foreach (string name in new[]
                    {
                        "BoardClass",
                        "InfoClass",
                        "CounterClass",
                        "InventoryClass"
                    }

                    )
                    {
                        Assert.Contains(children, c => c.Class == name);
                    }

                    Assert.Equal(3, children.Count(c => c.Class == "CounterClass"));
                    Assert.NotEqual(0, GetMenu(window));
                    if (run == 0)
                    {
                        nint board = children.First(c => c.Class == "BoardClass").Window;
                        Key(window, 0x20);
                        Thread.Sleep(250);
                        Assert.True(GetClientRect(board, out var rect));
                        nint dc = GetDC(board), capture = CreateCompatibleDC(dc), bitmap = CreateCompatibleBitmap(dc, rect.Right, rect.Bottom), old = SelectObject(capture, bitmap);
                        var colors = new HashSet<uint>();
                        try
                        {
                            Assert.True(PrintWindow(board, capture, 3));
                            for (int y = 8; y < rect.Bottom; y += 8)
                            {
                                for (int x = 8; x < rect.Right; x += 8)
                                {
                                    colors.Add(GetPixel(capture, x, y));
                                }
                            }
                        }
                        finally
                        {
                            SelectObject(capture, old);
                            DeleteObject(bitmap);
                            DeleteDC(capture);
                            ReleaseDC(board, dc);
                        }

                        colors.Remove(uint.MaxValue);
                        Assert.True(colors.Count >= 2, "Original board was blank; observed colors: " + string.Join(",", colors.Select(c => c.ToString("X6"))));
                        Key(window, 0x27); // ordinary key input, no guest-memory writes
                        CloseDialog(process, Dialog(process, window, 0x64)); // utility About
                        CloseDialog(process, Dialog(process, window, 0x73)); // original Best Times
                        uint color = Checked(window, 0x7a);
                        Command(window, 0x7a);
                        Until(() => Checked(window, 0x7a) != color, process, "Color menu did not toggle");
                        if (Checked(window, 0x7a) == 0)
                        {
                            Command(window, 0x7a);
                            Until(() => Checked(window, 0x7a) != 0, process, "Color menu did not enable");
                        }

                        savedColor = Checked(window, 0x7a);
                        nint go = Dialog(process, window, 0x77);
                        Assert.NotEqual(0, GetDlgItem(go, 100));
                        Assert.NotEqual(0, GetDlgItem(go, 101));
                        Assert.DoesNotContain("You must enter", Describe(process));
                        SetField(go, 100, "2");
                        SetField(go, 101, "JXMJ");
                        Command(go, 1);
                        try
                        {
                            Until(() =>
                            {
                                if (Describe(process).Contains("You must enter", StringComparison.Ordinal))
                                {
                                    throw new InvalidOperationException("Original Goto outcome: " + Describe(process));
                                }

                                return !IsWindow(go) && Text(window).Contains("LESSON 2", StringComparison.Ordinal);
                            }, process, "Original Goto/password did not load level 2");
                        }
                        catch (TimeoutException)
                        {
                            throw new InvalidOperationException("Original Goto outcome: " + Describe(process));
                        }
                    }
                    else
                    {
                        // True-color hosts must restore the saved color preference.
                        Assert.Equal(hostNumColors != -1 && hostNumColors <= 2
                            ? 0u
                            : savedColor, Checked(window, 0x7a));
                    }

                    string title = Text(window);
                    uint selectedColor = Checked(window, 0x7a);
                    Assert.True(PostMessageW(window, 0x10, 0, 0));
                    Assert.True(process.WaitForExit(10000), "Original shutdown timed out");
                    Assert.Equal(0, process.ExitCode);
                    var parsed = ReadTrace(trace, compilation.Manifest.Package!, run == 0);
                    observations.Add(new
                    {
                        Run = run,
                        Title = title,
                        SavedColorPreference = savedColor == 0
                        ? 0
                        : 1,
                        SelectedColor = selectedColor,
                        Children = children.Select(c => c.Class).ToArray(),
                        Trace = parsed
                    });
                    string ini = File.ReadAllText(Path.Combine(directory, "entpack.ini"));
                    Assert.Contains("Current Level=2", ini);
                    Assert.Contains("Level2=JXMJ", ini);
                    Assert.Contains("Color=" + (savedColor == 0
                        ? 0
                        : 1), ini);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        var stop = Stopwatch.StartNew();
                        while (!process.HasExited && stop.ElapsedMilliseconds < 3000)
                        {
                            nint modal = Find(process, "#32770");
                            if (modal != 0)
                            {
                                PostMessageW(modal, 0x10, 0, 0);
                            }
                            else
                            {
                                nint root = Find(process, "MainClass");
                                if (root != 0)
                                {
                                    PostMessageW(root, 0x10, 0, 0);
                                }
                            }

                            Thread.Sleep(50);
                        }

                        if (!process.HasExited)
                        {
                            process.Kill(true);
                            process.WaitForExit();
                        }
                    }
                }
            }

            if (evidence is not null)
            {
                Directory.CreateDirectory(evidence);
                foreach (string file in Directory.GetFiles(directory, "*.bin").Concat(Directory.GetFiles(directory, "*.ini")))
                {
                    File.Copy(file, Path.Combine(evidence, Path.GetFileName(file)), true);
                }

                File.WriteAllText(Path.Combine(evidence, "native-startup.json"), JsonSerializer.Serialize(new
                {
                    WindowsHost = true,
                    SourceSha256 = compilation.Manifest.SourceSha256,
                    NativePayloadSha256 = compilation.Manifest.Package.NativePayloadSha256,
                    ExecutableSha256 = Convert.ToHexString(SHA256.HashData(compilation.Executable!)).ToLowerInvariant(),
                    HostNumColors = hostNumColors,
                    DisplayPolicy = "True-color NUMCOLORS is adapted to a positive Win16 color count",
                    PersistedConfigurationVerified = true,
                    OriginalInitialAddress = "1000:001A",
                    OriginalStartup = true,
                    FixtureInitialization = false,
                    OriginalExecutablePresent = false,
                    UtilityPresent = false,
                    SidecarRequired = false,
                    Runs = observations,
                    ApplicationAccepted = false
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            KeyboardSet(0x17, delay, 0, 0);
            if (evidence is not null)
            {
                Directory.CreateDirectory(evidence);
                foreach (string file in Directory.GetFiles(directory, "*.bin").Concat(Directory.GetFiles(directory, "*.ini")))
                {
                    File.Copy(file, Path.Combine(evidence, Path.GetFileName(file)), true);
                }
            }

            NeApplicationTests.DeleteProcessDirectory(directory);
        }
    }

    private static object ReadTrace(string path, NePackageManifest package, bool dialogs)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        uint[] header = Enumerable.Range(0, 24).Select(_ => r.ReadUInt32()).ToArray();
        Assert.Equal(0x38525457u, header[0]);
        Assert.Equal(1u, header[1]);
        Assert.Equal(0x1000001au, header[7]);
        Assert.Equal(0u, header[10]);
        Assert.Equal(0u, header[11]);
        Assert.Equal(1u, header[12]);
        Assert.Equal(0u, header[13]);
        Assert.Equal(0u, header[14]);
        Assert.All(header.Skip(20), n => Assert.Equal(0u, n));
        var entries = new List<(uint Kind, uint Address, uint Depth)>();
        for (uint i = 0; i < header[2]; i++)
        {
            uint[] e = Enumerable.Range(0, 8).Select(_ => r.ReadUInt32()).ToArray();
            entries.Add((e[0], e[1], e[5]));
        }

        var imports = package.Control.Imports.Where(i => i.Kind != "Constant").ToDictionary(i => (uint)i.RuntimeAddress.Segment << 16 | i.RuntimeAddress.Offset, i => i.Import.Identity);
        string[] ids = entries.Where(e => e.Kind == 2 && imports.ContainsKey(e.Address)).Select(e => imports[e.Address]).ToArray();
        Assert.Equal(new[] { "KERNEL.#91", "KERNEL.#3", "KERNEL.#102", "KERNEL.#30", "USER.#5" }, ids.Take(5));
        foreach (string id in new[]
        {
            "USER.#57",
            "USER.#41",
            "USER.#39",
            "USER.#40",
            "USER.#109",
            "KERNEL.#129"
        }

        )
        {
            Assert.Contains(id, ids);
        }

        foreach (uint message in new uint[]
        {
            1,
            0xf,
            0x10
        }

        )
        {
            Assert.Contains(entries, e => (e.Kind & 0xffff) == 5 && (e.Kind >> 16) == message);
        }

        var code = package.Control.Entries.ToDictionary(e => (uint)e.RuntimeAddress.Segment << 16 | e.RuntimeAddress.Offset, e => e.Original.Identity);
        Assert.All(entries.Where(e => e.Kind == 7), e => Assert.True(code.ContainsKey(e.Address), "Unapproved original callback entry"));
        if (dialogs)
        {
            Assert.Contains(entries, e => e.Kind == 7 && e.Address == 0x10280000u);
            Assert.Contains(entries, e => e.Kind == 7 && e.Address == 0x1028018eu);
        }

        string[] originalCallbacks = entries.Where(e => (e.Kind is 4 or 7) && code.ContainsKey(e.Address)).Select(e => code[e.Address]).Distinct().Order().ToArray();
        Assert.Contains(imports[header[9]], new[] { "KERNEL.#1", "KERNEL.#102" });
        if (imports[header[9]] == "KERNEL.#102")
        {
            Assert.Equal(0x4cu, header[18] >> 24);
        }

        Assert.True(header[6] > 0);
        Assert.Equal(r.BaseStream.Length, r.BaseStream.Position);
        return new
        {
            OriginalCallbacks = originalCallbacks,
            Blocks = header[4],
            ImportCalls = header[5],
            Callbacks = header[6],
            DroppedEvents = header[3],
            DistinctImports = ids.Distinct().Order().ToArray(),
            WindowMessages = entries.Where(e => (e.Kind & 0xffff) == 5).Select(e => e.Kind >> 16).Distinct().Order().ToArray(),
            MaximumObservedCallbackDepth = entries.Max(e => e.Depth),
            ExitImport = imports[header[9]],
            ExitAx = header[18] >> 16,
            ExitCode = header[13],
            Error = header[10],
            ExecutableWrites = header[14],
            RuntimeStorageReleased = header.Skip(20).All(n => n == 0)
        };
    }
}
