using Iced.Intel;
using Translator.Binary;
using Win16_Runtime;

namespace Translator.Analysis;

/// <summary>Binary CFG and dependency discovery only; no IR lowering, emulation, or native emission.</summary>
public sealed class NeProgramAnalyzer
{
    private sealed record Entry(HashSet<string> Names, HashSet<string> Evidence);
    private readonly record struct Segments(int? Ds, int? Ax, int? Es)
    {
        public Segments Meet(Segments other) => new(Ds == other.Ds
            ? Ds
            : null, Ax == other.Ax
            ? Ax
            : null, Es == other.Es
            ? Es
            : null);
    }

    private sealed record Decoded(Instruction Instruction, NeInstructionInventory Inventory, ConstantOffsets Constants);

    public NeProgramGraph Analyze(ReadOnlySpan<byte> image, string root, NeVerifiedSymbols? symbols = null, NeDiscoveryLimits? limits = null, bool auditJumpTables = false, bool resolveIndirectTransfers = false)
    {
        byte[] original = image.ToArray();
        var executable = new NeExecutableReader().Read(original);
        if (symbols is not null && (symbols.ExecutableSha256 != executable.Sha256 || symbols.ExecutableSize != original.Length))
        {
            throw new InvalidDataException("Verified reconstruction metadata belongs to a different executable.");
        }

        limits ??= new();
        if (limits.MaximumFunctions < 1 || limits.MaximumInstructions < 1 || limits.MaximumInstructionsPerFunction < 1)
        {
            throw new ArgumentException("Discovery limits must be positive.");
        }

        var context = new Context(original, executable, limits, auditJumpTables || resolveIndirectTransfers, resolveIndirectTransfers);
        return context.Analyze(root, symbols);
    }

    /// <summary>Visits every evidenced code entry without a manually selected application root.
    /// This graph is for discovery; candidate pointers and competing decodes never authorize lowering.</summary>
    public NeProgramGraph AnalyzeWholeProgram(ReadOnlySpan<byte> image, NeVerifiedSymbols? symbols = null, NeDiscoveryLimits? limits = null)
    {
        byte[] original = image.ToArray();
        var executable = new NeExecutableReader().Read(original);
        if (symbols is not null && (symbols.ExecutableSha256 != executable.Sha256 || symbols.ExecutableSize != original.Length))
        {
            throw new InvalidDataException("Verified reconstruction metadata belongs to a different executable.");
        }

        limits ??= new();
        if (limits.MaximumFunctions < 1 || limits.MaximumInstructions < 1 || limits.MaximumInstructionsPerFunction < 1)
        {
            throw new ArgumentException("Discovery limits must be positive.");
        }

        return new Context(original, executable, limits, true, true, true).Analyze(null, symbols);
    }

    private sealed class Context(byte[] image, NeExecutable executable, NeDiscoveryLimits limits, bool auditJumpTables, bool resolveIndirectTransfers, bool wholeProgram = false)
    {
        private readonly Dictionary<NeBinaryAddress, Entry> _entries = new();
        private readonly Dictionary<NeBinaryAddress, NeRelocation> _fixups = executable.Relocations.SelectMany(r => r.SourceOffsets.Select(o => (Address: new NeBinaryAddress(r.SourceSegmentIndex, o), Record: r))).ToDictionary(p => p.Address, p => p.Record);
        private readonly Dictionary<NeBinaryAddress, NeDiscoveredFunction> _functions = new();
        private readonly Dictionary<NeBinaryAddress, int?> _inputDs = new();
        private readonly Queue<NeBinaryAddress> _pending = new();
        private readonly List<NeDiscoveryDiagnostic> _globalDiagnostics = new();
        private int _instructionCount;

        private void Entry(NeBinaryAddress address, string? name, string evidence)
        {
            if (!_entries.TryGetValue(address, out var entry))
            {
                _entries.Add(address, entry = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal)));
            }

            if (name is not null)
            {
                entry.Names.Add(name);
            }

            entry.Evidence.Add(evidence);
        }

        private bool KnownEntry(NeBinaryAddress address) => _entries.TryGetValue(address, out var entry) && entry.Evidence.Any(e => !e.StartsWith("CodePointer", StringComparison.Ordinal));

        private bool CodeAddress(NeBinaryAddress address) => address.SegmentIndex >= 1 && address.SegmentIndex <= executable.Segments.Count && !executable.Segments[address.SegmentIndex - 1].IsData && address.Offset < executable.Segments[address.SegmentIndex - 1].FileLength;

        private void Schedule(NeBinaryAddress address, int? ds)
        {
            if (!CodeAddress(address))
            {
                return;
            }

            if (_inputDs.TryGetValue(address, out var old))
            {
                if (old != ds && old is not null)
                {
                    _inputDs[address] = null;
                    _pending.Enqueue(address);
                }

                return;
            }

            if (_inputDs.Count >= limits.MaximumFunctions)
            {
                _globalDiagnostics.Add(new("FunctionLimit", address, "Function discovery limit reached; closure is incomplete."));
                return;
            }

            _inputDs.Add(address, ds);
            _pending.Enqueue(address);
        }

        public NeProgramGraph Analyze(string? root, NeVerifiedSymbols? symbols)
        {
            foreach (var entry in executable.EntryPoints.Where(e => e.SegmentIndex is not null && e.Offset is not null))
            {
                var address = new NeBinaryAddress(entry.SegmentIndex!.Value, entry.Offset!.Value);
                if (!CodeAddress(address))
                {
                    if (wholeProgram)
                    {
                        _globalDiagnostics.Add(new("NonExecutableEntry", address, $"NE ordinal {entry.Ordinal} is not file-backed code; preserved as metadata, not decoded."));
                    }

                    continue;
                }

                Entry(address, null, $"NEEntryOrdinal:{entry.Ordinal}");
                foreach (var name in executable.ResidentNames.Concat(executable.NonResidentNames).Where(n => n.Ordinal == entry.Ordinal))
                {
                    Entry(address, name.Name, "NEExportName");
                }
            }

            if (executable.NeHeader.InitialCs != 0)
            {
                Entry(new(executable.NeHeader.InitialCs, executable.NeHeader.InitialIp), null, "NEInitialCsIp");
            }

            if (symbols is not null)
            {
                foreach (var symbol in symbols.Symbols)
                {
                    Entry(symbol.Address, symbol.Name, "ExternalDebugSymbol");
                }
            }

            foreach (var relocation in executable.Relocations.Where(r => r.TargetSegmentIndex is not null && r.TargetOffset is not null && r.RawAddressType is 3 or 5))
            {
                var candidate = new NeBinaryAddress(relocation.TargetSegmentIndex!.Value, relocation.TargetOffset!.Value);
                if (CodeAddress(candidate))
                {
                    if (wholeProgram && (!relocation.Supported || relocation.Additive))
                    {
                        _globalDiagnostics.Add(new("UnprovenRelocationCodePointer", candidate, $"Relocation at file 0x{relocation.SourceFileOffset:X} is unsupported or additive; raw target is not an approved entry."));
                        continue;
                    }

                    Entry(candidate, null, wholeProgram
                        ? $"CodePointerCandidate:RelocationFile:{relocation.SourceFileOffset:X}:SourceSegment:{relocation.SourceSegmentIndex}"
                        : "CodePointerCandidate");
                }
            }

            NeBinaryAddress selected;
            if (wholeProgram)
            {
                var startup = new NeBinaryAddress(Math.Max(1, (int)executable.NeHeader.InitialCs), executable.NeHeader.InitialIp);
                if (_entries.Count == 0)
                {
                    throw new InvalidDataException("No evidenced file-backed executable entries were found.");
                }

                selected = CodeAddress(startup) && executable.NeHeader.InitialCs != 0
                    ? startup
                    : _entries.Keys.Order().First();
                foreach (var address in _entries.Keys.Order().ToArray())
                {
                    Schedule(address, address == startup && executable.NeHeader.InitialCs != 0 && executable.NeHeader.AutoDataSegment != 0
                        ? executable.NeHeader.AutoDataSegment
                        : null);
                }
            }
            else
            {
                if (root!.Contains(':'))
                {
                    selected = NeBinaryAddress.Parse(root);
                }
                else
                {
                    var matches = _entries.Where(e => e.Value.Names.Contains(root)).Select(e => e.Key).ToArray();
                    if (matches.Length != 1)
                    {
                        throw new InvalidDataException($"Root '{root}' has {matches.Length} verified matches; supply a verified symbol profile or a binary segment:offset.");
                    }

                    selected = matches[0];
                }

                if (!CodeAddress(selected))
                {
                    throw new InvalidDataException($"Root {selected} is not file-backed executable code.");
                }

                Entry(selected, null, "ExplicitRoot");
                Schedule(selected, executable.NeHeader.AutoDataSegment == 0
                    ? null
                    : executable.NeHeader.AutoDataSegment);
            }

            while (true)
            {
                while (_pending.TryDequeue(out var address))
                {
                    _functions[address] = Function(address, _inputDs[address]);
                }

                if (!wholeProgram)
                {
                    break;
                }

                foreach (var callback in NeRegisterApiCallbacks.Find(_functions.Values.ToArray(), executable))
                {
                    Entry(callback.Target, null, $"ApiCallback:DOSSetVector:{callback.Site.Identity}");
                    Schedule(callback.Target, null);
                }

                if (_pending.Count == 0)
                {
                    break;
                }
            }

            var ordered = _functions.Values.OrderBy(f => f.Address).ToArray();
            if (wholeProgram)
            {
                // A local guard proof must also hold against every independently evidenced
                // entry and incoming branch in the full segment, including other regions.
                var inventory = ordered.SelectMany(f => f.Instructions).DistinctBy(i => i.Address).GroupBy(i => i.Address.SegmentIndex).ToDictionary(g => g.Key, g => g.OrderBy(i => i.Address).ToArray());
                var independentEntries = _entries.Keys.ToHashSet();
                var invalidTables = ordered.SelectMany(f => f.Dependencies).Where(d => d.Kind is "JumpTableTarget" or "IndexedNearCallTarget").Select(d => d.Site).Distinct().Select(site => NeJumpTableAnalyzer.Analyze(image, executable, inventory[site.SegmentIndex], site, independentEntries)).Where(table => !table.Resolved).ToDictionary(table => table.Site);
                ordered = ordered.Select(f => f with
                {
                    Dependencies = f.Dependencies.Select(d => d.Kind is "JumpTableTarget" or "IndexedNearCallTarget" && invalidTables.TryGetValue(d.Site, out var table)
                    ? new NeDependency(d.Function, d.Site, d.Kind == "IndexedNearCallTarget"
                        ? "IndirectCall"
                        : "IndirectBranch", null, null, "UnresolvedDynamicTarget", false, table.Diagnostic)
                    : d).Distinct().ToArray()
                }).ToArray();
            }

            var owners = new Dictionary<NeBinaryAddress, (NeBinaryAddress Function, NeBinaryAddress Instruction)>();
            foreach (var function in ordered)
            {
                foreach (var instruction in function.Instructions)
                {
                    foreach (int offset in Enumerable.Range(instruction.Address.Offset, instruction.Size))
                    {
                        var address = new NeBinaryAddress(instruction.Address.SegmentIndex, (ushort)offset);
                        if (owners.TryGetValue(address, out var owner) && owner.Function != function.Address)
                        {
                            string kind = owner.Instruction == instruction.Address
                                ? "SharedFunctionBlock"
                                : "OverlappingFunctionInstructions";
                            var diagnostic = new NeDiscoveryDiagnostic(kind, instruction.Address, "Reachable functions share instruction bytes; ownership remains explicit and unresolved.");
                            if (!((resolveIndirectTransfers || wholeProgram) && kind == "SharedFunctionBlock") && !_globalDiagnostics.Contains(diagnostic))
                            {
                                _globalDiagnostics.Add(diagnostic);
                            }
                        }
                        else
                        {
                            owners.TryAdd(address, (function.Address, instruction.Address));
                        }
                    }
                }
            }

            var dependencies = ordered.SelectMany(f => f.Dependencies).ToArray();
            var diagnostics = _globalDiagnostics.Concat(ordered.SelectMany(f => f.Diagnostics)).Concat(dependencies.Where(d => !d.Resolved).Select(d => new NeDiscoveryDiagnostic("UnresolvedDependency", d.Site, $"{d.Kind}: {d.Detail}"))).OrderBy(d => d.Address).ThenBy(d => d.Code, StringComparer.Ordinal).ToArray();
            return new(executable.Sha256, executable.FileSize, selected, symbols?.Verification, _entries.OrderBy(e => e.Key).Select(e => new NeFunctionEntry(e.Key, e.Value.Names.Order(StringComparer.Ordinal).ToArray(), e.Value.Evidence.Order(StringComparer.Ordinal).ToArray())).ToArray(), ordered, dependencies, !wholeProgram && diagnostics.Length == 0 && ordered.All(f => f.BoundaryStatus == "ClosedControlFlow") && dependencies.All(d => d.Resolved), diagnostics)
            {
                DiscoveryMode = wholeProgram
                    ? "WholeProgram"
                    : "Selective"
            };
        }

        private NeDiscoveredFunction Function(NeBinaryAddress root, int? initialDs)
        {
            if (_functions.TryGetValue(root, out var previous))
            {
                _instructionCount -= previous.Instructions.Count;
            }

            var segment = executable.Segments[root.SegmentIndex - 1];
            // The NE loader zero-fills the allocation tail. An instruction may
            // straddle the last file byte (CHIPS COMPLETEMSGPROC ends in CA 0A
            // followed by the allocated zero high byte). New instruction entries
            // still require file-backed bytes; do not discover padding as code.
            var code = new byte[Math.Min(segment.MemoryLength, segment.FileLength + 14)];
            image.AsSpan(segment.FileOffset, segment.FileLength).CopyTo(code);
            var decoded = new SortedDictionary<int, Decoded>();
            var states = new Dictionary<int, Segments>();
            var occupied = new Dictionary<int, int>();
            var queue = new Queue<int>();
            var diagnostics = new List<NeDiscoveryDiagnostic>();
            var edges = new List<NeDependency>();
            var infoFactory = new InstructionInfoFactory();
            var formatter = new NasmFormatter();
            int steps = 0;

            NeBinaryAddress At(int offset) => new(root.SegmentIndex, unchecked((ushort)offset));


            void Error(int offset, string code, string detail)
            {
                var d = new NeDiscoveryDiagnostic(code, At(offset), detail);
                if (!diagnostics.Contains(d))
                {
                    diagnostics.Add(d);
                }
            }


            bool HasFixup(int offset, int length) => Enumerable.Range(offset, length).Any(p => _fixups.ContainsKey(At(p)));


            void Next(int offset, Segments state, bool fallthrough = false)
            {
                if (offset < 0 || offset >= segment.FileLength)
                {
                    Error(offset, "UnboundedControlFlow", "Control flow leaves initialized code bytes.");
                    return;
                }

                if (fallthrough && At(offset) != root && _entries.TryGetValue(At(offset), out var e) && e.Evidence.Any(v => !v.StartsWith("CodePointer", StringComparison.Ordinal)))
                {
                    if (wholeProgram)
                    {
                        edges.Add(new(root, At(offset), "SharedFallthrough", At(offset), null, "SharedCodeEntry", true, "Fallthrough continues at another evidenced entry; no prologue or unique function ownership assumed."));
                        Schedule(At(offset), state.Ds);
                    }
                    else
                    {
                        Error(offset, "SharedFallthrough", "Fallthrough reaches another evidenced entry; shared-code ownership is unresolved.");
                        return;
                    }
                }

                if (!states.TryGetValue(offset, out var old))
                {
                    states.Add(offset, state);
                    queue.Enqueue(offset);
                }
                else
                {
                    var joined = old.Meet(state);
                    if (joined != old)
                    {
                        states[offset] = joined;
                        queue.Enqueue(offset);
                    }
                }
            }


            Next(root.Offset, new(initialDs, null, null));
            while (queue.TryDequeue(out int offset))
            {
                if (++steps > limits.MaximumInstructionsPerFunction * 20L)
                {
                    Error(offset, "AnalysisLimit", "Control-flow state limit reached.");
                    break;
                }

                if (!decoded.TryGetValue(offset, out var current))
                {
                    if (decoded.Count >= limits.MaximumInstructionsPerFunction || _instructionCount >= limits.MaximumInstructions)
                    {
                        Error(offset, "InstructionLimit", "Instruction discovery limit reached.");
                        break;
                    }

                    var decoder = Decoder.Create(16, new ByteArrayCodeReader(code, offset, code.Length - offset));
                    decoder.IP = (uint)offset;
                    var instruction = decoder.Decode();
                    if (instruction.IsInvalid || instruction.Length == 0 || instruction.Length > code.Length - offset)
                    {
                        Error(offset, "InvalidInstruction", "Invalid or truncated 16-bit instruction.");
                        continue;
                    }

                    if (Enumerable.Range(offset, instruction.Length).Any(b => occupied.TryGetValue(b, out int owner) && owner != offset))
                    {
                        Error(offset, "OverlappingInstructions", "A branch enters the middle of an instruction; competing original-byte interpretation is ambiguous.");
                        if (!wholeProgram)
                        {
                            continue;
                        }
                    }

                    foreach (int b in Enumerable.Range(offset, instruction.Length))
                    {
                        occupied.TryAdd(b, offset);
                    }

                    var output = new StringOutput();
                    formatter.Format(instruction, output);
                    current = new(instruction, new(At(offset), instruction.Length, Convert.ToHexString(code.AsSpan(offset, instruction.Length)), instruction.Mnemonic.ToString(), instruction.FlowControl.ToString(), output.ToString()), decoder.GetConstantOffsets(instruction));
                    decoded.Add(offset, current);
                    _instructionCount++;
                }

                var ins = current.Instruction;
                var state = Transfer(ins, current.Constants, offset, root.SegmentIndex, states[offset], infoFactory);
                int next = offset + ins.Length;
                switch (ins.FlowControl)
                {
                    case FlowControl.Next:
                        Next(next, state, true);
                        break;
                    case FlowControl.Call:
                    case FlowControl.IndirectCall:
                        Next(next, state, true);
                        break;
                    case FlowControl.ConditionalBranch:
                        if (HasFixup(offset, ins.Length))
                        {
                            Error(offset, "RelocatedBranch", "A relocation overlaps a relative branch; raw displacement is not a valid resolved target.");
                        }
                        else if (ins.Op0Kind == OpKind.NearBranch16)
                        {
                            Next(ins.NearBranch16, state);
                        }
                        else
                        {
                            Error(offset, "UnsupportedTransfer", "Only 16-bit conditional branch targets are supported.");
                        }

                        Next(next, state, true);
                        break;
                    case FlowControl.UnconditionalBranch:
                        if (ins.Op0Kind == OpKind.NearBranch16)
                        {
                            if (HasFixup(offset, ins.Length))
                            {
                                Error(offset, "RelocatedBranch", "Relative branch overlaps a relocation.");
                                break;
                            }

                            var target = At(ins.NearBranch16);
                            if (target == root || !KnownEntry(target))
                            {
                                Next(ins.NearBranch16, state);
                            }
                        }

                        break;
                    case FlowControl.Return:
                        break;
                    case FlowControl.IndirectBranch:
                        if (auditJumpTables)
                        {
                            var table = NeJumpTableAnalyzer.Analyze(image, executable, decoded.Values.Select(d => d.Inventory).ToArray(), At(offset), wholeProgram
                                ? _entries.Keys.ToHashSet()
                                : null);
                            if (table.Resolved)
                            {
                                foreach (var target in table.Entries.Select(e => e.Target).Distinct())
                                {
                                    Next(target.Offset, state);
                                }
                            }
                        }

                        break;
                    case FlowControl.Interrupt:
                        Error(offset, "UnresolvedInterrupt", "Interrupt service/control effects are not modeled.");
                        Next(next, state, true);
                        break;
                    default:
                        Error(offset, "UnsupportedTransfer", $"Unsupported control effect {ins.FlowControl}.");
                        break;
                }
            }

            void Internal(int site, string kind, NeBinaryAddress target, int? ds)
            {
                bool valid = CodeAddress(target);
                edges.Add(new(root, At(site), kind, target, null, valid
                    ? "InternalFunctionPendingTranslation"
                    : "InvalidCodeTarget", valid, valid
                    ? "Original binary target; native instruction support is validated in the translation milestone"
                    : "Target is outside executable code"));
                if (valid)
                {
                    Entry(target, null, kind);
                    Schedule(target, ds);
                }
            }

            void Import(int site, string kind, NeImportReference import)
            {
                var resolved = NeImportResolver.Classify(import);
                bool supported = resolved.Support == NeImportSupport.SupportedRuntimeService;
                edges.Add(new(root, At(site), kind, null, import, resolved.Support.ToString(), supported, resolved.ResolvedIdentity is null
                    ? resolved.Diagnostic
                    : $"{resolved.ResolvedIdentity}: {resolved.Diagnostic}; host availability is a runtime concern"));
            }

            void Unknown(int site, string kind, string detail) => edges.Add(new(root, At(site), kind, null, null, "UnresolvedDynamicTarget", false, detail));


            void FixedTarget(int site, string kind, NeRelocation record, ushort? literalOffset, int? ds)
            {
                if (!record.Supported || record.Additive)
                {
                    Unknown(site, kind, "Unsupported or additive procedure-target evidence");
                    return;
                }

                if (record.Import is not null)
                {
                    Import(site, kind, record.Import);
                    return;
                }

                if (record.TargetSegmentIndex is int index)
                {
                    var address = new NeBinaryAddress(index, literalOffset ?? record.TargetOffset!.Value);
                    if (kind == "CallbackOrProcedureReference" && executable.Segments[index - 1].IsData)
                    {
                        edges.Add(new(root, At(site), "DataPointerReference", address, null, "ExternalData", true, "Internal far data pointer established by NE relocation"));
                    }
                    else
                    {
                        Internal(site, kind, address, ds);
                    }
                }
                else
                {
                    Unknown(site, kind, "Relocation does not establish a procedure target");
                }
            }


            foreach (var (offset, item) in decoded)
            {
                var ins = item.Instruction;
                var state = states[offset];
                bool call = ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall;
                bool jump = ins.FlowControl is FlowControl.UnconditionalBranch or FlowControl.IndirectBranch;
                if (call || jump)
                {
                    if (ins.Op0Kind == OpKind.NearBranch16)
                    {
                        if (HasFixup(offset, ins.Length))
                        {
                            Unknown(offset, "RelocatedNearTransfer", "Relative transfer overlaps an NE relocation; raw displacement is not used as a resolved target");
                        }
                        else if (call)
                        {
                            Internal(offset, "DirectNearCall", At(ins.NearBranch16), state.Ds);
                        }
                        else if (At(ins.NearBranch16) != root && KnownEntry(At(ins.NearBranch16)))
                        {
                            Internal(offset, "TailTransfer", At(ins.NearBranch16), state.Ds);
                        }
                    }
                    else if (ins.Op0Kind == OpKind.FarBranch16)
                    {
                        int pointer = offset + ins.Length - 4;
                        if (_fixups.TryGetValue(At(pointer), out var far) && far.RawAddressType == 3)
                        {
                            FixedTarget(offset, call
                                ? "DirectFarCall"
                                : "FarTailTransfer", far, null, state.Ds);
                        }
                        else if (_fixups.TryGetValue(At(pointer + 2), out var selector) && selector.RawAddressType == 2 && selector.Import is null)
                        {
                            FixedTarget(offset, call
                                ? "DirectFarCall"
                                : "FarTailTransfer", selector, ins.FarBranch16, state.Ds);
                        }
                        else
                        {
                            Unknown(offset, "UnresolvedFarTransfer", "Far selector has no validated internal/import relocation; raw segment values are not segment indices");
                        }
                    }
                    else if (ins.Code is Code.Call_m1616 or Code.Jmp_m1616 or Code.Call_rm16 or Code.Jmp_rm16 && ins.Op0Kind == OpKind.Memory && ins.MemoryBase == Register.None && ins.MemoryIndex == Register.None && ins.MemoryDisplacement64 <= ushort.MaxValue)
                    {
                        int? dataSegment = Segment(ins.MemorySegment, state, root.SegmentIndex);
                        var pointer = dataSegment is int data
                            ? new NeBinaryAddress(data, (ushort)ins.MemoryDisplacement64)
                            : (NeBinaryAddress?)null;
                        bool farPointer = ins.Code is Code.Call_m1616 or Code.Jmp_m1616;
                        if (pointer is not null && _fixups.TryGetValue(pointer.Value, out var target) && (farPointer && target.RawAddressType == 3 || !farPointer && target.RawAddressType == 5 && target.Import is null && target.TargetSegmentIndex == root.SegmentIndex))
                        {
                            FixedTarget(offset, call
                                ? farPointer
                                    ? "IndirectFarCall"
                                    : "IndirectNearCall"
                                : "IndirectTailTransfer", target, null, state.Ds);
                        }
                        else
                        {
                            Unknown(offset, call
                                ? "IndirectCall"
                                : "IndirectBranch", "Absolute pointer has no validated relocation target of the required near/far width");
                        }
                    }
                    else if (resolveIndirectTransfers && ins.FlowControl is FlowControl.IndirectBranch or FlowControl.IndirectCall)
                    {
                        var table = NeJumpTableAnalyzer.Analyze(image, executable, decoded.Values.Select(d => d.Inventory).ToArray(), At(offset), wholeProgram
                            ? _entries.Keys.ToHashSet()
                            : null);
                        if (table.Resolved && (call || table.Entries.All(e => decoded.ContainsKey(e.Target.Offset))))
                        {
                            foreach (var target in table.Entries.Select(e => e.Target).Distinct())
                            {
                                if (call)
                                {
                                    Internal(offset, "IndexedNearCallTarget", target, state.Ds);
                                }
                                else
                                {
                                    edges.Add(new(root, At(offset), "JumpTableTarget", target, null, "VerifiedLocalInstruction", true, table.IndexComputation));
                                }
                            }
                        }
                        else
                        {
                            Unknown(offset, "IndirectBranch", table.Diagnostic);
                        }
                    }
                    else
                    {
                        Unknown(offset, call
                            ? "IndirectCall"
                            : "IndirectBranch", "Register/indexed target set is unresolved; no target is guessed");
                    }
                }

                var info = infoFactory.GetInfo(ins);
                foreach (var memory in info.GetUsedMemory())
                {
                    int? data = Segment(memory.Segment, state, root.SegmentIndex);
                    var address = data is int index && memory.Base == Register.None && memory.Index == Register.None && memory.Displacement <= ushort.MaxValue
                        ? new NeBinaryAddress(index, (ushort)memory.Displacement)
                        : (NeBinaryAddress?)null;
                    bool validData = true;
                    if (address is not null)
                    {
                        var metadata = executable.Segments[address.Value.SegmentIndex - 1];
                        int size = metadata.MemoryLength;
                        if (metadata.Index == executable.NeHeader.InitialSs)
                        {
                            size = Math.Max(size + executable.NeHeader.StackSize, executable.NeHeader.InitialSp);
                        }

                        if (metadata.Index == executable.NeHeader.AutoDataSegment)
                        {
                            size += executable.NeHeader.HeapSize;
                        }

                        bool write = memory.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite;
                        validData = address.Value.Offset + memory.MemorySize.GetSize() <= size && (write
                            ? metadata.IsWritable
                            : metadata.IsReadable);
                    }

                    edges.Add(new(root, At(offset), "DataReference", address, null, validData
                        ? "ExternalData"
                        : "InvalidDataReference", validData, !validData
                        ? "Fixed data access exceeds declared segment storage or permissions"
                        : data is not null
                            ? $"NE segment {data}; indexed offsets require runtime bounds checks"
                            : $"Runtime input memory via {memory.Segment}; caller must supply valid selector mappings (including stack/far-pointer inputs)"));
                    if (!call && !jump && address is not null && _fixups.TryGetValue(address.Value, out var reference) && reference.RawAddressType == 3)
                    {
                        FixedTarget(offset, "CallbackOrProcedureReference", reference, null, state.Ds);
                    }
                }

                // Win16 WNDCLASS initializers store offset and relocated selector in
                // adjacent MOVs. Require identical addressing and contiguous word slots.
                if (ins.Code == Code.Mov_rm16_imm16 && ins.Op0Kind == OpKind.Memory && ins.MemoryBase == Register.BP && ins.MemoryIndex == Register.None && _fixups.TryGetValue(At(offset + item.Constants.ImmediateOffset), out var split) && split.Supported && !split.Additive && split.RawAddressType == 2 && split.Import is null && split.TargetSegmentIndex is int callbackSegment)
                {
                    var offsetStore = decoded.Values.LastOrDefault(d => d.Inventory.Address.Offset + d.Instruction.Length == offset);
                    if (offsetStore is not null && offsetStore.Instruction.Code == Code.Mov_rm16_imm16 && offsetStore.Instruction.Op0Kind == OpKind.Memory && offsetStore.Instruction.MemoryBase == Register.BP && offsetStore.Instruction.MemoryIndex == Register.None && offsetStore.Instruction.MemorySegment == ins.MemorySegment && unchecked((ushort)(offsetStore.Instruction.MemoryDisplacement64 + 2)) == unchecked((ushort)ins.MemoryDisplacement64) && !HasFixup(offsetStore.Inventory.Address.Offset, offsetStore.Instruction.Length) && CodeAddress(new(callbackSegment, offsetStore.Instruction.Immediate16)))
                    {
                        Internal(offset, "CallbackOrProcedureReference", new(callbackSegment, offsetStore.Instruction.Immediate16), state.Ds);
                    }
                }

                if (!call && !jump)
                {
                    foreach (var reference in executable.Relocations.Where(r => r.SourceSegmentIndex == root.SegmentIndex && r.RawAddressType == 3 && r.SourceOffsets.Any(s => s >= offset && s < offset + ins.Length)))
                    {
                        FixedTarget(offset, "CallbackOrProcedureReference", reference, null, state.Ds);
                    }
                }
            }

            string[] aliases = _entries[root].Names.Order(StringComparer.Ordinal).ToArray();
            return new(root, aliases.FirstOrDefault() ?? $"sub_{root.SegmentIndex:X4}_{root.Offset:X4}", aliases, diagnostics.Count == 0 && edges.All(e => e.Resolved)
                ? "ClosedControlFlow"
                : "Incomplete", "NotValidated", decoded.Values.Select(d => d.Inventory).ToArray(), edges.Distinct().OrderBy(e => e.Site).ThenBy(e => e.Kind, StringComparer.Ordinal).ToArray(), diagnostics.ToArray());
        }

        private Segments Transfer(Instruction ins, ConstantOffsets constants, int offset, int segmentIndex, Segments state, InstructionInfoFactory factory)
        {
            var used = factory.GetInfo(ins).GetUsedRegisters().ToArray();

            bool Writes(Register r) => used.Any(u => u.Register == r && u.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite);

            int? ax = state.Ax, ds = state.Ds, es = state.Es;
            if (Writes(Register.AX) || Writes(Register.EAX) || Writes(Register.AL) || Writes(Register.AH))
            {
                ax = null;
            }

            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && ins.Op0Register == Register.AX && ins.Op1Kind == OpKind.Register)
            {
                ax = Segment(ins.Op1Register, state, segmentIndex);
            }

            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Register == Register.AX && constants.ImmediateSize == 2 && _fixups.TryGetValue(new(segmentIndex, (ushort)(offset + constants.ImmediateOffset)), out var selector) && selector.Supported && !selector.Additive && selector.RawAddressType == 2 && selector.Import is null)
            {
                ax = selector.TargetSegmentIndex;
            }

            if (Writes(Register.DS))
            {
                ds = ins.Mnemonic == Mnemonic.Mov && ins.Op1Kind == OpKind.Register && ins.Op1Register == Register.AX
                    ? state.Ax
                    : null;
            }

            if (Writes(Register.ES))
            {
                es = ins.Mnemonic == Mnemonic.Mov && ins.Op1Kind == OpKind.Register
                    ? Segment(ins.Op1Register, state, segmentIndex)
                    : null;
            }

            if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
            {
                ax = null;
                es = null;
            }

            return new(ds, ax, es);
        }

        private static int? Segment(Register register, Segments state, int cs) => register switch
        {
            Register.DS => state.Ds,
            Register.ES => state.Es,
            Register.AX => state.Ax,
            Register.CS when cs != 0 => cs,
            _ => null
        };
    }
}
