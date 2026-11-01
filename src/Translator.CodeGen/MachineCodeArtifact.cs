using Translator.IR;

namespace Translator.CodeGen;

public enum X64Abi
{
    Windows,
    SystemV
}

public enum MachineCodeRelocationKind
{
    Relative32Call,
    Absolute64
}

public sealed record MachineCodeRelocation(int Offset, string Symbol, MachineCodeRelocationKind Kind, int Addend = 0);
public sealed record MachineCodeArtifact(byte[] Bytes, string Architecture, X64Abi Abi, string CallingConvention, IReadOnlyList<MachineCodeRelocation>? Relocations = null)
{
    public IReadOnlyDictionary<string, int>? NativeLabels
    {
        get; init;
    }
}

public sealed class UnsupportedInstructionException : NotSupportedException
{
    public UnsupportedInstructionException(string functionName, IrInstruction instruction, string reason) : base($"Cannot lower {instruction.OriginalMnemonic ?? instruction.Operation} in function '{functionName}' at {instruction.Source.File}:{instruction.Source.Line}: {reason} Source: {instruction.OriginalText}")
    {
        FunctionName = functionName;
        Operation = instruction.OriginalMnemonic ?? instruction.Operation;
        Location = instruction.Source;
        OriginalText = instruction.OriginalText;
    }

    public string FunctionName
    {
        get;
    }
    public string Operation
    {
        get;
    }
    public SourceLocation Location
    {
        get;
    }
    public string OriginalText
    {
        get;
    }
}
