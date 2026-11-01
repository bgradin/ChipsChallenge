using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win16_Runtime;

/// <summary>Native window checkpoint contracts, separate from the implemented managed API registry.
/// Supported=false entries are typed rejection endpoints, never successful painting stubs.</summary>
public static class Win16WindowContracts
{
    public static IReadOnlyList<Win16ApiMetadata> All { get; } = Load();
    public static IReadOnlyDictionary<string, Win16ApiMetadata> ByImportIdentity { get; } = All.ToDictionary(a => $"{a.Module}.#{a.Ordinal}");
    public static IReadOnlyList<Win16ApiMetadata> Paint { get; } = All.Where(a => a.Name is not ("BeginPaint" or "EndPaint" or "SetDIBitsToDevice")).Concat(All.Where(a => a.Name is "BeginPaint" or "EndPaint" or "SetDIBitsToDevice").Select(a => a with { Supported = true })).Concat(LoadFile("Win16PaintApis.json")).ToArray();
    public static IReadOnlyDictionary<string, Win16ApiMetadata> PaintByImportIdentity { get; } = Paint.ToDictionary(a => $"{a.Module}.#{a.Ordinal}");
    public static IReadOnlyDictionary<string, Win16ApiMetadata> SceneByImportIdentity { get; } = Paint.Concat(LoadFile("Win16SceneApis.json")).GroupBy(a => $"{a.Module}.#{a.Ordinal}").ToDictionary(g => g.Key, g => g.Last());
    public static IReadOnlyDictionary<string, Win16ApiMetadata> InputByImportIdentity { get; } = SceneByImportIdentity.Values.Concat(LoadFile("Win16InputApis.json")).ToDictionary(a => $"{a.Module}.#{a.Ordinal}");
    public static IReadOnlyDictionary<string, Win16ApiMetadata> TileByImportIdentity { get; } = LoadFile("Win16TileApis.json").ToDictionary(a => $"{a.Module}.#{a.Ordinal}");
    public static IReadOnlyDictionary<string, Win16ApiMetadata> HazardByImportIdentity { get; } = LoadFile("Win16HazardApis.json").ToDictionary(a => $"{a.Module}.#{a.Ordinal}");
    public static IReadOnlyDictionary<string, Win16ApiMetadata> CompletionByImportIdentity { get; } = LoadFile("Win16CompletionApis.json").ToDictionary(a => $"{a.Module}.#{a.Ordinal}");

    private static IReadOnlyList<Win16ApiMetadata> Load() => LoadFile("Win16WindowApis.json");

    private static IReadOnlyList<Win16ApiMetadata> LoadFile(string file)
    {
        using var stream = typeof(Win16WindowContracts).Assembly.GetManifestResourceStream("Translator.TestSupport.RuntimeFixtures." + file)!;
        return JsonSerializer.Deserialize<Win16ApiMetadata[]>(stream, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
    }
}
