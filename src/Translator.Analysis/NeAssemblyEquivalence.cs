using Translator.IR;

namespace Translator.Analysis;

public sealed record NeAssemblyDifference(int Index, string? Binary, string? Reconstruction);
public sealed record NeAssemblyComparison(string Function, bool Equivalent, string Verification, IReadOnlyList<NeAssemblyDifference> Differences, IReadOnlyList<string> Notes);
/// <summary>Focused semantic comparison of verified standard Win16 macro frames and reachable typed instructions.
/// It never supplies or patches the application body used for translation.</summary>
public static class NeAssemblyEquivalence
{
    public static NeAssemblyComparison Compare(IrFunction binary, IrFunction reconstruction, IReadOnlyDictionary<string, int>? bindings = null)
    {
        bindings ??= new Dictionary<string, int>();
        var notes = new List<string>();
        var differences = new List<NeAssemblyDifference>();
        try
        {
            var instructions = binary.Instructions.Where(i => i.BinarySource is not null).ToArray();
            string prefix = string.Concat(instructions.Take(7).Select(i => i.BinarySource!.BytesHex));
            string epilogue = string.Concat(instructions.TakeLast(5).Take(4).Select(i => i.BinarySource!.BytesHex));
            if (!binary.ExplicitFrame || reconstruction.ExplicitFrame || !reconstruction.SavesDataSegment || reconstruction.Variant != FunctionVariant.Default || prefix != "8CD89045558BEC1E8ED8" || epilogue != "8D66FE1F5D4D" || instructions[^1].Operation != "RETF")
            {
                throw new InvalidDataException("Comparison supports only the verified standard Win16 func/endfunc frame; other frames require an explicit comparator.");
            }

            var body = instructions.Skip(7).SkipLast(5).ToHashSet();
            var bodyInstructions = binary.Instructions.Where(i => i.Operation == "LABEL" || body.Contains(i)).ToArray();
            var a = Canonical(binary with
            {
                Instructions = bodyInstructions
            }, bindings);
            var b = Canonical(reconstruction, bindings);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                if (a.ElementAtOrDefault(i) != b.ElementAtOrDefault(i))
                {
                    differences.Add(new(i, a.ElementAtOrDefault(i), b.ElementAtOrDefault(i)));
                }
            }

            ushort cleanup = reconstruction.CleanupBytes ?? 0; // original func.mac initializes argsize=0
            if (binary.CleanupBytes != cleanup)
            {
                differences.Add(new(-1, $"RETF cleanup={binary.CleanupBytes}", $"endfunc cleanup={cleanup}"));
            }

            if (binary.EntryDistance != reconstruction.EntryDistance)
            {
                differences.Add(new(-1, binary.EntryDistance.ToString(), reconstruction.EntryDistance.ToString()));
            }

            notes.Add("Standard prologue/epilogue verified by exact original bytes; NOPs and unreachable instructions excluded from both paths.");
            notes.Add("Branch targets compared by reachable instruction index; effective memory operands include argument offsets and selector defaults.");
            if (reconstruction.CleanupBytes is null && reconstruction.Arguments?.Count > 0)
            {
                notes.Add("Original func.mac defaults to caller cleanup (argsize=0). Legacy fixture emission infers argument-size cleanup; explicitly use CleanupBytes=0 when executing this reconstruction as the reference.");
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or KeyNotFoundException)
        {
            differences.Add(new(-1, error.Message, null));
        }

        return new(binary.Name, differences.Count == 0, "Reachable typed instruction/operand/control-flow comparison; reconstruction has no independent encoded-byte listing", differences, notes);
    }

    private static string[] Canonical(IrFunction function, IReadOnlyDictionary<string, int> symbols)
    {
        var code = function.Instructions;
        var labels = code.Select((i, n) => (i, n)).Where(p => p.i.Operation == "LABEL").ToDictionary(p => p.i.Operands[0].Text, p => p.n);
        var visited = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(0);
        while (pending.TryDequeue(out int index))
        {
            if (index >= code.Count || !visited.Add(index))
            {
                continue;
            }

            var ins = code[index];
            if (ins.Operation == "JMP" || ins.Condition != BranchCondition.None)
            {
                if (!labels.TryGetValue(ins.Operands[0].Text, out int target))
                {
                    throw new InvalidDataException("Unknown comparison branch target: " + ins.Operands[0].Text);
                }

                pending.Enqueue(target);
                if (ins.Operation == "JMP")
                {
                    continue;
                }
            }

            if (ins.Operation is not ("RET" or "RETF" or "RETN"))
            {
                pending.Enqueue(index + 1);
            }
        }

        var indexes = visited.Where(n => code[n].Operation is not ("LABEL" or "NOP")).Order().ToArray();

        int Target(string label)
        {
            int source = labels[label];
            int found = Array.FindIndex(indexes, index => index >= source);
            return found < 0
                ? indexes.Length
                : found;
        }


        return indexes.Select(index =>
        {
            var ins = code[index];
            string op = ins.Condition != BranchCondition.None
                ? "CC:" + ins.Condition
                : ins.Operation;
            var width = ins.Operands.FirstOrDefault(o => o.Kind is "Register" or "Memory")?.Width ?? RegisterWidth.Word;

            string Operand(IrOperand operand)
            {
                if (ins.Operation == "JMP" || ins.Condition != BranchCondition.None)
                {
                    return "target:" + Target(operand.Text);
                }

                if (operand.Register is { } register)
                {
                    return "reg:" + register;
                }

                if (operand.Memory is { } memory)
                {
                    int offset = memory.Displacement;
                    var baseRegister = memory.Base;
                    var segment = memory.Segment;
                    if (memory.Symbol is not null)
                    {
                        var argument = function.Arguments?.FirstOrDefault(a => a.Name == memory.Symbol);
                        if (argument is not null)
                        {
                            baseRegister = X86Register.BP;
                            segment = "SS";
                            offset += argument.Offset + (function.EntryDistance == CallDistance.Far
                                ? 6
                                : 4);
                        }
                        else
                        {
                            offset += symbols.TryGetValue(memory.Symbol, out int value)
                                ? value
                                : throw new InvalidDataException("Unresolved comparison memory symbol: " + memory.Symbol);
                        }
                    }

                    segment ??= baseRegister == X86Register.BP || memory.Index == X86Register.BP
                        ? "SS"
                        : "DS";
                    var size = ins.Operation is "LDS" or "LES"
                        ? RegisterWidth.Dword
                        : memory.Width ?? operand.Width ?? width;
                    return $"mem:{size}:{segment}:{baseRegister}:{memory.Index}:{unchecked((ushort)offset)}";
                }

                if (operand.Kind == "Immediate" || symbols.ContainsKey(operand.Text))
                {
                    long value = operand.ImmediateValue ?? symbols[operand.Text];
                    if (operand.Width == RegisterWidth.Byte && width != RegisterWidth.Byte)
                    {
                        value = unchecked((sbyte)value);
                    }

                    uint mask = width == RegisterWidth.Byte
                        ? 255u
                        : width == RegisterWidth.Dword
                            ? uint.MaxValue
                            : 65535u;
                    return "imm:" + (unchecked((uint)value) & mask);
                }

                return "symbol:" + operand.Text;
            }


            return op + "/" + ins.Prefix + "/" + (ins.Operation == "CALL"
                ? ins.CallDistance + "/"
                : "") + string.Join(";", ins.Operands.Select(Operand));
        }).ToArray();
    }
}
