using System.Text.RegularExpressions;
using Translator.IR;

namespace Translator.Assembly;

public sealed record ParseDiagnostic(string Code, string Message, SourceLocation Source, string Text);
public sealed record ParseResult(IrModule Module, IReadOnlyList<ParseDiagnostic> Diagnostics);
public sealed class AssemblyParser
{
    private static readonly HashSet<string> _supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "lea",
        "lds",
        "les",
        "mov",
        "add",
        "adc",
        "sub",
        "sbb",
        "neg",
        "not",
        "imul",
        "mul",
        "idiv",
        "div",
        "inc",
        "dec",
        "and",
        "or",
        "xor",
        "cmp",
        "test",
        "shl",
        "shr",
        "sar",
        "rcl",
        "rol",
        "ror",
        "rcr",
        "jmp",
        "je",
        "jz",
        "jne",
        "jnz",
        "jb",
        "jc",
        "jnae",
        "jnb",
        "jnc",
        "jae",
        "jbe",
        "jna",
        "ja",
        "jnbe",
        "jl",
        "jng",
        "jle",
        "jnl",
        "jnle",
        "jge",
        "jg",
        "js",
        "jns",
        "jo",
        "jno",
        "jcxz",
        "loop",
        "call",
        "ret",
        "retn",
        "retf",
        "push",
        "pop",
        "nop",
        "rep",
        "repe",
        "repz",
        "repne",
        "repnz",
        "lodsb",
        "lodsw",
        "movsb",
        "movsw",
        "cmpsb",
        "cmpsw",
        "stosb",
        "scasb",
        "clc",
        "stc",
        "cwd",
        "xchg",
        "int",
        "cld",
        "std",
        "db",
        "dw",
        "dd"
    };
    private static readonly HashSet<string> _registers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ax",
        "bx",
        "cx",
        "dx",
        "si",
        "di",
        "bp",
        "sp",
        "cs",
        "ds",
        "es",
        "ss",
        "al",
        "ah",
        "bl",
        "bh",
        "cl",
        "ch",
        "dl",
        "dh",
        "eax",
        "ebx",
        "ecx",
        "edx",
        "esi",
        "edi",
        "ebp",
        "esp"
    };
    private static readonly Dictionary<string, BranchCondition> _conditions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["je"] = BranchCondition.Equal,
        ["jz"] = BranchCondition.Equal,
        ["jne"] = BranchCondition.NotEqual,
        ["jnz"] = BranchCondition.NotEqual,
        ["jb"] = BranchCondition.Below,
        ["jc"] = BranchCondition.Below,
        ["jnae"] = BranchCondition.Below,
        ["jnb"] = BranchCondition.AboveOrEqual,
        ["jnc"] = BranchCondition.AboveOrEqual,
        ["jae"] = BranchCondition.AboveOrEqual,
        ["jbe"] = BranchCondition.BelowOrEqual,
        ["jna"] = BranchCondition.BelowOrEqual,
        ["ja"] = BranchCondition.Above,
        ["jnbe"] = BranchCondition.Above,
        ["jl"] = BranchCondition.Less,
        ["jng"] = BranchCondition.LessOrEqual,
        ["jnle"] = BranchCondition.Greater,
        ["jle"] = BranchCondition.LessOrEqual,
        ["jnl"] = BranchCondition.GreaterOrEqual,
        ["jge"] = BranchCondition.GreaterOrEqual,
        ["jg"] = BranchCondition.Greater,
        ["js"] = BranchCondition.Sign,
        ["jns"] = BranchCondition.NotSign,
        ["jo"] = BranchCondition.Overflow,
        ["jno"] = BranchCondition.NotOverflow,
        ["jcxz"] = BranchCondition.CounterZero,
        ["loop"] = BranchCondition.Loop
    };

    public ParseResult Parse(string path) => ParseLines(File.ReadLines(path), path);
    /// <summary>Parse explicit translation roots without preprocessing unrelated corpus functions.</summary>
    public ParseResult ParseSelected(string path, IReadOnlyCollection<string> names)
    {
        var selected = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        bool include = false;
        var lines = new List<string>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Split(';')[0].Trim();
            var start = Regex.Match(line, @"^func(?:_nods)?\s+(?<name>\S+)$", RegexOptions.IgnoreCase);
            if (start.Success)
            {
                include = selected.Contains(start.Groups["name"].Value);
            }

            bool metadata = line.StartsWith("segment ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("extern ", StringComparison.OrdinalIgnoreCase);
            lines.Add(include || metadata
                ? raw
                : "");
            if (line.Equals("endfunc", StringComparison.OrdinalIgnoreCase) || line.Equals("endfunc_crt", StringComparison.OrdinalIgnoreCase))
            {
                include = false;
            }
        }

        return ParseLines(lines, path);
    }

    public ParseResult ParseText(string text, string source = "<assembly>") => ParseLines(text.Replace("\r", "").Split('\n'), source);
    private ParseResult ParseLines(IEnumerable<string> lines, string path)
    {
        var functions = new List<IrFunction>();
        var diagnostics = new List<ParseDiagnostic>();
        string? name = null;
        var instructions = new List<IrInstruction>();
        var variant = FunctionVariant.Default;
        SourceLocation? start = null;
        var conditionalEnds = new Stack<string>();
        int conditionalId = 0;
        var arguments = new List<IrArgument>();
        var locals = new Dictionary<string, (int Offset, RegisterWidth Width)>();
        var externals = new List<string>();
        var segments = new List<string>();
        string? segmentName = null;
        bool savesDs = true;
        ushort? cleanupBytes = null;
        int lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var source = new SourceLocation(path, lineNo);
            var line = raw.Split(';')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("func ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("func_nods ", StringComparison.OrdinalIgnoreCase))
            {
                if (name is not null)
                {
                    diagnostics.Add(new("NestedFunction", "Function started before previous function ended.", source, raw));
                }

                bool noDs = line.StartsWith("func_nods ", StringComparison.OrdinalIgnoreCase);
                name = line[(noDs
                    ? 10
                    : 5)..].Trim();
                arguments = new();
                locals.Clear();
                cleanupBytes = null;
                savesDs = !noDs;
                variant = noDs
                    ? FunctionVariant.NoDataSegment
                    : FunctionVariant.Default;
                start = source;
                instructions = new();
                conditionalEnds.Clear();
                conditionalId = 0;
                continue;
            }

            if (line.Equals("endfunc", StringComparison.OrdinalIgnoreCase) || line.Equals("endfunc_crt", StringComparison.OrdinalIgnoreCase))
            {
                if (conditionalEnds.Count != 0)
                {
                    diagnostics.Add(new("UnterminatedIf", "Function ends inside an if block.", source, raw));
                }

                if (name is null)
                {
                    diagnostics.Add(new("UnexpectedEnd", "Function terminator outside a function.", source, raw));
                }
                else
                {
                    if (line.Equals("endfunc_crt", StringComparison.OrdinalIgnoreCase))
                    {
                        variant = FunctionVariant.Crt;
                    }

                    functions.Add(new(name, instructions.ToArray(), Variant: variant, Start: start, End: source, Arguments: arguments.ToArray(), Segment: segmentName, SavesDataSegment: savesDs, CleanupBytes: cleanupBytes));
                }

                name = null;
                continue;
            }

            var cleanupDirective = Regex.Match(line, @"^%assign\s+%\$argsize\s+(?<size>0x[0-9a-f]+|[0-9]+)$", RegexOptions.IgnoreCase);
            if (cleanupDirective.Success && name is not null)
            {
                string text = cleanupDirective.Groups["size"].Value;
                if (TryNumber(text, out int count) && count >= 0 && count <= 65535 && (count & 1) == 0)
                {
                    cleanupBytes = (ushort)count;
                }
                else
                {
                    diagnostics.Add(new("InvalidCleanup", "Expected an even word-stack cleanup size.", source, raw));
                }

                continue;
            }

            if (line.StartsWith("%local ", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string declaration in Regex.Split(line[7..].Trim(), @"[,\s]+"))
                {
                    var local = Regex.Match(declaration, @"^(?<name>\w+):word$", RegexOptions.IgnoreCase);
                    if (!local.Success || name is null || locals.ContainsKey(local.Groups["name"].Value) || arguments.Any(a => a.Name == local.Groups["name"].Value))
                    {
                        diagnostics.Add(new("InvalidLocal", "Expected a unique named word local inside a function.", source, raw));
                        continue;
                    }

                    // func.mac already reserves saved_ds at BP-2. Declarations do not allocate stack bytes.
                    locals.Add(local.Groups["name"].Value, (-4 - locals.Count * 2, RegisterWidth.Word));
                }

                continue;
            }

            if (line.StartsWith("%arg ", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string declaration in Regex.Split(line[5..].Trim(), @"[,\s]+"))
                {
                    var arg = Regex.Match(declaration.Trim(), @"^(?<name>\w+):(?<type>byte|word|dword)$", RegexOptions.IgnoreCase);
                    if (!arg.Success || name is null)
                    {
                        diagnostics.Add(new("InvalidArgument", "Expected a named byte, word, or dword argument inside a function.", source, raw));
                        continue;
                    }

                    var width = arg.Groups["type"].Value.ToLowerInvariant() switch
                    {
                        "byte" => RegisterWidth.Byte,
                        "dword" => RegisterWidth.Dword,
                        _ => RegisterWidth.Word
                    };
                    if (arguments.Any(a => a.Name == arg.Groups["name"].Value) || locals.ContainsKey(arg.Groups["name"].Value))
                    {
                        diagnostics.Add(new("DuplicateArgument", "Argument names must be unique.", source, raw));
                        continue;
                    }

                    arguments.Add(new(arg.Groups["name"].Value, width, arguments.Sum(a => Math.Max(2, (int)a.Width / 8))));
                }

                continue;
            }

            if (line.StartsWith("extern ", StringComparison.OrdinalIgnoreCase))
            {
                externals.AddRange(line[7..].Split(',').Select(x => x.Trim()));
                continue;
            }

            if (line.StartsWith("segment ", StringComparison.OrdinalIgnoreCase))
            {
                segmentName = line[8..].Split(' ')[0];
                segments.Add(segmentName);
                continue;
            }

            if (line.StartsWith("STRUC ", StringComparison.OrdinalIgnoreCase) || line.Equals("ENDSTRUC", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith('%'))
            {
                diagnostics.Add(new("UnsupportedDirective", $"Directive '{line.Split(' ')[0]}' is not supported.", source, raw));
                continue;
            }

            if (name is null)
            {
                continue;
            }

            var conditional = Regex.Match(line, @"^if\s+(?<condition>nz|z|ne|e)$", RegexOptions.IgnoreCase);
            if (conditional.Success)
            {
                string endLabel = $"{name}::.if_end_{conditionalId++}";
                conditionalEnds.Push(endLabel);
                var target = new IrOperand("Symbol", endLabel);
                var skipCondition = (conditional.Groups["condition"].Value.Equals("nz", StringComparison.OrdinalIgnoreCase) || conditional.Groups["condition"].Value.Equals("ne", StringComparison.OrdinalIgnoreCase))
                    ? BranchCondition.Equal
                    : BranchCondition.NotEqual;
                string branchOp = skipCondition == BranchCondition.Equal
                    ? "jz"
                    : "jnz";
                instructions.Add(new(branchOp.ToUpperInvariant(), new[] { target }, source, raw.Trim(), Semantics: BuildSemantics(branchOp, new[] { target }, skipCondition, RepeatPrefix.None), OriginalMnemonic: "if", Condition: skipCondition));
                continue;
            }

            if (line.Equals("endif", StringComparison.OrdinalIgnoreCase))
            {
                if (conditionalEnds.Count == 0)
                {
                    diagnostics.Add(new("UnexpectedEndIf", "endif has no matching if.", source, raw));
                }
                else
                {
                    instructions.Add(new("LABEL", new[] { new IrOperand("Label", conditionalEnds.Pop()) }, source, raw.Trim(), OriginalMnemonic: "endif"));
                }

                continue;
            }

            if (line.EndsWith(':'))
            {
                instructions.Add(new("LABEL", new[] { new IrOperand("Label", $"{name}::{line[..^1]}") }, source, raw.Trim(), OriginalMnemonic: "label"));
                continue;
            }

            var match = Regex.Match(line, @"^(?<op>[A-Za-z][A-Za-z0-9]*)\s*(?<args>.*?)\s*$");
            if (!match.Success)
            {
                diagnostics.Add(new("InvalidSyntax", "Cannot parse instruction.", source, raw));
                continue;
            }

            string op = match.Groups["op"].Value;
            RepeatPrefix prefix = RepeatPrefix.None;
            if (op.Equals("rep", StringComparison.OrdinalIgnoreCase) || op.Equals("repe", StringComparison.OrdinalIgnoreCase) || op.Equals("repz", StringComparison.OrdinalIgnoreCase) || op.Equals("repne", StringComparison.OrdinalIgnoreCase) || op.Equals("repnz", StringComparison.OrdinalIgnoreCase))
            {
                prefix = op.Equals("rep", StringComparison.OrdinalIgnoreCase)
                    ? RepeatPrefix.Rep
                    : op.StartsWith("repn", StringComparison.OrdinalIgnoreCase)
                        ? RepeatPrefix.Repne
                        : RepeatPrefix.Repe;
                var next = Regex.Match(match.Groups["args"].Value, @"^(?<op>\w+)\s*(?<args>.*)$");
                if (!next.Success)
                {
                    diagnostics.Add(new("DanglingPrefix", "Repeat prefix must precede a string instruction.", source, raw));
                    continue;
                }

                op = next.Groups["op"].Value;
                match = next;
            }

            if (!_supported.Contains(op))
            {
                diagnostics.Add(new("UnsupportedInstruction", $"Instruction '{op}' is not supported.", source, raw));
                continue;
            }

            var args = SplitOperands(match.Groups["args"].Value).ToArray();
            CallDistance distance = CallDistance.Unspecified;
            if ((op.Equals("call", StringComparison.OrdinalIgnoreCase) || op.StartsWith("ret", StringComparison.OrdinalIgnoreCase)) && args.Length > 0)
            {
                var distanceMatch = Regex.Match(args[0], @"^(?<distance>near|far)(?:\s+(?<target>.+))?$", RegexOptions.IgnoreCase);
                if (distanceMatch.Success)
                {
                    distance = distanceMatch.Groups["distance"].Value.Equals("far", StringComparison.OrdinalIgnoreCase)
                        ? CallDistance.Far
                        : CallDistance.Near;
                    args = distanceMatch.Groups["target"].Success
                        ? new[]
                    {
                        distanceMatch.Groups["target"].Value
                    }.Concat(args.Skip(1)).ToArray()
                        : args.Skip(1).ToArray();
                }
            }

            if (op.Equals("jmp", StringComparison.OrdinalIgnoreCase) && args.Length > 0 && args[0].StartsWith("short ", StringComparison.OrdinalIgnoreCase))
            {
                args[0] = args[0][6..].Trim();
            }

            IrOperand[] operands;
            try
            {
                operands = args.Select(ParseOperand).Select(operand =>
                {
                    if (operand.Memory is not { Symbol: not null } memory || !locals.TryGetValue(memory.Symbol, out var local))
                    {
                        return operand;
                    }

                    if (memory.Base is not null || memory.Index is not null || memory.Segment is not null)
                    {
                        throw new FormatException("Local references cannot include base/index registers or segment overrides.");
                    }

                    var width = operand.Width ?? local.Width;
                    return operand with
                    {
                        Width = width,
                        Memory = memory with
                        {
                            Width = width,
                            Base = X86Register.BP,
                            Segment = "SS",
                            Symbol = null,
                            Displacement = memory.Displacement + local.Offset
                        }
                    };
                }).ToArray();
            }
            catch (FormatException error)
            {
                diagnostics.Add(new("InvalidOperand", error.Message, source, raw));
                continue;
            }

            if (op.Equals("call", StringComparison.OrdinalIgnoreCase) && distance == CallDistance.Unspecified && operands.Length > 0 && operands[0].Text.StartsWith("far ", StringComparison.OrdinalIgnoreCase))
            {
                distance = CallDistance.Far;
                operands[0] = ParseOperand(operands[0].Text[4..]);
            }

            string normalized = NormalizeOperation(op);
            if ((normalized == "JMP" || _conditions.ContainsKey(op)) && operands.Length > 0 && operands[0].Kind == "Symbol" && operands[0].Text.StartsWith('.'))
            {
                operands[0] = operands[0] with
                {
                    Text = $"{name}::{operands[0].Text}"
                };
            }

            ushort? cleanup = null;
            if (op.StartsWith("ret", StringComparison.OrdinalIgnoreCase) && operands.FirstOrDefault()?.ImmediateValue is long cleanupValue && cleanupValue is >= 0 and <= ushort.MaxValue)
            {
                cleanup = (ushort)cleanupValue;
            }

            if (op.Equals("call", StringComparison.OrdinalIgnoreCase) && distance == CallDistance.Unspecified)
            {
                distance = CallDistance.Near;
            }

            if (op.Equals("retf", StringComparison.OrdinalIgnoreCase))
            {
                distance = CallDistance.Far;
            }

            if (op.Equals("retn", StringComparison.OrdinalIgnoreCase) || op.Equals("ret", StringComparison.OrdinalIgnoreCase))
            {
                distance = distance == CallDistance.Unspecified
                    ? CallDistance.Near
                    : distance;
            }

            var condition = _conditions.GetValueOrDefault(op);
            instructions.Add(new(normalized, operands, source, raw.Trim(), Semantics: BuildSemantics(op, operands, condition, prefix), OriginalMnemonic: op.ToUpperInvariant(), CallDistance: distance, ReturnCleanup: cleanup, Prefix: prefix, Condition: condition));
        }

        if (name is not null)
        {
            diagnostics.Add(new("UnterminatedFunction", $"Function '{name}' has no terminator.", new(path, lineNo), ""));
        }

        return new(new(path, functions, externals, segments), diagnostics);
    }

    // Keep the original instruction identity for existing analysis and lowering stages;
    // BranchCondition carries the normalized semantic condition independently.
    private static string NormalizeOperation(string op) => op.ToUpperInvariant();

    private static IEnumerable<string> SplitOperands(string args)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == '[')
            {
                depth++;
            }
            else if (args[i] == ']')
            {
                depth--;
            }
            else if (args[i] == ',' && depth == 0)
            {
                yield return args[start..i].Trim();
                start = i + 1;
            }
        }

        if (start < args.Length)
        {
            yield return args[start..].Trim();
        }
    }

    private static IrOperand ParseOperand(string value)
    {
        string v = value.Trim();
        RegisterWidth? width = Regex.IsMatch(v, @"^byte\s", RegexOptions.IgnoreCase)
            ? RegisterWidth.Byte
            : Regex.IsMatch(v, @"^word\s", RegexOptions.IgnoreCase)
                ? RegisterWidth.Word
                : Regex.IsMatch(v, @"^dword\s", RegexOptions.IgnoreCase)
                    ? RegisterWidth.Dword
                    : null;
        v = Regex.Replace(v, @"^(?:byte|word|dword)\s+(?:ptr\s+)?", "", RegexOptions.IgnoreCase);
        if (_registers.Contains(v))
        {
            return new("Register", v.ToUpperInvariant(), WidthOf(v), Register: Enum.Parse<X86Register>(v, true));
        }

        if (v.Contains('['))
        {
            string? segment = null;
            bool explicitSegment = Regex.IsMatch(v, @"\b(?:cs|ds|es|ss):", RegexOptions.IgnoreCase);
            var m = Regex.Match(v, @"(?:(?<seg>cs|ds|es|ss):)?\[(?<expr>[^]]+)\]", RegexOptions.IgnoreCase);
            string expr = m.Success
                ? m.Groups["expr"].Value.Trim()
                : v;
            if (m.Groups["seg"].Success)
            {
                segment = m.Groups["seg"].Value.ToUpperInvariant();
            }

            var insideSegment = Regex.Match(expr, @"^(?<seg>cs|ds|es|ss):(?<rest>.*)$", RegexOptions.IgnoreCase);
            if (insideSegment.Success)
            {
                segment = insideSegment.Groups["seg"].Value.ToUpperInvariant();
                expr = insideSegment.Groups["rest"].Value.Trim();
            }

            X86Register? b = null, ix = null;
            string? symbol = null;
            int displacement = 0;
            foreach (var token0 in Regex.Split(expr, @"(?=[+-])"))
            {
                var token = token0.Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                string t = token.TrimStart('+');
                bool neg = token.StartsWith('-');
                t = t.TrimStart('-').Trim();
                if (_registers.Contains(t))
                {
                    if (neg || ix is not null)
                    {
                        throw new FormatException("8086 effective addresses support at most two nonnegative register terms.");
                    }

                    var r = Enum.Parse<X86Register>(t, true);
                    if (r is not (X86Register.BX or X86Register.BP or X86Register.SI or X86Register.DI))
                    {
                        throw new FormatException($"{r} is not a legal 8086 address register.");
                    }

                    if (b is null)
                    {
                        b = r;
                    }
                    else
                    {
                        ix = r;
                    }

                    if (ix is not null && !((b is X86Register.BX or X86Register.BP && ix is X86Register.SI or X86Register.DI) || (ix is X86Register.BX or X86Register.BP && b is X86Register.SI or X86Register.DI)))
                    {
                        throw new FormatException("Illegal 8086 base/index register pair.");
                    }
                }
                else if (TryNumber(t, out int n))
                {
                    displacement += neg
                        ? -n
                        : n;
                }
                else
                {
                    string term = (neg
                        ? "-"
                        : "") + t;
                    symbol = symbol is null
                        ? term
                        : symbol + (neg
                            ? ""
                            : "+") + term;
                }
            }

            var mem = new MemoryReference(expr, width, segment, b, ix, displacement, symbol, explicitSegment);
            return new("Memory", v, width, mem);
        }

        if (TryNumber(v, out int immediate))
        {
            return new("Immediate", v, width, ImmediateValue: immediate);
        }

        return new("Symbol", v, width);
    }

    private static bool TryNumber(string s, out int value)
    {
        value = 0;
        s = s.Trim();
        bool negative = s.StartsWith('-');
        s = s.TrimStart('+', '-');
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = Convert.ToInt32(s[2..], 16);
            }
            else if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            {
                value = Convert.ToInt32(s[..^1], 16);
            }
            else
            {
                value = int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (negative)
            {
                value = checked(-value);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RegisterWidth WidthOf(string register)
    {
        register = register.ToUpperInvariant();
        return register.Length == 2 && (register.EndsWith('L') || register.EndsWith('H'))
            ? RegisterWidth.Byte
            : register.Length == 3 && register.StartsWith('E')
                ? RegisterWidth.Dword
                : RegisterWidth.Word;
    }

    private static InstructionSemantics BuildSemantics(string op, IReadOnlyList<IrOperand> operands, BranchCondition condition, RepeatPrefix prefix)
    {
        op = op.ToLowerInvariant();
        RegisterWidth? width = operands.Select(o => o.Width ?? (o.Register is null
            ? null
            : WidthOf(o.Register.Value.ToString()))).FirstOrDefault(w => w is not null);
        if (width is null)
        {
            width = op.EndsWith('b')
                ? RegisterWidth.Byte
                : op.EndsWith('w')
                    ? RegisterWidth.Word
                    : null;
        }

        var reads = new HashSet<CpuFlag>();
        var writes = new HashSet<CpuFlag>();
        switch (condition)
        {
            case BranchCondition.Equal:
                reads.Add(CpuFlag.Zero);
                break;
            case BranchCondition.NotEqual:
                reads.Add(CpuFlag.Zero);
                break;
            case BranchCondition.Below:
                reads.Add(CpuFlag.Carry);
                break;
            case BranchCondition.AboveOrEqual:
                reads.Add(CpuFlag.Carry);
                break;
            case BranchCondition.BelowOrEqual:
                reads.UnionWith(new[] { CpuFlag.Carry, CpuFlag.Zero });
                break;
            case BranchCondition.Above:
                reads.UnionWith(new[] { CpuFlag.Carry, CpuFlag.Zero });
                break;
            case BranchCondition.Less:
                reads.UnionWith(new[] { CpuFlag.Sign, CpuFlag.Overflow });
                break;
            case BranchCondition.LessOrEqual:
                reads.UnionWith(new[] { CpuFlag.Zero, CpuFlag.Sign, CpuFlag.Overflow });
                break;
            case BranchCondition.GreaterOrEqual:
                reads.UnionWith(new[] { CpuFlag.Sign, CpuFlag.Overflow });
                break;
            case BranchCondition.Greater:
                reads.UnionWith(new[] { CpuFlag.Zero, CpuFlag.Sign, CpuFlag.Overflow });
                break;
            case BranchCondition.Sign:
            case BranchCondition.NotSign:
                reads.Add(CpuFlag.Sign);
                break;
            case BranchCondition.Overflow:
            case BranchCondition.NotOverflow:
                reads.Add(CpuFlag.Overflow);
                break;
        }

        if (op is "adc" or "sbb" or "rcl" or "rcr")
        {
            reads.Add(CpuFlag.Carry);
        }

        if (op is "add" or "adc" or "sub" or "sbb" or "cmp" or "neg" or "scasb" or "cmpsb" or "cmpsw")
        {
            writes.UnionWith(new[] { CpuFlag.Carry, CpuFlag.Parity, CpuFlag.AuxiliaryCarry, CpuFlag.Zero, CpuFlag.Sign, CpuFlag.Overflow });
        }
        else if (op is "inc" or "dec")
        {
            reads.Add(CpuFlag.Carry);
            writes.UnionWith(new[] { CpuFlag.Parity, CpuFlag.AuxiliaryCarry, CpuFlag.Zero, CpuFlag.Sign, CpuFlag.Overflow });
        }
        else if (op is "and" or "or" or "xor" or "test")
        {
            writes.UnionWith(new[] { CpuFlag.Carry, CpuFlag.Parity, CpuFlag.Zero, CpuFlag.Sign, CpuFlag.Overflow });
        }
        else if (op is "rol" or "ror" or "rcl" or "rcr")
        {
            writes.UnionWith(new[] { CpuFlag.Carry, CpuFlag.Overflow });
        }
        else if (op is "shl" or "shr" or "sar")
        {
            writes.UnionWith(new[] { CpuFlag.Carry, CpuFlag.Parity, CpuFlag.Zero, CpuFlag.Sign, CpuFlag.Overflow });
        }
        else if (op is "mul" or "imul")
        {
            writes.UnionWith(new[] { CpuFlag.Carry, CpuFlag.Overflow });
        }
        else if (op is "clc" or "stc")
        {
            writes.Add(CpuFlag.Carry);
        }

        // REP(E/NE) string compare repeats also consume the prior comparison's ZF.
        if (prefix is RepeatPrefix.Repe or RepeatPrefix.Repne)
        {
            reads.Add(CpuFlag.Zero);
        }

        if (op is "cld" or "std")
        {
            writes.Add(CpuFlag.Direction);
        }

        if (op is "lodsb" or "lodsw" or "movsb" or "movsw" or "stosb" or "scasb" or "cmpsb" or "cmpsw")
        {
            reads.Add(CpuFlag.Direction);
        }

        return new(width, reads.OrderBy(f => f).ToArray(), writes.OrderBy(f => f).ToArray());
    }
}
