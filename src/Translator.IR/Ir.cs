namespace Translator.IR;

public enum RegisterWidth
{
    Byte = 8,
    Word = 16,
    Dword = 32
}

public enum X86Register
{
    AX,
    BX,
    CX,
    DX,
    SI,
    DI,
    BP,
    SP,
    CS,
    DS,
    ES,
    SS,
    AL,
    AH,
    BL,
    BH,
    CL,
    CH,
    DL,
    DH,
    EAX,
    EBX,
    ECX,
    EDX,
    ESI,
    EDI,
    EBP,
    ESP
}

public enum FunctionVariant
{
    Default,
    NoDataSegment,
    Crt
}

public enum CallDistance
{
    Unspecified,
    Near,
    Far
}

public enum RepeatPrefix
{
    None,
    Rep,
    Repe,
    Repne
}

public enum BranchCondition
{
    None,
    Equal,
    NotEqual,
    Below,
    AboveOrEqual,
    BelowOrEqual,
    Above,
    Less,
    LessOrEqual,
    GreaterOrEqual,
    Greater,
    Sign,
    NotSign,
    Overflow,
    NotOverflow,
    CounterZero,
    Loop
}

public enum CpuFlag
{
    Carry,
    Parity,
    AuxiliaryCarry,
    Zero,
    Sign,
    Overflow,
    Direction
}

public sealed record MemoryReference(string Expression, RegisterWidth? Width, string? Segment = null, X86Register? Base = null, X86Register? Index = null, int Displacement = 0, string? Symbol = null, bool HasExplicitSegment = false);
public sealed record ControlFlowEdge(string From, string To, string Kind);
public sealed record BasicBlock(string Label, IReadOnlyList<IrInstruction> Instructions, IReadOnlyList<ControlFlowEdge> Successors);
public sealed record SourceLocation(string File, int Line);
public sealed record IrOperand(string Kind, string Text, RegisterWidth? Width = null, MemoryReference? Memory = null, X86Register? Register = null, long? ImmediateValue = null);
public sealed record InstructionSemantics(RegisterWidth? OperandWidth, IReadOnlyList<CpuFlag> FlagsRead, IReadOnlyList<CpuFlag> FlagsWritten)
{
    public IReadOnlyList<CpuFlag> UndefinedFlags { get; init; } = Array.Empty<CpuFlag>();
    public string? ConditionalEffects
    {
        get; init;
    }
}

public sealed record BinaryInstructionSource(int SegmentIndex, ushort Offset, string BytesHex, string Opcode, string PrefixesHex, IReadOnlyList<string> OperandKinds);
public enum NativeControlEffect : ushort
{
    NearJump,
    FarJump,
    NearCall,
    FarCall,
    NearReturn,
    FarReturn
}

public sealed record IrInstruction(string Operation, IReadOnlyList<IrOperand> Operands, SourceLocation Source, string OriginalText, uint? Address = null, int? Size = null, InstructionSemantics? Semantics = null, string? OriginalMnemonic = null, CallDistance CallDistance = CallDistance.Unspecified, ushort? ReturnCleanup = null, RepeatPrefix Prefix = RepeatPrefix.None, BranchCondition Condition = BranchCondition.None)
{
    public NativeControlEffect? ControlEffect
    {
        get; init;
    }
    public BinaryInstructionSource? BinarySource
    {
        get; init;
    }
    public IrIndirectTransfer? IndirectTransfer
    {
        get; init;
    }
}

public sealed record IrIndirectTarget(int SegmentIndex, ushort Offset, string? Label, string? Function);
public sealed record IrIndirectTransfer(CallDistance Distance, IReadOnlyList<IrIndirectTarget> Targets, string Evidence, ushort? TableOffset = null, int? TableEntries = null);
public sealed record IrArgument(string Name, RegisterWidth Width, int Offset);
public sealed record IrFunction(string Name, IReadOnlyList<IrInstruction> Instructions, IReadOnlyList<BasicBlock>? BasicBlocks = null, FunctionVariant Variant = FunctionVariant.Default, SourceLocation? Start = null, SourceLocation? End = null, IReadOnlyList<IrArgument>? Arguments = null, string? Segment = null, CallDistance EntryDistance = CallDistance.Far, bool SavesDataSegment = true, ushort? CleanupBytes = null)
{
    public bool ExplicitFrame
    {
        get; init;
    }
    public int? OriginalSegmentIndex
    {
        get; init;
    }
    public ushort? OriginalOffset
    {
        get; init;
    }
    public bool CaptureNativeLabels
    {
        get; init;
    }
}

public sealed record IrModule(string SourceFile, IReadOnlyList<IrFunction> Functions, IReadOnlyList<string>? Externals = null, IReadOnlyList<string>? Segments = null);
