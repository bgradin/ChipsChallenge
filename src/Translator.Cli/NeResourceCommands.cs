using System.Text.Json;
using Translator.Binary;

internal static class NeResourceCommands
{
    private static int? Index(string[] args)
    {
        int i = Array.IndexOf(args, "--resource-index");
        if (i < 0)
        {
            return null;
        }

        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int value) || value < 1)
        {
            throw new ArgumentException("--resource-index must be a positive one-based resource index.");
        }

        return value;
    }

    public static NeResourceReport Inspect(string[] args, byte[] bytes)
    {
        using var catalog = new NeResourceCatalog(bytes);
        return Index(args) is int index
            ? new(catalog.ExecutableSha256, new[] { catalog.Inspect(index) })
            : catalog.InspectAll();
    }

    public static int Extract(string[] args, string input, string? output)
    {
        int index = Index(args) ?? throw new ArgumentException("Resource extraction requires --resource-index.");
        if (output is null)
        {
            throw new ArgumentException("Resource extraction requires --output.");
        }

        using var catalog = new NeResourceCatalog(File.ReadAllBytes(input));
        var resource = catalog.Extract(index);
        File.WriteAllBytes(output, resource.Bytes);
        File.WriteAllText(output + ".json", JsonSerializer.Serialize(resource, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Extracted original resource {index} ({resource.Bytes.Length} bytes); source identity and bounds recorded in {output}.json.");
        return 0;
    }
}
