using System.Text.Json;
using Translator.Binary;

namespace Win16_Runtime;

public enum NeImportSupport
{
    SupportedRuntimeService,
    KnownUnsupportedService,
    UnknownImport,
    InvalidImportReference,
    ConstantExport
}

public sealed record NeImportSite(int RelocationFileOffset, int SegmentIndex, ushort Offset, bool RelocationSupported);
public sealed record NeDiscoveredImport(NeImportReference Original, NeImportSupport Support, string? ResolvedIdentity, Win16ApiMetadata? Api, string Diagnostic, IReadOnlyList<NeImportSite> Sources, Win16CodeAddress? BoundAddress = null, string BindingStatus = "NotBound")
{
    public ushort? ConstantValue
    {
        get; init;
    }
}

/// <summary>Identity-only export metadata does not define a second API signature registry.</summary>
public sealed record Win16KnownExport(string Module, string Name, ushort Ordinal, string Source);
public static class NeImportResolver
{
    private static readonly IReadOnlyList<Win16KnownExport> _knownExports = LoadKnownExports();

    private static IReadOnlyList<Win16KnownExport> LoadKnownExports()
    {
        using var stream = typeof(NeImportResolver).Assembly.GetManifestResourceStream("Win16.Runtime.Win16KnownExports.json") ?? throw new InvalidDataException("Known Win16 export identities are missing.");
        return Array.AsReadOnly(JsonSerializer.Deserialize<Win16KnownExport[]>(stream) ?? throw new InvalidDataException("Known Win16 export identities are invalid."));
    }

    public static NeDiscoveredImport Classify(NeImportReference import, IReadOnlyList<NeImportSite>? sources = null, bool includeExportContracts = false)
    {
        sources ??= Array.Empty<NeImportSite>();
        if (import.ModuleIndex < 1 || string.IsNullOrWhiteSpace(import.Module) || (import.Name is null) == (import.Ordinal is null) || import.Name == "" || import.Ordinal == 0)
        {
            return new(import, NeImportSupport.InvalidImportReference, null, null, "Invalid module or procedure identity", sources);
        }

        // Use the same exact name/ordinal registry lookup as assembly calls. Ordinals never become guessed names.
        var api = Win16ApiRegistry.Find(import.Module, import.Name, import.Ordinal);
        if (api is not null)
        {
            return new(import, api.Supported
                ? NeImportSupport.SupportedRuntimeService
                : NeImportSupport.KnownUnsupportedService, api.Identity.CanonicalName, api, api.Supported
                ? "Registry contract supported; runtime registration required"
                : "API adapter is not implemented", sources);
        }

        var contract = includeExportContracts
            ? Win16ExportContracts.Find(import)
            : null;
        if (contract is not null)
        {
            return new(import, contract.Kind == "Constant"
                ? NeImportSupport.ConstantExport
                : NeImportSupport.KnownUnsupportedService, $"{contract.Module}.{contract.Name}", null, contract.Kind == "Constant"
                ? "Verified constant export; apply original word offset relocation, never register a procedure"
                : "Verified Win16 ABI evidence; whole-task compatibility adapter not certified by identity lookup", sources);
        }

        var known = _knownExports.FirstOrDefault(e => string.Equals(e.Module, import.Module, StringComparison.OrdinalIgnoreCase) && (import.Ordinal is not null
            ? e.Ordinal == import.Ordinal
            : string.Equals(e.Name, import.Name, StringComparison.OrdinalIgnoreCase)));
        if (known is not null)
        {
            return new(import, NeImportSupport.KnownUnsupportedService, $"{known.Module}.{known.Name}", null, $"Known export has no implemented API adapter; identity source: {known.Source}", sources);
        }

        return new(import, NeImportSupport.UnknownImport, null, null, $"No verified registry/export identity for {import.Identity}; provide verified metadata and a runtime adapter", sources);
    }

    private static bool MatchesOriginalReference(NeRelocation record, NeImportReference import, NeExecutable executable)
    {
        if (record.RawTarget1 != import.ModuleIndex)
        {
            return false;
        }

        return record.TargetKind switch
        {
            NeRelocationTargetKind.ImportOrdinal => import.Name is null && import.Ordinal == record.RawTarget2,
            NeRelocationTargetKind.ImportName => import.Ordinal is null && executable.ImportedNames.Any(n => n.TableOffset == record.RawTarget2 && n.Name == import.Name),
            _ => false
        };
    }

    public static IReadOnlyList<NeDiscoveredImport> Discover(NeExecutable executable, bool includeExportContracts = false)
    {
        var imports = new List<NeDiscoveredImport>();
        foreach (var group in executable.Relocations.Where(r => r.Import is not null).GroupBy(r => r.Import!))
        {
            var source = group.Key;
            var module = executable.ModuleReferences.FirstOrDefault(m => m.Index == source.ModuleIndex);
            var sites = group.SelectMany(r => r.SourceOffsets.Select(offset => new NeImportSite(r.SourceFileOffset, r.SourceSegmentIndex, offset, r.Supported))).ToArray();
            if (module is null || module.Name != source.Module || group.Any(r => !MatchesOriginalReference(r, source, executable)))
            {
                imports.Add(new(source, NeImportSupport.InvalidImportReference, null, null, "Import disagrees with its original module/name/ordinal reference", sites));
            }
            else
            {
                imports.Add(Classify(source, Array.AsReadOnly(sites), includeExportContracts));
            }
        }

        return imports.AsReadOnly();
    }
}

/// <summary>Borrowed runtime API registrations. Resolved Win16 addresses are never native pointers.</summary>
public sealed class NeProcedureBindings : IDisposable
{
    private readonly Dictionary<NeImportReference, Win16CodeAddress> _addresses = new();
    private bool _disposed;
    private readonly Dictionary<NeImportReference, ushort> _constants = new();
    public IReadOnlyList<NeDiscoveredImport> Imports
    {
        get;
    }

    public NeProcedureBindings(NeExecutable executable, Win16CallDispatcher dispatcher)
    {
        var imports = new List<NeDiscoveredImport>();
        foreach (var import in NeImportResolver.Discover(executable))
        {
            if (import.Support == NeImportSupport.ConstantExport)
            {
                var contract = Win16ExportContracts.Find(import.Original)!;
                if (executable.Relocations.Any(r => r.Import == import.Original && r.RawAddressType != 5))
                {
                    throw new NotSupportedException("Constant export requires a verified 16-bit offset relocation.");
                }

                _constants.Add(import.Original, contract.ConstantValue!.Value);
                imports.Add(import with
                {
                    ConstantValue = contract.ConstantValue,
                    BindingStatus = "ConstantRelocation"
                });
            }
            else if (import.Support == NeImportSupport.SupportedRuntimeService && dispatcher.TryResolveApi(import.Api!, out var target))
            {
                _addresses.Add(import.Original, target);
                imports.Add(import with
                {
                    BoundAddress = target,
                    BindingStatus = "Bound"
                });
            }
            else
            {
                imports.Add(import with
                {
                    BindingStatus = import.Support == NeImportSupport.SupportedRuntimeService
                    ? "RuntimeServiceUnavailable"
                    : "Unsupported",
                    Diagnostic = import.Support == NeImportSupport.SupportedRuntimeService
                    ? import.Diagnostic + "; register its existing runtime adapter with the dispatcher (GDI/USER require Windows)"
                    : import.Diagnostic
                });
            }
        }

        Imports = imports.AsReadOnly();
    }

    public Win16CodeAddress? Resolve(NeImportReference import)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _addresses.TryGetValue(import, out var address)
            ? address
            : null;
    }

    public ushort? ResolveConstant(NeImportReference import)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _constants.TryGetValue(import, out var value)
            ? value
            : null;
    }

    public void Dispose()
    {
        _disposed = true;
        _addresses.Clear();
        _constants.Clear();
    }
}

public enum NeMemoryBackend
{
    Managed,
    Native
}

public sealed record NeRuntimeOptions(NeMemoryBackend MemoryBackend = NeMemoryBackend.Native, Win16NativeMemoryLimits? NativeLimits = null, Win16GlobalMemoryLimits? GlobalLimits = null);
public sealed record NeRuntimeInitialization(string ExecutableSha256, int MachineStateAbiVersion, int MachineStateAbiSize, NeStartupRegisters InitialRegisters, IReadOnlyList<NeSegmentMapping> Segments, IReadOnlyList<NeDiscoveredImport> Imports, NeLoadOptions ImageLimits, NeRuntimeOptions RuntimeLimits, Win16MemoryStatistics Storage)
{
    public string Ownership => "Runtime owns image segments, dynamic allocations and service adapters; native program owns generated code and procedure bindings; caller owns fixture overrides";
}

/// <summary>Owns initialized image, allocation backend, imports and the selected runtime service owners.</summary>
public sealed class NeRuntimeProgram : IDisposable
{
    private readonly NeLoadedProgram _image;
    private readonly Win16GlobalMemoryServices _globals;
    private readonly NeProcedureBindings _bindings;
    private readonly Win16GdiServices? _gdi;
    private readonly Win16WindowHandles? _windows;
    private readonly Win16CallDispatcher _dispatcher;
    private readonly Win16NativeMemoryAllocator? _allocator;
    private readonly NeLoadOptions _loadOptions;
    private readonly NeRuntimeOptions _runtimeOptions;
    private bool _disposed;
    public NeLoadedProgram Image
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _image;
        }
    }

    public Win16CallDispatcher Dispatcher
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _dispatcher;
        }
    }

    public Win16GlobalMemoryServices GlobalMemory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _globals;
        }
    }

    public Win16MemoryStatistics StorageStatistics => _allocator?.Statistics ?? new("Managed", _image.Memory.Segments.Sum(s => (long)s.Size), _image.Memory.Segments.Count, 0);
    public NeLoadedImageReport Report => _image.Report with
    {
        Imports = _bindings.Imports
    };

    public NeRuntimeInitialization Initialization
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(_image.Report.Sha256, MachineState.AbiVersion, MachineState.AbiSize, _image.Report.InitialRegisters, _image.Report.Segments, _bindings.Imports, _loadOptions, _runtimeOptions, StorageStatistics);
        }
    }

    private NeRuntimeProgram(NeLoadedProgram image, Win16GlobalMemoryServices globals, NeProcedureBindings bindings, Win16CallDispatcher dispatcher, Win16GdiServices? gdi, Win16WindowHandles? windows, Win16NativeMemoryAllocator? allocator, NeLoadOptions loadOptions, NeRuntimeOptions runtimeOptions)
    {
        _image = image;
        _globals = globals;
        _bindings = bindings;
        _dispatcher = dispatcher;
        _gdi = gdi;
        _windows = windows;
        _allocator = allocator;
        _loadOptions = loadOptions;
        _runtimeOptions = runtimeOptions;
    }

    public static NeRuntimeProgram Load(ReadOnlySpan<byte> bytes, NeLoadOptions? options = null, NeRuntimeOptions? runtimeOptions = null, IReadOnlyDictionary<string, Win16CodeAddress>? nativeImportBindings = null)
    {
        byte[] original = bytes.ToArray();
        var executable = new NeExecutableReader().Read(original);
        options ??= new();
        runtimeOptions ??= new();
        if (!Enum.IsDefined(runtimeOptions.MemoryBackend))
        {
            throw new ArgumentException("Unknown memory backend.");
        }

        runtimeOptions = runtimeOptions with
        {
            NativeLimits = runtimeOptions.NativeLimits ?? new(),
            GlobalLimits = runtimeOptions.GlobalLimits ?? new()
        };
        var allocator = runtimeOptions.MemoryBackend == NeMemoryBackend.Native
            ? new Win16NativeMemoryAllocator(runtimeOptions.NativeLimits)
            : null;
        var memory = new SegmentMemory(allocator);
        var dispatcher = new Win16CallDispatcher();
        Win16GlobalMemoryServices? globals = null;
        NeProcedureBindings? bindings = null;
        Win16GdiServices? gdi = null;
        Win16WindowHandles? windows = null;
        try
        {
            globals = new Win16GlobalMemoryServices(memory, dispatcher, limits: runtimeOptions.GlobalLimits);
            if (OperatingSystem.IsWindows())
            {
                gdi = new Win16GdiServices(dispatcher);
                windows = new Win16WindowHandles();
                _ = new Win16UserServices(dispatcher, windows);
            }

            bindings = new(executable, dispatcher);
            var loaded = new NeProgramLoader().Load(original, options, import => nativeImportBindings is not null && nativeImportBindings.TryGetValue(import.Identity, out var nativeBinding)
                ? nativeBinding
                : bindings.Resolve(import), memory, bindings.ResolveConstant);
            return new(loaded, globals, bindings, dispatcher, gdi, windows, allocator, options, runtimeOptions);
        }
        catch
        {
            bindings?.Dispose();
            globals?.Dispose();
            gdi?.Dispose();
            windows?.Dispose();
            allocator?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bindings.Dispose();
        _globals.Dispose();
        _gdi?.Dispose();
        _windows?.Dispose();
        _image.Dispose();
        _allocator?.Dispose();
    }
}
