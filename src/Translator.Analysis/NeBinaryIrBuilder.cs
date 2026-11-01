using Iced.Intel;
using Translator.Binary;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Analysis;

/// <summary>Decodes authoritative original bytes into the existing typed IR. No assembly parser is involved.</summary>
public sealed class NeBinaryIrBuilder
{
    public static string SelectorSymbol(int index) => $"__ne_selector_{index:X4}";

    public static string NativeSymbol(NeBinaryAddress address) => $"ne_{address.SegmentIndex:X4}_{address.Offset:X4}";
    private static readonly HashSet<string> _strings = new(StringComparer.Ordinal)
    {
        "MOVSB",
        "MOVSW",
        "LODSB",
        "LODSW",
        "STOSB",
        "SCASB",
        "CMPSB",
        "CMPSW"
    };

    public IrModule Build(ReadOnlySpan<byte> image, NeProgramGraph graph) => BuildCore(image, graph, null);

    // Diagnostic-only lowering: does not certify a closure or produce executable code.
    public IrModule AuditFunction(ReadOnlySpan<byte> image, NeProgramGraph graph, NeBinaryAddress function) => BuildCore(image, graph, function);

    /// <summary>Diagnostic form lowering only. Does not establish function boundaries or authorize execution.</summary>
    public IrInstruction AuditInstruction(ReadOnlySpan<byte> image, NeProgramGraph graph, NeBinaryAddress region, NeBinaryAddress address)
    {
        var function = graph.Functions.SingleOrDefault(f => f.Address == region) ?? throw new ArgumentException("Unknown discovery region.", nameof(region));
        if (!function.Instructions.Any(i => i.Address == address))
        {
            throw new ArgumentException("Instruction is absent from discovery region.", nameof(address));
        }

        return BuildCore(image, graph, region, auditInstruction: address).Functions.Single().Instructions.Single(i => i.Operation != "LABEL");
    }

    public IrModule BuildWithContracts(ReadOnlySpan<byte> image, NeProgramGraph graph, IReadOnlyDictionary<string, Win16ApiMetadata> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        if (graph.Diagnostics.Any(d => d.Code != "UnresolvedDependency") || graph.Dependencies.Any(d => !d.Resolved && (d.Import is null || !contracts.ContainsKey(d.Import.Identity))))
        {
            throw new InvalidDataException("Explicit native contracts cannot authorize unknown targets or control-flow diagnostics.");
        }

        return BuildCore(image, graph, null, contracts);
    }

    // Explicit fatal boundaries never supply a return value or guessed API signature.
    public IrModule BuildBounded(ReadOnlySpan<byte> image, NeProgramGraph graph, IReadOnlyDictionary<string, Win16ApiMetadata> contracts, IReadOnlySet<string> rejectedImports, IReadOnlySet<NeBinaryAddress> rejectedFunctions, IReadOnlySet<NeBinaryAddress>? rejectedIndirectSites = null)
    {
        if (rejectedIndirectSites is not null && rejectedIndirectSites.Any(site => !graph.Functions.SelectMany(f => f.Instructions).Any(i => i.Address == site && i.FlowControl == "IndirectCall")))
        {
            throw new InvalidDataException("A fatal indirect boundary must identify an evidenced selected indirect call.");
        }

        if (graph.Diagnostics.Any(d => d.Code != "UnresolvedDependency") || graph.Dependencies.Any(d => !d.Resolved && !(d.Import is { } import && (contracts.ContainsKey(import.Identity) || rejectedImports.Contains(import.Identity))) && !(d.Kind == "IndirectCall" && rejectedIndirectSites?.Contains(d.Site) == true)))
        {
            throw new InvalidDataException("Fatal boundaries cannot authorize unknown control flow.");
        }

        return BuildCore(image, graph, null, contracts, rejectedImports, rejectedFunctions, rejectedIndirectSites);
    }

    private IrModule BuildCore(ReadOnlySpan<byte> image, NeProgramGraph graph, NeBinaryAddress? auditFunction, IReadOnlyDictionary<string, Win16ApiMetadata>? contracts = null, IReadOnlySet<string>? rejectedImports = null, IReadOnlySet<NeBinaryAddress>? rejectedFunctions = null, IReadOnlySet<NeBinaryAddress>? rejectedIndirectSites = null, NeBinaryAddress? auditInstruction = null)
    {
        if (graph.DiscoveryMode == "WholeProgram" && auditFunction is null)
        {
            throw new InvalidDataException("Whole-program discovery includes candidate and ambiguous code; executable lowering requires the later whole-program validation milestones.");
        }

        byte[] bytes = image.ToArray();
        var executable = new NeExecutableReader().Read(bytes);
        if (graph.ExecutableSha256 != executable.Sha256 || graph.ExecutableSize != bytes.Length)
        {
            throw new InvalidDataException("Binary IR graph/executable identity mismatch.");
        }

        if (auditFunction is null && contracts is null && !graph.ClosureComplete)
        {
            throw new InvalidDataException("Binary IR requires a complete dependency closure; inspect the graph's unresolved dependencies.");
        }

        var functions = new List<IrFunction>();
        foreach (var function in graph.Functions.Where(f => auditFunction is null || f.Address == auditFunction))
        {
            var code = new List<IrInstruction>();
            var returns = new List<(CallDistance Distance, ushort Cleanup)>();
            var segment = executable.Segments[function.Address.SegmentIndex - 1];
            var loadedBytes = new byte[segment.MemoryLength];
            bytes.AsSpan(segment.FileOffset, segment.FileLength).CopyTo(loadedBytes);
            foreach (var original in function.Instructions.Where(i => auditInstruction is null || i.Address == auditInstruction))
            {
                int offset = original.Address.Offset;
                if (original.Address.SegmentIndex != segment.Index || offset >= segment.FileLength || offset > loadedBytes.Length - original.Size || Convert.ToHexString(loadedBytes.AsSpan(offset, original.Size)) != original.BytesHex)
                {
                    throw new InvalidDataException($"Binary instruction inventory mismatch at {original.Address}.");
                }

                var decoder = Decoder.Create(16, new ByteArrayCodeReader(loadedBytes, offset, original.Size));
                decoder.IP = (uint)offset;
                var ins = decoder.Decode();
                if (ins.IsInvalid || ins.Length != original.Size)
                {
                    throw new InvalidDataException($"Binary decoding mismatch at {original.Address}.");
                }

                var constants = decoder.GetConstantOffsets(ins);
                var source = new SourceLocation($"NE:{original.Address}", 0);

                NotSupportedException Fail(string reason) => new($"Binary IR at {original.Address}, {ins.Code}: {reason}. Original bytes {original.BytesHex}.");

                var raw = Convert.FromHexString(original.BytesHex);
                int prefixCount = 0;
                while (prefixCount < raw.Length && raw[prefixCount] is 0x26 or 0x2e or 0x36 or 0x3e or 0x64 or 0x65 or 0x66 or 0x67 or 0xf0 or 0xf2 or 0xf3)
                {
                    prefixCount++;
                }

                if (ins.HasLockPrefix || raw.AsSpan(0, prefixCount).Contains((byte)0x67))
                {
                    throw Fail("LOCK and 32-bit address forms are unsupported");
                }

                string operation = ins.Mnemonic.ToString().ToUpperInvariant();
                var operands = new List<IrOperand>();
                var distance = CallDistance.Unspecified;
                ushort? cleanup = null;
                IrIndirectTransfer? transfer = null;
                var condition = Condition(operation);
                RepeatPrefix prefix = ins.HasRepnePrefix
                    ? RepeatPrefix.Repne
                    : ins.HasRepPrefix
                        ? operation is "SCASB" or "CMPSB" or "CMPSW"
                            ? RepeatPrefix.Repe
                            : RepeatPrefix.Rep
                        : RepeatPrefix.None;
                code.Add(new("LABEL", new[] { new IrOperand("Label", Label(offset)) }, source, Label(offset) + ":", (uint)offset, 0));
                var fixes = executable.Relocations.Where(r => r.SourceSegmentIndex == segment.Index && r.SourceOffsets.Any(o => o >= offset && o < offset + ins.Length)).ToArray();
                bool control = ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch;
                if (rejectedIndirectSites?.Contains(original.Address) == true)
                {
                    if (ins.FlowControl != FlowControl.IndirectCall)
                    {
                        throw Fail("fatal indirect boundary does not identify an indirect call");
                    }

                    operation = "FAULT";
                }
                else if (ins.FlowControl == FlowControl.Return)
                {
                    distance = ins.Code switch
                    {
                        Code.Retnw or Code.Retnw_imm16 => CallDistance.Near,
                        Code.Retfw or Code.Retfw_imm16 => CallDistance.Far,
                        _ => throw Fail("return form is unsupported")
                    };
                    operation = distance == CallDistance.Far
                        ? "RETF"
                        : "RET";
                    cleanup = ins.OpCount == 0
                        ? (ushort)0
                        : ins.Immediate16;
                    returns.Add((distance, cleanup.Value));
                }
                else if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                {
                    operation = "CALL";
                    distance = ins.Op0Kind == OpKind.FarBranch16 || ins.Code == Code.Call_m1616
                        ? CallDistance.Far
                        : CallDistance.Near;
                    if (ins.Op0Kind is OpKind.NearBranch16 or OpKind.FarBranch16)
                    {
                        var dependency = function.Dependencies.SingleOrDefault(d => d.Site == original.Address && d.Kind is "DirectNearCall" or "DirectFarCall") ?? throw Fail("direct call has no unique discovered target");
                        NeBinaryAddress? actualTarget = null;
                        NeImportReference? actualImport = null;
                        if (ins.Op0Kind == OpKind.NearBranch16)
                        {
                            actualTarget = new(segment.Index, ins.NearBranch16);
                        }
                        else
                        {
                            int pointer = offset + ins.Length - 4;
                            var relocation = fixes.SingleOrDefault(r => r.RawAddressType == 3 && r.SourceOffsets.Contains((ushort)pointer)) ?? fixes.SingleOrDefault(r => r.RawAddressType == 2 && r.SourceOffsets.Contains((ushort)(pointer + 2)));
                            if (relocation is null || !relocation.Supported || relocation.Additive)
                            {
                                throw Fail("direct far target has no supported relocation evidence");
                            }

                            actualImport = relocation.Import;
                            if (relocation.TargetSegmentIndex is int targetSegment)
                            {
                                actualTarget = new(targetSegment, relocation.RawAddressType == 2
                                    ? ins.FarBranch16
                                    : relocation.TargetOffset!.Value);
                            }
                        }

                        if (dependency.Target != actualTarget || dependency.Import != actualImport)
                        {
                            throw new InvalidDataException($"Binary call graph target mismatch at {original.Address}.");
                        }

                        if (dependency.Target is { } target)
                        {
                            operands.Add(new("Symbol", graph.Functions.SingleOrDefault(f => f.Address == target)?.Name ?? (rejectedFunctions?.Contains(target) == true
                                ? NativeSymbol(target)
                                : throw Fail("internal target has no executable binding or fatal boundary"))));
                        }
                        else if (dependency.Import is { } import)
                        {
                            operands.Add(new("Symbol", (NeImportResolver.Classify(import).Api ?? contracts?.GetValueOrDefault(import.Identity))?.Identity.CanonicalName ?? (rejectedImports?.Contains(import.Identity) == true
                                ? "__rejected_" + import.Identity
                                : auditFunction is not null
                                    ? "__unimplemented_" + import.Identity
                                    : throw Fail("import has no supported registry contract"))));
                        }
                        else
                        {
                            throw Fail("unresolved call");
                        }
                    }
                    else
                    {
                        operands.Add(Operand(0));
                        if (auditFunction is null)
                        {
                            var indexedTargets = function.Dependencies.Where(d => d.Site == original.Address && d.Kind == "IndexedNearCallTarget").Select(d => d.Target!.Value).Order().ToArray();
                            if (indexedTargets.Length > 0)
                            {
                                var indexedTable = NeJumpTableAnalyzer.Analyze(bytes, executable, function.Instructions, original.Address);
                                if (!indexedTable.Resolved || !indexedTable.Entries.Select(e => e.Target).Distinct().Order().SequenceEqual(indexedTargets))
                                {
                                    throw Fail("indexed call target set mismatch");
                                }

                                transfer = new(distance, indexedTargets.Select(t => new IrIndirectTarget(t.SegmentIndex, t.Offset, null, graph.Functions.Single(f => f.Address == t).Name)).ToArray(), indexedTable.IndexComputation, indexedTable.TableBase!.Value.Offset, indexedTable.Entries.Count);
                            }
                            else
                            {
                                var dependency = function.Dependencies.SingleOrDefault(d => d.Site == original.Address && d.Kind is "IndirectFarCall" or "IndirectNearCall");
                                if (dependency?.Target is not { } target)
                                {
                                    throw Fail("indirect call has no approved internal target set");
                                }

                                var pointer = new NeBinaryAddress(ins.MemorySegment == Register.CS
                                    ? segment.Index
                                    : executable.NeHeader.AutoDataSegment, (ushort)ins.MemoryDisplacement64);
                                var relocation = executable.Relocations.SingleOrDefault(r => r.SourceSegmentIndex == pointer.SegmentIndex && r.SourceOffsets.Contains(pointer.Offset));
                                if (relocation is null || relocation.Additive || !relocation.Supported || relocation.TargetSegmentIndex != target.SegmentIndex || relocation.TargetOffset != target.Offset)
                                {
                                    throw Fail("indirect call target disagrees with relocation evidence");
                                }

                                transfer = new(distance, [new(target.SegmentIndex, target.Offset, null, graph.Functions.Single(f => f.Address == target).Name)], "Validated NE pointer relocation");
                            }
                        }
                    }
                }
                else if (ins.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
                {
                    if (ins.Op0Kind == OpKind.NearBranch16 && function.Instructions.Any(i => i.Address.Offset == ins.NearBranch16))
                    {
                        operands.Add(new("Label", Label(ins.NearBranch16)));
                    }
                    else
                    {
                        var dependency = function.Dependencies.SingleOrDefault(d => d.Site == original.Address && d.Kind is "TailTransfer" or "FarTailTransfer");
                        if (dependency?.Target is not { } target)
                        {
                            throw Fail("tail transfer has no verified target");
                        }

                        // Validate the original transfer again instead of trusting a supplied graph.
                        if (ins.Op0Kind == OpKind.NearBranch16 && target != new NeBinaryAddress(segment.Index, ins.NearBranch16))
                        {
                            throw Fail("tail target mismatch");
                        }

                        if (ins.Op0Kind == OpKind.FarBranch16 && !fixes.Any(r => r.Supported && !r.Additive && r.Import is null && r.TargetSegmentIndex == target.SegmentIndex && (r.RawAddressType == 3
                            ? r.TargetOffset == target.Offset
                            : r.RawAddressType == 2 && ins.FarBranch16 == target.Offset)))
                        {
                            throw Fail("far tail relocation mismatch");
                        }

                        operation = "TAIL";
                        distance = ins.Op0Kind == OpKind.FarBranch16
                            ? CallDistance.Far
                            : CallDistance.Near;
                        operands.Add(new("Symbol", graph.Functions.Single(f => f.Address == target).Name));
                        transfer = new(distance, [new(target.SegmentIndex, target.Offset, null, operands[0].Text)], "Original direct tail transfer");
                    }
                }
                else if (ins.FlowControl == FlowControl.IndirectBranch && auditInstruction is not null)
                {
                    operation = "DYNAMICJMP";
                    distance = ins.Code == Code.Jmp_m1616
                        ? CallDistance.Far
                        : CallDistance.Near;
                    operands.Add(Operand(0));
                }
                else if (ins.FlowControl == FlowControl.IndirectBranch)
                {
                    var fixedTail = function.Dependencies.SingleOrDefault(d => d.Site == original.Address && d.Kind == "IndirectTailTransfer");
                    if (fixedTail?.Target is { } tailTarget)
                    {
                        int pointerSegment = ins.MemorySegment == Register.CS
                            ? segment.Index
                            : executable.NeHeader.AutoDataSegment;
                        var fixup = executable.Relocations.SingleOrDefault(r => r.SourceSegmentIndex == pointerSegment && r.SourceOffsets.Contains((ushort)ins.MemoryDisplacement64));
                        if (ins.MemoryBase != Register.None || ins.MemoryIndex != Register.None || fixup is null || !fixup.Supported || fixup.Additive || fixup.TargetSegmentIndex != tailTarget.SegmentIndex || fixup.TargetOffset != tailTarget.Offset)
                        {
                            throw Fail("unverified indirect tail pointer");
                        }

                        operation = "TAIL";
                        distance = ins.Code == Code.Jmp_m1616
                            ? CallDistance.Far
                            : CallDistance.Near;
                        operands.Add(Operand(0));
                        transfer = new(distance, [new(tailTarget.SegmentIndex, tailTarget.Offset, null, graph.Functions.Single(f => f.Address == tailTarget).Name)], "NE relocation-backed indirect tail");
                    }
                    else
                    {
                        var table = NeJumpTableAnalyzer.Analyze(bytes, executable, function.Instructions, original.Address);
                        var expected = table.Entries.Select(e => e.Target).Distinct().Order().ToArray();
                        var approved = function.Dependencies.Where(d => d.Site == original.Address && d.Kind == "JumpTableTarget" && d.Resolved).Select(d => d.Target!.Value).Order().ToArray();
                        if (!table.Resolved || !expected.SequenceEqual(approved) || expected.Any(t => !function.Instructions.Any(i => i.Address == t)))
                        {
                            throw Fail("indirect branch lacks a complete verified target set");
                        }

                        operation = "DISPATCH";
                        distance = CallDistance.Near;
                        operands.Add(Operand(0));
                        transfer = new(distance, expected.Select(t => new IrIndirectTarget(t.SegmentIndex, t.Offset, Label(t.Offset), null)).ToArray(), table.IndexComputation, table.TableBase!.Value.Offset, table.Entries.Count);
                    }
                }
                else if (_strings.Contains(operation))
                {
                    if (ins.SegmentPrefix != Register.None)
                    {
                        throw Fail("string segment overrides are not supported by the current native string helper");
                    }
                }
                else
                {
                    if (prefix != RepeatPrefix.None)
                    {
                        throw Fail("repeat prefix requires a supported string form");
                    }

                    for (int i = 0; i < ins.OpCount; i++)
                    {
                        operands.Add(Operand(i));
                    }
                }

                if (!control && fixes.Any(r => r.RawAddressType != 2 || r.Import is not null || !r.Supported || r.Additive || !r.SourceOffsets.Any(o => o == offset + constants.ImmediateOffset) || constants.ImmediateSize != 2))
                {
                    throw Fail("instruction relocation is not a supported symbolic selector immediate");
                }

                var ir = new IrInstruction(operation, operands, source, original.Text, (uint)offset, ins.Length, Semantics: new(operands.Select(o => o.Width).FirstOrDefault(w => w is not null) ?? (_strings.Contains(operation)
                    ? operation.EndsWith('B')
                        ? RegisterWidth.Byte
                        : RegisterWidth.Word
                    : null), Flags(ins.RflagsRead), Flags(ins.RflagsWritten | ins.RflagsCleared | ins.RflagsSet))
                {
                    UndefinedFlags = Flags(ins.RflagsUndefined),
                    ConditionalEffects = operation is "SHL" or "SHR" or "SAR" or "RCL" or "RCR"
                    ? "Effects depend on masked/effective count; count zero preserves all flags; OF defined only for count one."
                    : _strings.Contains(operation)
                        ? "Index direction follows DF; REP with CX=0 performs no accesses or flag writes; compare repeat stops on ZF."
                        : null
                }, OriginalMnemonic: ins.Mnemonic.ToString().ToUpperInvariant(), CallDistance: distance, ReturnCleanup: cleanup, Prefix: prefix, Condition: condition)
                {
                    IndirectTransfer = transfer,
                    BinarySource = new(segment.Index, (ushort)offset, original.BytesHex, ins.Code.ToString(), Convert.ToHexString(raw.AsSpan(0, prefixCount)), Enumerable.Range(0, ins.OpCount).Select(i => ins.GetOpKind(i).ToString()).ToArray())
                };
                code.Add(ir);

                IrOperand Operand(int index)
                {
                    OpKind kind = ins.GetOpKind(index);
                    if (kind == OpKind.Register)
                    {
                        var register = ins.GetOpRegister(index);
                        if (!Enum.TryParse<X86Register>(register.ToString(), out var irRegister))
                        {
                            throw Fail($"register {register} is unsupported");
                        }

                        return new("Register", register.ToString(), Width(register.GetSize()), Register: irRegister);
                    }

                    if (kind == OpKind.Memory)
                    {
                        X86Register? Reg(Register r) => r == Register.None
                            ? null
                            : Enum.TryParse<X86Register>(r.ToString(), out var value)
                                ? value
                                : throw Fail($"address register {r} is unsupported");

                        var baseRegister = Reg(ins.MemoryBase);
                        var memoryIndex = Reg(ins.MemoryIndex);
                        if (!EffectiveAddress.IsLegal8086Addressing(baseRegister, memoryIndex) || ins.MemoryIndexScale != 1)
                        {
                            throw Fail("addressing mode is not an 8086 effective address");
                        }

                        int size = ins.MemorySize.GetSize();
                        var width = size == 0 && operation == "LEA"
                            ? RegisterWidth.Word
                            : Width(size);
                        var memory = new MemoryReference(original.Text, width, ins.MemorySegment.ToString(), baseRegister, memoryIndex, unchecked((short)(ushort)ins.MemoryDisplacement64), HasExplicitSegment: ins.SegmentPrefix != Register.None);
                        return new("Memory", original.Text, width, memory);
                    }

                    long value;
                    RegisterWidth immediateWidth;
                    switch (kind)
                    {
                        case OpKind.Immediate8:
                            value = ins.Immediate8;
                            immediateWidth = RegisterWidth.Byte;
                            break;
                        case OpKind.Immediate16:
                            value = ins.Immediate16;
                            immediateWidth = RegisterWidth.Word;
                            break;
                        case OpKind.Immediate32:
                            value = ins.Immediate32;
                            immediateWidth = RegisterWidth.Dword;
                            break;
                        case OpKind.Immediate8to16:
                            value = ins.Immediate8to16;
                            immediateWidth = RegisterWidth.Word;
                            break;
                        case OpKind.Immediate8to32:
                            value = ins.Immediate8to32;
                            immediateWidth = RegisterWidth.Dword;
                            break;
                        default:
                            throw Fail($"operand kind {kind} is unsupported");
                    }

                    if (fixes.SingleOrDefault(r => r.RawAddressType == 2 && r.Import is null && r.SourceOffsets.Contains((ushort)(offset + constants.ImmediateOffset))) is { TargetSegmentIndex: int targetSegment })
                    {
                        return new("Symbol", SelectorSymbol(targetSegment), RegisterWidth.Word);
                    }

                    return new("Immediate", value.ToString(System.Globalization.CultureInfo.InvariantCulture), immediateWidth, ImmediateValue: value);
                }


                RegisterWidth Width(int size) => size switch
                {
                    1 => RegisterWidth.Byte,
                    2 => RegisterWidth.Word,
                    4 => RegisterWidth.Dword,
                    _ => throw Fail($"operand size {size} bytes is unsupported")
                };
            }

            if (auditInstruction is not null)
            {
                functions.Add(new(function.Name, code, Arguments: [], EntryDistance: returns.FirstOrDefault().Distance == CallDistance.Near
                    ? CallDistance.Near
                    : CallDistance.Far, SavesDataSegment: false)
                {
                    ExplicitFrame = true,
                    OriginalSegmentIndex = function.Address.SegmentIndex,
                    OriginalOffset = function.Address.Offset
                });
                continue;
            }

            if (returns.Count == 0)
            {
                foreach (var target in function.Dependencies.Where(d => d.Kind is "TailTransfer" or "FarTailTransfer" or "IndirectTailTransfer" && d.Target is not null))
                {
                    foreach (var ret in graph.Functions.Single(f => f.Address == target.Target).Instructions.Where(i => i.FlowControl == "Return"))
                    {
                        byte opcode = Convert.FromHexString(ret.BytesHex)[0];
                        if (opcode is 0xc3 or 0xcb or 0xc2 or 0xca)
                        {
                            returns.Add((opcode is 0xcb or 0xca
                                ? CallDistance.Far
                                : CallDistance.Near, opcode is 0xc2 or 0xca
                                ? BitConverter.ToUInt16(Convert.FromHexString(ret.BytesHex), 1)
                                : (ushort)0));
                        }
                    }
                }
            }

            if (returns.Count == 0 || returns.Select(r => r.Distance).Distinct().Count() != 1)
            {
                throw new NotSupportedException($"Binary function {function.Address} has no established consistent near/far return contract.");
            }

            functions.Add(new IrFunction(function.Name, code, Arguments: Array.Empty<IrArgument>(), Segment: function.Address.SegmentIndex.ToString(), EntryDistance: returns[0].Distance, SavesDataSegment: false, CleanupBytes: returns.Select(r => r.Cleanup).Distinct().Count() == 1
                ? returns[0].Cleanup
                : null)
            {
                ExplicitFrame = true,
                OriginalSegmentIndex = function.Address.SegmentIndex,
                OriginalOffset = function.Address.Offset
            });
        }

        if (auditInstruction is not null)
        {
            return new("NE:diagnostic-only:" + executable.Sha256, functions);
        }

        foreach (var function in functions)
        {
            foreach (var call in function.Instructions.Where(i => i.Operation == "CALL" && i.Operands[0].Kind == "Symbol"))
            {
                var target = functions.SingleOrDefault(f => f.Name == call.Operands[0].Text);
                if (target is not null && target.EntryDistance != call.CallDistance)
                {
                    throw new InvalidDataException($"Binary call at {call.Source.File}: {call.CallDistance} transfer disagrees with {target.Name} return distance {target.EntryDistance}.");
                }
            }
        }

        foreach (var function in functions)
        {
            foreach (var instruction in function.Instructions.Where(i => i.IndirectTransfer is not null && i.Operation is "CALL" or "TAIL"))
            {
                foreach (var target in instruction.IndirectTransfer!.Targets.Where(t => t.Function is not null))
                {
                    var body = functions.SingleOrDefault(f => f.Name == target.Function);
                    if (instruction.Operation == "TAIL" && function.EntryDistance == CallDistance.Near && target.SegmentIndex != function.OriginalSegmentIndex)
                    {
                        throw new NotSupportedException("Cross-segment tail with near return requires a different caller CS contract.");
                    }

                    if (body is null || body.EntryDistance != (instruction.Operation == "TAIL"
                        ? function.EntryDistance
                        : instruction.CallDistance))
                    {
                        throw new InvalidDataException($"Indirect/tail return contract mismatch at {instruction.Source.File}.");
                    }
                }
            }
        }

        return new("NE:" + executable.Sha256, functions);
    }

    private static IReadOnlyList<CpuFlag> Flags(RflagsBits bits) => new (RflagsBits Bit, CpuFlag Flag)[]
    {
        (RflagsBits.CF, CpuFlag.Carry),
        (RflagsBits.PF, CpuFlag.Parity),
        (RflagsBits.AF, CpuFlag.AuxiliaryCarry),
        (RflagsBits.ZF, CpuFlag.Zero),
        (RflagsBits.SF, CpuFlag.Sign),
        (RflagsBits.OF, CpuFlag.Overflow),
        (RflagsBits.DF, CpuFlag.Direction)
    }.Where(pair => (bits & pair.Bit) != 0).Select(pair => pair.Flag).ToArray();

    private static string Label(int offset) => $"L_{offset:X4}";

    private static BranchCondition Condition(string mnemonic) => mnemonic switch
    {
        "JE" => BranchCondition.Equal,
        "JNE" => BranchCondition.NotEqual,
        "JB" => BranchCondition.Below,
        "JAE" => BranchCondition.AboveOrEqual,
        "JA" => BranchCondition.Above,
        "JBE" => BranchCondition.BelowOrEqual,
        "JL" => BranchCondition.Less,
        "JLE" => BranchCondition.LessOrEqual,
        "JG" => BranchCondition.Greater,
        "JGE" => BranchCondition.GreaterOrEqual,
        "JS" => BranchCondition.Sign,
        "JNS" => BranchCondition.NotSign,
        "JO" => BranchCondition.Overflow,
        "JNO" => BranchCondition.NotOverflow,
        "JCXZ" => BranchCondition.CounterZero,
        "LOOP" => BranchCondition.Loop,
        _ => BranchCondition.None
    };
}
