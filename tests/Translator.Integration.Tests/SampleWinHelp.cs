using System.Text;

namespace Translator.Integration.Tests;

/// <summary>Authored, minimal HC30 fixtures. No bytes or expected content are taken from game assets.</summary>
internal static class SampleWinHelp
{
    internal static byte[] Create(string body = "Repeated phrase & sample <text>", bool phrases = false, bool packedBitmap = false, string keyword = "sample-key", int contentsId = 23, int detailsId = 41, int extraKeywords = 0, bool ambiguousKeyword = false, int bitmapBits = 24)
    {
        var files = new Dictionary<string, byte[]>();
        var records = new List<byte[]>();
        int position = 12;

        byte[] Record(byte type, byte[] format, byte[] text, int? expanded = null)
        {
            int length = 21 + format.Length + text.Length;
            int after = position, remaining = length;
            while (remaining > 0)
            {
                int take = Math.Min(remaining, 2048 - after % 2048);
                after += take;
                remaining -= take;
                if (after % 2048 == 0)
                {
                    after += 12;
                }
            }

            byte[] record = Bytes(w =>
            {
                w.Write(length);
                w.Write(expanded ?? text.Length);
                w.Write(0);
                w.Write(after - position);
                w.Write(21 + format.Length);
                w.Write(type);
                w.Write(format);
                w.Write(text);
            });
            return Bytes(w =>
            {
                int at = position;
                foreach (byte value in record)
                {
                    if (at % 2048 == 0)
                    {
                        w.Write(new byte[12]);
                        at += 12;
                    }

                    w.Write(value);
                    at++;
                }

                if (at % 2048 == 0)
                {
                    w.Write(new byte[12]);
                }
            });
        }


        int Header(string title)
        {
            int start = position;
            var record = Record(2, [], Text(title));
            records.Add(record);
            position += record.Length;
            return start;
        }

        void Display(string[] chunks, Action<BinaryWriter> commands)
        {
            byte[] plain = chunks.SelectMany(Text).ToArray();
            byte[] text = plain;
            if (phrases)
            {
                var compressed = new List<byte>();
                byte[] phrase = Encoding.ASCII.GetBytes("Repeated phrase");
                for (int at = 0; at < plain.Length;)
                {
                    if (plain.AsSpan(at).StartsWith(phrase))
                    {
                        compressed.AddRange([1, 0]);
                        at += phrase.Length;
                    }
                    else
                    {
                        compressed.Add(plain[at++]);
                    }
                }

                text = compressed.ToArray();
            }

            byte[] format = Bytes(w =>
            {
                w.Write((ushort)0x8000);
                w.Write(0);
                w.Write((ushort)0);
                commands(w);
            });
            byte[] record = Record(1, format, text, plain.Length);
            records.Add(record);
            position += record.Length;
        }

        int contentsPosition = Header("Sample contents");
        Display(["", body, "", "details", "", ""], w =>
        {
            w.Write((byte)0x80);
            w.Write((ushort)1); // authored bold/italic font
            w.Write((byte)0x81); // newline
            w.Write((byte)0xe0);
            w.Write(detailsId); // context link to second topic
            w.Write((byte)0x89); // hotspot end
            w.Write((byte)0x86);
            w.Write((byte)3);
            w.Write((ushort)0x8008);
            w.Write((ushort)0);
            w.Write((ushort)7);
            w.Write((byte)0xff);
        });
        int detailsPosition = Header("Sample details");
        Display(["A separate authored topic."], w => w.Write((byte)0xff));
        records.Add(Record(2, [], []));
        files["|TOPIC"] = Bytes(w =>
        {
            w.Write(0);
            w.Write(12);
            w.Write(0);
            foreach (var r in records)
            {
                w.Write(r);
            }
        });
        files["|TOMAP"] = Bytes(w =>
        {
            w.Write(contentsPosition);
            for (int n = 1; n <= Math.Max(contentsId, detailsId); n++)
            {
                w.Write(n == contentsId
                    ? contentsPosition
                    : n == detailsId
                        ? detailsPosition
                        : 0);
            }
        });
        files["|SYSTEM"] = Bytes(w =>
        {
            w.Write((ushort)0x036c);
            w.Write((ushort)15);
            w.Write((ushort)1);
        });
        files["|Phrases"] = phrases
            ? Bytes(w =>
        {
            w.Write((ushort)1);
            w.Write((ushort)0x100);
            w.Write((ushort)4);
            w.Write((ushort)19);
            w.Write(Encoding.ASCII.GetBytes("Repeated phrase"));
        })
            : Bytes(w =>
        {
            w.Write((ushort)0);
            w.Write((ushort)0x100);
            w.Write((ushort)2);
        });
        files["|FONT"] = Bytes(w =>
        {
            w.Write((ushort)1);
            w.Write((ushort)2);
            w.Write((ushort)8);
            w.Write((ushort)16);
            w.Write(new byte[] { (byte)'T', (byte)'e', (byte)'s', (byte)'t', 0, 0, 0, 0 });
            foreach (byte attributes in new byte[]
            {
                0,
                3
            }

            )
            {
                w.Write(attributes);
                w.Write((byte)24);
                w.Write((byte)0);
                w.Write((ushort)0);
                w.Write(new byte[] { 128, 0, 0, 255, 255, 255 });
            }
        });
        files["bm7"] = Bitmap(packedBitmap, bitmapBits);
        files["|KWDATA"] = Bytes(w =>
        {
            w.Write(detailsPosition);
            if (ambiguousKeyword)
            {
                w.Write(contentsPosition);
            }
        });
        var keywords = new List<byte[]>
        {
            Bytes(w =>
            {
                w.Write(Text(keyword));
                w.Write((ushort)(ambiguousKeyword
                    ? 2
                    : 1));
                w.Write(0);
            })
        };
        for (int i = 0; i < extraKeywords; i++)
        {
            string name = "Authored-keyword-" + i.ToString("D4");
            keywords.Add(Bytes(w =>
            {
                w.Write(Text(name));
                w.Write((ushort)1);
                w.Write(0);
            }));
        }

        files["|KWBTREE"] = Tree(keywords);
        return Container(files);
    }

    internal static byte[] Bitmap(bool packed, int bits = 24)
    {
        byte[] palette = bits == 24
            ? []
            : Enumerable.Range(0, 1 << bits).SelectMany(i => new byte[] { (byte)(bits == 1
                ? i * 255
                : i), (byte)(bits == 1
                ? i * 255
                : i), (byte)(bits == 1
                ? i * 255
                : i), 0 }).ToArray();
        byte[] raw = bits == 24
            ? [0, 0, 255, 0, 255, 0, 0, 0]
            : [(byte)(bits == 1
                ? 0x80
                : bits == 4
                    ? 0x12
                    : 1), 0, 0, 0];
        byte[] pixels = packed
            ? [(byte)raw.Length, 0xaa]
            : raw;
        return Bytes(w =>
        {
            w.Write((ushort)0x506c);
            w.Write((ushort)1);
            w.Write(8);
            w.Write((byte)6);
            w.Write((byte)(packed
                ? 1
                : 0));
            w.Write((ushort)192);
            w.Write((ushort)192); // 96 DPI, packed integers
            w.Write((byte)2);
            w.Write((byte)(bits * 2)); // packed plane/bit counts
            foreach (ushort value in new ushort[]
            {
                4,
                2,
                0,
                0,
                (ushort)(pixels.Length * 2),
                0
            }

            )
            {
                w.Write(value);
            }

            w.Write(28 + palette.Length);
            w.Write(0); // picture-relative offset, no hotspots
            w.Write(palette);
            w.Write(pixels);
        });
    }

    internal static byte[] Container(IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);
        w.Write(0x00035f3f);
        w.Write(0);
        w.Write(-1);
        w.Write(0);
        var directory = new List<byte[]>();
        foreach (var file in files.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            int offset = checked((int)output.Position);
            w.Write(file.Value.Length + 9);
            w.Write(file.Value.Length);
            w.Write((byte)0);
            w.Write(file.Value);
            directory.Add(Bytes(entry =>
            {
                entry.Write(Text(file.Key));
                entry.Write(offset);
            }));
        }

        int root = checked((int)output.Position);
        byte[] tree = Tree(directory);
        w.Write(tree.Length + 9);
        w.Write(tree.Length);
        w.Write((byte)0);
        w.Write(tree);
        int length = checked((int)output.Length);
        output.Position = 4;
        w.Write(root);
        output.Position = 12;
        w.Write(length);
        return output.ToArray();
    }

    internal static byte[] Tree(IReadOnlyCollection<byte[]> entries)
    {
        const int size = 512;
        var pages = new List<List<byte[]>>
        {
            new()
        };
        int used = 0;
        foreach (var entry in entries)
        {
            if (entry.Length > size - 8)
            {
                throw new InvalidOperationException("Oversized fixture entry.");
            }

            if (used + entry.Length > size - 8)
            {
                pages.Add(new());
                used = 0;
            }

            pages[^1].Add(entry);
            used += entry.Length;
        }

        return Bytes(w =>
        {
            w.Write((ushort)0x293b);
            w.Write((ushort)0);
            w.Write((ushort)size);
            w.Write(new byte[20]);
            w.Write((short)0);
            w.Write((ushort)0);
            w.Write((ushort)pages.Count);
            w.Write((ushort)1);
            w.Write(entries.Count);
            for (int i = 0; i < pages.Count; i++)
            {
                byte[] data = pages[i].SelectMany(e => e).ToArray();
                w.Write((ushort)(size - 8 - data.Length));
                w.Write((ushort)pages[i].Count);
                w.Write((short)(i - 1));
                w.Write((short)(i + 1 == pages.Count
                    ? -1
                    : i + 1));
                w.Write(data);
                w.Write(new byte[size - 8 - data.Length]);
            }
        });
    }

    internal static byte[] Text(string text) => Encoding.Latin1.GetBytes(text + "\0");

    internal static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            write(writer);
        }

        return stream.ToArray();
    }
}
