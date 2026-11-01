using System.Collections.ObjectModel;
using System.Text.Json;

namespace Win16_Runtime;

public sealed record Win16TaskServiceBinding(string Identity, string ServiceClass, string ManagedSource, string NativeSource, string NativeExport, string Invocation, IReadOnlyList<string> ContractTests, string Limitations)
{
    public ushort NativeModule => Identity.Split('.')[0] switch
    {
        "KERNEL" => 1,
        "USER" => 2,
        "GDI" => 3,
        "WEP4UTIL" => 4,
        _ => throw new InvalidDataException("Unknown service module.")
    };
    public ushort Ordinal => Win16ExportContracts.Get(Identity).Ordinal;
}

/// <summary>Shared routing metadata. Availability and exported code are not behavioral verification.</summary>
public static class Win16TaskServiceBindings
{
    public const string Dispatcher = "__win16_import_dispatch";
    public static IReadOnlyDictionary<string, Win16TaskServiceBinding> All { get; } = Load();

    private static IReadOnlyDictionary<string, Win16TaskServiceBinding> Load()
    {
        using var stream = typeof(Win16TaskServiceBindings).Assembly.GetManifestResourceStream("Win16.Runtime.Win16TaskServiceBindings.json")!;
        var rows = JsonSerializer.Deserialize<Win16TaskServiceBinding[]>(stream) ?? throw new InvalidDataException("Missing service bindings.");
        var result = new Dictionary<string, Win16TaskServiceBinding>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (Win16ExportContracts.Get(row.Identity).Kind == "Constant" || !result.TryAdd(row.Identity, row))
            {
                throw new InvalidDataException("Constant or duplicate callable service binding: " + row.Identity);
            }

            if (row.Invocation is not ("State" or "Ordinal" or "ModuleOrdinal") || row.ContractTests.Count == 0)
            {
                throw new InvalidDataException("Invalid service binding: " + row.Identity);
            }
        }

        return new ReadOnlyDictionary<string, Win16TaskServiceBinding>(result);
    }

    public static bool IsNativeBound(Win16TaskServiceBinding binding, IReadOnlyDictionary<string, int> exports) => exports.ContainsKey(Dispatcher) && exports.ContainsKey(binding.NativeExport);

    // Default managed whole-program owner mode initializes this earlier subset.
    // Explicit lifecycleOptions adds five lifecycle imports; this inventory count describes the default.
    // Isolated adapter tests must not be promoted to owner/startup integration.
    public static bool IsManagedOwnerWired(string identity) => Win16TaskKernelServices.Implemented.Contains(identity) || Win16TaskResources.Implemented.Contains(identity) || Win16TaskFiles.Implemented.Contains(identity) || Win16TaskProfiles.Implemented.Contains(identity) || Win16TaskSystem.Implemented.Contains(identity) || Win16TaskInstances.Implemented.Contains(identity) || Win16TaskGui.Implemented.Contains(identity);
}
