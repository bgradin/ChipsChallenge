using Translator.Analysis;
using Translator.Binary;
using Translator.CodeGen;
using Translator.IR;
using Translator.Transform;
using Win16_Runtime;

namespace Translator.Linker;

public sealed record NeCompiledFunction(NeBinaryAddress Address, string Name, string NativeSymbol, int NativeOffset, CallDistance Distance, MachineCodeArtifact Code);
public sealed record NeNativeArtifact(string ExecutableSha256, NeProgramGraph Graph, IrModule Module, IReadOnlyList<NeCompiledFunction> Functions, MachineCodeArtifact LinkedCode, IReadOnlyDictionary<string, int> EntryPoints, IReadOnlyList<NeSegmentMapping> SegmentMappings)
{
    public IReadOnlyList<string> FatalBoundaries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<NeAssemblyComparison> ReconstructionComparisons { get; init; } = Array.Empty<NeAssemblyComparison>();
}

/// <summary>Coordinates existing analysis, IR passes, emission and native symbol layout.</summary>
public static class NeNativeTranslation
{
    public static IrModule Normalize(IrModule module)
    {
        var pipeline = new TranslationPipeline(new RegisterNormalizationPass(), new OperandWidthNormalizationPass(), new ArithmeticWidthNormalizationPass(), new FlagSemanticsNormalizationPass(), new ControlFlowNormalizationPass(), new NativeCompatibilityValidationPass());
        var result = pipeline.Run(module);
        if (result.Diagnostics.Count != 0)
        {
            throw new NotSupportedException(string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code} at {d.Source?.File}: {d.Message} [{d.OriginalText}]")));
        }

        return result.Module;
    }

    public static NeNativeArtifact Compile(ReadOnlySpan<byte> bytes, string root, NeLoadedProgram image, NeVerifiedSymbols? symbols = null, IrModule? reconstruction = null, IReadOnlyDictionary<string, int>? comparisonBindings = null, IReadOnlyDictionary<string, Win16ApiMetadata>? nativeContracts = null, IReadOnlyList<string>? additionalRoots = null, IReadOnlySet<NeBinaryAddress>? includedFunctions = null, IReadOnlyDictionary<string, Win16CodeAddress>? rejectedImports = null, bool resolveIndirectTransfers = false, IReadOnlySet<NeBinaryAddress>? rejectedIndirectSites = null)
    {
        var graph = new NeProgramAnalyzer().Analyze(bytes, root, symbols, resolveIndirectTransfers: resolveIndirectTransfers);
        if (additionalRoots is { Count: > 0 })
        {
            byte[] snapshot = bytes.ToArray();
            var graphs = new[]
            {
                graph
            }.Concat(additionalRoots.Select(r => new NeProgramAnalyzer().Analyze(snapshot, r, symbols, resolveIndirectTransfers: resolveIndirectTransfers))).ToArray();
            graph = graph with
            {
                Functions = graphs.SelectMany(g => g.Functions).DistinctBy(f => f.Address).OrderBy(f => f.Address).ToArray(),
                Dependencies = graphs.SelectMany(g => g.Dependencies).Distinct().OrderBy(d => d.Site).ToArray(),
                Diagnostics = graphs.SelectMany(g => g.Diagnostics).Distinct().OrderBy(d => d.Address).ToArray(),
                Entries = graphs.SelectMany(g => g.Entries).DistinctBy(e => e.Address).OrderBy(e => e.Address).ToArray(),
                ClosureComplete = graphs.All(g => g.ClosureComplete)
            };
        }

        var rejectedFunctions = new HashSet<NeBinaryAddress>();
        if (includedFunctions is not null)
        {
            if (!includedFunctions.Contains(graph.Root) || includedFunctions.Any(a => !graph.Functions.Any(f => f.Address == a)))
            {
                throw new InvalidDataException("Every bounded translation function must have original discovery evidence.");
            }

            var selected = graph.Functions.Where(f => includedFunctions.Contains(f.Address)).ToArray();
            rejectedFunctions = selected.SelectMany(f => f.Dependencies).Where(d => d.Target is { } t && !includedFunctions.Contains(t) && d.Kind is not ("DataReference" or "JumpTableTarget")).Select(d => d.Target!.Value).ToHashSet();
            var deps = selected.SelectMany(f => f.Dependencies).ToArray();
            graph = graph with
            {
                Functions = selected,
                Dependencies = deps,
                ClosureComplete = false,
                Diagnostics = graph.Diagnostics.Where(d => selected.Any(f => f.Instructions.Any(i => i.Address == d.Address))).ToArray()
            };
        }

        if (image.Report.Sha256 != graph.ExecutableSha256)
        {
            throw new InvalidDataException("Loaded image and binary graph identities disagree.");
        }

        var module = Normalize(includedFunctions is not null
            ? new NeBinaryIrBuilder().BuildBounded(bytes, graph, nativeContracts!, (rejectedImports?.Keys ?? []).ToHashSet(), rejectedFunctions, rejectedIndirectSites)
            : nativeContracts is null
                ? new NeBinaryIrBuilder().Build(bytes, graph)
                : new NeBinaryIrBuilder().BuildWithContracts(bytes, graph, nativeContracts));
        var comparisons = new List<NeAssemblyComparison>();
        if (reconstruction is not null)
        {
            var reference = Normalize(reconstruction);
            foreach (var function in module.Functions)
            {
                var other = reference.Functions.SingleOrDefault(f => f.Name == function.Name) ?? throw new InvalidDataException($"No reconstructed function matching binary symbol {function.Name}; supply verified name metadata.");
                var comparison = NeAssemblyEquivalence.Compare(function, other, comparisonBindings);
                comparisons.Add(comparison);
                if (!comparison.Equivalent)
                {
                    throw new InvalidDataException($"Reconstruction/binary mismatch for {function.Name}: " + System.Text.Json.JsonSerializer.Serialize(comparison.Differences));
                }
            }
        }

        // Required original fixups must be applied even though unrelated game imports may be unsupported.
        foreach (var audit in image.Report.Relocations)
        {
            var record = audit.Relocation;
            bool required = image.Report.Segments.Single(s => s.SegmentIndex == record.SourceSegmentIndex).IsData || graph.Functions.SelectMany(f => f.Instructions).Any(i => i.Address.SegmentIndex == record.SourceSegmentIndex && record.SourceOffsets.Any(o => o >= i.Address.Offset && o < i.Address.Offset + i.Size)) || graph.Dependencies.Any(d => d.Kind == "DataReference" && d.Target is { } data && data.SegmentIndex == record.SourceSegmentIndex && record.SourceOffsets.Contains(data.Offset));
            if (required && audit.Status != "Applied")
            {
                throw new InvalidDataException($"Required binary relocation {record.SourceSegmentIndex:X4}:{record.SourceOffset:X4} is {audit.Status}: {audit.Diagnostic}.");
            }
        }

        var selectors = image.Report.Segments.ToDictionary(s => NeBinaryIrBuilder.SelectorSymbol(s.SegmentIndex), s => (int)s.Selector);
        var targets = module.Functions.ToDictionary(f => f.Name, f => new Win16CodeAddress(image.SelectorFor(f.OriginalSegmentIndex!.Value), f.OriginalOffset!.Value));
        if (nativeContracts is not null)
        {
            foreach (var api in nativeContracts.Values)
            {
                targets[api.Identity.CanonicalName] = new(0xf000, api.EntryOffset);
            }
        }

        foreach (var rejected in rejectedFunctions)
        {
            targets[NeBinaryIrBuilder.NativeSymbol(rejected)] = image.Resolve(rejected.SegmentIndex, rejected.Offset);
        }

        if (rejectedImports is not null)
        {
            foreach (var rejected in rejectedImports)
            {
                targets["__rejected_" + rejected.Key] = rejected.Value;
            }
        }

        var functions = new List<NeCompiledFunction>();
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        var code = new List<byte>();
        var relocations = new List<MachineCodeRelocation>();
        foreach (var function in module.Functions)
        {
            while (code.Count % 16 != 0)
            {
                code.Add(0x90);
            }

            int offset = code.Count;
            var address = new NeBinaryAddress(function.OriginalSegmentIndex!.Value, function.OriginalOffset!.Value);
            string symbol = NeBinaryIrBuilder.NativeSymbol(address);
            var artifact = new Win16BodyCodeGenerator().Generate(function, symbols: selectors, module: module, targets: targets);
            entries.Add(symbol, offset);
            functions.Add(new(address, function.Name, symbol, offset, function.EntryDistance, artifact));
            code.AddRange(artifact.Bytes);
            relocations.AddRange((artifact.Relocations ?? Array.Empty<MachineCodeRelocation>()).Select(r => r with { Offset = r.Offset + offset }));
        }

        graph = graph with
        {
            Functions = graph.Functions.Select(f => f with { TranslationStatus = "NativeCompiled" }).ToArray()
        };
        return new NeNativeArtifact(graph.ExecutableSha256, graph, module, functions.AsReadOnly(), new(code.ToArray(), "AMD64", X64Abi.Windows, "Linked binary-derived Win16 bodies; MachineState ABI v2/44 bytes", relocations.AsReadOnly()), entries, image.Report.Segments)
        {
            ReconstructionComparisons = comparisons.AsReadOnly(),
            FatalBoundaries = rejectedFunctions.Select(a => "Original routine " + a.Identity + ": unregistered target, fatal if reached").Concat((rejectedImports?.Keys ?? []).Select(i => "Import " + i + ": fatal rejection, no signature or successful stub")).Concat((rejectedIndirectSites ?? new HashSet<NeBinaryAddress>()).Select(a => "Indirect call " + a.Identity + ": fatal if reached, no target guessed")).Order(StringComparer.Ordinal).ToArray()
        };
    }
}

/// <summary>One linked native code allocation and a loaded original image. Application bodies are statically emitted AMD64.</summary>
public sealed record NeInitializedProcedure(NeBinaryAddress Address, string Name, string NativeSymbol, Win16CodeAddress RuntimeAddress, CallDistance Distance);
public sealed record NeNativeInitialization(NeRuntimeInitialization Runtime, IReadOnlyList<NeInitializedProcedure> Procedures, int NativeCodeBytes, string CodeOwnership);
public sealed class NeNativeProgram : IDisposable
{
    private readonly NeRuntimeProgram _runtime;
    private readonly NativeWin16Body _native;
    private bool _disposed;
    public NeNativeArtifact Artifact
    {
        get;
    }
    public NeNativeInitialization Initialization => new(Runtime.Initialization, Artifact.Functions.Select(f => new NeInitializedProcedure(f.Address, f.Name, f.NativeSymbol, Runtime.Image.Resolve(f.Address.SegmentIndex, f.Address.Offset), f.Distance)).ToArray(), Artifact.LinkedCode.Bytes.Length, "One owned native code allocation; procedure bindings are valid only for this program lifetime");

    public NeRuntimeProgram Runtime
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _runtime;
        }
    }

    private NeNativeProgram(NeRuntimeProgram runtime, NativeWin16Body native, NeNativeArtifact artifact)
    {
        _runtime = runtime;
        _native = native;
        Artifact = artifact;
    }

    public static NeNativeProgram Create(ReadOnlySpan<byte> bytes, string root, NeVerifiedSymbols? symbols = null, IrModule? reconstruction = null, IReadOnlyDictionary<string, int>? comparisonBindings = null, NeRuntimeOptions? runtimeOptions = null, bool resolveIndirectTransfers = false)
    {
        // Incomplete whole-game imports are retained; Compile rejects every unresolved fixup needed by this closure.
        var runtime = NeRuntimeProgram.Load(bytes, new(AllowIncompleteRelocations: true), runtimeOptions);
        NativeWin16Body? native = null;
        try
        {
            var artifact = NeNativeTranslation.Compile(bytes, root, runtime.Image, symbols, reconstruction, comparisonBindings, resolveIndirectTransfers: resolveIndirectTransfers);
            native = new(artifact.LinkedCode, artifact.EntryPoints);
            foreach (var function in artifact.Functions)
            {
                native.Register(runtime.Dispatcher, runtime.Image.Memory, function.Name, runtime.Image.Resolve(function.Address.SegmentIndex, function.Address.Offset), function.Distance, function.NativeSymbol);
            }

            return new(runtime, native, artifact);
        }
        catch
        {
            native?.Dispose();
            runtime.Dispose();
            throw;
        }
    }

    public MachineState CreateState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = _runtime.Image.InitialState;
        if (Artifact.Functions.Single(f => f.Address == Artifact.Graph.Root).Distance == CallDistance.Near)
        {
            state.CS = _runtime.Image.SelectorFor(Artifact.Graph.Root.SegmentIndex);
        }

        return state;
    }

    public void Invoke(ref MachineState state, IReadOnlyList<ushort> argumentWords)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Logical argument zero is nearest the return address. Caller cleanup remains observable.
        for (int i = argumentWords.Count - 1; i >= 0; i--)
        {
            Win16Stack.Push(ref state, _runtime.Image.Memory, argumentWords[i]);
        }

        var root = Artifact.Functions.Single(f => f.Address == Artifact.Graph.Root);
        _runtime.Dispatcher.Call(ref state, _runtime.Image.Memory, root.Name, root.Distance);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _native.Dispose();
        _runtime.Dispose();
    }
}
