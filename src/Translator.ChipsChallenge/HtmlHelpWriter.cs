using System.Buffers.Binary;
using System.Net;
using System.Text;
using Translator.Core;

namespace Translator.ChipsChallenge;

/// <summary>Deterministic ITSF v3 / ITSP v1, uncompressed section-zero CHM writer.
/// The directory uses linked PMGL pages, with no index page (root = -1).</summary>
internal static class HtmlHelpWriter
{
    public static byte[] Build(HelpDocument document)
    {
        string title = document.Topics.Single(t => t.Number == document.Contents).Title;
        string defaultFile = "html/" + WinHelpReader.TopicFile(document.Contents);

        byte[] Html(string body, string? pageTitle = null, string head = "") => Encoding.UTF8.GetBytes("<!DOCTYPE HTML><HTML><HEAD><META http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"><TITLE>" + WebUtility.HtmlEncode(pageTitle ?? title) + "</TITLE>" + head + "</HEAD><BODY>\n" + body + "</BODY></HTML>");

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["/"] = [],
            ["/html/"] = [],
            ["/images/"] = [],
            ["/keywords/"] = [],
            ["/#ITBITS"] = []
        };
        foreach (var topic in document.Topics)
        {
            files.Add("/html/" + WinHelpReader.TopicFile(topic.Number), Html(topic.Html, topic.Title));
        }

        foreach (var image in document.Images)
        {
            files.Add("/images/" + image.Key, image.Value);
        }

        string Site(string name, IEnumerable<(string Title, string File)> targets)
        {
            var links = targets.ToArray();
            var text = new StringBuilder("<LI><OBJECT type=\"text/sitemap\"><param name=\"Name\" value=\"").Append(WebUtility.HtmlEncode(name)).Append("\">");
            foreach (var target in links)
            {
                if (links.Length > 1)
                {
                    text.Append("<param name=\"Name\" value=\"").Append(WebUtility.HtmlEncode(target.Title)).Append("\">");
                }

                text.Append("<param name=\"Local\" value=\"").Append(WebUtility.HtmlEncode(target.File)).Append("\">");
            }

            return text.Append("</OBJECT>\n").ToString();
        }

        files.Add("/contents.hhc", Html("<UL>\n" + string.Concat(document.Topics.Select(t => Site(t.Title, [(t.Title, "html/" + WinHelpReader.TopicFile(t.Number))]))) + "</UL>"));
        var titles = document.Topics.ToDictionary(t => t.Number, t => t.Title);
        // Topic titles are useful keyword aliases as well as explicit WinHelp keywords.
        // This preserves menu-driven title lookup without game-specific topic numbers.
        var keywords = document.Keywords.ToList();
        foreach (var topic in document.Topics)
        {
            if (!keywords.Any(k => k.Text.Equals(topic.Title, StringComparison.OrdinalIgnoreCase)))
            {
                keywords.Add(new(topic.Title, [topic.Number]));
            }
        }

        if (!keywords.Any(k => k.Text.Equals("Contents", StringComparison.OrdinalIgnoreCase)))
        {
            keywords.Add(new("Contents", [document.Contents]));
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in keywords.GroupBy(k => HelpKeywordRoute.Normalize(k.Text), StringComparer.Ordinal))
        {
            string name = HelpKeywordRoute.File(group.Key);
            if (!aliases.TryAdd(name, group.Key))
            {
                throw new InvalidDataException("Help keyword routing collision.");
            }

            int[] targets = group.SelectMany(k => k.Topics).Distinct().ToArray();
            string links = string.Concat(targets.Select(target => "<LI><A href=\"../html/" + WinHelpReader.TopicFile(target) + "\">" + WebUtility.HtmlEncode(titles[target]) + "</A></LI>"));
            string redirect = targets.Length == 1
                ? "<META http-equiv=\"refresh\" content=\"0;url=../html/" + WinHelpReader.TopicFile(targets[0]) + "\">"
                : "";
            files.Add("/keywords/" + name, Html("<UL>" + links + "</UL>", group.First().Text, redirect));
        }

        files.Add("/index.hhk", Html("<UL>\n" + string.Concat(keywords.Select(k => Site(k.Text, k.Topics.Distinct().Select(n => (titles[n], "html/" + WinHelpReader.TopicFile(n)))))) + "</UL>"));
        using (var system = new MemoryStream())
        {
            using var w = new BinaryWriter(system, Encoding.UTF8, leaveOpen: true);
            w.Write(3);

            void Record(ushort tag, byte[] data)
            {
                w.Write(tag);
                w.Write(checked((ushort)data.Length));
                w.Write(data);
            }


            Record(0, Encoding.UTF8.GetBytes("contents.hhc\0"));
            Record(1, Encoding.UTF8.GetBytes("index.hhk\0"));
            Record(2, Encoding.UTF8.GetBytes(defaultFile + "\0"));
            Record(3, Encoding.UTF8.GetBytes(title + "\0"));
            var locale = new byte[36];
            BinaryPrimitives.WriteInt32LittleEndian(locale, 0x409);
            Record(4, locale);
            files.Add("/#SYSTEM", system.ToArray());
        }

        using (var names = new MemoryStream())
        {
            using var w = new BinaryWriter(names);
            w.Write((ushort)16);
            w.Write((ushort)1);
            w.Write((ushort)12);
            w.Write(Encoding.Unicode.GetBytes("Uncompressed\0"));
            files.Add("::DataSpace/NameList", names.ToArray());
        }

        using var payload = new MemoryStream();
        var entries = new List<byte[]>();
        foreach (var file in files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            using var entry = new MemoryStream();
            byte[] name = Encoding.UTF8.GetBytes(file.Key);
            Integer(entry, name.Length);
            entry.Write(name);
            Integer(entry, 0);
            Integer(entry, checked((int)payload.Length));
            Integer(entry, file.Value.Length);
            payload.Write(file.Value);
            entries.Add(entry.ToArray());
        }

        // Linked leaves keep the writer bounded without assuming that all names
        // fit in one directory block. Quick-reference offsets are every 5 entries.
        var pages = new List<List<byte[]>>();
        var page = new List<byte[]>();
        int used = 20;
        foreach (byte[] entry in entries)
        {
            if (entry.Length + 24 > 4096)
            {
                throw new InvalidDataException("CHM filename exceeds directory page size.");
            }

            if (used + entry.Length + 2 * ((page.Count + 1) / 5 + 1) > 4096)
            {
                pages.Add(page);
                page = [];
                used = 20;
            }

            page.Add(entry);
            used += entry.Length;
        }

        if (page.Count > 0)
        {
            pages.Add(page);
        }

        using var directory = new MemoryStream();
        using (var w = new BinaryWriter(directory, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes("ITSP"));
            foreach (int n in new[]
            {
                1,
                84,
                10,
                4096,
                2,
                1,
                -1,
                0,
                pages.Count - 1,
                -1,
                pages.Count,
                0x409
            }

            )
            {
                w.Write(n);
            }

            w.Write(Convert.FromHexString("6a92025d2e21d0119df900a0c922e6ec"));
            foreach (int n in new[]
            {
                84,
                -1,
                -1,
                -1
            }

            )
            {
                w.Write(n);
            }

            for (int i = 0; i < pages.Count; i++)
            {
                byte[] block = new byte[4096];
                Encoding.ASCII.GetBytes("PMGL").CopyTo(block, 0);
                int at = 20;
                var offsets = new List<int>();
                foreach (var entry in pages[i])
                {
                    offsets.Add(at - 20);
                    entry.CopyTo(block, at);
                    at += entry.Length;
                }

                BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), 4096 - at);
                BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(12), i - 1);
                BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(16), i + 1 < pages.Count
                    ? i + 1
                    : -1);
                for (int q = 1; q * 5 < offsets.Count; q++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4096 - 2 * (q + 1)), checked((ushort)offsets[q * 5]));
                }

                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4094), checked((ushort)offsets.Count));
                w.Write(block);
            }
        }

        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes("ITSF"));
            foreach (int n in new[]
            {
                3,
                96,
                1,
                0,
                0x409
            }

            )
            {
                w.Write(n);
            }

            w.Write(Convert.FromHexString("10fd017caa7bd0119e0c00a0c922e6ec"));
            w.Write(Convert.FromHexString("11fd017caa7bd0119e0c00a0c922e6ec"));
            w.Write(96L);
            w.Write(24L);
            w.Write(120L);
            w.Write(directory.Length);
            w.Write(120 + directory.Length);
            w.Write(510);
            w.Write(0);
            w.Write(120 + directory.Length + payload.Length);
            w.Write(0);
            w.Write(0);
            w.Write(directory.ToArray());
            w.Write(payload.ToArray());
        }

        return output.ToArray();
    }

    private static void Integer(Stream output, int n)
    {
        if (n < 0)
        {
            throw new InvalidDataException("Negative CHM directory value.");
        }

        Span<byte> bytes = stackalloc byte[5];
        int at = 4;
        bytes[at] = (byte)(n & 127);
        while ((n >>= 7) != 0)
        {
            bytes[--at] = (byte)((n & 127) | 128);
        }

        output.Write(bytes[at..]);
    }
}
