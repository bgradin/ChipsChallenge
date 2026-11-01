using System.Text;
using Translator.Binary;
using Translator.ChipsChallenge;

namespace Translator.Delivery;

public sealed record ConvertedHelpAsset(string RelativePath, byte[] Bytes);
/// <summary>Resolves literal and module-derived WinHelp references inside companion roots.
/// Arbitrarily computed filenames cannot be proved by static inspection.</summary>
public static class HelpFileConversion
{
    public static IReadOnlyList<ConvertedHelpAsset> Plan(byte[] executable, byte[] utility, string inputPath, string outputPath)
    {
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
        var image = new NeExecutableReader().Read(executable);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool derived = image.Relocations.Any(r => r.Import is { Ordinal: 5 } import && import.Module.Equals("WEP4UTIL", StringComparison.OrdinalIgnoreCase));
        bool unknown = false, extensionOnly = false;

        void Reference(string text)
        {
            int window = text.IndexOf('>');
            if (window >= 0)
            {
                text = text[..window];
            }

            if (!text.EndsWith(".hlp", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (text.Equals(".hlp", StringComparison.OrdinalIgnoreCase))
            {
                extensionOnly = true;
            }
            else if (text.Contains('%') || text.Contains('*') || text.Contains('?'))
            {
                unknown = true;
            }
            else
            {
                references.Add(Normalize(text));
            }
        }


        void Strings(byte[] bytes, int start, int length)
        {
            int end = start + length;
            for (int at = start; at < end; at++)
            {
                int first = at;
                while (at < end && bytes[at] >= 32 && bytes[at] != 127)
                {
                    at++;
                }

                if (at != first && at < end && bytes[at] == 0)
                {
                    Reference(_ansi.GetString(bytes, first, at - first));
                }
            }
        }

        foreach (byte[] bytes in new[]
        {
            executable,
            utility
        }

        )
        {
            var module = new NeExecutableReader().Read(bytes);
            foreach (var segment in module.Segments.Where(s => s.IsData))
            {
                Strings(bytes, segment.FileOffset, segment.FileLength);
            }

            foreach (var resource in module.Resources)
            {
                if (resource.Type.Id != 6)
                {
                    Strings(bytes, resource.FileOffset, resource.Length);
                    continue;
                }

                int at = resource.FileOffset, end = at + resource.Length;
                for (int i = 0; i < 16; i++)
                {
                    if (at >= end)
                    {
                        throw new InvalidDataException("Truncated NE string resource while resolving help references.");
                    }

                    int length = bytes[at++];
                    if (at > end - length)
                    {
                        throw new InvalidDataException("Truncated NE string resource while resolving help references.");
                    }

                    Reference(_ansi.GetString(bytes, at, length));
                    at += length;
                }
            }
        }

        if (unknown || (extensionOnly && !derived) || (!derived && references.Count == 0 && image.Relocations.Any(r => r.Import?.Name?.Equals("WinHelp", StringComparison.OrdinalIgnoreCase) == true)))
        {
            throw new NotSupportedException("A help filename is computed dynamically. Its HLP reference cannot be resolved during translation.");
        }

        string? moduleHelp = null;
        if (derived)
        {
            string preferred = Path.GetFileNameWithoutExtension(inputPath) + ".HLP";
            string? resident = image.ResidentNames.FirstOrDefault(n => n.Ordinal == 0)?.Name;
            moduleHelp = Directory.GetFiles(sourceDirectory).Any(p => Path.GetFileName(p).Equals(preferred, StringComparison.OrdinalIgnoreCase)) || string.IsNullOrWhiteSpace(resident)
                ? preferred
                : Normalize(resident + ".HLP");
            references.Add(moduleHelp);
        }

        var output = new Dictionary<string, ConvertedHelpAsset>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, byte[] bytes)
        {
            name = Normalize(name);
            if (output.TryGetValue(name, out var existing) && !existing.Bytes.AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidDataException("Conflicting converted help files: " + name);
            }

            output[name] = new(name, bytes);
        }


        foreach (string reference in references.Order(StringComparer.OrdinalIgnoreCase))
        {
            string source = Resolve(sourceDirectory, reference);
            byte[] original = File.ReadAllBytes(source);
            byte[] converted;
            try
            {
                converted = SupportedChipsHelp.Convert(original);
            }
            catch (InvalidDataException error)
            {
                throw new InvalidDataException($"Cannot convert referenced help file '{reference}': {error.Message}", error);
            }

            Add(reference, original);
            Add(Path.ChangeExtension(reference, ".CHM"), converted);
            if (reference.Equals(moduleHelp, StringComparison.OrdinalIgnoreCase))
            {
                string alias = Path.GetFileNameWithoutExtension(outputPath);
                Add(alias + ".HLP", original);
                Add(alias + ".CHM", converted);
            }
        }

        return output.Values.ToArray();
    }

    private static readonly Encoding _ansi = CreateAnsi();

    private static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static string Normalize(string name)
    {
        name = name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(name) || name.Contains(':') || name.Split(Path.DirectorySeparatorChar).Any(p => p is ".." or ""))
        {
            throw new NotSupportedException("Help reference must resolve inside the companion folder: " + name);
        }

        return string.Join(Path.DirectorySeparatorChar, name.Split(Path.DirectorySeparatorChar).Where(p => p != "."));
    }

    private static string Resolve(string root, string relative)
    {
        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            if (!Directory.Exists(current))
            {
                throw new FileNotFoundException("Referenced help file is missing: " + relative);
            }

            string[] matches = Directory.GetFileSystemEntries(current).Where(p => Path.GetFileName(p).Equals(part, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                throw new FileNotFoundException("Referenced help file is missing: " + relative, Path.Combine(root, relative));
            }

            if (matches.Length != 1)
            {
                throw new IOException("Ambiguous help reference: " + relative);
            }

            current = matches[0];
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new NotSupportedException("Help references through symbolic links are not supported: " + relative);
            }
        }

        if (!File.Exists(current))
        {
            throw new FileNotFoundException("Referenced help file is missing: " + relative, current);
        }

        return current;
    }
}
