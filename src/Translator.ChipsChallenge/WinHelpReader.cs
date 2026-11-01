using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace Translator.ChipsChallenge;

internal sealed record HelpTopic(int Number, int Position, string Title, string Html);
internal sealed record HelpKeyword(string Text, IReadOnlyList<int> Topics);
internal sealed record HelpDocument(IReadOnlyList<HelpTopic> Topics, IReadOnlyDictionary<string, byte[]> Images, IReadOnlyList<HelpKeyword> Keywords, int Contents);
/// <summary>Bounded HC30 reader: directory, phrases, topics, fonts, keywords and DIBs.
/// No prepared content is used.</summary>
internal static class WinHelpReader
{
    private const int _limit = 32 * 1024 * 1024;

    private static InvalidDataException Invalid(string reason) => new("Invalid or unsupported WinHelp file: " + reason);

    internal static string TopicFile(int n) => "topic" + n.ToString(CultureInfo.InvariantCulture) + ".htm";

    private static string Escape(string text) => WebUtility.HtmlEncode(text);
    private sealed class Cursor(byte[] data, int position = 0)
    {
        public int Position = position;
        public int Remaining => data.Length - Position;

        public void Require(int size)
        {
            if (size < 0 || Position < 0 || size > Remaining)
            {
                throw Invalid("truncated record");
            }
        }

        public byte Byte()
        {
            Require(1);
            return data[Position++];
        }

        public ushort Word()
        {
            Require(2);
            ushort n = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position));
            Position += 2;
            return n;
        }

        public int Int()
        {
            Require(4);
            int n = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Position));
            Position += 4;
            return n;
        }

        public byte[] Bytes(int size)
        {
            Require(size);
            var b = data.AsSpan(Position, size).ToArray();
            Position += size;
            return b;
        }

        public void Skip(int size)
        {
            Require(size);
            Position += size;
        }

        public int PackedWord()
        {
            int n = Byte();
            return (n >> 1) + ((n & 1) != 0
                ? Byte() * 128
                : 0);
        }

        public int PackedInt()
        {
            int n = Word();
            return checked((n >> 1) + ((n & 1) != 0
                ? Word() * 32768
                : 0));
        }

        public int SignedPackedInt()
        {
            int at = Position, n = PackedInt();
            return n - (Position - at == 2
                ? 16384
                : 1073741824);
        }

        public int SignedPackedWord()
        {
            int at = Position, n = PackedWord();
            return n - (Position - at == 1
                ? 64
                : 16384);
        }

        public string Text()
        {
            int start = Position;
            while (Byte() != 0)
            {
            }

            return Ansi(data.AsSpan(start, Position - start - 1));
        }
    }

    private static string Ansi(ReadOnlySpan<byte> bytes)
    {
        const string high = "\u20ac\u0081\u201a\u0192\u201e\u2026\u2020\u2021\u02c6\u2030\u0160\u2039\u0152\u008d\u017d\u008f\u0090\u2018\u2019\u201c\u201d\u2022\u2013\u2014\u02dc\u2122\u0161\u203a\u0153\u009d\u017e\u0178";
        var text = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            text.Append(b is >= 128 and < 160
                ? high[b - 128]
                : (char)b);
        }

        return text.ToString();
    }

    public static HelpDocument Read(byte[] source)
    {
        if (source.Length < 16 || source.Length > _limit)
        {
            throw Invalid("invalid file size");
        }

        var h = new Cursor(source);
        if (h.Int() != 0x00035f3f)
        {
            throw Invalid("not a WinHelp file");
        }

        int directory = h.Int();
        h.Int();
        if (h.Int() != source.Length)
        {
            throw Invalid("file length does not match header");
        }

        byte[] InternalFile(int offset)
        {
            var c = new Cursor(source, offset);
            int reserved = c.Int(), used = c.Int();
            c.Byte();
            if (reserved < 9 || used < 0 || used > reserved - 9 || offset > source.Length - reserved)
            {
                throw Invalid("invalid internal file extent");
            }

            return c.Bytes(used);
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long internalBytes = 0;
        foreach (var page in Leaves(InternalFile(directory)))
        {
            for (int i = 0; i < page.Count; i++)
            {
                string name = page.Data.Text();
                byte[] file = InternalFile(page.Data.Int());
                internalBytes += file.Length;
                if (internalBytes > _limit)
                {
                    throw Invalid("internal files exceed conversion limits");
                }

                if (!files.TryAdd(name, file))
                {
                    throw Invalid("duplicate filename");
                }
            }
        }

        byte[] Required(string name) => files.TryGetValue(name, out var data)
            ? data
            : throw Invalid("missing " + name);

        var system = new Cursor(Required("|SYSTEM"));
        if (system.Word() != 0x036c || system.Word() != 15 || system.Word() != 1)
        {
            throw Invalid("only HC30 / WinHelp 3.0 is currently supported");
        }

        var map = new Cursor(Required("|TOMAP"));
        var numbers = new Dictionary<int, int>();
        int contentsPosition = map.Int();
        for (int number = 1; map.Remaining != 0; number++)
        {
            int position = map.Int();
            if (position != 0 && !numbers.TryAdd(position, number))
            {
                throw Invalid("duplicate topic position");
            }
        }

        var phrases = ReadPhrases(Required("|Phrases"));
        var fonts = ReadFonts(Required("|FONT"));
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long imageBytes = 0;
        foreach (var file in files)
        {
            if (file.Key.StartsWith("bm", StringComparison.Ordinal) && int.TryParse(file.Key.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                byte[] image = ReadBitmap(file.Value);
                imageBytes += image.Length;
                if (imageBytes > _limit)
                {
                    throw Invalid("images exceed conversion limits");
                }

                images.Add(file.Key + ".bmp", image);
            }
        }

        var topics = ReadTopics(Required("|TOPIC"), numbers, phrases, fonts, images);
        if (topics.Count != numbers.Count || !numbers.TryGetValue(contentsPosition, out int contents))
        {
            throw Invalid("incomplete topic map");
        }

        var keywords = new List<HelpKeyword>();
        if (files.TryGetValue("|KWBTREE", out var tree))
        {
            byte[] data = Required("|KWDATA");
            foreach (var page in Leaves(tree))
            {
                for (int i = 0; i < page.Count; i++)
                {
                    string text = page.Data.Text();
                    int count = page.Data.Word();
                    var offsets = new Cursor(data, page.Data.Int());
                    var targets = new List<int>();
                    for (int j = 0; j < count; j++)
                    {
                        if (!numbers.TryGetValue(offsets.Int(), out int number))
                        {
                            throw Invalid("unknown keyword target");
                        }

                        targets.Add(number);
                    }

                    keywords.Add(new(text, targets));
                }
            }
        }

        return new(topics, images, keywords, contents);
    }

    private sealed record Leaf(Cursor Data, int Count);

    private static List<Leaf> Leaves(byte[] tree)
    {
        var c = new Cursor(tree);
        if (c.Word() != 0x293b)
        {
            throw Invalid("invalid B+ tree signature");
        }

        c.Word();
        int size = c.Word();
        c.Skip(20);
        int root = (short)c.Word();
        c.Word();
        int pages = c.Word(), levels = c.Word(), entries = c.Int();
        if (size < 8 || pages == 0 || levels is < 1 or > 16 || entries < 0 || entries > tree.Length || tree.Length != 38 + checked(pages * size))
        {
            throw Invalid("invalid B+ tree header");
        }

        Cursor Page(int page)
        {
            if (page < 0 || page >= pages)
            {
                throw Invalid("invalid B+ tree page");
            }

            return new Cursor(tree.AsSpan(38 + page * size, size).ToArray());
        }

        var visited = new HashSet<int>();
        for (int level = 1; level < levels; level++)
        {
            if (!visited.Add(root))
            {
                throw Invalid("cyclic B+ tree");
            }

            var index = Page(root);
            index.Skip(4);
            root = (short)index.Word();
        }

        visited.Clear();
        var leaves = new List<Leaf>();
        int total = 0, previous = -1;
        while (root != -1)
        {
            if (!visited.Add(root))
            {
                throw Invalid("cyclic B+ tree leaf chain");
            }

            var page = Page(root);
            int unused = page.Word(), count = page.Word();
            if ((short)page.Word() != previous || unused > size - 8)
            {
                throw Invalid("invalid B+ tree leaf");
            }

            int next = (short)page.Word();
            leaves.Add(new(new Cursor(page.Bytes(size - unused - 8)), count));
            total = checked(total + count);
            previous = root;
            root = next;
        }

        if (total != entries)
        {
            throw Invalid("B+ tree entry count differs");
        }

        return leaves;
    }

    private static byte[][] ReadPhrases(byte[] bytes)
    {
        var c = new Cursor(bytes);
        int count = c.Word();
        if (c.Word() != 0x100)
        {
            throw Invalid("unsupported phrase table");
        }

        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            offsets[i] = c.Word();
        }

        if (offsets[0] != 2 * (count + 1) || offsets[^1] != bytes.Length - 4)
        {
            throw Invalid("invalid phrase table size");
        }

        var phrases = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            if (offsets[i + 1] < offsets[i])
            {
                throw Invalid("invalid phrase extent");
            }

            phrases[i] = new Cursor(bytes, 4 + offsets[i]).Bytes(offsets[i + 1] - offsets[i]);
        }

        return phrases;
    }

    private static byte[] Expand(byte[] bytes, int length, byte[][] phrases)
    {
        if (length < 0 || length > _limit)
        {
            throw Invalid("invalid expanded topic size");
        }

        if (bytes.Length >= length)
        {
            return bytes.AsSpan(0, length).ToArray();
        }

        using var output = new MemoryStream();
        var c = new Cursor(bytes);
        while (c.Remaining > 0)
        {
            byte b = c.Byte();
            if (b is > 0 and < 16)
            {
                int code = (b - 1) * 256 + c.Byte();
                if (code / 2 >= phrases.Length)
                {
                    throw Invalid("invalid phrase reference");
                }

                output.Write(phrases[code / 2]);
                if ((code & 1) != 0)
                {
                    output.WriteByte(32);
                }
            }
            else
            {
                output.WriteByte(b);
            }

            if (output.Length > length)
            {
                throw Invalid("phrase expansion exceeds topic length");
            }
        }

        if (output.Length != length)
        {
            throw Invalid("phrase expansion differs from topic length");
        }

        return output.ToArray();
    }

    private static string[] ReadFonts(byte[] bytes)
    {
        var c = new Cursor(bytes);
        int faces = c.Word(), count = c.Word(), names = c.Word(), descriptors = c.Word();
        if (faces == 0 || names != 8 || descriptors <= names || (descriptors - names) % faces != 0)
        {
            throw Invalid("unsupported font table");
        }

        int width = (descriptors - names) / faces;
        var fonts = new string[count];
        for (int i = 0; i < count; i++)
        {
            var f = new Cursor(bytes, descriptors + i * 11);
            byte attributes = f.Byte(), halfPoints = f.Byte();
            f.Byte();
            int face = f.Word();
            if (face >= faces || halfPoints == 0)
            {
                throw Invalid("invalid font descriptor");
            }

            byte[] nameBytes = new Cursor(bytes, names + width * face).Bytes(width);
            int end = Array.IndexOf(nameBytes, (byte)0);
            string name = Ansi(nameBytes.AsSpan(0, end < 0
                ? width
                : end)).Replace("\\", "\\\\").Replace("'", "\\'");
            int red = f.Byte(), green = f.Byte(), blue = f.Byte();
            f.Skip(3);
            fonts[i] = $"font-family:'{name}';font-size:{(halfPoints / 2.0).ToString(CultureInfo.InvariantCulture)}pt;color:#{red:x2}{green:x2}{blue:x2};" + ((attributes & 1) != 0
                ? "font-weight:bold;"
                : "") + ((attributes & 2) != 0
                ? "font-style:italic;"
                : "") + ((attributes & 0x1c) != 0
                ? "text-decoration:" + ((attributes & 0x14) != 0
                    ? "underline "
                    : "") + ((attributes & 8) != 0
                    ? "line-through"
                    : "") + ";"
                : "") + ((attributes & 0x20) != 0
                ? "font-variant:small-caps;"
                : "");
        }

        return fonts;
    }

    private static List<HelpTopic> ReadTopics(byte[] bytes, Dictionary<int, int> numbers, byte[][] phrases, string[] fonts, Dictionary<string, byte[]> images)
    {
        byte[] Record(int position, int length)
        {
            if (length < 0 || length > _limit)
            {
                throw Invalid("invalid topic record length");
            }

            using var result = new MemoryStream();
            while (length > 0)
            {
                if (position % 2048 < 12)
                {
                    throw Invalid("topic points into a block header");
                }

                int count = Math.Min(length, 2048 - position % 2048);
                result.Write(new Cursor(bytes, position).Bytes(count));
                position += count;
                length -= count;
                if (position % 2048 == 0)
                {
                    position += 12;
                }
            }

            return result.ToArray();
        }

        var topics = new List<HelpTopic>();
        var visited = new HashSet<int>();
        var targets = numbers.Values.ToHashSet();
        var linkedTargets = new HashSet<int>();
        int position = new Cursor(bytes, 4).Int(), number = 0, topicPosition = 0, font = 0;
        long htmlBytes = 0;
        string title = "";
        var html = new StringBuilder();
        while (position < bytes.Length)
        {
            if (!visited.Add(position))
            {
                throw Invalid("cyclic topic chain");
            }

            var c = new Cursor(Record(position, 21));
            int size = c.Int(), textSize = c.Int();
            c.Int();
            int next = c.Int(), formatSize = c.Int();
            byte type = c.Byte();
            if (size < 21 || formatSize < 21 || formatSize > size || next <= 0 || next < size)
            {
                throw Invalid("invalid topic link");
            }

            var record = new Cursor(Record(position, size), 21);
            byte[] format = record.Bytes(formatSize - 21);
            byte[] text = Expand(record.Bytes(size - formatSize), textSize, phrases);
            if (type == 2)
            {
                if (number != 0)
                {
                    topics.Add(new(number, topicPosition, title, html.ToString()));
                }

                if (textSize == 0)
                {
                    if (position + next != bytes.Length)
                    {
                        throw Invalid("invalid terminal topic");
                    }

                    // HC30's TOMAP includes the empty sentinel after the last
                    // real topic. It is not a user-visible help page.
                    numbers.Remove(position);
                    if (linkedTargets.Except(topics.Select(t => t.Number)).Any())
                    {
                        throw Invalid("link targets the terminal topic");
                    }

                    return topics;
                }

                if (!numbers.TryGetValue(position, out number))
                {
                    throw Invalid("topic header is absent from map");
                }

                topicPosition = position;
                int end = Array.IndexOf(text, (byte)0);
                title = Ansi(end < 0
                    ? text
                    : text.AsSpan(0, end));
                if (end >= 0 && end != text.Length - 1)
                {
                    throw Invalid("topic macros are not supported");
                }

                html.Clear();
            }
            else if (type == 1 && number != 0)
            {
                string fragment = Display(format, text, fonts, images, targets, linkedTargets, ref font);
                htmlBytes += fragment.Length;
                if (htmlBytes > _limit)
                {
                    throw Invalid("topic HTML exceeds conversion limits");
                }

                html.Append(fragment);
            }
            else
            {
                throw Invalid("unsupported topic record type");
            }

            position = checked(position + next);
        }

        throw Invalid("missing terminal topic");
    }

    private static string Display(byte[] format, byte[] text, string[] fonts, Dictionary<string, byte[]> images, HashSet<int> topics, HashSet<int> linkedTargets, ref int font)
    {
        var f = new Cursor(format);
        f.SignedPackedInt();
        f.Skip(4);
        int flags = f.Word();
        // HC30 emits 0x2000 as a no-payload paragraph marker as well.
        if ((flags & ~0x3f7f) != 0)
        {
            throw Invalid("unsupported paragraph flags");
        }

        if ((flags & 1) != 0)
        {
            f.SignedPackedInt();
        }

        var spacing = new int[6];
        for (int i = 0; i < 6; i++)
        {
            if ((flags & (2 << i)) != 0)
            {
                spacing[i] = f.SignedPackedWord();
            }
        }

        if ((flags & 0x100) != 0)
        {
            f.Skip(3);
        }

        if ((flags & 0x200) != 0)
        {
            int count = f.SignedPackedWord();
            if (count < 0 || count > 128)
            {
                throw Invalid("invalid tab count");
            }

            for (int i = 0; i < count; i++)
            {
                if ((f.PackedWord() & 0x4000) != 0)
                {
                    f.PackedWord();
                }
            }
        }

        var output = new StringBuilder();
        var strings = new Cursor(text);
        bool link = false;

        string Metric(int n) => (n / 2.0).ToString(CultureInfo.InvariantCulture) + "pt";

        string paragraph = "<P style=\"margin-top:" + Metric(spacing[0]) + ";margin-bottom:" + Metric(spacing[1]) + ";margin-left:" + Metric(spacing[3]) + ";margin-right:" + Metric(spacing[4]) + ";text-indent:" + Metric(spacing[5]) + ";" + ((flags & 0x400) != 0
            ? "text-align:right;"
            : (flags & 0x800) != 0
                ? "text-align:center;"
                : "") + "\">";
        output.Append(paragraph);
        while (f.Remaining != 0)
        {
            string value = strings.Text();
            if (value.Length != 0)
            {
                if (font >= fonts.Length)
                {
                    throw Invalid("invalid font reference");
                }

                output.Append("<SPAN style=\"").Append(Escape(fonts[font])).Append("\">").Append(Escape(value)).Append("</SPAN>");
            }

            byte command = f.Byte();
            switch (command)
            {
                case 0xff:
                    if (link || f.Remaining != 0 || strings.Remaining != 0)
                    {
                        throw Invalid("invalid display terminator");
                    }

                    return output.Append("</P>\n").ToString();
                case 0x80:
                    font = f.Word();
                    if (font >= fonts.Length)
                    {
                        throw Invalid("invalid font reference");
                    }

                    break;
                case 0x81:
                    output.Append("<BR>\n");
                    break;
                case 0x82:
                    output.Append("</P>\n").Append(paragraph);
                    break;
                case 0x83:
                    output.Append("&#160;&#160;&#160;&#160;");
                    break;
                case 0x86:
                case 0x87:
                case 0x88:
                    if (f.Byte() != 3 || f.SignedPackedInt() != 4 || f.Word() != 0)
                    {
                        throw Invalid("unsupported embedded picture");
                    }

                    string image = "bm" + f.Word().ToString(CultureInfo.InvariantCulture) + ".bmp";
                    if (!images.ContainsKey(image))
                    {
                        throw Invalid("missing bitmap " + image);
                    }

                    output.Append("<IMG alt=\"\" src=\"../images/").Append(image).Append("\">");
                    break;
                case 0x89:
                    if (!link)
                    {
                        throw Invalid("hotspot end without link");
                    }

                    output.Append("</A>");
                    link = false;
                    break;
                case 0x8b:
                    output.Append("&#160;");
                    break;
                case 0x8c:
                    break;
                case 0xe0:
                case 0xe1:
                    int target = f.Int();
                    if (link || !topics.Contains(target))
                    {
                        throw Invalid("invalid topic link target");
                    }

                    linkedTargets.Add(target);
                    output.Append("<A href=\"").Append(TopicFile(target)).Append("\">");
                    link = true;
                    break;
                default:
                    throw Invalid($"unsupported display command 0x{command:X2}");
            }
        }

        throw Invalid("missing display terminator");
    }

    private static byte[] ReadBitmap(byte[] bytes)
    {
        var c = new Cursor(bytes);
        int signature = c.Word();
        if (signature is not (0x506c or 0x706c) || c.Word() != 1)
        {
            throw Invalid("unsupported picture group");
        }

        int picture = c.Int();
        if (picture < 8)
        {
            throw Invalid("invalid picture offset");
        }

        c = new Cursor(bytes, picture);
        if (c.Byte() != 6)
        {
            throw Invalid("only DIB help pictures are supported");
        }

        int packing = c.Byte(), xdpi = c.PackedInt(), ydpi = c.PackedInt(), planes = c.PackedWord(), bits = c.PackedWord();
        int width = c.PackedInt(), height = c.PackedInt(), colors = c.PackedInt(), important = c.PackedInt(), size = c.PackedInt(), hotspots = c.PackedInt();
        int offset = c.Int(), hotspotOffset = c.Int();
        if (planes != 1 || bits is not (1 or 4 or 8 or 24) || width <= 0 || height <= 0 || width > 16384 || height > 16384 || hotspots != 0 || hotspotOffset != 0)
        {
            throw Invalid("unsupported bitmap dimensions/format/hotspots");
        }

        int paletteCount = colors == 0 && bits <= 8
            ? 1 << bits
            : colors;
        if (paletteCount < 0 || paletteCount > 256 || (bits == 24 && paletteCount != 0))
        {
            throw Invalid("invalid bitmap palette");
        }

        byte[] palette = c.Bytes(checked(paletteCount * 4));
        if (offset < c.Position - picture)
        {
            throw Invalid("bitmap pixels overlap the picture header");
        }

        int pixelBytes = checked(((width * bits + 31) / 32) * 4 * height);
        if (pixelBytes > _limit || xdpi > 100000 || ydpi > 100000)
        {
            throw Invalid("bitmap exceeds conversion limits");
        }

        byte[] pixels = new Cursor(bytes, checked(picture + offset)).Bytes(size);
        if (packing == 1)
        {
            using var unpacked = new MemoryStream();
            var packed = new Cursor(pixels);
            while (packed.Remaining != 0)
            {
                int count = packed.Byte();
                if ((count & 0x80) != 0)
                {
                    unpacked.Write(packed.Bytes(count & 0x7f));
                }
                else
                {
                    byte value = packed.Byte();
                    for (int i = 0; i < count; i++)
                    {
                        unpacked.WriteByte(value);
                    }
                }

                if (unpacked.Length > pixelBytes)
                {
                    throw Invalid("bitmap expansion exceeds dimensions");
                }
            }

            pixels = unpacked.ToArray();
        }
        else if (packing != 0)
        {
            throw Invalid("unsupported bitmap compression");
        }

        if (pixels.Length != pixelBytes)
        {
            throw Invalid("bitmap size differs from dimensions");
        }

        using var bmp = new MemoryStream();
        using var w = new BinaryWriter(bmp);
        w.Write((ushort)0x4d42);
        w.Write(checked(54 + palette.Length + pixels.Length));
        w.Write(0);
        w.Write(54 + palette.Length);
        w.Write(40);
        w.Write(width);
        w.Write(height);
        w.Write((ushort)planes);
        w.Write((ushort)bits);
        w.Write(0);
        w.Write(pixelBytes);
        // HLP carries display DPI outside the DIB. The standalone BMP uses
        // unspecified density, as in the independent original-image extraction.
        w.Write(0);
        w.Write(0);
        w.Write(colors);
        w.Write(important);
        w.Write(palette);
        w.Write(pixels);
        return bmp.ToArray();
    }
}
