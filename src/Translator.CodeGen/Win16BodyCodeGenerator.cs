using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Translator.IR;
using Win16_Runtime;

namespace Translator.CodeGen;

/// <summary>
/// Direct AMD64 lowering over the shared MachineState. Native flags are captured before helper
/// calls; memory and service helpers receive selectors, never host pointers masquerading as far pointers.
/// The entry returns the RET/RETF cleanup count; the dispatcher owns the matching return transfer.
/// </summary>
public sealed class Win16BodyCodeGenerator
{
    public static byte[] EntryPrologue => [0x41, 0x54, 0x41, 0x55, 0x48, 0x83, 0xEC, 0x38, 0x49, 0x89, 0xCC, 0x49, 0x89, 0xD5];

    private readonly List<byte> _code = new();
    private readonly List<MachineCodeRelocation> _relocations = new();
    private readonly List<(int Offset, string Target, IrInstruction Instruction)> _branches = new();
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private IrFunction _function = null!;
    private IrInstruction _instruction = null!;
    private static readonly IReadOnlyDictionary<string, int> _fields = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["GameStatePtr"] = 0x1680,
        ["MonsterListLen"] = 0x928,
        ["MonsterListPtr+FarPtr.Off"] = 0x92E,
        ["MonsterListPtr+FarPtr.Seg"] = 0x930,
        ["Monster.x"] = 1,
        ["Monster.y"] = 3,
        ["Monster_size"] = 11
    };
    private IReadOnlyDictionary<string, int> _bindings = _fields;
    public MachineCodeArtifact Generate(IrFunction input, X64Abi abi = X64Abi.Windows, IReadOnlyDictionary<string, int>? symbols = null, IrModule? module = null, IReadOnlyDictionary<string, Win16CodeAddress>? targets = null)
    {
        if (abi != X64Abi.Windows)
        {
            throw new NotSupportedException("Win16 bodies use the Windows AMD64 ABI; use NativeWin16Body for host ABI adaptation.");
        }

        _function = input;
        _bindings = symbols ?? _fields;
        _code.Clear();
        _relocations.Clear();
        _labels.Clear();
        _branches.Clear();
        _instruction = input.Instructions.FirstOrDefault() ?? new("NOP", Array.Empty<IrOperand>(), input.Start ?? new("<assembly>", 0), "");
        if (input.EntryDistance is not (CallDistance.Near or CallDistance.Far))
        {
            Fail("function entry must declare near or far distance");
        }

        if (input.Arguments is null)
        {
            Fail("argument metadata is required; parse the %arg declarations before lowering");
        }

        // Preserve nonvolatile native registers. R12=context, R13=state. 48 bytes include shadow
        // space and two spill words. Entry RSP=8 mod16; two pushes and sub56 align before CALL.
        Emit(EntryPrologue);
        if (!input.ExplicitFrame)
        {
            if (input.SavesDataSegment)
            {
                LoadRegister(X86Register.DS);
                StoreRegister(X86Register.AX);
            }

            LoadRegister(X86Register.BP);
            RestoreFlags();
            Emit(0x66, 0xFF, 0xC0);
            SaveFlags(0x8D4);
            StoreRegister(X86Register.BP);
            Push();
            LoadRegister(X86Register.SP);
            StoreRegister(X86Register.BP);
            LoadRegister(X86Register.DS);
            Push();
        }

        foreach (var item in input.Instructions)
        {
            _instruction = item;
            if (item.Operands.Any(o => o.Memory is { Width: null }) && item.Operation is not ("PUSH" or "POP" or "IMUL" or "MUL" or "CALL" or "LEA" or "LDS" or "LES") && !item.Operands.Any(o => o.Kind is "Register" or "Memory" && o.Width is not null))
            {
                Fail("memory operand width is ambiguous; specify word explicitly");
            }

            if (item.Prefix != RepeatPrefix.None && item.Operation is not ("LODSB" or "LODSW" or "MOVSB" or "MOVSW" or "STOSB" or "SCASB" or "CMPSB" or "CMPSW"))
            {
                Fail($"prefix {item.Prefix} requires a supported string instruction");
            }

            switch (item.Operation.ToUpperInvariant())
            {
                case "LABEL":
                    if (!_labels.TryAdd(item.Operands.Single().Text, _code.Count))
                    {
                        Fail("duplicate label");
                    }

                    break;
                case "MOV":
                    Require(2);
                    ValidateBinaryForm();
                    Read(item.Operands[1]);
                    Write(item.Operands[0]);
                    break;
                case "LEA":
                    Require(2);
                    if (item.Operands[0].Kind != "Register" || item.Operands[0].Width != RegisterWidth.Word || item.Operands[1].Memory is null)
                    {
                        Fail("LEA requires a word register and memory address");
                    }

                    Address(item.Operands[1].Memory!);
                    Emit(0x44, 0x89, 0xC0);
                    Write(item.Operands[0]);
                    break;
                case "LDS":
                case "LES":
                    Require(2);
                    if (item.Operands[0].Kind != "Register" || item.Operands[0].Width != RegisterWidth.Word || item.Operands[1].Memory is null)
                    {
                        Fail("LDS/LES requires a word register and far-pointer memory operand");
                    }

                    Address(item.Operands[1].Memory!);
                    Call("__win16_read_dword");
                    Emit(0x89, 0x44, 0x24, 0x20);
                    Write(item.Operands[0]);
                    Emit(0x8B, 0x44, 0x24, 0x20, 0xC1, 0xE8, 0x10);
                    StoreRegister(item.Operation == "LDS"
                        ? X86Register.DS
                        : X86Register.ES);
                    break;
                case "CLD":
                case "STD":
                    Require(0);
                    LoadFlags();
                    Emit(0x41, 0x81, item.Operation == "CLD"
                        ? (byte)0xE3
                        : (byte)0xCB);
                    Int(item.Operation == "CLD"
                        ? ~0x400
                        : 0x400);
                    StoreFlags();
                    break;
                case "LODSB":
                case "LODSW":
                case "MOVSB":
                case "MOVSW":
                case "STOSB":
                case "SCASB":
                case "CMPSB":
                case "CMPSW":
                    Require(0);
                    EmitContext();
                    Emit(0x41, 0xB8);
                    Int(Array.IndexOf(new[] { "LODSB", "LODSW", "MOVSB", "MOVSW", "STOSB", "SCASB", "CMPSB", "CMPSW" }, item.Operation));
                    Emit(0x41, 0xB9);
                    Int((int)item.Prefix);
                    Call("__win16_string");
                    break;
                case "FLOW":
                    Require(1);
                    // Form is a statically selected CPU control effect, never a guest opcode.
                    if (item.ControlEffect is null || !Enum.IsDefined(item.ControlEffect.Value))
                    {
                        Fail("invalid whole-program control effect");
                    }

                    Read(item.Operands[0]);
                    if (item.ControlEffect is NativeControlEffect.NearJump or NativeControlEffect.NearCall && item.CallDistance == CallDistance.Near)
                    {
                        LoadRegisterToHighCs();
                    }

                    if (item.IndirectTransfer is { } flowSet)
                    {
                        string accepted = "__flow_approved_" + _code.Count;
                        foreach (var candidate in flowSet.Targets)
                        {
                            if (candidate.Function is null || targets is null || !targets.TryGetValue(candidate.Function, out var address))
                            {
                                Fail("whole-program target set has no original-address binding");
                            }

                            var addressValue = targets![candidate.Function!];
                            if (flowSet.Distance == CallDistance.Near)
                            {
                                Emit(0x66, 0x3D, (byte)addressValue.Offset, (byte)(addressValue.Offset >> 8));
                            }
                            else
                            {
                                Emit(0x3D);
                                Int(unchecked((int)((uint)addressValue.Segment << 16 | addressValue.Offset)));
                            }

                            Emit(0x0F, 0x84);
                            Branch(accepted);
                        }

                        EmitDispatchFault();
                        _labels.Add(accepted, _code.Count);
                    }

                    if (item.ControlEffect is NativeControlEffect.NearCall or NativeControlEffect.FarCall && item.BinarySource is { } callSource && item.Size is int callSize)
                    {
                        ushort next = unchecked((ushort)(callSource.Offset + callSize));
                        Emit(0x66, 0x41, 0xC7, 0x45, 0x10, (byte)next, (byte)(next >> 8));
                    }

                    Emit(0x41, 0x89, 0xC0);
                    EmitContext();
                    Emit(0x41, 0xB9);
                    Int((int)item.ControlEffect!.Value);
                    Call("__win16_flow_transfer");
                    Emit(0x31, 0xC0, 0xE9);
                    Branch("__native_binary_return");
                    break;
                case "INT":
                    Require(1);
                    if (item.Operands[0].Kind != "Immediate" || item.Operands[0].ImmediateValue is not (>= 0 and <= 255))
                    {
                        Fail("INT requires an original byte vector");
                    }

                    if (item.BinarySource is { } interruptSource && item.Size is int interruptSize)
                    {
                        ushort interruptNext = unchecked((ushort)(interruptSource.Offset + interruptSize));
                        Emit(0x66, 0x41, 0xC7, 0x45, 0x10, (byte)interruptNext, (byte)(interruptNext >> 8));
                    }

                    EmitContext();
                    Emit(0x41, 0xB8);
                    Int((int)item.Operands[0].ImmediateValue!.Value);
                    Emit(0x45, 0x31, 0xC9);
                    Call("__win16_interrupt");
                    Emit(0x66, 0x85, 0xC0, 0x0F, 0x84);
                    Branch("__native_interrupt_abort");
                    break;
                case "DYNAMICJMP":
                    Require(1);
                    if (item.CallDistance is not (CallDistance.Near or CallDistance.Far))
                    {
                        Fail("indirect jump requires near/far distance");
                    }

                    if (item.Operands[0].Memory is { } jumpMemory)
                    {
                        Address(jumpMemory);
                        Call(item.CallDistance == CallDistance.Far
                            ? "__win16_read_dword"
                            : Win16HelperSymbols.ReadWordHelperSymbol);
                    }
                    else
                    {
                        if (item.CallDistance != CallDistance.Near || item.Operands[0].Kind != "Register" || item.Operands[0].Width != RegisterWidth.Word)
                        {
                            Fail("near indirect jump requires a word register or pointer memory");
                        }

                        Read(item.Operands[0]);
                    }

                    if (item.CallDistance == CallDistance.Near)
                    {
                        LoadRegisterToHighCs();
                    }

                    Emit(0x41, 0x89, 0xC0);
                    EmitContext();
                    Emit(0x41, 0xB9);
                    Int(item.CallDistance == CallDistance.Far
                        ? 1
                        : 0);
                    Call("__win16_tail_target");
                    Emit(0xE9);
                    Branch("__native_binary_return");
                    break;
                case "DIV":
                case "IDIV":
                    Require(1);
                    if (item.Operands[0].Kind is not ("Register" or "Memory"))
                    {
                        Fail("division requires a register or memory divisor");
                    }

                    Read(item.Operands[0]);
                    Emit(0x41, 0x89, 0xC0);
                    EmitContext();
                    Emit(0x41, 0xB9);
                    Int((int)Width(item.Operands[0]) | (item.Operation == "IDIV"
                        ? 0x100
                        : 0));
                    Call("__win16_divide");
                    break;
                case "XCHG":
                    Require(2);
                    ValidateBinaryForm();
                    if (item.Operands.Any(o => o.Kind is not ("Register" or "Memory")))
                    {
                        Fail("XCHG requires two writable operands");
                    }

                    Read(item.Operands[1]);
                    Emit(0x89, 0x44, 0x24, 0x20);
                    Read(item.Operands[0]);
                    Emit(0x89, 0x44, 0x24, 0x24);
                    // Write memory before changing a register that may participate in its address.
                    if (item.Operands[0].Memory is not null)
                    {
                        Emit(0x8B, 0x44, 0x24, 0x20);
                        Write(item.Operands[0]);
                        Emit(0x8B, 0x44, 0x24, 0x24);
                        Write(item.Operands[1]);
                    }
                    else
                    {
                        Write(item.Operands[1]);
                        Emit(0x8B, 0x44, 0x24, 0x20);
                        Write(item.Operands[0]);
                    }

                    break;
                case "ADD":
                case "ADC":
                case "SUB":
                case "SBB":
                case "CMP":
                case "AND":
                case "OR":
                case "XOR":
                case "TEST":
                    Binary(item.Operation.ToUpperInvariant());
                    break;
                case "INC":
                case "DEC":
                case "NEG":
                case "NOT":
                    Require(1);
                    Read(item.Operands[0]);
                    RestoreFlags();
                    PrefixWidth(Width(item.Operands[0]));
                    Emit(item.Operation is "INC" or "DEC"
                        ? (Width(item.Operands[0]) == RegisterWidth.Byte
                            ? (byte)0xFE
                            : (byte)0xFF)
                        : (Width(item.Operands[0]) == RegisterWidth.Byte
                            ? (byte)0xF6
                            : (byte)0xF7), item.Operation switch
                            {
                                "INC" => (byte)0xC0,
                                "DEC" => (byte)0xC8,
                                "NEG" => (byte)0xD8,
                                _ => (byte)0xD0
                            });
                    if (item.Operation != "NOT")
                    {
                        SaveFlags(item.Operation is "INC" or "DEC"
                            ? 0x8D4
                            : 0x8D5);
                    }

                    Write(item.Operands[0]);
                    break;
                case "MUL":
                case "IMUL":
                    Require(1);
                    if (item.Operands[0].Kind is not ("Register" or "Memory"))
                    {
                        Fail("one-operand multiplication requires a register or memory operand");
                    }

                    var multiplyWidth = Width(item.Operands[0]);
                    Read(item.Operands[0]);
                    Emit(0x89, 0xC1);
                    LoadRegister(multiplyWidth == RegisterWidth.Byte
                        ? X86Register.AL
                        : multiplyWidth == RegisterWidth.Dword
                            ? X86Register.EAX
                            : X86Register.AX);
                    RestoreFlags();
                    PrefixWidth(multiplyWidth);
                    Emit(multiplyWidth == RegisterWidth.Byte
                        ? (byte)0xF6
                        : (byte)0xF7, item.Operation == "MUL"
                        ? (byte)0xE1
                        : (byte)0xE9);
                    Emit(0x89, 0x54, 0x24, 0x24);
                    SaveFlags(0x801);
                    StoreRegister(multiplyWidth == RegisterWidth.Dword
                        ? X86Register.EAX
                        : X86Register.AX);
                    if (multiplyWidth != RegisterWidth.Byte)
                    {
                        Emit(0x8B, 0x44, 0x24, 0x24);
                        StoreRegister(multiplyWidth == RegisterWidth.Dword
                            ? X86Register.EDX
                            : X86Register.DX);
                    }

                    break;
                case "CWD":
                    Require(0);
                    LoadRegister(X86Register.AX);
                    Emit(0x66, 0x99, 0x89, 0xD0);
                    StoreRegister(X86Register.DX);
                    break;
                case "PUSH":
                    Require(1);
                    if (item.Operands[0].Kind == "Register" && item.Operands[0].Width == RegisterWidth.Byte)
                    {
                        Fail("x86 cannot PUSH a byte register");
                    }

                    Read(item.Operands[0]);
                    if (Width(item.Operands[0]) == RegisterWidth.Dword)
                    {
                        Emit(0x89, 0x44, 0x24, 0x30, 0xC1, 0xE8, 0x10);
                        Push();
                        Emit(0x8B, 0x44, 0x24, 0x30);
                    }

                    Push();
                    break;
                case "POP":
                    Require(1);
                    if (item.Operands[0].Width == RegisterWidth.Byte)
                    {
                        Fail("x86 cannot POP a byte operand");
                    }

                    Pop();
                    if (Width(item.Operands[0]) == RegisterWidth.Dword)
                    {
                        Emit(0x89, 0x44, 0x24, 0x30);
                        Pop();
                        Emit(0xC1, 0xE0, 0x10, 0x0B, 0x44, 0x24, 0x30);
                    }

                    Write(item.Operands[0]);
                    break;
                case "CMC":
                    Require(0);
                    LoadFlags();
                    Emit(0x41, 0x83, 0xF3, 0x01);
                    StoreFlags();
                    break;
                case "CLC":
                case "STC":
                    Require(0);
                    LoadFlags();
                    Emit(0x41, 0x81, item.Operation == "CLC"
                        ? (byte)0xE3
                        : (byte)0xCB);
                    Int(item.Operation == "CLC"
                        ? ~1
                        : 1);
                    StoreFlags();
                    break;
                case "SHL":
                case "SHR":
                case "SAR":
                case "RCL":
                case "RCR":
                    Require(2);
                    ReadShiftCount(item.Operands[1]);
                    Emit(0x89, 0x44, 0x24, 0x20);
                    Read(item.Operands[0]);
                    Emit(0x8B, 0x4C, 0x24, 0x20);
                    RestoreFlags();
                    PrefixWidth(Width(item.Operands[0]));
                    Emit(Width(item.Operands[0]) == RegisterWidth.Byte
                        ? (byte)0xD2
                        : (byte)0xD3, item.Operation switch
                        {
                            "SHL" => (byte)0xE0,
                            "SHR" => (byte)0xE8,
                            "SAR" => (byte)0xF8,
                            "RCR" => (byte)0xD8,
                            _ => (byte)0xD0
                        });
                    SaveShiftFlags(item.Operation is "RCL" or "RCR");
                    Write(item.Operands[0]);
                    break;
                case "RET":
                case "RETN":
                case "RETF":
                    if (!input.ExplicitFrame)
                    {
                        Fail("explicit returns require an explicit binary frame");
                    }

                    if ((item.Operation == "RETF"
                        ? CallDistance.Far
                        : CallDistance.Near) != input.EntryDistance)
                    {
                        Fail("return distance disagrees with the registered function entry");
                    }

                    Emit(0xB8);
                    Int(item.ReturnCleanup ?? 0);
                    Emit(0xE9);
                    Branch("__native_binary_return");
                    break;
                case "FAULT":
                    Require(0);
                    EmitDispatchFault();
                    break;
                case "DISPATCH":
                    Require(1);
                    if (item.IndirectTransfer is not { Targets.Count: > 0 } dispatch || dispatch.Distance != CallDistance.Near || dispatch.Targets.Any(t => t.Label is null))
                    {
                        Fail("local dispatch requires a verified nonempty near target set");
                    }

                    var dispatchInfo = item.IndirectTransfer!;
                    string dispatchFault = "__dispatch_fault_" + _code.Count;
                    if (dispatchInfo.TableEntries is int count)
                    {
                        LoadRegister(X86Register.BX);
                        Emit(0xA9);
                        Int(1);
                        Emit(0x0F, 0x85);
                        Branch(dispatchFault);
                        Emit(0x3D);
                        Int(count * 2);
                        Emit(0x0F, 0x83);
                        Branch(dispatchFault);
                    }

                    Read(item.Operands[0]);
                    foreach (var candidate in dispatchInfo.Targets)
                    {
                        Emit(0x3D);
                        Int(candidate.Offset);
                        Emit(0x0F, 0x84);
                        Branch(candidate.Label!);
                    }

                    _labels.Add(dispatchFault, _code.Count);
                    EmitDispatchFault();
                    break;
                case "TAIL":
                    Require(1);
                    if (item.IndirectTransfer is not { Targets.Count: 1 })
                    {
                        Fail("tail transfer requires one verified translated target");
                    }

                    string tailName = item.IndirectTransfer!.Targets[0].Function!;
                    if (targets is null || !targets.ContainsKey(tailName))
                    {
                        Fail("tail transfer lacks native binding");
                    }

                    var tail = targets![tailName];
                    if (item.Operands[0].Memory is { } pointerMemory)
                    {
                        Address(pointerMemory);
                        Call(item.CallDistance == CallDistance.Far
                            ? "__win16_read_dword"
                            : Win16HelperSymbols.ReadWordHelperSymbol);
                        string approvedTail = "__tail_approved_" + _code.Count;
                        Emit(0x3D);
                        Int(item.CallDistance == CallDistance.Far
                            ? unchecked((int)((uint)tail.Segment << 16 | tail.Offset))
                            : tail.Offset);
                        Emit(0x0F, 0x84);
                        Branch(approvedTail);
                        EmitDispatchFault();
                        _labels.Add(approvedTail, _code.Count);
                    }

                    Emit(0x41, 0xB8);
                    Int(unchecked((int)((uint)tail.Segment << 16 | tail.Offset)));
                    EmitContext();
                    Emit(0x41, 0xB9);
                    Int(item.CallDistance == CallDistance.Far
                        ? 1
                        : 0);
                    Call("__win16_tail_target");
                    Emit(0xE9);
                    Branch("__native_binary_return");
                    break;
                case "CALL":
                    Require(1);
                    if (input.ExplicitFrame && item.BinarySource is { } source)
                    {
                        // Dispatcher return addresses must refer to the next original Win16 instruction.
                        ushort nextIp = unchecked((ushort)(source.Offset + item.Size!.Value));
                        Emit(0x66, 0x41, 0xC7, 0x45, 0x10, (byte)nextIp, (byte)(nextIp >> 8));
                    }

                    if (item.Operands[0].Kind != "Symbol" || item.Operands[0].Text.Contains(':'))
                    {
                        var operand = item.Operands[0];
                        if (operand.Text.Contains(':') && operand.Kind == "Symbol")
                        {
                            var pair = operand.Text.Split(':');
                            if (pair.Length != 2 || !Enum.TryParse<X86Register>(pair[0], true, out var seg) || !Enum.TryParse<X86Register>(pair[1], true, out var off))
                            {
                                Fail("far register pair must be segment:offset registers");
                            }

                            LoadRegister(Enum.Parse<X86Register>(pair[0], true));
                            Emit(0xC1, 0xE0, 0x10, 0x89, 0x44, 0x24, 0x20);
                            LoadRegister(Enum.Parse<X86Register>(pair[1], true));
                            Emit(0x0B, 0x44, 0x24, 0x20);
                        }
                        else if (operand.Memory is not null)
                        {
                            Address(operand.Memory);
                            Call(item.CallDistance == CallDistance.Far
                                ? "__win16_read_dword"
                                : Win16HelperSymbols.ReadWordHelperSymbol);
                        }
                        else
                        {
                            if (item.CallDistance == CallDistance.Far)
                            {
                                Fail("far indirect register calls require an explicit segment:offset pair");
                            }

                            if (operand.Width != RegisterWidth.Word)
                            {
                                Fail("near indirect calls require word offsets");
                            }

                            Read(operand);
                        }

                        if (item.IndirectTransfer is { } approved)
                        {
                            // Pointer remains a Win16 value in EAX; comparisons never alter MachineState flags.
                            string accepted = "__call_approved_" + _code.Count;
                            foreach (var candidate in approved.Targets)
                            {
                                if (targets is null || candidate.Function is null || !targets.TryGetValue(candidate.Function, out var address))
                                {
                                    Fail("approved indirect call lacks native binding");
                                }

                                var addressValue = targets![candidate.Function!];
                                Emit(0x3D);
                                Int(item.CallDistance == CallDistance.Far
                                    ? unchecked((int)((uint)addressValue.Segment << 16 | addressValue.Offset))
                                    : addressValue.Offset);
                                Emit(0x0F, 0x84);
                                Branch(accepted);
                            }

                            EmitDispatchFault();
                            _labels.Add(accepted, _code.Count);
                        }

                        EmitTargetCall(item.CallDistance);
                        break;
                    }

                    if (targets is not null && targets.TryGetValue(item.Operands[0].Text, out var target))
                    {
                        Emit(0xB8);
                        Int(unchecked((int)((uint)target.Segment << 16 | target.Offset)));
                        EmitTargetCall(item.CallDistance, hasSegment: true);
                        break;
                    }

                    var resolution = Win16ImportResolver.Resolve(item.Operands[0].Text, module);
                    if (resolution.Kind != Win16CallTargetKind.Imported || resolution.Api is null || !resolution.Api.Supported)
                    {
                        Fail($"CALL target '{item.Operands[0].Text}' is {resolution.Kind}: {resolution.Diagnostic}; implement call linkage or register an API binding");
                    }

                    if (item.CallDistance != CallDistance.Far)
                    {
                        Fail("imported procedures require a far call");
                    }

                    EmitContext();
                    Emit(0x41, 0xB8);
                    Int(resolution.Api!.ServiceId);
                    Call(Win16HelperSymbols.CallServiceHelperSymbol);
                    break;
                case "JMP":
                    Require(1);
                    Emit(0xE9);
                    Branch(item.Operands[0].Text);
                    break;
                case "JCXZ":
                case "LOOP":
                    Require(1);
                    LoadRegister(X86Register.CX);
                    if (item.Operation == "LOOP")
                    {
                        Emit(0x66, 0xFF, 0xC8);
                        StoreRegister(X86Register.CX);
                    }

                    Emit(0x66, 0x85, 0xC0, 0x0F, item.Operation == "LOOP"
                        ? (byte)0x85
                        : (byte)0x84);
                    Branch(item.Operands[0].Text);
                    break;
                case "NOP":
                    Require(0);
                    Emit(0x90);
                    break;
                default:
                    if (item.Condition != BranchCondition.None)
                    {
                        Require(1);
                        RestoreFlags();
                        Emit(0x0F, ConditionCode(item.Condition));
                        Branch(item.Operands[0].Text);
                    }
                    else
                    {
                        Fail("no AMD64 lowering is registered for this instruction form");
                    }

                    break;
            }
        }

        if (!input.ExplicitFrame)
        {
            // Expand endfunc: LEA SP,[BP-2]; POP DS; POP BP; DEC BP; RETF argsize.
            LoadRegister(X86Register.BP);
            Emit(0x66, 0x83, 0xE8, 0x02);
            if (input.Variant == FunctionVariant.Crt)
            {
                SaveFlags(0x8D5);
                StoreRegister(X86Register.BP);
            }

            StoreRegister(X86Register.SP);
            Pop();
            StoreRegister(X86Register.DS);
            Pop();
            RestoreFlags();
            Emit(0x66, 0xFF, 0xC8);
            SaveFlags(0x8D4);
            StoreRegister(X86Register.BP);
            Emit(0xB8);
            Int(input.CleanupBytes ?? input.Arguments!.Sum(a => Math.Max(2, (int)a.Width / 8)));
        }
        else
        {
            // An unexpected fallthrough must not masquerade as a Win16 return.
            Emit(0x0F, 0x0B);
            _labels.Add("__native_binary_return", _code.Count);
        }

        Emit(0x48, 0x83, 0xC4, 0x38, 0x41, 0x5D, 0x41, 0x5C, 0xC3);
        if (_branches.Any(b => b.Target == "__native_interrupt_abort"))
        {
            _labels.Add("__native_interrupt_abort", _code.Count);
            // No subsequent guest instruction may execute after an unhandled service.
            Emit(0x31, 0xC0, 0x48, 0x83, 0xC4, 0x38, 0x41, 0x5D, 0x41, 0x5C, 0xC3);
        }

        foreach (var branch in _branches)
        {
            if (!_labels.TryGetValue(branch.Target, out int target))
            {
                throw new UnsupportedInstructionException(_function.Name, branch.Instruction, $"unresolved branch target '{branch.Target}'");
            }

            BinaryPrimitives.WriteInt32LittleEndian(CollectionsMarshal.AsSpan(_code).Slice(branch.Offset, 4), target - branch.Offset - 4);
        }

        return new(_code.ToArray(), "AMD64", abi, "uint16 body(context*, MachineState*) [state ABI v2: 44 bytes]", _relocations.ToArray())
        {
            NativeLabels = input.CaptureNativeLabels
                ? new Dictionary<string, int>(_labels, StringComparer.Ordinal)
                : null
        };
    }

    private void ValidateBinaryForm()
    {
        if (_instruction.Operands[0].Kind is not ("Register" or "Memory"))
        {
            Fail("destination must be a register or memory operand");
        }

        if (_instruction.Operands.Where(o => o.Kind is "Register" or "Memory").Select(Width).Distinct().Count() > 1)
        {
            Fail("binary operand widths do not match");
        }

        if (_instruction.Operands.All(o => o.Memory is not null))
        {
            Fail("memory-to-memory binary instruction forms are not valid 8086 instructions");
        }
    }

    private void Binary(string op)
    {
        Require(2);
        ValidateBinaryForm();
        Read(_instruction.Operands[1]);
        Emit(0x89, 0x44, 0x24, 0x20);
        Read(_instruction.Operands[0]);
        Emit(0x8B, 0x54, 0x24, 0x20);
        RestoreFlags();
        var width = Width(_instruction.Operands[0]);
        PrefixWidth(width);
        byte opcode = op switch
        {
            "ADD" => (byte)0x01,
            "ADC" => (byte)0x11,
            "SUB" => (byte)0x29,
            "SBB" => (byte)0x19,
            "CMP" => (byte)0x39,
            "AND" => (byte)0x21,
            "OR" => (byte)0x09,
            "XOR" => (byte)0x31,
            _ => (byte)0x85
        };
        Emit(width == RegisterWidth.Byte
            ? (byte)(opcode - 1)
            : opcode, 0xD0);
        SaveFlags(0x8D5, op is "AND" or "OR" or "XOR" or "TEST");
        if (op is not ("CMP" or "TEST"))
        {
            Write(_instruction.Operands[0]);
        }
    }

    private void Read(IrOperand operand)
    {
        if (operand.Kind == "Register")
        {
            LoadRegister(Register(operand));
            return;
        }

        if (operand.Kind == "Immediate")
        {
            Emit(0xB8);
            Int(ImmediateValue(operand, operand.ImmediateValue ?? ParseImmediate(operand.Text)));
            return;
        }

        if (operand.Kind == "Symbol" && _bindings.TryGetValue(operand.Text, out int constant))
        {
            Emit(0xB8);
            Int(ImmediateValue(operand, constant));
            return;
        }

        if (operand.Memory is null)
        {
            Fail($"unresolved operand '{operand.Text}'");
            return;
        }

        Address(operand.Memory);
        Call(ReadSymbol(Width(operand)));
    }

    private void Write(IrOperand operand)
    {
        if (operand.Kind == "Register")
        {
            StoreRegister(Register(operand));
            return;
        }

        if (operand.Memory is null)
        {
            Fail("destination must be a register or resolved memory reference");
            return;
        }

        Emit(0x89, 0x44, 0x24, 0x28);
        Address(operand.Memory);
        Emit(0x44, 0x8B, 0x4C, 0x24, 0x28);
        Call(WriteSymbol(Width(operand)));
    }

    private void Address(MemoryReference memory)
    {
        if (!EffectiveAddress.IsLegal8086Addressing(memory.Base, memory.Index))
        {
            Fail("illegal 8086 effective-address registers");
        }

        int displacement = memory.Displacement;
        var arg = _function.Arguments!.FirstOrDefault(a => a.Name == memory.Symbol || (memory.Symbol?.StartsWith(a.Name + "+", StringComparison.Ordinal) == true));
        X86Register? baseRegister = memory.Base;
        string? segment = memory.Segment;
        if (arg is not null)
        {
            if (baseRegister is not null || memory.Index is not null || segment is not null)
            {
                Fail("argument references cannot include explicit base/index or segment overrides");
            }

            baseRegister = X86Register.BP;
            segment = "SS";
            if (memory.Symbol != arg.Name)
            {
                string suffix = memory.Symbol![(arg.Name.Length + 1)..];
                if (!_bindings.TryGetValue(suffix, out int extra))
                {
                    Fail($"unresolved argument field {suffix}");
                }

                displacement += extra;
            }

            displacement += arg.Offset + (_function.EntryDistance == CallDistance.Far
                ? 6
                : 4);
        }
        else if (memory.Symbol is not null)
        {
            if (!_bindings.TryGetValue(memory.Symbol, out int value))
            {
                Fail($"unresolved memory symbol '{memory.Symbol}'");
            }

            displacement += value;
        }

        Emit(0x41, 0xB8);
        Int(displacement);
        foreach (var r in new[]
        {
            baseRegister,
            memory.Index
        }.Where(r => r is not null))
        {
            LoadRegister(r!.Value);
            Emit(0x66, 0x41, 0x01, 0xC0);
        }

        var segmentRegister = segment is not null
            ? Enum.TryParse<X86Register>(segment, true, out var parsed) && parsed is X86Register.CS or X86Register.DS or X86Register.ES or X86Register.SS
                ? parsed
                : throw new UnsupportedInstructionException(_function.Name, _instruction, "invalid segment override")
            : baseRegister == X86Register.BP || memory.Index == X86Register.BP
                ? X86Register.SS
                : X86Register.DS;
        LoadRegister(segmentRegister);
        Emit(0x89, 0xC2, 0x4C, 0x89, 0xE1, 0x45, 0x0F, 0xB7, 0xC0);
    }

    private void Push()
    {
        Emit(0x89, 0x44, 0x24, 0x28);
        LoadRegister(X86Register.SP);
        Emit(0x66, 0x83, 0xE8, 0x02);
        StoreRegister(X86Register.SP);
        Emit(0x44, 0x0F, 0xB7, 0xC0);
        LoadRegister(X86Register.SS);
        Emit(0x89, 0xC2, 0x4C, 0x89, 0xE1, 0x44, 0x8B, 0x4C, 0x24, 0x28);
        Call(Win16HelperSymbols.WriteWordHelperSymbol);
    }

    private void Pop()
    {
        LoadRegister(X86Register.SP);
        Emit(0x44, 0x0F, 0xB7, 0xC0);
        Emit(0x66, 0x83, 0xC0, 0x02);
        StoreRegister(X86Register.SP);
        LoadRegister(X86Register.SS);
        Emit(0x89, 0xC2, 0x4C, 0x89, 0xE1);
        Call(Win16HelperSymbols.ReadWordHelperSymbol);
    }

    private void ReadShiftCount(IrOperand operand)
    {
        if (operand.Kind == "Register" && Register(operand) == X86Register.CL)
        {
            LoadRegister(X86Register.CX);
            Emit(0x25);
            Int(255);
            return;
        }

        if (operand.Kind != "Immediate" && !(operand.Kind == "Symbol" && _bindings.ContainsKey(operand.Text)))
        {
            Fail("shift count must be an immediate, bound constant or CL");
        }

        Read(operand);
    }

    private void SaveShiftFlags(bool rotate)
    {
        int mask = rotate
            ? 0x801
            : 0x8C5;
        Emit(0x9C, 0x41, 0x5B, 0x41, 0x81, 0xE3);
        Int(mask);
        Emit(0x41, 0x89, 0xCA, 0x41, 0x83, 0xE2, 0x1F);
        Emit(0x41, 0x83, 0xFA, 0x01, 0x74);
        int preserveJump = _code.Count;
        Emit(0);
        // OF is undefined for counts above one. Preserve its previous modeled value,
        // matching X86Semantics rather than depending on the host CPU's undefined result.
        Emit(0x41, 0x81, 0xE3);
        Int(~0x800);
        Emit(0x45, 0x0F, 0xB7, 0x55, 0x1A, 0x41, 0x81, 0xE2);
        Int(0x800);
        Emit(0x45, 0x09, 0xD3);
        _code[preserveJump] = checked((byte)(_code.Count - preserveJump - 1));
        Emit(0x45, 0x0F, 0xB7, 0x55, 0x1A, 0x41, 0x81, 0xE2);
        Int(~mask);
        Emit(0x45, 0x09, 0xD3);
        StoreFlags();
    }

    private void RestoreFlags()
    {
        LoadFlags();
        Emit(0x41, 0x81, 0xE3);
        Int(0x8D5);
        Emit(0x41, 0x83, 0xCB, 0x02, 0x41, 0x53, 0x9D);
    }

    private void SaveFlags(int mask, bool clearAuxiliaryCarry = false)
    {
        Emit(0x9C, 0x41, 0x5B, 0x41, 0x81, 0xE3);
        Int(mask);
        if (clearAuxiliaryCarry)
        {
            Emit(0x41, 0x83, 0xE3, 0xEF);
        }

        // R10 is scratch; AX/DX and their arithmetic result remain untouched.
        Emit(0x45, 0x0F, 0xB7, 0x55, 0x1A, 0x41, 0x81, 0xE2);
        Int(~mask);
        Emit(0x45, 0x09, 0xD3);
        StoreFlags();
    }

    private void LoadFlags() => Emit(0x45, 0x0F, 0xB7, 0x5D, 0x1A);

    private void StoreFlags() => Emit(0x66, 0x45, 0x89, 0x5D, 0x1A);

    private void EmitContext() => Emit(0x4C, 0x89, 0xE1, 0x4C, 0x89, 0xEA);

    private void EmitDispatchFault()
    {
        EmitContext();
        Call("__win16_dispatch_fault");
        Emit(0x31, 0xC0, 0xE9);
        Branch("__native_binary_return");
    }

    private void EmitTargetCall(CallDistance distance, bool hasSegment = false)
    {
        if (distance != CallDistance.Far && !hasSegment)
        {
            Emit(0x25);
            Int(0xFFFF);
            LoadRegisterToHighCs();
        }

        Emit(0x41, 0x89, 0xC0);
        EmitContext();
        Emit(0x41, 0xB9);
        Int(distance == CallDistance.Far
            ? 1
            : 0);
        Call("__win16_call_target");
    }

    private void LoadRegisterToHighCs()
    {
        Emit(0x89, 0x44, 0x24, 0x20);
        LoadRegister(X86Register.CS);
        Emit(0xC1, 0xE0, 0x10, 0x0B, 0x44, 0x24, 0x20);
    }

    private static string ReadSymbol(RegisterWidth width) => width switch
    {
        RegisterWidth.Byte => "__win16_read_byte",
        RegisterWidth.Dword => "__win16_read_dword",
        _ => Win16HelperSymbols.ReadWordHelperSymbol
    };

    private static string WriteSymbol(RegisterWidth width) => width switch
    {
        RegisterWidth.Byte => "__win16_write_byte",
        RegisterWidth.Dword => "__win16_write_dword",
        _ => Win16HelperSymbols.WriteWordHelperSymbol
    };

    private void PrefixWidth(RegisterWidth width)
    {
        if (width == RegisterWidth.Word)
        {
            Emit(0x66);
        }
    }

    private RegisterWidth Width(IrOperand operand) => operand.Kind is "Immediate" or "Symbol"
        ? operand.Width == RegisterWidth.Dword
            ? RegisterWidth.Dword
            : RegisterWidth.Word
        : operand.Width ?? _instruction.Operands.FirstOrDefault(o => o.Kind == "Register")?.Width ?? RegisterWidth.Word;
    private void LoadRegister(X86Register r)
    {
        var (offset, high, width) = RegisterView(r);
        Emit(0x41, 0x0F, width == RegisterWidth.Byte
            ? (byte)0xB6
            : (byte)0xB7, 0x45, offset);
        if (width == RegisterWidth.Dword)
        {
            Emit(0x45, 0x0F, 0xB7, 0x55, high, 0x41, 0xC1, 0xE2, 0x10, 0x44, 0x09, 0xD0);
        }
    }

    private void StoreRegister(X86Register r)
    {
        var (offset, high, width) = RegisterView(r);
        if (width != RegisterWidth.Byte)
        {
            Emit(0x66);
        }

        Emit(0x41, (byte)(0x88 + (width == RegisterWidth.Byte
            ? 0
            : 1)), 0x45, offset);
        if (width == RegisterWidth.Dword)
        {
            Emit(0x41, 0x89, 0xC2, 0x41, 0xC1, 0xEA, 0x10, 0x66, 0x45, 0x89, 0x55, high);
        }
    }

    private (byte Offset, byte High, RegisterWidth Width) RegisterView(X86Register r)
    {
        string name = r.ToString();
        if (r is >= X86Register.EAX)
        {
            string word = name[1..];
            return (Offset(word), Offset(word + "High"), RegisterWidth.Dword);
        }

        if (r is >= X86Register.AL and <= X86Register.DH)
        {
            return ((byte)(Offset(name[0] + "X") + (name[1] == 'H'
                ? 1
                : 0)), 0, RegisterWidth.Byte);
        }

        return (Offset(name), 0, RegisterWidth.Word);
    }

    private static byte Offset(string name) => checked((byte)Marshal.OffsetOf<MachineState>(name).ToInt32());

    private X86Register Register(IrOperand operand) => operand.Register ?? Enum.Parse<X86Register>(operand.Text, true);
    private int ImmediateValue(IrOperand operand, long value)
    {
        var width = _instruction.Operation is "PUSH" or "FLOW"
            ? (operand.Width == RegisterWidth.Dword
                ? RegisterWidth.Dword
                : RegisterWidth.Word)
            : _instruction.Operands.FirstOrDefault(o => o.Kind is "Register" or "Memory") is { } dest
                ? Width(dest)
                : RegisterWidth.Word;
        if (operand.Width == RegisterWidth.Byte && width != RegisterWidth.Byte)
        {
            if (value is < -128 or > 255)
            {
                Fail("encoded byte immediate is outside -128..255");
            }

            return unchecked((sbyte)value);
        }

        if (width == RegisterWidth.Dword)
        {
            if (value < int.MinValue || value > uint.MaxValue)
            {
                Fail("dword immediate out of range");
            }

            return unchecked((int)value);
        }

        if (width == RegisterWidth.Byte)
        {
            if (value is < -128 or > 255)
            {
                Fail("byte immediate out of range");
            }
        }
        else if (value is < short.MinValue or > ushort.MaxValue)
        {
            Fail("word immediate is outside -32768..65535");
        }

        return (int)value;
    }

    private long ParseImmediate(string text)
    {
        bool negative = text.StartsWith('-');
        string value = text.TrimStart('+', '-');
        long result = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(value[2..], 16)
            : long.Parse(value);
        return negative
            ? -result
            : result;
    }

    private byte ConditionCode(BranchCondition condition) => condition switch
    {
        BranchCondition.Equal => 0x84,
        BranchCondition.NotEqual => 0x85,
        BranchCondition.Below => 0x82,
        BranchCondition.AboveOrEqual => 0x83,
        BranchCondition.BelowOrEqual => 0x86,
        BranchCondition.Above => 0x87,
        BranchCondition.Less => 0x8C,
        BranchCondition.GreaterOrEqual => 0x8D,
        BranchCondition.LessOrEqual => 0x8E,
        BranchCondition.Greater => 0x8F,
        BranchCondition.Sign => 0x88,
        BranchCondition.NotSign => 0x89,
        BranchCondition.Overflow => 0x80,
        BranchCondition.NotOverflow => 0x81,
        _ => throw new UnsupportedInstructionException(_function.Name, _instruction, $"condition {condition} is not lowered")
    };

    private void Call(string symbol)
    {
        Emit(0xE8);
        _relocations.Add(new(_code.Count, symbol, MachineCodeRelocationKind.Relative32Call));
        Int(0);
    }

    private void Branch(string target)
    {
        _branches.Add((_code.Count, target, _instruction));
        Int(0);
    }

    private void Require(int count)
    {
        if (_instruction.Operands.Count != count)
        {
            Fail($"expected {count} operands");
        }
    }

    private void Fail(string reason) => throw new UnsupportedInstructionException(_function.Name, _instruction, reason);

    private void Emit(params byte[] bytes) => _code.AddRange(bytes);

    private void Int(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _code.AddRange(bytes);
    }
}
