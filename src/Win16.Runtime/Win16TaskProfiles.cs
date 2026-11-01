using System.Globalization;
using System.Text;

namespace Win16_Runtime;

/// <summary>Profile values and filenames come from original callers. This service
/// implements INI compatibility, not application progress/password decisions.</summary>
public sealed class Win16TaskProfiles(SegmentMemory memory, string directory) : IDisposable
{
    private bool _disposed;
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "KERNEL.#127",
        "KERNEL.#128",
        "KERNEL.#129"
    };

    private string FileName(string? name)
    {
        name ??= "WIN.INI";
        if (!OperatingSystem.IsWindows())
        {
            name = name.Replace('\\', Path.DirectorySeparatorChar);
        }

        return Path.GetFullPath(name, Path.GetFullPath(directory));
    }

    private static bool Header(string line, out string name)
    {
        line = line.Trim();
        name = line.Length >= 2 && line[0] == '[' && line[^1] == ']'
            ? line[1..^1].Trim()
            : "";
        return name.Length != 0;
    }

    private static bool Key(string line, out string key, out string value)
    {
        line = line.Trim();
        int equals = line.IndexOf('=');
        key = "";
        value = "";
        if (line.StartsWith(';') || line.StartsWith('#') || equals < 0)
        {
            return false;
        }

        key = line[..equals].Trim();
        value = line[(equals + 1)..].Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
        {
            value = value[1..^1];
        }

        return key.Length != 0;
    }

    private static List<string> Lines(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        if (new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            throw new IOException("Profile exceeds compatibility storage limit.");
        }

        return File.ReadAllLines(path, Encoding.Latin1).ToList();
    }

    private static (int Start, int End) Section(List<string> lines, string name)
    {
        int start = -1;
        for (int n = 0; n < lines.Count; n++)
        {
            if (Header(lines[n], out string section))
            {
                if (start >= 0)
                {
                    return (start, n);
                }

                if (section.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    start = n;
                }
            }
        }

        return (start, lines.Count);
    }

    private static string? Value(List<string> lines, string section, string key)
    {
        var range = Section(lines, section);
        if (range.Start < 0)
        {
            return null;
        }

        for (int n = range.Start + 1; n < range.End; n++)
        {
            if (Key(lines[n], out string k, out string value) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static void Save(string path, List<string> lines)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".w16-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllLines(temporary, lines, Encoding.Latin1);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var contract = Win16ExportContracts.Get(identity);
        var saved = state;

        uint Arg(int n) => contract.Argument(n, memory, saved);


        string? Text(uint p) => p == 0
            ? null
            : Win16TaskKernelServices.ReadString(memory, p);

        uint result = 0;
        if (identity == "KERNEL.#127")
        {
            string? app = Text(Arg(0)), key = Text(Arg(1)), file = Text(Arg(3));
            ushort fallback = (ushort)Arg(2);
            result = fallback;
            try
            {
                string? text = app is null || key is null
                    ? null
                    : Value(Lines(FileName(file)), app, key);
                if (text is not null)
                {
                    text = text.Trim();
                    bool negative = text.StartsWith('-');
                    string unsigned = text.TrimStart('+', '-');
                    bool hex = unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                    bool parsed = uint.TryParse(hex
                        ? unsigned[2..]
                        : unsigned, hex
                        ? NumberStyles.AllowHexSpecifier
                        : NumberStyles.None, CultureInfo.InvariantCulture, out uint number);
                    result = parsed
                        ? unchecked((ushort)(negative
                            ? 0u - number
                            : number))
                        : 0u;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                result = fallback;
            }
        }
        else if (identity == "KERNEL.#128")
        {
            string? app = Text(Arg(0)), key = Text(Arg(1)), fallback = Text(Arg(2)), file = Text(Arg(5));
            uint output = Arg(3);
            int capacity = (ushort)Arg(4);
            if (capacity != 0)
            {
                memory.ValidateBufferWrite((ushort)(output >> 16), (ushort)output, capacity);
                List<string> lines;
                try
                {
                    lines = Lines(FileName(file));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    lines = [];
                }

                bool enumeration = app is null || key is null;
                string text;
                if (app is null)
                {
                    text = string.Join('\0', lines.Where(l => Header(l, out _)).Select(l =>
                    {
                        Header(l, out string name);
                        return name;
                    }).Distinct(StringComparer.OrdinalIgnoreCase));
                }
                else if (key is null)
                {
                    var range = Section(lines, app);
                    text = range.Start < 0
                        ? ""
                        : string.Join('\0', lines.Skip(range.Start + 1).Take(range.End - range.Start - 1).Where(l => Key(l, out _, out _)).Select(l =>
                    {
                        Key(l, out string name, out _);
                        return name;
                    }).Distinct(StringComparer.OrdinalIgnoreCase));
                }
                else
                {
                    text = Value(lines, app, key) ?? fallback?.TrimEnd() ?? "";
                }

                byte[] bytes = Encoding.Latin1.GetBytes(text);
                var buffer = new byte[Math.Min(capacity, bytes.Length + (enumeration
                    ? 2
                    : 1))];
                int copied = Math.Min(bytes.Length, Math.Max(0, capacity - (enumeration
                    ? 2
                    : 1)));
                bytes.AsSpan(0, copied).CopyTo(buffer);
                Win16TaskKernelServices.WriteBuffer(memory, output, buffer);
                result = (uint)(enumeration
                    ? bytes.Length + 2 > capacity
                        ? Math.Max(0, capacity - 2)
                        : bytes.Length == 0
                            ? 0
                            : copied + 1
                    : copied);
            }
        }
        else
        {
            string? app = Text(Arg(0)), key = Text(Arg(1)), value = Text(Arg(2)), file = Text(Arg(3));
            try
            {
                string path = FileName(file);
                // WritePrivateProfileString reports zero for its cache-flush operation.
                // The provider writes synchronously, so there is no deferred cache to flush.
                if (app is null)
                {
                    result = 0;
                }
                else
                {
                    var lines = Lines(path);
                    var original = lines.ToArray();
                    var range = Section(lines, app);
                    if (key is null)
                    {
                        if (range.Start >= 0)
                        {
                            lines.RemoveRange(range.Start, range.End - range.Start);
                        }
                    }
                    else if (range.Start < 0)
                    {
                        if (value is not null)
                        {
                            if (lines.Count > 0)
                            {
                                lines.Add("");
                            }

                            lines.Add("[" + app + "]");
                            lines.Add(key + "=" + value);
                        }
                    }
                    else
                    {
                        int found = -1;
                        for (int n = range.Start + 1; n < range.End; n++)
                        {
                            if (Key(lines[n], out string k, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                            {
                                found = n;
                                break;
                            }
                        }

                        if (found >= 0)
                        {
                            if (value is null)
                            {
                                lines.RemoveAt(found);
                            }
                            else
                            {
                                lines[found] = key + "=" + value;
                            }
                        }
                        else if (value is not null)
                        {
                            lines.Insert(range.End, key + "=" + value);
                        }
                    }

                    if (!original.SequenceEqual(lines))
                    {
                        Save(path, lines);
                    }

                    result = 1;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                result = 0;
            }
        }

        state.AX = (ushort)result;
        return contract.CleanupBytes;
    }

    public void Dispose() => _disposed = true;
}
