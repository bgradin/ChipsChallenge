using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Translator.Binary;
using Translator.ChipsChallenge;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(nint window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    private sealed class ParitySession : IDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "native-parity-native-" + Guid.NewGuid().ToString("N"));
        public readonly string Evidence;
        public readonly Process Process;
        public readonly nint Window;
        private readonly uint _keyboardDelay;

        public ParitySession(string name, byte[]? levelData = null, string initialTitle = "LESSON 1", string? profile = null)
        {
            Assert.True(KeyboardGet(0x16, 0, out _keyboardDelay, 0));
            string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
            Evidence = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_PARITY_EVIDENCE") ?? Directory, name);
            System.IO.Directory.CreateDirectory(Directory);
            System.IO.Directory.CreateDirectory(Evidence);
            string output = Path.Combine(Directory, "CHIPS64.EXE");
            File.WriteAllBytes(output, _package.Value.Executable!);
            foreach (string asset in Translator.Delivery.TranslationService.RequiredAssets)
            {
                File.Copy(Path.Combine(Path.GetDirectoryName(source)!, asset), Path.Combine(Directory, asset));
            }

            File.WriteAllBytes(Path.Combine(Directory, "CHIPS.HLP"), SampleWinHelp.Create(keyword: "How to Play"));
            File.Copy(Path.Combine(Directory, "CHIPS.HLP"), Path.Combine(Directory, "CHIPS64.HLP"));
            File.WriteAllBytes(Path.Combine(Directory, "CHIPS64.CHM"), SupportedChipsHelp.Convert(File.ReadAllBytes(Path.Combine(Directory, "CHIPS.HLP"))));
            if (levelData is not null)
            {
                File.WriteAllBytes(Path.Combine(Directory, "CHIPS.DAT"), levelData);
            }

            if (profile is not null)
            {
                File.WriteAllText(Path.Combine(Directory, "entpack.ini"), profile);
            }

            Assert.False(File.Exists(Path.Combine(Directory, "CHIPS.EXE")));
            Assert.False(File.Exists(Path.Combine(Directory, "WEP4UTIL.DLL")));
            var start = new ProcessStartInfo(output)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            };
            start.Environment["WIN16_TRACE_FILE"] = Path.Combine(Directory, "execution.bin");
            Process = System.Diagnostics.Process.Start(start)!;
            try
            {
                nint w = 0;
                Until(() =>
                {
                    w = Find(Process, "MainClass");
                    return w != 0 && Text(w).Contains(initialTitle, StringComparison.Ordinal);
                }, Process, "Native parity startup failed");
                Window = w;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Close()
        {
            Assert.True(PostMessageW(Window, 0x10, 0, 0));
            Assert.True(Process.WaitForExit(15000), "Native parity shutdown timed out");
            Assert.Equal(0, Process.ExitCode);
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(Directory, "execution.bin")));
            uint[] h = Enumerable.Range(0, 24).Select(_ => reader.ReadUInt32()).ToArray();
            Assert.Equal(0x38525457u, h[0]);
            Assert.Equal(0x1000001au, h[7]);
            Assert.Equal(0u, h[10]);
            Assert.Equal(0u, h[11]);
            Assert.Equal(1u, h[12]);
            Assert.Equal(0u, h[13]);
            Assert.Equal(0u, h[14]);
            Assert.All(h.Skip(20), n => Assert.Equal(0u, n));
            Assert.Equal(96L + h[2] * 32L, reader.BaseStream.Length);
            File.WriteAllText(Path.Combine(Evidence, "trace-summary.json"), JsonSerializer.Serialize(new
            {
                OriginalInitialAddress = "1000:001A",
                Blocks = h[4],
                Imports = h[5],
                Callbacks = h[6],
                OmittedEvents = h[3],
                Error = h[10],
                ExitCode = h[13],
                ExecutableWrites = h[14],
                RuntimeStorageReleased = true
            }));
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    var timer = Stopwatch.StartNew();
                    while (!Process.HasExited && timer.ElapsedMilliseconds < 3000)
                    {
                        nint modal = Find(Process, "#32770");
                        PostMessageW(modal != 0
                            ? modal
                            : Window, 0x10, 0, 0);
                        Thread.Sleep(50);
                    }

                    if (!Process.HasExited)
                    {
                        Process.Kill(true);
                        Process.WaitForExit();
                    }
                }

                foreach (string p in System.IO.Directory.GetFiles(Directory, "*.bin").Concat(System.IO.Directory.GetFiles(Directory, "*.ini")))
                {
                    File.Copy(p, Path.Combine(Evidence, Path.GetFileName(p)), true);
                }
            }
            finally
            {
                KeyboardSet(0x17, _keyboardDelay, 0, 0);
                Process.Dispose();
                NeApplicationTests.DeleteProcessDirectory(Directory);
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] CaptureCenter(nint window, int width, int height)
    {
        Assert.True(GetClientRect(window, out var rect));
        nint dc = GetDC(window), copy = CreateCompatibleDC(dc), bitmap = CreateCompatibleBitmap(dc, rect.Right, rect.Bottom), old = SelectObject(copy, bitmap);
        try
        {
            Assert.True(PrintWindow(window, copy, 3));
            var result = new byte[width * height * 4];
            int left = (rect.Right - width) / 2, top = (rect.Bottom - height) / 2;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    uint pixel = GetPixel(copy, left + x, top + y);
                    Assert.NotEqual(uint.MaxValue, pixel);
                    int at = (y * width + x) * 4;
                    result[at] = (byte)(pixel >> 16);
                    result[at + 1] = (byte)(pixel >> 8);
                    result[at + 2] = (byte)pixel;
                }
            }

            return result;
        }
        finally
        {
            SelectObject(copy, old);
            DeleteObject(bitmap);
            DeleteDC(copy);
            ReleaseDC(window, dc);
        }
    }

    private static string VerifyCounter(nint window, int value, ushort flags, bool monochrome, NeDecodedBitmap digits)
    {
        var specification = new CounterPixelCase((ushort)value, flags, (ushort)(monochrome
            ? 1
            : 4), 51, 23, 0, 0, 51, 23, 0, "");
        string expected = Hash(ChipsCounterGraphics.Expected(digits, specification)), actual;
        // Title/dialog completion can precede the last counter update in the same
        // original command. Require eventual exact pixels, rather than racing it.
        var deadline = Stopwatch.StartNew();
        do
        {
            actual = Hash(CaptureCenter(window, 51, 23));
            if (actual == expected)
            {
                return actual;
            }

            Thread.Sleep(20);
        }
        while (deadline.ElapsedMilliseconds < 3000);
        Assert.True(expected == actual, $"Counter {GetDlgCtrlID(window)} value {value}, flags {flags}, mono={monochrome}: expected {expected}, actual {actual}");
        return actual;
    }

    [WindowsSuppliedNeFact]
    public void All149LevelsLoadThroughOriginalGotoAndPaintDatDerivedCountersInCleanDelivery()
    {
        string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        var levels = OriginalLevelCatalog.Read(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "CHIPS.DAT")));
        using var catalog = new NeResourceCatalog(File.ReadAllBytes(source));
        var resource = catalog.Resources.Single(r => r.Type.Id == 2 && r.Identity.Id == 200);
        var digits = NeBitmapDecoder.Decode(catalog.Extract(catalog.Resources.ToList().IndexOf(resource) + 1).Bytes);
        using var session = new ParitySession("all-levels");
        var observed = new List<object>();
        bool complete = false;
        try
        {
            var counters = new Dictionary<int, nint>();
            EnumChildWindows(session.Window, (w, _) =>
            {
                if (Class(w) == "CounterClass")
                {
                    counters.Add(GetDlgCtrlID(w), w);
                }

                return true;
            }, 0);
            Assert.Equal(new[] { 2, 3, 4 }, counters.Keys.Order());
            foreach (var level in levels)
            {
                nint go = Dialog(session.Process, session.Window, 0x77);
                SetField(go, 100, level.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SetField(go, 101, level.Password);
                Command(go, 1);
                Until(() => !IsWindow(go) && Text(session.Window).Contains(level.Title, StringComparison.Ordinal), session.Process, $"Original Goto failed for level {level.Number}: {level.Title}");
                bool mono = Checked(session.Window, 0x7a) == 0;
                // Original 0002:0CF4 selects the alternate palette at <=15 seconds,
                // including untimed levels, which also retain the blank-digit flag.
                ushort timeFlags = (ushort)((level.TimeLimit <= 15
                    ? 1
                    : 0) | (level.TimeLimit == 0
                    ? 2
                    : 0));
                // Original 0002:0D34 selects that palette when no chips remain,
                // including levels that start with zero required chips.
                ushort chipFlags = (ushort)(level.Chips == 0
                    ? 1
                    : 0);
                var hashes = new[]
                {
                    VerifyCounter(counters[2], level.Number, 0, mono, digits),
                    VerifyCounter(counters[3], level.TimeLimit, timeFlags, mono, digits),
                    VerifyCounter(counters[4], level.Chips, chipFlags, mono, digits)
                };
                observed.Add(new
                {
                    level.Number,
                    level.Title,
                    level.RecordSha256,
                    level.TimeLimit,
                    level.Chips,
                    CounterPixelHashes = hashes
                });
            }

            session.Close();
            string ini = File.ReadAllText(Path.Combine(session.Directory, "entpack.ini"));
            Assert.Contains("Current Level=149", ini);
            Assert.Contains("Level149=" + levels[^1].Password, ini);
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "levels.json"), JsonSerializer.Serialize(new
            {
                Format = "NeAllLevels/v1",
                SourceSha256 = _package.Value.Manifest.SourceSha256,
                ExecutableSha256 = Hash(_package.Value.Executable!),
                DataSha256 = OriginalLevelCatalog.SourceSha256,
                Expected = 149,
                Complete = complete,
                FixtureInitialization = false,
                OriginalExecutablePresent = false,
                Observations = observed
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [WindowsSuppliedNeFact]
    public void NativeWindowResizeMinimizeRestoreAndPauseRestartKeepOriginalUiResponsive()
    {
        using var session = new ParitySession("window-menus");
        bool complete = false;
        try
        {
            void ResumeOutstandingPauses()
            {
                // Original PauseGame/UnpauseGame increment/decrement a counter.
                // Minimize/restore and desktop activation can contribute more
                // than one pause; one menu command need not resume the game.
                for (int attempts = 0; attempts < 16; attempts++)
                {
                    ushort before = Observe(session).Global(0x24);
                    if (before == 0)
                    {
                        return;
                    }

                    Command(session.Window, 0x74);
                    Until(() => Observe(session).Global(0x24) < before, session.Process, "Pause menu did not decrement the original pause counter");
                }

                Assert.Equal((ushort)0, Observe(session).Global(0x24));
            }

            Assert.True(SetWindowPos(session.Window, 0, 0, 0, 800, 650, 0x16));
            Thread.Sleep(150);
            ShowWindow(session.Window, 6);
            Until(() => IsIconic(session.Window), session.Process, "Minimize failed");
            ShowWindow(session.Window, 9);
            Until(() => !IsIconic(session.Window), session.Process, "Restore failed");
            ResumeOutstandingPauses();
            Key(session.Window, 0x20);
            Key(session.Window, 0x25);
            Key(session.Window, 0x72); // F3 passes through the original accelerator/message loop
            Until(() => Checked(session.Window, 0x74) != 0, session.Process, "F3 did not pause the game");
            nint info = 0;
            EnumChildWindows(session.Window, (w, _) =>
            {
                if (Class(w) == "InfoClass")
                {
                    info = w;
                }

                return true;
            }, 0);
            nint time = GetDlgItem(info, 3);
            Assert.NotEqual(0, time);
            byte[] frozen = CaptureCenter(time, 51, 23);
            Thread.Sleep(1200);
            Assert.Equal(frozen, CaptureCenter(time, 51, 23));
            ResumeOutstandingPauses();
            Until(() => Checked(session.Window, 0x74) == 0, session.Process, "Pause menu did not resume after balancing nested pauses");
            Until(() => !frozen.AsSpan().SequenceEqual(CaptureCenter(time, 51, 23)), session.Process, "Counter did not advance after resume", 5000);
            Command(session.Window, 0x71);
            Thread.Sleep(200);
            Assert.Contains("LESSON 1", Text(session.Window));
            CloseDialog(session.Process, Dialog(session.Process, session.Window, 0x73));
            session.Close();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "window-menus.json"), JsonSerializer.Serialize(new
            {
                Format = "NeWindowMenus/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                Verified = new[] { "resize", "minimize", "restore", "F3-pause-accelerator", "pause-freezes-time-counter", "menu-resume-advances-time-counter", "restart-command-responsiveness", "best-times-after-restart" },
                Limitation = "Does not establish complete focus ownership, all accelerator semantics or restart gameplay state."
            }));
        }
    }
}
