using System.Text.Json;
using System.Runtime.InteropServices;
using Translator.Binary;
using Translator.ChipsChallenge;
using Translator.TestSupport;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint first, uint lines, byte[] pixels, byte[] info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint dc, ref Rect rect, nint brush);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint window, ref Rect rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(nint window);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);

    private static byte[] CaptureBoard(nint window, Rect? clip = null)
    {
        Assert.True(GetClientRect(window, out var rect));
        Assert.Equal(288, rect.Right);
        Assert.Equal(288, rect.Bottom);
        nint dc = GetDC(window), copy = CreateCompatibleDC(dc), bitmap = CreateCompatibleBitmap(dc, 288, 288), old = SelectObject(copy, bitmap);
        try
        {
            if (clip is { } bounds)
            {
                // Poison only this private test window's surface, then ask the
                // native invalid-region/BeginPaint path to repair the clip.
                // PrintWindow would replace the supplied HDC clip with its own
                // full-client paint region and cannot establish partial paint.
                nint brush = CreateSolidBrush(0x664422);
                try
                {
                    Assert.NotEqual(0, FillRect(dc, ref rect, brush));
                }
                finally
                {
                    DeleteObject(brush);
                }

                Assert.True(InvalidateRect(window, ref bounds, false));
                Assert.True(UpdateWindow(window));
                Assert.True(BitBlt(copy, 0, 0, 288, 288, dc, 0, 0, 0xcc0020));
            }
            else
            {
                Assert.True(PrintWindow(window, copy, 3));
            }

            SelectObject(copy, old);
            var info = new byte[40];
            BitConverter.GetBytes(40).CopyTo(info, 0);
            BitConverter.GetBytes(288).CopyTo(info, 4);
            BitConverter.GetBytes(-288).CopyTo(info, 8);
            BitConverter.GetBytes((ushort)1).CopyTo(info, 12);
            BitConverter.GetBytes((ushort)32).CopyTo(info, 14);
            var bytes = new byte[288 * 288 * 4];
            Assert.Equal(288, GetDIBits(copy, bitmap, 0, 288, bytes, info, 0));
            for (int at = 3; at < bytes.Length; at += 4)
            {
                bytes[at] = 0;
            }

            return bytes;
        }
        finally
        {
            SelectObject(copy, old);
            DeleteObject(bitmap);
            DeleteDC(copy);
            ReleaseDC(window, dc);
        }
    }

    [WindowsSuppliedNeFact]
    public void NativeLessonThreeAcquiresAllBootsAndCrossesWaterIceFireAndForceWithExactInventoryPixels()
    {
        string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        var level = OriginalLevelCatalog.Read(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "CHIPS.DAT")))[2];
        using var catalog = new NeResourceCatalog(File.ReadAllBytes(source));
        using var session = new ParitySession("lesson3-equipment");
        bool complete = false;
        var observed = new List<object>();
        try
        {
            nint go = Dialog(session.Process, session.Window, 0x77);
            SetField(go, 100, "3");
            SetField(go, 101, level.Password);
            Command(go, 1);
            Until(() => !IsWindow(go) && Text(session.Window).Contains(level.Title, StringComparison.Ordinal), session.Process, "LESSON 3 selection failed");
            bool mono = Checked(session.Window, 0x7a) == 0;
            string atlasName = mono
                ? "OBJ32_1"
                : "OBJ32_4";
            int atlasIndex = catalog.Resources.ToList().FindIndex(r => r.Type.Id == 2 && r.Identity.Name == atlasName) + 1;
            var atlas = NeBitmapDecoder.Decode(catalog.Extract(atlasIndex).Bytes);
            nint board = 0, inventory = 0, chips = 0;
            EnumChildWindows(session.Window, (w, _) =>
            {
                if (Class(w) == "BoardClass")
                {
                    board = w;
                }

                if (Class(w) == "InventoryClass")
                {
                    inventory = w;
                }

                if (Class(w) == "CounterClass" && GetDlgCtrlID(w) == 4)
                {
                    chips = w;
                }

                return true;
            }, 0);
            Assert.NotEqual(0, board);
            Assert.NotEqual(0, inventory);
            Assert.NotEqual(0, chips);
            int digitIndex = catalog.Resources.ToList().FindIndex(r => r.Type.Id == 2 && r.Identity.Id == 200) + 1;
            var digits = NeBitmapDecoder.Decode(catalog.Extract(digitIndex).Bytes);
            byte[] terrain = level.Upper.ToArray();
            int position = Array.FindIndex(terrain, t => t is >= 0x6c and <= 0x6f);
            Assert.True(position >= 0);
            terrain[position] = level.Lower[position];
            ushort[] equipment = new ushort[8];
            int remaining = level.Chips;
            var traversed = new HashSet<byte>();
            Key(session.Window, 0x20);

            // Test-side path planning uses the original static tutorial board.
            // It supplies ordinary keys only, never game globals or guest calls.
            bool Allowed(byte t) => t is 0 or 2 or 11 or 47 or >= 104 and <= 107 || t == 3 && equipment[4] != 0 || t == 4 && equipment[5] != 0 || t is 12 or 26 or 27 or 28 or 29 && equipment[6] != 0 || t is 13 or 18 or 19 or 20 && equipment[7] != 0;


            List<int> Route(int target)
            {
                var queue = new Queue<int>();
                var previous = new Dictionary<int, int>
                {
                    [position] = -1
                };
                queue.Enqueue(position);
                while (queue.TryDequeue(out int at) && !previous.ContainsKey(target))
                {
                    foreach (int delta in new[]
                    {
                        -32,
                        -1,
                        1,
                        32
                    }

                    )
                    {
                        int next = at + delta;
                        if (next < 0 || next >= 1024 || Math.Abs(next % 32 - at % 32) > 1 || previous.ContainsKey(next) || !Allowed(terrain[next]))
                        {
                            continue;
                        }

                        previous.Add(next, at);
                        queue.Enqueue(next);
                    }
                }

                Assert.True(previous.ContainsKey(target), "No safe original-board route to equipment/hazard");
                var path = new List<int>();
                for (int at = target; at != position; at = previous[at])
                {
                    path.Add(at);
                }

                path.Reverse();
                return path;
            }


            byte facing = 0x6c;

            void Walk(int target)
            {
                foreach (int next in Route(target))
                {
                    int delta = next - position;
                    uint key = delta == -32
                        ? 0x26u
                        : delta == 32
                            ? 0x28u
                            : delta == -1
                                ? 0x25u
                                : 0x27u;
                    facing = delta == -32
                        ? (byte)0x6c
                        : delta == 32
                            ? (byte)0x6e
                            : delta == -1
                                ? (byte)0x6d
                                : (byte)0x6f;
                    byte tile = terrain[next];
                    traversed.Add(tile);
                    Key(session.Window, key);
                    // The original accepts one buffered voluntary move and
                    // clears it on later even ticks. Pace scripted keys so a
                    // backlog cannot silently replace/drop another direction.
                    Thread.Sleep(300);
                    position = next;
                    if (tile == 2)
                    {
                        remaining--;
                        terrain[next] = 0;
                    }

                    if (tile is >= 104 and <= 107)
                    {
                        equipment[4 + tile - 104] = 1;
                        terrain[next] = 0;
                    }

                    if (tile == 11)
                    {
                        terrain[next] = 0;
                    }
                }
            }


            void CheckInventory(string stage)
            {
                byte[] expected = ChipsSceneProfile.Expected(atlas, atlas, new byte[0xa42], 6, 128, 64, 0, 0, 128, 64, authenticLevel: true, inventory: equipment);
                string reference = Hash(expected);
                string actual = "";
                Until(() =>
                {
                    actual = Hash(CaptureCenter(inventory, 128, 64));
                    return actual == reference;
                }, session.Process, "Inventory pixel mismatch after " + stage);
                VerifyCounter(chips, remaining, 0, mono, digits);
                // A centered sprite alone cannot establish absolute position.
                // Compare the entire DAT-derived viewport and its surroundings.
                var state = new byte[0xa42];
                terrain.CopyTo(state, 0);
                level.Lower.CopyTo(state, 0x400);
                state[position] = facing;
                BitConverter.GetBytes((ushort)Math.Clamp(position % 32 - 4, 0, 23)).CopyTo(state, 0xa24);
                BitConverter.GetBytes((ushort)Math.Clamp(position / 32 - 4, 0, 23)).CopyTo(state, 0xa26);
                string expectedBoard = Hash(ChipsSceneProfile.Expected(atlas, atlas, state, 1, 288, 288, 0, 0, 288, 288, authenticLevel: true)), actualBoard = "";
                Until(() =>
                {
                    actualBoard = Hash(CaptureBoard(board));
                    return actualBoard == expectedBoard;
                }, session.Process, "Original full-board pixels/position mismatch after " + stage);
                observed.Add(new
                {
                    Stage = stage,
                    X = position % 32,
                    Y = position / 32,
                    Inventory = equipment.ToArray(),
                    RemainingChips = remaining,
                    InventoryPixelSha256 = actual,
                    BoardPixelSha256 = actualBoard
                });
            }

            foreach (byte boot in new byte[]
            {
                104,
                106,
                105,
                107
            }

            )
            {
                int target = Array.IndexOf(terrain, boot);
                Assert.True(target >= 0);
                Walk(target);
                CheckInventory("pickup-" + boot.ToString("X2"));
            }

            // With suction boots the force floor must allow deliberate movement.
            // Return to a known floor afterward for an independent position image.
            int force = Enumerable.Range(0, 1024).Where(i => terrain[i] is 13 or 18 or 19 or 20).OrderBy(i => Math.Abs(i % 32 - position % 32) + Math.Abs(i / 32 - position / 32)).First();
            Walk(force);
            Walk(15 * 32 + 15);
            CheckInventory("force-with-suction-and-return");
            foreach (byte tile in new byte[]
            {
                3,
                4,
                12
            }

            )
            {
                Assert.Contains(tile, traversed);
            }

            Assert.Contains(traversed, t => t is 13 or 18 or 19 or 20);
            Assert.Contains("LESSON 3", Text(session.Window));
            session.Close();
            complete = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(session.Evidence, "equipment.json"), JsonSerializer.Serialize(new
            {
                Format = "NeLesson3Equipment/v1",
                ExecutableSha256 = Hash(_package.Value.Executable!),
                Complete = complete,
                DataSha256 = OriginalLevelCatalog.SourceSha256,
                FixtureInitialization = false,
                Observations = observed,
                Limitation = "Authentic LESSON 3 positive equipment route; no claim about every collision, loss, hazard or actor interaction."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
