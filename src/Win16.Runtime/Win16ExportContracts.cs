using System.Text.Json;
using Translator.Binary;

namespace Win16_Runtime;

public sealed record Win16WireArgument(string Name, string WireType, int Bytes, int StackWord, string Marshalling, string? OriginalType = null);
public sealed record Win16ExportContract(string Module, ushort Ordinal, string Name, string Kind, string Convention, IReadOnlyList<Win16WireArgument> Arguments, int? ReturnBytes, ushort CleanupBytes, bool Variadic, ushort? ConstantValue, string Source, string SourceSha256, int? OriginalEntry = null, int? ReturnInstruction = null, string? ReturnBytesHex = null, string? ReturnType = null, string? ReturnTypeSource = null)
{
    public string Identity => $"{Module}.#{Ordinal}";

    public uint Argument(int index, SegmentMemory memory, in MachineState state)
    {
        var argument = Arguments[index];
        ushort offset = unchecked((ushort)(state.SP + 4 + argument.StackWord * 2));
        return argument.Bytes switch
        {
            2 => memory.ReadWord(state.SS, offset),
            4 => memory.ReadDword(state.SS, offset),
            _ => throw new NotSupportedException("Unimplemented wire argument width.")
        };
    }
}

/// <summary>Verified ABI evidence is independent of implementation availability.</summary>
public static class Win16ExportContracts
{
    public static IReadOnlyList<Win16ExportContract> All { get; } = Load();

    private static IReadOnlyList<Win16ExportContract> Load()
    {
        using var stream = typeof(Win16ExportContracts).Assembly.GetManifestResourceStream("Win16.Runtime.Win16ExportContracts.json")!;
        var rows = JsonSerializer.Deserialize<Win16ExportContract[]>(stream) ?? throw new InvalidDataException("Missing Win16 export contracts.");
        if (rows.Select(r => r.Identity).Distinct().Count() != rows.Length)
        {
            throw new InvalidDataException("Duplicate export contract.");
        }

        foreach (var row in rows)
        {
            var slots = row.Arguments.SelectMany(a => Enumerable.Range(a.StackWord, a.Bytes / 2)).Order().ToArray();
            if (!slots.SequenceEqual(Enumerable.Range(0, slots.Length)) || row.Convention == "Pascal" && row.CleanupBytes != slots.Length * 2 || row.Kind == "Constant" && (row.Arguments.Count != 0 || row.ConstantValue is null))
            {
                throw new InvalidDataException($"Invalid export ABI {row.Identity}.");
            }
        }

        return Array.AsReadOnly(rows);
    }

    public static Win16ExportContract? Find(NeImportReference import) => All.FirstOrDefault(c => c.Module.Equals(import.Module, StringComparison.OrdinalIgnoreCase) && (import.Ordinal is not null
        ? c.Ordinal == import.Ordinal
        : c.Name.Equals(import.Name, StringComparison.OrdinalIgnoreCase)));

    public static Win16ExportContract Get(string identity) => All.Single(c => c.Identity == identity);

    public static void VerifyWepImage(byte[] bytes)
    {
        var executable = new NeExecutableReader().Read(bytes);
        var contracts = All.Where(c => c.Module == "WEP4UTIL").ToArray();
        if (contracts.Any(c => c.SourceSha256 != executable.Sha256))
        {
            throw new InvalidDataException("WEP4UTIL image identity mismatch.");
        }

        foreach (var c in contracts)
        {
            var entry = executable.EntryPoints.SingleOrDefault(e => e.Ordinal == c.Ordinal);
            if (entry is null || entry.SegmentIndex != 1 || entry.Offset != c.OriginalEntry || !executable.ResidentNames.Concat(executable.NonResidentNames).Any(n => n.Ordinal == c.Ordinal && n.Name == c.Name))
            {
                throw new InvalidDataException($"WEP export mismatch: {c.Identity}.");
            }

            int offset = executable.Segments[0].FileOffset + c.ReturnInstruction!.Value;
            if (Convert.ToHexString(bytes.AsSpan(offset, 3)) != c.ReturnBytesHex)
            {
                throw new InvalidDataException($"WEP cleanup evidence mismatch: {c.Identity}.");
            }
        }
    }
}
