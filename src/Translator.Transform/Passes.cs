using Translator.Core;
using Translator.IR;

namespace Translator.Transform;

public interface IIrPass
{
    string Name
    {
        get;
    }

    (IrModule Module, IReadOnlyList<TranslationDiagnostic> Diagnostics) Run(IrModule module);
}

public sealed class RegisterNormalizationPass : IIrPass
{
    public string Name => "register-normalization";

    public (IrModule, IReadOnlyList<TranslationDiagnostic>) Run(IrModule module)
    {
        var functions = module.Functions.Select(function => function with
        {
            Instructions = function.Instructions.Select(instruction => instruction with
            {
                Operands = instruction.Operands.Select(operand => operand.Kind == "Register"
                    ? operand with
                    {
                        Text = operand.Text.ToUpperInvariant()
                    }
                    : operand).ToArray()
            }).ToArray()
        }).ToArray();
        return (module with
        {
            Functions = functions
        }, Array.Empty<TranslationDiagnostic>());
    }
}

public sealed class OperandWidthNormalizationPass : IIrPass
{
    public string Name => "operand-width-normalization";

    private static readonly HashSet<string> _needsWidth = new(StringComparer.OrdinalIgnoreCase)
    {
        "MOV",
        "ADD",
        "SUB",
        "CMP",
        "AND",
        "OR",
        "XOR",
        "TEST",
        "SHL",
        "SHR"
    };
    public (IrModule Module, IReadOnlyList<TranslationDiagnostic> Diagnostics) Run(IrModule module)
    {
        var errors = new List<TranslationDiagnostic>();
        foreach (var f in module.Functions)
        {
            foreach (var i in f.Instructions)
            {
                if (_needsWidth.Contains(i.Operation) && i.Operands.Any(o => o.Kind == "Memory" && o.Width is null) && !i.Operands.Any(o => o.Width is not null))
                {
                    errors.Add(new("AmbiguousOperandWidth", Name, "Memory operand width cannot be inferred.", i.Source, i.OriginalText));
                }
            }
        }

        if (errors.Count > 0)
        {
            return (module, errors);
        }

        var functions = module.Functions.Select(f => f with
        {
            Instructions = f.Instructions.Select(i =>
        {
            var operands = i.Operands.ToArray();
            var width = operands.Select(o => o.Width).FirstOrDefault(w => w is not null);
            for (int n = 0; n < operands.Length; n++)
            {
                if (operands[n].Kind == "Memory" && operands[n].Width is null)
                {
                    operands[n] = operands[n] with
                    {
                        Width = width,
                        Memory = operands[n].Memory is { } memory
                            ? memory with
                            {
                                Width = width
                            }
                            : null
                    };
                }
            }

            return i with
            {
                Operands = operands
            };
        }).ToArray()
        }).ToArray();
        return (module with
        {
            Functions = functions
        }, errors);
    }
}

public sealed class ArithmeticWidthNormalizationPass : IIrPass
{
    public string Name => "arithmetic-width-normalization";

    public (IrModule Module, IReadOnlyList<TranslationDiagnostic> Diagnostics) Run(IrModule module)
    {
        var errors = new List<TranslationDiagnostic>();
        foreach (var f in module.Functions)
        {
            foreach (var i in f.Instructions.Where(i => i.Operation is "ADD" or "SUB" or "CMP"))
            {
                var widths = i.Operands.Where(o => o.Kind is "Register" or "Memory" && o.Width is not null).Select(o => o.Width).Distinct().ToArray();
                if (widths.Length > 1)
                {
                    errors.Add(new("WidthMismatch", Name, "Arithmetic operands have conflicting widths.", i.Source, i.OriginalText));
                }
            }
        }

        return (module, errors);
    }
}

public sealed class FlagSemanticsNormalizationPass : IIrPass
{
    public string Name => "flag-semantics-normalization";

    private static readonly CpuFlag[] _arithmeticFlags =
    {
        CpuFlag.Carry,
        CpuFlag.Parity,
        CpuFlag.AuxiliaryCarry,
        CpuFlag.Zero,
        CpuFlag.Sign,
        CpuFlag.Overflow
    };

    public (IrModule Module, IReadOnlyList<TranslationDiagnostic> Diagnostics) Run(IrModule module)
    {
        var functions = module.Functions.Select(function => function with
        {
            Instructions = function.Instructions.Select(instruction => instruction with
            {
                Semantics = Describe(instruction)
            }).ToArray()
        }).ToArray();
        return (module with
        {
            Functions = functions
        }, Array.Empty<TranslationDiagnostic>());
    }

    private static InstructionSemantics Describe(IrInstruction instruction)
    {
        if (instruction.Semantics is not null)
        {
            return instruction.Semantics;
        }

        string op = instruction.Operation.ToUpperInvariant();
        RegisterWidth? width = instruction.Operands.Select(operand => operand.Width).FirstOrDefault(candidate => candidate is not null);
        CpuFlag[] reads = op switch
        {
            "JE" or "JZ" => new[]
            {
                CpuFlag.Zero
            },
            "JNE" or "JNZ" => new[]
            {
                CpuFlag.Zero
            },
            "JB" or "JC" => new[]
            {
                CpuFlag.Carry
            },
            "JAE" or "JNC" => new[]
            {
                CpuFlag.Carry
            },
            "JA" => new[]
            {
                CpuFlag.Carry,
                CpuFlag.Zero
            },
            "JBE" => new[]
            {
                CpuFlag.Carry,
                CpuFlag.Zero
            },
            "JL" => new[]
            {
                CpuFlag.Sign,
                CpuFlag.Overflow
            },
            "JLE" => new[]
            {
                CpuFlag.Zero,
                CpuFlag.Sign,
                CpuFlag.Overflow
            },
            "JG" => new[]
            {
                CpuFlag.Zero,
                CpuFlag.Sign,
                CpuFlag.Overflow
            },
            "JGE" => new[]
            {
                CpuFlag.Sign,
                CpuFlag.Overflow
            },
            _ => Array.Empty<CpuFlag>()
        };
        CpuFlag[] writes = op switch
        {
            "ADD" or "SUB" or "CMP" => _arithmeticFlags,
            "INC" or "DEC" => _arithmeticFlags.Where(flag => flag != CpuFlag.Carry).ToArray(),
            "AND" or "OR" or "XOR" or "TEST" => new[]
            {
                CpuFlag.Carry,
                CpuFlag.Parity,
                CpuFlag.Zero,
                CpuFlag.Sign,
                CpuFlag.Overflow
            },
            "SHL" or "SHR" => new[]
            {
                CpuFlag.Carry,
                CpuFlag.Parity,
                CpuFlag.Zero,
                CpuFlag.Sign,
                CpuFlag.Overflow
            },
            _ => Array.Empty<CpuFlag>()
        };
        return new(width, reads, writes);
    }
}

public sealed class ControlFlowNormalizationPass : IIrPass
{
    public string Name => "control-flow-normalization";

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

    public (IrModule Module, IReadOnlyList<TranslationDiagnostic> Diagnostics) Run(IrModule module)
    {
        var functions = module.Functions.Select(f => f with { BasicBlocks = Build(f) }).ToArray();
        return (module with
        {
            Functions = functions
        }, Array.Empty<TranslationDiagnostic>());
    }

    private static IReadOnlyList<BasicBlock> Build(IrFunction f)
    {
        var code = f.Instructions;
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var leaders = new SortedSet<int>();
        if (code.Count > 0)
        {
            leaders.Add(0);
        }

        for (int n = 0; n < code.Count; n++)
        {
            if (code[n].Operation == "LABEL")
            {
                labels[code[n].Operands[0].Text] = n;
            }
        }

        for (int n = 0; n < code.Count; n++)
        {
            if (code[n].Operation is not ("JMP" or "DISPATCH" or "TAIL" or "DYNAMICJMP" or "FLOW") && code[n].Condition == BranchCondition.None && !_conditional.Contains(code[n].Operation) && code[n].Operation is not ("RET" or "RETF" or "RETN" or "FAULT"))
            {
                continue;
            }

            if (n + 1 < code.Count)
            {
                leaders.Add(n + 1);
            }

            if (code[n].IndirectTransfer is { } transfer)
            {
                foreach (var t in transfer.Targets.Where(t => t.Label is not null))
                {
                    if (labels.TryGetValue(t.Label!, out int labelIndex))
                    {
                        leaders.Add(labelIndex);
                    }
                }
            }

            if (code[n].Operands.Count > 0 && labels.TryGetValue(code[n].Operands[0].Text, out int target))
            {
                leaders.Add(target);
            }
        }

        var starts = leaders.ToArray();
        var blocks = new List<BasicBlock>();
        var blockNames = new Dictionary<int, string>();
        foreach (int at in starts)
        {
            blockNames[at] = code[at].Operation == "LABEL"
                ? code[at].Operands[0].Text
                : $"block_{at}";
        }

        for (int j = 0; j < starts.Length; j++)
        {
            int from = starts[j], to = j + 1 < starts.Length
                ? starts[j + 1]
                : code.Count;
            var body = code.Skip(from).Take(to - from).Where(x => x.Operation != "LABEL").ToArray();
            if (body.Length == 0)
            {
                continue;
            }

            string name = blockNames[from];
            var tail = body[^1];
            var edges = new List<ControlFlowEdge>();
            if (tail.Operation == "DISPATCH")
            {
                edges.AddRange(tail.IndirectTransfer!.Targets.Select(t => new ControlFlowEdge(name, t.Label!, "indirect")));
            }
            else if (tail.Operation is "TAIL" or "DYNAMICJMP" or "FLOW")
            {
                // These transfers leave the block without a local successor.
            }
            else if (tail.Operation == "JMP" && tail.Operands.Count > 0)
            {
                edges.Add(new(name, tail.Operands[0].Text, "unconditional"));
            }
            else if ((tail.Condition != BranchCondition.None || _conditional.Contains(tail.Operation)) && tail.Operands.Count > 0)
            {
                edges.Add(new(name, tail.Operands[0].Text, "true"));
                if (j + 1 < starts.Length)
                {
                    edges.Add(new(name, blockNames[starts[j + 1]], "false"));
                }
            }
            else if (tail.Operation is not ("RET" or "RETF" or "RETN" or "FAULT") && j + 1 < starts.Length)
            {
                edges.Add(new(name, blockNames[starts[j + 1]], "fallthrough"));
            }

            blocks.Add(new(name, body, edges));
        }

        return blocks;
    }
}

public sealed class NativeCompatibilityValidationPass : IIrPass
{
    public string Name => "native-compatibility-validation";

    private static readonly HashSet<string> _supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "MOV",
        "ADD",
        "SUB",
        "INC",
        "DEC",
        "AND",
        "OR",
        "XOR",
        "CMP",
        "TEST",
        "FAULT",
        "DISPATCH",
        "TAIL",
        "JMP",
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
        "JGE",
        "CALL",
        "RET",
        "PUSH",
        "POP",
        "NOP",
        "SHL",
        "SHR",
        "LABEL",
        "DB",
        "DW",
        "DD",
        "CALCULATE_FLAGS",
        "IMUL",
        "CWD",
        "JNG",
        "JNLE",
        "JNAE",
        "JNB",
        "JNA",
        "JNBE",
        "JNL",
        "JS",
        "JNS",
        "JO",
        "JNO",
        "JCXZ",
        "LOOP",
        "RETF",
        "RETN",
        "ADC",
        "SBB",
        "NEG",
        "NOT",
        "MUL",
        "SAR",
        "RCL",
        "RCR",
        "CMC",
        "CLC",
        "STC",
        "XCHG",
        "LEA",
        "LDS",
        "LES",
        "DIV",
        "IDIV",
        "CLD",
        "STD",
        "LODSB",
        "LODSW",
        "MOVSB",
        "MOVSW",
        "STOSB",
        "SCASB",
        "CMPSB",
        "CMPSW",
        "INT",
        "DYNAMICJMP",
        "FLOW"
    };

    public (IrModule Module, IReadOnlyList<TranslationDiagnostic> Diagnostics) Run(IrModule module)
    {
        var errors = module.Functions
            .SelectMany(function => function.Instructions)
            .Where(instruction => !_supported.Contains(instruction.Operation))
            .Select(instruction => new TranslationDiagnostic(
                "UnsupportedOperation", Name,
                $"'{instruction.Operation}' has no native or runtime lowering.",
                instruction.Source, instruction.OriginalText))
            .ToArray();
        return (module, errors);
    }
}

public sealed class TranslationPipeline
{
    private readonly IIrPass[] _passes;

    public TranslationPipeline(params IIrPass[] passes) => _passes = passes;

    public (IrModule Module, IReadOnlyList<TranslationDiagnostic> Diagnostics) Run(IrModule module)
    {
        var errors = new List<TranslationDiagnostic>();
        foreach (var pass in _passes)
        {
            var result = pass.Run(module);
            module = result.Module;
            errors.AddRange(result.Diagnostics);
            if (errors.Count > 0)
            {
                break;
            }
        }

        return (module, errors);
    }
}
