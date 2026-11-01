using System.Security.Cryptography;
using Iced.Intel;
using Translator.Analysis;
using Translator.Binary;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Linker;

public sealed record NeControlEntry(NeBinaryAddress Original, Win16CodeAddress RuntimeAddress, string NativeSymbol, int NativeOffset, string BoundaryEvidence);
public sealed record NeControlImport(NeImportReference Import, Win16CodeAddress RuntimeAddress, string Status)
{
    public string Kind { get; init; } = "Procedure";
    public ushort? ConstantValue
    {
        get; init;
    }
}

public sealed record NeControlSite(NeBinaryAddress Site, string Form, string Kind, IReadOnlyList<NeBinaryAddress> OriginalTargets, IReadOnlyList<string> ImportTargets, string Resolution, bool Resolved, bool DynamicResolver, bool VerifiedTargetSet);
public sealed record NeControlRejection(NeBinaryAddress Address, string BytesHex, string Reason);
public sealed record NeNativeDispatchSlot(int Index, NeBinaryAddress Slot, NeBinaryAddress Target, int NativeOffset);
public sealed record NeNativeDispatchTable(NeBinaryAddress Site, NeBinaryAddress TableBase, IReadOnlyList<NeNativeDispatchSlot> Slots);
public sealed record NeControlFlowSummary(int DirectTransferSites, int IndirectTransferSites, int ReturnSites, int ResolvedTargets, int VerifiedTargetSets, int UnresolvedTargetSets, int DynamicAddressResolutionSites, int UnsupportedTransfers, int InterruptServiceSites, int NativeEntries, int NativeBlocks, int NativeCodeBytes, bool AllStaticTargetsResolved, bool WholeProgramExecutionSupported);
public sealed record NeControlFlowReport(string Format, string ExecutableSha256, string NativeCodeSha256, string? SymbolVerification, string Policy, NeControlFlowSummary Summary, IReadOnlyList<NeControlEntry> Entries, IReadOnlyList<NeControlImport> Imports, IReadOnlyList<NeControlSite> Sites, IReadOnlyList<MachineCodeRelocation> NativeHelperFixups, IReadOnlyList<NeNativeDispatchTable> NativeDispatchTables, IReadOnlyList<NeControlRejection> RejectedContinuations);
public sealed record NeWholeProgramControlArtifact(NeControlFlowReport Report, MachineCodeArtifact Code, IReadOnlyDictionary<string, int> NativeEntries, IReadOnlyDictionary<int, ushort> Selectors);
/// <summary>Links original control flow to statically compiled native basic blocks and interior-entry thunks.
/// Native runtime dispatch never reads or decodes original instruction streams.</summary>
public static class NeWholeProgramControlCompiler
{
    private static string Label(NeBinaryAddress a) => $"L_{a.Offset:X4}";

    private static string Symbol(NeBinaryAddress a) => $"flow_{a.SegmentIndex:X4}_{a.Offset:X4}";

    private static uint Pack(Win16CodeAddress a) => (uint)a.Segment << 16 | a.Offset;
    private static readonly SourceLocation _synthetic = new("NE:control-link", 0);
    public static NeWholeProgramControlArtifact Compile(byte[] image, NeVerifiedSymbols? symbols = null)
    {
        var executable = new NeExecutableReader().Read(image);
        var discovery = NeWholeProgramDiscoveryBuilder.Build(image, symbols);
        if (NeMemoryAuditBuilder.Build(image, discovery).StaticResolutionRequired)
        {
            throw new NotSupportedException("Proven executable-memory writes require explicit static resolution before native compilation.");
        }

        if (!discovery.Summary.WorklistExhausted)
        {
            throw new InvalidDataException("Whole-program discovery limits prevent authoritative control linking.");
        }

        var graph = discovery.Graph;
        var selectors = executable.Segments.ToDictionary(s => s.Index, s => checked((ushort)(0x1000 + (s.Index - 1) * 8)));
        if (selectors.Values.Any(s => s >= 0xf000))
        {
            throw new InvalidDataException("Code/data selectors overlap the reserved import namespace.");
        }

        Win16CodeAddress Runtime(NeBinaryAddress a) => new(selectors[a.SegmentIndex], a.Offset);

        var original = discovery.Instructions.ToDictionary(i => i.Original.Address);
        var admitted = discovery.Instructions.Where(i => !i.OverlapsAnotherInterpretation).Select(i => i.Original.Address).ToHashSet();
        var evidence = admitted.ToDictionary(a => a, _ => "Original-byte unambiguous CFG instruction");
        // A direct branch proves its target boundary even when another interpretation
        // overlaps it. Preserve the other stream as rejected, rather than assuming an
        // import is non-returning or selecting a decode based on a conventional prologue.
        var explicitTargets = discovery.ControlFlow.Where(e => e.Target is not null && e.Kind is not ("Fallthrough" or "PossibleInterruptReturn")).Select(e => e.Target!.Value).Concat(discovery.Roots.Where(r => r.Evidence.Any(e => !e.StartsWith("CodePointerCandidate", StringComparison.Ordinal))).Select(r => r.Address)).ToHashSet();
        foreach (var target in explicitTargets.Where(original.ContainsKey).Order())
        {
            if (admitted.Add(target))
            {
                evidence[target] = "Explicit original transfer/entry boundary; competing decode retained separately";
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var instruction in discovery.Instructions.Where(i => i.OverlapsAnotherInterpretation && admitted.Contains(i.Original.Address)))
            {
                var next = instruction.Original.Address with
                {
                    Offset = unchecked((ushort)(instruction.Original.Address.Offset + instruction.Original.Size))
                };
                if (!original.TryGetValue(next, out var candidate) || admitted.Contains(next) || candidate.Original.FlowControl != "Next")
                {
                    continue;
                }

                // Do not admit a continuation that enters a different admitted instruction's interior.
                bool conflict = admitted.Any(a => a.SegmentIndex == next.SegmentIndex && a != next && a.Offset < next.Offset + candidate.Original.Size && a.Offset + original[a].Original.Size > next.Offset);
                if (!conflict)
                {
                    admitted.Add(next);
                    evidence[next] = "Contiguous original decode from explicitly evidenced overlapping entry";
                    changed = true;
                }
            }
        }
        while (changed);
        var rejected = discovery.Instructions.Where(i => !admitted.Contains(i.Original.Address)).Select(i => new NeControlRejection(i.Original.Address, i.Original.BytesHex, "Competing decode without an independently established compatible entry; native resolver rejects this continuation. No non-returning import contract is assumed.")).ToArray();
        var imports = executable.Relocations.Where(r => r.Import is not null).Select(r => r.Import!).DistinctBy(i => i.Identity).OrderBy(i => i.Identity, StringComparer.Ordinal).Select((i, n) => new NeControlImport(i, new(0xf000, checked((ushort)(0x1000 + n * 8))), "Original import identity mapped; compatibility contract/implementation not certified (7.6)")).Select(i => Win16ExportContracts.Find(i.Import) is { Kind: "Constant", ConstantValue: { } value }
            ? i with
            {
                RuntimeAddress = new(0, value),
                Status = "Verified original constant export; word value fixup only; not callable",
                Kind = "Constant",
                ConstantValue = value
            }
            : i).ToArray();
        if (imports.Where(i => i.Kind == "Constant").Any(i => executable.Relocations.Any(r => r.Import == i.Import && r.RawAddressType != 5)))
        {
            throw new NotSupportedException("Constant export has a non-word relocation requiring explicit analysis.");
        }

        var importMap = imports.ToDictionary(i => i.Import.Identity, i => i.RuntimeAddress, StringComparer.Ordinal);
        var owners = graph.Functions.SelectMany(f => f.Instructions.Select(i => (i.Address, Owner: f.Address))).GroupBy(i => i.Address).ToDictionary(g => g.Key, g => g.First().Owner);
        var edges = discovery.ControlFlow.GroupBy(e => e.Site).ToDictionary(g => g.Key, g => g.ToArray());
        var tables = discovery.IndirectTransfers.ToDictionary(t => t.Site);
        var addressBindings = admitted.ToDictionary(Symbol, a => Runtime(a), StringComparer.Ordinal);
        foreach (var import in imports)
        {
            addressBindings.Add("import_" + import.Import.Identity, import.RuntimeAddress);
        }

        var selectorBindings = selectors.ToDictionary(p => NeBinaryIrBuilder.SelectorSymbol(p.Key), p => (int)p.Value);
        var builder = new NeBinaryIrBuilder();
        var bodyCode = new List<byte>();
        var fixups = new List<MachineCodeRelocation>();
        var nativeEntries = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = new List<NeControlEntry>();
        var sites = new List<NeControlSite>();
        int blockCount = 0;

        IrOperand Constant(uint value) => new("Immediate", value.ToString(System.Globalization.CultureInfo.InvariantCulture), RegisterWidth.Dword, ImmediateValue: value);


        IrInstruction Flow(ushort form, IrOperand operand, IrInstruction? source = null, CallDistance distance = CallDistance.Far) => (source ?? new("FLOW", [operand], _synthetic, "Native original-address transfer")) with
        {
            Operation = "FLOW",
            Operands = [operand],
            ReturnCleanup = null,
            ControlEffect = (NativeControlEffect)form,
            CallDistance = distance,
            IndirectTransfer = null,
            Prefix = RepeatPrefix.None,
            Condition = BranchCondition.None
        };


        IrInstruction Branch(NeBinaryAddress target) => Flow(0, Constant(target.Offset) with
        {
            Width = RegisterWidth.Word
        }, distance: CallDistance.Near);

        foreach (var block in discovery.BasicBlocks)
        {
            var addresses = block.Instructions.Where(admitted.Contains).ToArray();
            if (addresses.Length == 0)
            {
                continue;
            }

            for (int n = 1; n < addresses.Length; n++)
            {
                if (addresses[n - 1].Offset + original[addresses[n - 1]].Original.Size != addresses[n].Offset)
                {
                    throw new InvalidDataException("A native basic block cannot skip unapproved original bytes.");
                }
            }

            var code = new List<IrInstruction>();
            foreach (var address in addresses)
            {
                var inventory = original[address].Original;
                var decoder = Decoder.Create(16, new ByteArrayCodeReader(Convert.FromHexString(inventory.BytesHex)));
                decoder.IP = address.Offset;
                var ins = decoder.Decode();
                IrInstruction ir;
                try
                {
                    ir = builder.AuditInstruction(image, graph, owners[address], address);
                }
                catch (NotSupportedException) when (ins.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch)
                {
                    // Retain diagnostic coverage instead of inventing a literal far selector
                    // or function contract. Executable construction rejects this report.
                    ir = new("FAULT", [], new("NE:" + address.Identity, 0), inventory.Text, (uint)address.Offset, inventory.Size, OriginalMnemonic: ins.Mnemonic.ToString());
                }

                code.Add(new("LABEL", [new("Label", Label(address))], ir.Source, "Original instruction boundary"));
                var siteEdges = edges.GetValueOrDefault(address) ?? [];
                var next = address with
                {
                    Offset = unchecked((ushort)(address.Offset + inventory.Size))
                };
                bool direct = ins.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch;
                bool indirect = ins.FlowControl is FlowControl.IndirectCall or FlowControl.IndirectBranch;
                bool returns = ins.FlowControl == FlowControl.Return;
                if (direct || indirect || returns)
                {
                    var targetEdges = siteEdges.Where(e => e.Kind is not ("Fallthrough" or "PossibleInterruptReturn") && (e.Target is not null || e.Import is not null)).ToArray();
                    var targets = targetEdges.Where(e => e.Target is not null).Select(e => e.Target!.Value).Distinct().Order().ToArray();
                    var importTargets = targetEdges.Where(e => e.Import is not null).Select(e => e.Import!.Identity).Distinct().Order(StringComparer.Ordinal).ToArray();
                    bool readonlyPointer = indirect && ins.MemorySegment == Register.CS && ins.MemoryBase == Register.None && ins.MemoryIndex == Register.None && targetEdges.Length > 0 && targetEdges.All(e => e.Target is { } t && admitted.Contains(t) || e.Import is { } import && importMap.ContainsKey(import.Identity));
                    bool bounded = indirect && (tables[address].Resolved || readonlyPointer);
                    bool resolved = returns || indirect || targetEdges.Length > 0 && targets.All(admitted.Contains) && importTargets.All(importMap.ContainsKey);
                    sites.Add(new(address, ins.Code.ToString(), returns
                        ? "Return"
                        : indirect
                            ? "Indirect"
                            : "Direct", targets, importTargets, returns
                        ? "OriginalStackReturnToApprovedNativeEntry"
                        : bounded
                            ? "VerifiedNativeDispatchTable"
                            : indirect
                                ? "BoundedOriginalAddressResolver"
                                : resolved
                                    ? "NativeEntryOrOriginalImportIdentity"
                                    : "RejectedUnprovenStaticTarget", resolved, indirect && !bounded || returns, bounded));
                }

                if (ins.FlowControl == FlowControl.Interrupt)
                {
                    sites.Add(new(address, ins.Code.ToString(), "InterruptService", [], [], "ExplicitInterruptServiceBoundary;freestanding-adapter-unimplemented", true, false, false));
                }

                switch (ins.FlowControl)
                {
                    case FlowControl.Next:
                    case FlowControl.Interrupt:
                        code.Add(ir);
                        if (address == addresses[^1])
                        {
                            code.Add(Branch(next));
                        }

                        break;
                    case FlowControl.Return:
                        code.Add(Flow(ins.Code is Code.Retfw or Code.Retfw_imm16
                            ? (ushort)5
                            : (ushort)4, Constant(ir.ReturnCleanup ?? 0), ir));
                        break;
                    case FlowControl.ConditionalBranch:
                        {
                            var proven = siteEdges.SingleOrDefault(e => e.Kind == "ConditionalBranch" && e.Target is not null)?.Target;
                            if (proven is null || ir.Operation == "FAULT")
                            {
                                code.Add(new("FAULT", [], ir.Source, "Unproven conditional target"));
                                break;
                            }

                            var taken = proven.Value;
                            string label = "taken_" + address.Identity;
                            code.Add(ir with
                            {
                                Operands = [new("Label", label)]
                            });
                            code.Add(Branch(next));
                            code.Add(new("LABEL", [new("Label", label)], ir.Source, label));
                            code.Add(Branch(taken));
                            break;
                        }

                    case FlowControl.Call:
                    case FlowControl.UnconditionalBranch:
                        {
                            var transfer = siteEdges.SingleOrDefault(e => e.Kind != "Fallthrough" && (e.Target is not null || e.Import is not null));
                            if (transfer is null)
                            {
                                code.Add(new("FAULT", [], ir.Source, "Unproven direct target"));
                                break;
                            }

                            if (transfer.Import is not null && imports.Any(i => i.Import.Identity == transfer.Import.Identity && i.Kind == "Constant"))
                            {
                                throw new InvalidDataException("A constant export cannot be used as a control-flow target.");
                            }

                            var target = transfer.Target is { } originalTarget
                                ? Runtime(originalTarget)
                                : importMap[transfer.Import!.Identity];
                            bool far = ins.Op0Kind == OpKind.FarBranch16;
                            code.Add(Flow(ins.FlowControl == FlowControl.Call
                                ? far
                                    ? (ushort)3
                                    : (ushort)2
                                : far
                                    ? (ushort)1
                                    : (ushort)0, far
                                ? Constant(Pack(target))
                                : Constant(target.Offset) with
                                {
                                    Width = RegisterWidth.Word
                                }, ir, far
                                ? CallDistance.Far
                                : CallDistance.Near));
                            break;
                        }

                    case FlowControl.IndirectCall:
                    case FlowControl.IndirectBranch:
                        {
                            bool call = ins.FlowControl == FlowControl.IndirectCall;
                            bool far = ins.Code is Code.Call_m1616 or Code.Jmp_m1616;
                            var flow = Flow(call
                                ? far
                                    ? (ushort)3
                                    : (ushort)2
                                : far
                                    ? (ushort)1
                                    : (ushort)0, ir.Operands[0], ir, far
                                ? CallDistance.Far
                                : CallDistance.Near);
                            var table = tables[address];
                            var fixedEdges = siteEdges.Where(e => e.Kind != "Fallthrough" && (e.Target is not null || e.Import is not null)).ToArray();
                            bool readonlyPointer = ins.MemorySegment == Register.CS && ins.MemoryBase == Register.None && ins.MemoryIndex == Register.None && fixedEdges.Length > 0 && fixedEdges.All(e => e.Target is { } t && admitted.Contains(t) || e.Import is { } import && importMap.ContainsKey(import.Identity));
                            if (table.Resolved && !call)
                            {
                                var candidates = table.Entries.Select(e => e.Target).Distinct().Order().ToArray();
                                code.Add(ir with
                                {
                                    Operation = "DISPATCH",
                                    Condition = BranchCondition.None,
                                    IndirectTransfer = new(CallDistance.Near, candidates.Select(t => new IrIndirectTarget(t.SegmentIndex, t.Offset, "dispatch_" + t.Identity, null)).ToArray(), table.IndexComputation, table.TableBase!.Value.Offset, table.Entries.Count)
                                });
                                foreach (var target in candidates)
                                {
                                    code.Add(new("LABEL", [new("Label", "dispatch_" + target.Identity)], ir.Source, "Verified dispatch slot"));
                                    code.Add(Branch(target));
                                }
                            }
                            else
                            {
                                if (table.Resolved)
                                {
                                    flow = flow with
                                    {
                                        IndirectTransfer = new(CallDistance.Near, table.Entries.Select(e => e.Target).Distinct().Order().Select(t => new IrIndirectTarget(t.SegmentIndex, t.Offset, null, Symbol(t))).ToArray(), table.IndexComputation)
                                    };
                                }

                                if (readonlyPointer)
                                {
                                    flow = flow with
                                    {
                                        IndirectTransfer = new(far
                                            ? CallDistance.Far
                                            : CallDistance.Near, fixedEdges.Select(e => e.Target is { } t
                                            ? new IrIndirectTarget(t.SegmentIndex, t.Offset, null, Symbol(t))
                                            : new IrIndirectTarget(0, importMap[e.Import!.Identity].Offset, null, "import_" + e.Import.Identity)).Distinct().ToArray(), "Validated relocation-backed readonly CS pointer; code aliases must retain immutability")
                                    };
                                }

                                code.Add(flow);
                            }

                            break;
                        }

                    default:
                        throw new NotSupportedException($"Unresolved original control effect {ins.FlowControl} at {address}.");
                }
            }

            var function = new IrFunction("block_" + block.Address.Identity, code, Arguments: [], SavesDataSegment: false)
            {
                ExplicitFrame = true,
                CaptureNativeLabels = true
            };
            function = NeNativeTranslation.Normalize(new("NE:whole-program-control", [function])).Functions.Single();
            var native = new Win16BodyCodeGenerator().Generate(function, symbols: selectorBindings, targets: addressBindings);
            while (bodyCode.Count % 16 != 0)
            {
                bodyCode.Add(0x90);
            }

            int start = bodyCode.Count;
            bodyCode.AddRange(native.Bytes);
            blockCount++;
            fixups.AddRange(native.Relocations!.Select(r => r with { Offset = start + r.Offset }));
            foreach (var address in addresses)
            {
                int offset;
                if (address == addresses[0])
                {
                    offset = start;
                }
                else
                {
                    offset = bodyCode.Count;
                    bodyCode.AddRange(Win16BodyCodeGenerator.EntryPrologue);
                    bodyCode.Add(0xe9);
                    int displacement = start + native.NativeLabels![Label(address)] - (bodyCode.Count + 4);
                    bodyCode.AddRange(BitConverter.GetBytes(displacement));
                }

                nativeEntries.Add(Symbol(address), offset);
                entries.Add(new(address, Runtime(address), Symbol(address), offset, evidence[address]));
            }
        }

        var linked = new MachineCodeArtifact(bodyCode.ToArray(), "AMD64", X64Abi.Windows, "Statically compiled original basic blocks and original-address entry thunks; no guest instruction decoder", fixups);
        int directSites = sites.Count(s => s.Kind == "Direct"), indirectSites = sites.Count(s => s.Kind == "Indirect"), returnSites = sites.Count(s => s.Kind == "Return");
        int unsupported = sites.Count(s => !s.Resolved);
        var report = new NeControlFlowReport("NeControlFlowResolution.v1", executable.Sha256, Convert.ToHexString(SHA256.HashData(linked.Bytes)).ToLowerInvariant(), graph.SymbolVerification, "Whole-program static native block linking. Every admitted original instruction boundary has an actual emitted AMD64 entry (interior entries have prologue thunks). Static transfers use original selectors/offsets; imports have distinct identity-only slots, not invented contracts. Bounded tables retain exact target sets. Dynamic calls/jumps/returns select only registered compiled entries. Basic-block dispatch reads no original opcode stream, performs no JIT and never interprets guest instructions. Competing fallthrough interpretations remain rejected; no FatalExit non-return contract is assumed. Runtime compatibility, complete memory/lifecycle and PE packaging remain later milestones.", new(directSites, indirectSites, returnSites, sites.SelectMany(s => s.OriginalTargets.Select(a => a.Identity).Concat(s.ImportTargets)).Distinct().Count(), sites.Count(s => s.VerifiedTargetSet), sites.Count(s => s.Kind == "Indirect" && !s.VerifiedTargetSet), sites.Count(s => s.DynamicResolver), unsupported, sites.Count(s => s.Kind == "InterruptService"), entries.Count, blockCount, linked.Bytes.Length, unsupported == 0, false), entries.OrderBy(e => e.Original).ToArray(), imports, sites.OrderBy(s => s.Site).ToArray(), fixups.AsReadOnly(), tables.Values.Where(t => t.Resolved).OrderBy(t => t.Site).Select(t => new NeNativeDispatchTable(t.Site, t.TableBase!.Value, t.Entries.Select(e => new NeNativeDispatchSlot(e.Index, e.Slot, e.Target, nativeEntries[Symbol(e.Target)])).ToArray())).ToArray(), rejected);
        return new(report, linked, nativeEntries, selectors);
    }
}

/// <summary>Explicit task environment for managed verification of original lifecycle code.</summary>
public sealed record NeTaskLifecycleOptions(string CommandLine = "", ushort ShowCommand = 1, Func<bool>? WaitEvent = null);
public sealed record NeWholeTaskServiceOptions(byte[] UtilityImage, string ModulePath, IWin16MultimediaBackend? MultimediaBackend = null, string? WindowsDirectory = null);
/// <summary>Owned native code and original segmented memory. Optional lifecycle adapters support
/// original-startup verification without authored game state or a replacement application host.</summary>
public sealed class NeWholeProgramControlProgram : IDisposable
{
    private readonly NeLoadedProgram _image;
    private readonly NativeWin16Body _native;
    private readonly Win16ProgramControl _control;
    private bool _disposed;
    public NeWholeProgramControlArtifact Artifact
    {
        get;
    }
    public SegmentMemory Memory => _image.Memory;
    public MachineState InitialState => _image.InitialState;
    public Win16CallDispatcher Dispatcher
    {
        get;
    }
    public Win16ProgramControl Control => _control;
    public Win16TaskMemory TaskMemory
    {
        get;
    }
    public Win16TaskKernelServices? KernelServices
    {
        get;
    }
    public Win16TaskResources? ResourceServices
    {
        get;
    }
    public Win16TaskFiles? FileServices
    {
        get;
    }
    public Win16TaskProfiles? ProfileServices
    {
        get;
    }
    public Win16TaskSystem? SystemServices
    {
        get;
    }
    public Win16TaskInstances? InstanceServices
    {
        get;
    }
    public Win16TaskGui? GuiServices
    {
        get;
    }
    public Win16TaskLifecycle? LifecycleServices
    {
        get;
    }
    public Win16TaskGraphics? GraphicsServices
    {
        get;
    }
    public Win16TaskUserStructures? StructureServices
    {
        get;
    }
    public Win16TaskGuiResources? GuiResources
    {
        get;
    }
    public Win16TaskWindowHost? WindowServices
    {
        get;
    }
    public Win16TaskModules? ModuleServices
    {
        get;
    }
    public Win16TaskWep? UtilityServices
    {
        get;
    }

    private readonly WindowsTaskMultimediaBackend? _ownedMultimediaBackend;
    private readonly List<string> _serviceCalls = [];
    private readonly HashSet<string> _installedImports = new(StringComparer.Ordinal);
    public IReadOnlyList<string> ServiceCalls => _serviceCalls.AsReadOnly();
    public IReadOnlyList<string> InstalledImports => _installedImports.Order(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<string> LifecycleCalls => _lifecycleCalls.AsReadOnly();

    private readonly List<string> _lifecycleCalls = [];
    private readonly WindowsTaskGuiBackend? _windowsGuiBackend;
    public NeWholeProgramControlProgram(byte[] bytes, NeVerifiedSymbols? symbols = null, long maximumTransfers = 1000000, bool initializeKernelServices = false, string? workingDirectory = null, IWin16GuiBackend? guiBackend = null, NeTaskLifecycleOptions? lifecycleOptions = null, NeWholeTaskServiceOptions? taskServicesOptions = null)
    {
        if (taskServicesOptions is not null)
        {
            if (!initializeKernelServices)
            {
                throw new ArgumentException("Whole-task services require kernel services.");
            }

            ArgumentNullException.ThrowIfNull(taskServicesOptions.UtilityImage);
            Win16ExportContracts.VerifyWepImage(taskServicesOptions.UtilityImage);
            ArgumentException.ThrowIfNullOrWhiteSpace(taskServicesOptions.ModulePath);
            if (guiBackend is null && !OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Whole-task GUI services need Windows or an explicit recording backend.");
            }

            if (taskServicesOptions.MultimediaBackend is null && !OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Whole-task multimedia needs Windows or an explicit recording backend.");
            }

            workingDirectory ??= Path.GetDirectoryName(Path.GetFullPath(taskServicesOptions.ModulePath));
            lifecycleOptions ??= new();
        }

        if (lifecycleOptions is not null && !initializeKernelServices)
        {
            throw new ArgumentException("Task lifecycle requires kernel services.", nameof(lifecycleOptions));
        }

        Artifact = NeWholeProgramControlCompiler.Compile(bytes, symbols);
        if (!Artifact.Report.Summary.AllStaticTargetsResolved)
        {
            throw new InvalidDataException("Unproven static transfers prevent execution.");
        }

        var imports = Artifact.Report.Imports.ToDictionary(i => i.Import.Identity, i => i.RuntimeAddress, StringComparer.Ordinal);
        _image = new NeProgramLoader().Load(bytes, importResolver: i => imports[i.Identity], constantResolver: i => Artifact.Report.Imports.Single(p => p.Import.Identity == i.Identity).ConstantValue);
        NativeWin16Body? code = null;
        try
        {
            code = new(Artifact.Code, Artifact.NativeEntries);
            _native = code;
            Dispatcher = new();
            var approved = Artifact.Report.Entries.Select(e => new Win16ProgramBinding(e.RuntimeAddress, Win16ProgramAddressKind.Code, e.Original.Identity, (ref MachineState state) => _native.Invoke(ref state, _image.Memory, Dispatcher, e.NativeSymbol))).Concat(Artifact.Report.Imports.Where(i => i.Kind != "Constant").Select(i => new Win16ProgramBinding(i.RuntimeAddress, Win16ProgramAddressKind.Import, i.Import.Identity, null)));
            _control = new(approved, _image.Memory, Dispatcher, maximumTransfers: maximumTransfers);
            TaskMemory = new(_image, Dispatcher);
            if (initializeKernelServices)
            {
                if (guiBackend is not null || OperatingSystem.IsWindows())
                {
                    if (guiBackend is null)
                    {
                        guiBackend = _windowsGuiBackend = new WindowsTaskGuiBackend();
                    }

                    GuiServices = new(_image.Memory, guiBackend);
                    InstallImports(Win16TaskGui.Implemented, GuiServices.Invoke);
                }

                KernelServices = new(TaskMemory, _image.Memory, _image.InitialState.DS);
                InstanceServices = new(_control, _image.Memory);
                InstallImports(Win16TaskInstances.Implemented, InstanceServices.Invoke);
                ResourceServices = new(bytes, _image.Memory, TaskMemory.Global, _image.InitialState.DS);
                FileServices = new(_image.Memory, workingDirectory ?? Directory.GetCurrentDirectory());
                ProfileServices = new(_image.Memory, workingDirectory ?? Directory.GetCurrentDirectory());
                string moduleName = new NeExecutableReader().Read(bytes).ResidentNames.FirstOrDefault(n => n.Ordinal == 0)?.Name ?? "MODULE";
                string taskDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
                SystemServices = new(_image.Memory, _image.InitialState.DS, taskServicesOptions?.ModulePath ?? Path.Combine(taskDirectory, moduleName + ".EXE"), taskServicesOptions?.WindowsDirectory ?? (OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                    : taskDirectory));
                InstallImports(Win16TaskSystem.Implemented, SystemServices.Invoke);
                InstallImports(Win16TaskProfiles.Implemented, ProfileServices.Invoke);
                InstallImports(Win16TaskFiles.Implemented, FileServices.Invoke);
                InstallImports(Win16TaskResources.Implemented, ResourceServices.Invoke);
                InstallImports(Win16TaskKernelServices.Implemented, KernelServices.Invoke);
                if (lifecycleOptions is not null)
                {
                    LifecycleServices = new(_image, _control, Dispatcher, KernelServices, lifecycleOptions.WaitEvent, lifecycleOptions.CommandLine, lifecycleOptions.ShowCommand);
                    InstallImports(Win16TaskLifecycle.Implemented, (string identity, ref MachineState state) =>
                    {
                        _lifecycleCalls.Add(identity);
                        return LifecycleServices.Invoke(identity, ref state);
                    });
                }

                if (taskServicesOptions is not null)
                {
                    var handles = GuiServices!.Handles;
                    GraphicsServices = new(_image.Memory, guiBackend!, handles);
                    StructureServices = new(_image.Memory, guiBackend!, handles, _image.InitialState.DS);
                    GuiResources = new(bytes, _image.Memory, guiBackend!, handles, _image.InitialState.DS);
                    WindowServices = new(_image.Memory, TaskMemory, _control, guiBackend!, handles, GuiResources, _image.InitialState);
                    var multimedia = taskServicesOptions.MultimediaBackend;
                    if (multimedia is null)
                    {
                        multimedia = _ownedMultimediaBackend = new WindowsTaskMultimediaBackend();
                    }

                    ModuleServices = new(_image.Memory, _control, multimedia, handles, taskDirectory);
                    UtilityServices = new(taskServicesOptions.UtilityImage.ToArray(), bytes, _image.Memory, guiBackend!, handles, _image.InitialState.DS, Path.GetFullPath(taskServicesOptions.ModulePath));
                    WindowServices.IsTerminated = () => LifecycleServices!.Terminated;
                    WindowServices.MultimediaDeviceToGuest = ModuleServices.GuestDevice;
                    InstallImports(Win16TaskGraphics.Implemented, GraphicsServices.Invoke);
                    InstallImports(Win16TaskUserStructures.Implemented, StructureServices.Invoke);
                    InstallImports(Win16TaskGuiResources.Implemented, GuiResources.Invoke);
                    InstallImports(Win16TaskWindowHost.Implemented, WindowServices.Invoke);
                    InstallImports(Win16TaskModules.Implemented, ModuleServices.Invoke);
                    InstallImports(Win16TaskWep.Implemented, UtilityServices.Invoke);
                }
            }
        }
        catch
        {
            DisposeApplicationServices();
            LifecycleServices?.Dispose();
            GuiServices?.Dispose();
            _windowsGuiBackend?.Dispose();
            InstanceServices?.Dispose();
            SystemServices?.Dispose();
            ProfileServices?.Dispose();
            FileServices?.Dispose();
            ResourceServices?.Dispose();
            KernelServices?.Dispose();
            TaskMemory?.Dispose();
            _control?.Dispose();
            code?.Dispose();
            _image.Dispose();
            throw;
        }
    }

    private delegate ushort ServiceAdapter(string identity, ref MachineState state);

    private void InstallImports(IReadOnlySet<string> identities, ServiceAdapter adapter)
    {
        foreach (var import in Artifact.Report.Imports.Where(i => identities.Contains(i.Import.Identity)))
        {
            _control.RegisterImport(import.RuntimeAddress, (ref MachineState state, SegmentMemory _) =>
            {
                _serviceCalls.Add(import.Import.Identity);
                return adapter(import.Import.Identity, ref state);
            });
            _installedImports.Add(import.Import.Identity);
        }
    }

    private void DisposeApplicationServices()
    {
        WindowServices?.Dispose();
        ModuleServices?.Dispose();
        _ownedMultimediaBackend?.Dispose();
        UtilityServices?.Dispose();
        GuiResources?.Dispose();
        StructureServices?.Dispose();
        GraphicsServices?.Dispose();
    }

    public void RegisterImport(string originalIdentity, Win16ProgramImport adapter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var import = Artifact.Report.Imports.SingleOrDefault(i => i.Import.Identity == originalIdentity) ?? throw new InvalidOperationException("Import identity does not belong to this source-pinned program.");
        _control.RegisterImport(import.RuntimeAddress, adapter);
    }

    /// <summary>Verification entry at NE loader CS:IP, without a synthetic CALL frame.</summary>
    public void EnterStartup(ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (state.CS != InitialState.CS || state.IP != InitialState.IP || state.SS != InitialState.SS || state.SP != InitialState.SP)
        {
            throw new ArgumentException("Startup requires the loader's original CS:IP and SS:SP.", nameof(state));
        }

        _control.Enter(ref state, new(state.CS, state.IP));
    }

    public void Invoke(ref MachineState state, NeBinaryAddress entry, CallDistance distance = CallDistance.Far, ReadOnlySpan<ushort> arguments = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _control.Invoke(ref state, new(Artifact.Selectors[entry.SegmentIndex], entry.Offset), distance, arguments);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DisposeApplicationServices();
        LifecycleServices?.Dispose();
        GuiServices?.Dispose();
        _windowsGuiBackend?.Dispose();
        InstanceServices?.Dispose();
        SystemServices?.Dispose();
        ProfileServices?.Dispose();
        FileServices?.Dispose();
        ResourceServices?.Dispose();
        KernelServices?.Dispose();
        TaskMemory.Dispose();
        _control.Dispose();
        _native.Dispose();
        _image.Dispose();
        _disposed = true;
    }
}
