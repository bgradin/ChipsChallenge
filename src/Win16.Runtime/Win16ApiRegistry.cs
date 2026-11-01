using System.Text.Json;
using System.Text.Json.Serialization;
using Translator.IR;

namespace Win16_Runtime;

public enum Win16ValueType
{
    Word,
    Dword,
    Handle,
    Bool,
    FarPointer,
    SignedWord,
    SignedDword,
    Byte,
    SignedByte
}

public enum Win16ImportKind
{
    Procedure
}

public enum Win16ApiConvention
{
    Pascal,
    Cdecl
}

public enum Win16ArgumentOrder
{
    LeftToRight,
    RightToLeft
}

public enum Win16Marshalling
{
    StackValues
}

public sealed record Win16ImportIdentity(string Module, string? Name, ushort? Ordinal, string OriginalIdentity)
{
    public string CanonicalName => $"{Module.ToUpperInvariant()}.{Name ?? $"#{Ordinal}"}";
}

public sealed record Win16ApiArgument(string Name, Win16ValueType Type, int StackWord)
{
    public int Width => Type switch
    {
        Win16ValueType.Dword or Win16ValueType.SignedDword or Win16ValueType.FarPointer => 32,
        Win16ValueType.Byte or Win16ValueType.SignedByte => 8,
        _ => 16
    };
}

public sealed record Win16ApiMetadata(ushort ServiceId, string Module, string Name, ushort? Ordinal, Win16ImportKind Kind, Win16ApiConvention Convention, Win16ArgumentOrder ArgumentOrder, Win16ValueType ReturnType, Win16Marshalling Marshalling, bool CalleeCleanup, bool Supported, ushort EntryOffset, IReadOnlyList<Win16ApiArgument> Arguments)
{
    public Win16ImportIdentity Identity => new(Module, Name, Ordinal, $"{Module}.{Name}");
    public ushort CleanupBytes => CalleeCleanup
        ? checked((ushort)Arguments.Sum(a => Math.Max(2, a.Width / 8)))
        : (ushort)0;

    public uint ReadArgument(string name, SegmentMemory memory, in MachineState state, Win16CallContext call)
    {
        var argument = Arguments.Single(a => a.Name == name);
        uint value = Win16StackArguments.ReadWord(memory, state.SS, call.EntryStackPointer, call.Distance, argument.StackWord);
        if (argument.Width == 32)
        {
            value |= (uint)Win16StackArguments.ReadWord(memory, state.SS, call.EntryStackPointer, call.Distance, argument.StackWord + 1) << 16;
        }

        return argument.Width == 8
            ? value & 255
            : value;
    }
}

/// <summary>Stable service IDs and original stack signatures shared with the freestanding runtime generator.</summary>
public static class Win16ApiRegistry
{
    public static IReadOnlyList<Win16ApiMetadata> All { get; } = Load();

    private static IReadOnlyList<Win16ApiMetadata> Load()
    {
        using var stream = typeof(Win16ApiRegistry).Assembly.GetManifestResourceStream("Win16.Runtime.Win16Apis.json") ?? throw new InvalidOperationException("Embedded Win16 API registry is missing.");
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var apis = JsonSerializer.Deserialize<Win16ApiMetadata[]>(stream, options) ?? throw new InvalidDataException("Embedded Win16 API registry is null.");
        return Array.AsReadOnly(apis);
    }

    public static Win16ApiMetadata Get(ushort id) => All.SingleOrDefault(a => a.ServiceId == id) ?? throw new NotSupportedException($"Unknown Win16 service ID {id}; register an API binding.");
    public static Win16ApiMetadata? Find(string module, string? name, ushort? ordinal) => All.FirstOrDefault(a => string.Equals(a.Module, module, StringComparison.OrdinalIgnoreCase) && (name is not null && ordinal is null
        ? string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)
        : name is null && ordinal is not null && a.Ordinal == ordinal));

    public static Win16ApiMetadata? Find(string symbol) => All.FirstOrDefault(a => string.Equals(a.Identity.CanonicalName, symbol, StringComparison.OrdinalIgnoreCase) || (a.Ordinal is not null && string.Equals($"{a.Module}.#{a.Ordinal}", symbol, StringComparison.OrdinalIgnoreCase)));
}

public enum Win16CallTargetKind
{
    Internal,
    Imported,
    Dynamic,
    Unresolved
}

public sealed record Win16CallResolution(Win16CallTargetKind Kind, string OriginalIdentity, Win16ApiMetadata? Api, string Diagnostic);
public static class Win16ImportResolver
{
    public static Win16CallResolution Resolve(string symbol, IrModule? module = null, IReadOnlyDictionary<string, string>? bindings = null)
    {
        if (module?.Functions.Any(f => f.Name == symbol) == true)
        {
            return new(Win16CallTargetKind.Internal, symbol, null, "translated function linkage required");
        }

        string identity = bindings is not null && bindings.TryGetValue(symbol, out var mapped)
            ? mapped
            : symbol;
        var api = Win16ApiRegistry.Find(identity);
        if (api is not null)
        {
            return new(Win16CallTargetKind.Imported, symbol, api, api.Supported
                ? "bound"
                : "unsupported API adapter");
        }

        return new(Win16CallTargetKind.Unresolved, symbol, null, module?.Externals?.Contains(symbol) == true
            ? "declared external has no API metadata"
            : "no internal symbol or API binding");
    }

    public static Win16CallResolution Resolve(IrInstruction call, IrModule? module = null) => call.Operands.Count != 1 || call.Operands[0].Kind != "Symbol"
        ? new(Win16CallTargetKind.Dynamic, call.OriginalText, null, "indirect call mapping required")
        : Resolve(call.Operands[0].Text, module);
}
