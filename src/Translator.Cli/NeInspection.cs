using System.Text.Json;
using Translator.Binary;
using Win16_Runtime;

internal static class NeInspection
{
    public static NeRuntimeOptions RuntimeOptions(string[] args)
    {
        int option = Array.IndexOf(args, "--memory-backend");
        if (option < 0)
        {
            return new();
        }

        if (option + 1 >= args.Length || !Enum.TryParse<NeMemoryBackend>(args[option + 1], true, out var backend) || !Enum.IsDefined(backend))
        {
            throw new ArgumentException("--memory-backend must be Native or Managed.");
        }

        return new(backend);
    }

    private sealed record Binding(string Module, string? Name, ushort? Ordinal, ushort Selector, ushort Offset);
    public static Func<NeImportReference, Win16CodeAddress?>? ReadImportBindings(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            throw new InvalidDataException("--import-bindings must name an existing JSON file.");
        }

        Binding[] bindings;
        try
        {
            bindings = JsonSerializer.Deserialize<Binding[]>(File.ReadAllText(path)) ?? throw new InvalidDataException("Import binding JSON must contain an array.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Invalid import binding JSON: {error.Message}", error);
        }

        var addresses = new Dictionary<(string Module, string? Name, ushort? Ordinal), Win16CodeAddress>();
        foreach (var binding in bindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.Module) || (binding.Name is null) == (binding.Ordinal is null) || binding.Name == "" || binding.Ordinal == 0 || binding.Selector == 0)
            {
                throw new InvalidDataException("Each import binding requires Module, exactly one of Name/Ordinal, and a nonzero Selector.");
            }

            if (!addresses.TryAdd((binding.Module.ToUpperInvariant(), binding.Name, binding.Ordinal), new(binding.Selector, binding.Offset)))
            {
                throw new InvalidDataException("Duplicate import identity in binding JSON.");
            }
        }

        return import => addresses.TryGetValue((import.Module.ToUpperInvariant(), import.Name, import.Ordinal), out var address)
            ? address
            : null;
    }
}
