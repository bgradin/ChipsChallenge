using Translator.IR;

namespace Translator.Analysis;

public sealed class ControlFlowAnalyzer
{
    private static readonly HashSet<string> _conditional = new(StringComparer.OrdinalIgnoreCase)
    {
        "JE",
        "JZ",
        "JNE",
        "JNZ",
        "JB",
        "JC",
        "JAE",
        "JNC",
        "JA",
        "JBE",
        "JL",
        "JLE",
        "JG",
        "JGE"
    };
    public IReadOnlyList<BasicBlock> BuildBlocks(IrFunction function)
    {
        var instructions = function.Instructions;
        var starts = new SortedSet<int>
        {
            0
        };
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].Operation == "LABEL")
            {
                labels[instructions[i].Operands[0].Text] = i;
            }
        }

        for (int i = 0; i < instructions.Count; i++)
        {
            var operation = instructions[i].Operation;
            if ((operation == "JMP" || (instructions[i].Condition != BranchCondition.None || _conditional.Contains(operation))) && instructions[i].Operands.Count > 0 && labels.TryGetValue(instructions[i].Operands[0].Text, out int target))
            {
                starts.Add(target);
            }

            if ((operation == "JMP" || (instructions[i].Condition != BranchCondition.None || _conditional.Contains(operation)) || operation is "RET" or "RETF" or "RETN") && i + 1 < instructions.Count)
            {
                starts.Add(i + 1);
            }
        }

        var blockStartIndexes = starts.Where(i => i < instructions.Count).ToArray();
        var blocks = new List<BasicBlock>();
        for (int blockIndex = 0; blockIndex < blockStartIndexes.Length; blockIndex++)
        {
            int from = blockStartIndexes[blockIndex], to = blockIndex + 1 < blockStartIndexes.Length
                ? blockStartIndexes[blockIndex + 1]
                : instructions.Count;
            var blockInstructions = instructions.Skip(from).Take(to - from).Where(instruction => instruction.Operation != "LABEL").ToArray();
            if (blockInstructions.Length == 0)
            {
                continue;
            }

            string label = instructions[from].Operation == "LABEL"
                ? instructions[from].Operands[0].Text
                : $"block_{from}";
            var lastInstruction = blockInstructions[^1];
            var edges = new List<ControlFlowEdge>();
            if (lastInstruction.Operation == "JMP" && lastInstruction.Operands.Count > 0)
            {
                edges.Add(new(label, lastInstruction.Operands[0].Text, "unconditional"));
            }
            else if ((lastInstruction.Condition != BranchCondition.None || _conditional.Contains(lastInstruction.Operation)) && lastInstruction.Operands.Count > 0)
            {
                edges.Add(new(label, lastInstruction.Operands[0].Text, "true"));
                if (blockIndex + 1 < blockStartIndexes.Length)
                {
                    edges.Add(new(label, $"block_{blockStartIndexes[blockIndex + 1]}", "false"));
                }
            }
            else if (lastInstruction.Operation is not ("RET" or "RETF" or "RETN") && blockIndex + 1 < blockStartIndexes.Length)
            {
                edges.Add(new(label, $"block_{blockStartIndexes[blockIndex + 1]}", "fallthrough"));
            }

            blocks.Add(new(label, blockInstructions, edges));
        }

        return blocks;
    }
}
