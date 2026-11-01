using Translator.Binary;

namespace Translator.Analysis;

public sealed record NeFunctionEntry(NeBinaryAddress Address, IReadOnlyList<string> Names, IReadOnlyList<string> Evidence);
public sealed record NeInstructionInventory(NeBinaryAddress Address, int Size, string BytesHex, string Mnemonic, string FlowControl, string Text);
public sealed record NeDiscoveryDiagnostic(string Code, NeBinaryAddress Address, string Message);
public sealed record NeDependency(NeBinaryAddress Function, NeBinaryAddress Site, string Kind, NeBinaryAddress? Target, NeImportReference? Import, string Classification, bool Resolved, string Detail);
public sealed record NeDiscoveredFunction(NeBinaryAddress Address, string Name, IReadOnlyList<string> Aliases, string BoundaryStatus, string TranslationStatus, IReadOnlyList<NeInstructionInventory> Instructions, IReadOnlyList<NeDependency> Dependencies, IReadOnlyList<NeDiscoveryDiagnostic> Diagnostics);
public sealed record NeProgramGraph(string ExecutableSha256, int ExecutableSize, NeBinaryAddress Root, string? SymbolVerification, IReadOnlyList<NeFunctionEntry> Entries, IReadOnlyList<NeDiscoveredFunction> Functions, IReadOnlyList<NeDependency> Dependencies, bool ClosureComplete, IReadOnlyList<NeDiscoveryDiagnostic> Diagnostics)
{
    public string DiscoveryMode { get; init; } = "Selective";
}

public sealed record NeDiscoveryLimits(int MaximumFunctions = 1024, int MaximumInstructions = 100000, int MaximumInstructionsPerFunction = 10000);
