using Iced.Intel;
using Translator.Analysis;
using Translator.Binary;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Linker;

public sealed record NeInstructionMemoryEffect(string Segment, string Base, string Index, int Scale, ulong Displacement, string Size, string Access);
public sealed record NeInstructionFlagEffects(string Read, string Written, string Cleared, string Set, string Undefined, string Policy);
public sealed record NeInstructionFormCoverage(string Form, string Opcode, IReadOnlyList<string> OperandWidths, string AddressingMode, string SegmentOverride, string PrefixesHex, NeInstructionFlagEffects Flags, int StackPointerIncrement, string StackEffects, IReadOnlyList<NeInstructionMemoryEffect> MemoryEffects, int Instances, IReadOnlyList<NeBinaryAddress> Sites, string IrStatus, string NativeLoweringStatus, IReadOnlyList<string> LinkageRequirements, IReadOnlyList<string> NativeHelpers, IReadOnlyList<string> Tests, IReadOnlyList<string> Diagnostics);
public sealed record NeInstructionCoverageReport(string Format, string ExecutableSha256, int ExecutableSize, string Policy, string? SymbolVerification, bool DiscoveryWorklistExhausted, int AuditedInstructions, int ExcludedAmbiguousInstructions, int ObservedForms, int UnsupportedForms, bool AllAuditedInstructionsLowered, bool WholeProgramExecutionSupported, IReadOnlyList<NeInstructionFormCoverage> Forms, IReadOnlyList<NeInstructionInventory> AmbiguousInstructions);
/// <summary>Diagnostic CPU-form emission, independently of function/OS/whole-program linkage.
/// Synthetic address bindings and label anchors are used only to exercise the native emitter;
/// no executable is exported and no unresolved service or target is certified.</summary>
public static class NeInstructionCoverageBuilder
{
    public static NeInstructionCoverageReport Build(byte[] image, NeWholeProgramDiscovery discovery)
    {
        var executable = new NeExecutableReader().Read(image);
        if (discovery.ExecutableSha256 != executable.Sha256 || discovery.ExecutableSize != image.Length)
        {
            throw new InvalidDataException("Instruction coverage discovery/image identity mismatch.");
        }

        var graph = discovery.Graph;
        if (graph.ExecutableSha256 != executable.Sha256 || graph.ExecutableSize != image.Length)
        {
            throw new InvalidDataException("Instruction coverage graph/image identity mismatch.");
        }

        var canonical = graph.Functions.SelectMany(f => f.Instructions).DistinctBy(i => i.Address).ToDictionary(i => i.Address);
        if (discovery.Instructions.Count != canonical.Count || discovery.Instructions.Select(i => i.Original.Address).Distinct().Count() != canonical.Count || discovery.Instructions.Any(i => !canonical.TryGetValue(i.Original.Address, out var original) || original != i.Original))
        {
            throw new InvalidDataException("Instruction coverage canonical inventory disagrees with the original discovery graph.");
        }

        var overlapBytes = canonical.Values.SelectMany(i => Enumerable.Range(i.Address.Offset, i.Size).Select(offset => new NeBinaryAddress(i.Address.SegmentIndex, unchecked((ushort)offset)))).GroupBy(address => address).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (discovery.Instructions.Any(i => i.OverlapsAnotherInterpretation != Enumerable.Range(i.Original.Address.Offset, i.Original.Size).Any(offset => overlapBytes.Contains(new(i.Original.Address.SegmentIndex, unchecked((ushort)offset))))))
        {
            throw new InvalidDataException("Instruction coverage ambiguity flags disagree with competing original boundaries.");
        }

        var builder = new NeBinaryIrBuilder();
        var infoFactory = new InstructionInfoFactory();
        var results = new List<(string Form, Instruction Ins, NeInstructionInventory Original, string Ir, string Native, string[] Requirements, string[] Helpers, string? Error)>();
        var sourceOwners = graph.Functions.SelectMany(f => f.Instructions.Select(i => (i.Address, Owner: f.Address))).GroupBy(i => i.Address).ToDictionary(g => g.Key, g => g.First().Owner);
        var bindings = executable.Segments.ToDictionary(s => NeBinaryIrBuilder.SelectorSymbol(s.Index), s => 0x1000 + s.Index * 8);
        var targets = graph.Functions.ToDictionary(f => f.Name, f => new Win16CodeAddress((ushort)(0x1000 + f.Address.SegmentIndex * 8), f.Address.Offset), StringComparer.Ordinal);
        foreach (var candidate in discovery.Instructions.Where(i => !i.OverlapsAnotherInterpretation).OrderBy(i => i.Original.Address))
        {
            var original = candidate.Original;
            var segment = executable.Segments[original.Address.SegmentIndex - 1];
            byte[] loaded = new byte[segment.MemoryLength];
            image.AsSpan(segment.FileOffset, segment.FileLength).CopyTo(loaded);
            if (original.Size <= 0 || original.Address.Offset >= segment.FileLength || original.Address.Offset > loaded.Length - original.Size || Convert.ToHexString(loaded.AsSpan(original.Address.Offset, original.Size)) != original.BytesHex)
            {
                throw new InvalidDataException($"Original instruction bytes mismatch at {original.Address}.");
            }

            var decoder = Decoder.Create(16, new ByteArrayCodeReader(Convert.FromHexString(original.BytesHex)));
            decoder.IP = original.Address.Offset;
            var ins = decoder.Decode();
            if (ins.IsInvalid || ins.Length != original.Size)
            {
                throw new InvalidDataException($"Invalid original instruction at {original.Address}.");
            }

            string prefix = Prefixes(original.BytesHex);
            var widths = Enumerable.Range(0, ins.OpCount).Select(n => OperandWidth(ins, n)).ToArray();
            string addressMode = $"{ins.MemoryBase}+{ins.MemoryIndex}*{ins.MemoryIndexScale};16-bit-offset";
            string form = string.Join("|", ins.Code, string.Join(",", widths), addressMode, ins.SegmentPrefix, prefix);
            string irStatus = "Unsupported", nativeStatus = "NotAttempted";
            var requirements = new List<string>();
            if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch or FlowControl.ConditionalBranch or FlowControl.Return)
            {
                requirements.Add("OriginalControlFlowAndReturnContracts:milestone7.4");
            }

            if (ins.FlowControl == FlowControl.Interrupt)
            {
                requirements.Add("InterruptCompatibilityService:milestone7.6;freestanding-adapter-unimplemented");
            }

            if (graph.Dependencies.Any(d => d.Site == original.Address && d.Import is not null))
            {
                requirements.Add("Win16ImportContract:milestone7.6");
            }

            string[] helpers = [];
            string? error = null;
            try
            {
                var ir = builder.AuditInstruction(image, graph, sourceOwners[original.Address], original.Address);
                irStatus = "Represented";
                var anchors = ir.Operands.Where(o => o.Kind == "Label").Select(o => o.Text).Concat(ir.IndirectTransfer?.Targets.Where(t => t.Label is not null).Select(t => t.Label!) ?? []).Distinct().ToArray();
                var instructions = new List<IrInstruction>
                {
                    ir
                };
                foreach (string label in anchors)
                {
                    instructions.Add(new("LABEL", [new("Label", label)], ir.Source, "Diagnostic target anchor"));
                }

                if (ir.Operation == "CALL" && ir.Operands[0].Kind == "Symbol" && !targets.ContainsKey(ir.Operands[0].Text))
                {
                    targets[ir.Operands[0].Text] = new(0xfff0, 0);
                }

                var function = new IrFunction("instruction_form_audit", instructions, Arguments: [], SavesDataSegment: false, EntryDistance: ir.Operation is "RET" or "RETN"
                    ? CallDistance.Near
                    : CallDistance.Far)
                {
                    ExplicitFrame = true
                };
                function = NeNativeTranslation.Normalize(new("NE:diagnostic-instruction-audit", [function])).Functions.Single();
                var artifact = new Win16BodyCodeGenerator().Generate(function, symbols: bindings, targets: targets);
                helpers = artifact.Relocations!.Select(r => r.Symbol).Distinct().Order(StringComparer.Ordinal).ToArray();
                nativeStatus = "Emitted";
            }
            catch (Exception failure) when (failure is NotSupportedException or InvalidDataException or UnsupportedInstructionException)
            {
                error = failure.Message;
                nativeStatus = irStatus == "Represented"
                    ? "Unsupported"
                    : "NotAttempted";
            }

            results.Add((form, ins, original, irStatus, nativeStatus, requirements.ToArray(), helpers, error));
        }

        var forms = results.GroupBy(r => r.Form).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var first = g.First();
            var ins = first.Ins;
            var memory = infoFactory.GetInfo(ins).GetUsedMemory().Select(m => new NeInstructionMemoryEffect(m.Segment.ToString(), m.Base.ToString(), m.Index.ToString(), m.Scale, m.Displacement, m.MemorySize.ToString(), m.Access.ToString())).ToArray();
            return new NeInstructionFormCoverage(g.Key, ins.Code.ToString(), Enumerable.Range(0, ins.OpCount).Select(n => OperandWidth(ins, n)).ToArray(), $"{ins.MemoryBase}+{ins.MemoryIndex}*{ins.MemoryIndexScale};16-bit-offset", ins.SegmentPrefix.ToString(), Prefixes(first.Original.BytesHex), new(((RflagsBits)ins.RflagsRead).ToString(), ((RflagsBits)ins.RflagsWritten).ToString(), ((RflagsBits)ins.RflagsCleared).ToString(), ((RflagsBits)ins.RflagsSet).ToString(), ((RflagsBits)ins.RflagsUndefined).ToString(), "Undefined flags are not original semantic results. Existing preservation/AF-clear policies are implementation choices; tests mask undefined bits. Shift/rotate effects also depend on masked/effective count; zero count preserves flags."), ins.StackPointerIncrement, ins.FlowControl == FlowControl.Interrupt
                ? "16-bit entry: push FLAGS,CS,nextIP (-6), clear IF/TF; service returns through saved frame (+6). Standalone service adapter deferred, explicit failure."
                : "Static instruction delta shown; calls/returns additionally require original target cleanup contracts; direct SP writes remain runtime-valued.", memory, g.Count(), g.Select(r => r.Original.Address).ToArray(), g.All(r => r.Ir == "Represented")
                ? "Represented"
                : "Unsupported", g.All(r => r.Native == "Emitted")
                ? "Emitted"
                : "Unsupported", g.SelectMany(r => r.Requirements).Distinct().Order(StringComparer.Ordinal).ToArray(), g.SelectMany(r => r.Helpers).Distinct().Order(StringComparer.Ordinal).ToArray(), ins.Mnemonic is Mnemonic.Cmpsb or Mnemonic.Cmpsw
                ? ["NeInstructionTests.CmpsMatchesHardwareCmpForDefinedFlagsAndPreservesRegistersMemoryAndStack", "NeInstructionTests.CmpsRepeatStopsAtTheFirstMismatchOrMatchWithWrappedIndices", "NeInstructionTests.ZeroCountDoesNotAccessMemoryAndFaultsDoNotAdvanceIndicesOrFlags", "NeInstructionTests.OriginalCmpsInstructionsLowerAndExecuteAndOpcodeMutationChangesWidth", "Native/verify_ne_cpu.py", "WindowsCallbackStandaloneTests.FreestandingWindowsCallbackRunsApiGraphicsAndActualWindowsCallbacks"]
                : ins.Mnemonic == Mnemonic.Int
                    ? ["NeInstructionTests.InterruptUsesARealWrappedFrameAndReturnsDefinedFlagsThroughSavedFlags", "NeInstructionTests.InterruptRejectsMissingOrCorruptedFramesAndUnsupportedReturnAddresses", "NeInstructionTests.OriginalInterruptSitesEmitAnExplicitBoundaryWithExactOriginalReturnIp", "Native/verify_ne_cpu.py:freestanding service explicitly deferred"]
                    : ins.FlowControl == FlowControl.IndirectBranch
                        ? ["NeInstructionTests.DynamicTailOperandsUseRegisteredOriginalAddressesWithoutPushingReturnFrames"]
                        : ["Existing compiler and gameplay semantic regression suites;NeInstructionCoverageTests:every observed instance IR/native emission;not a per-form semantic equivalence proof"], g.Where(r => r.Error is not null).Select(r => r.Original.Address.Identity + ": " + r.Error).ToArray());
        }).ToArray();
        var ambiguous = discovery.Instructions.Where(i => i.OverlapsAnotherInterpretation).Select(i => i.Original).ToArray();
        return new("NeInstructionCoverage.v1", executable.Sha256, image.Length, "Audits every unambiguous discovered instruction instance against original bytes, typed IR and AMD64 emitter. Diagnostic bindings/target labels isolate CPU-form support from whole-program control-flow and OS contracts. No native executable or invented service implementation is produced. Ambiguous interpretations (including the apparent IN) remain excluded, not silently counted as supported. Memory-effects displacements below describe each form's first example; exact instances are retained in discovery by Sites. Conditional string/shift effects depend on runtime CX/DF/count. Emission is not a claim of original-program execution or full semantic equivalence.", graph.SymbolVerification, discovery.Summary.WorklistExhausted, results.Count, ambiguous.Length, forms.Length, forms.Count(f => f.IrStatus != "Represented" || f.NativeLoweringStatus != "Emitted"), forms.All(f => f.IrStatus == "Represented" && f.NativeLoweringStatus == "Emitted"), false, forms, ambiguous);
    }

    private static string OperandWidth(Instruction ins, int n)
    {
        var kind = ins.GetOpKind(n);
        return kind == OpKind.Register
            ? $"Register:{ins.GetOpRegister(n).GetSize() * 8}:{ins.GetOpRegister(n)}"
            : kind == OpKind.Memory
                ? $"Memory:{ins.MemorySize.GetSize() * 8}"
                : kind.ToString();
    }

    private static string Prefixes(string bytesHex)
    {
        byte[] bytes = Convert.FromHexString(bytesHex);
        int n = 0;
        while (n < bytes.Length && bytes[n] is 0x26 or 0x2e or 0x36 or 0x3e or 0x64 or 0x65 or 0x66 or 0x67 or 0xf0 or 0xf2 or 0xf3)
        {
            n++;
        }

        return Convert.ToHexString(bytes.AsSpan(0, n));
    }
}
