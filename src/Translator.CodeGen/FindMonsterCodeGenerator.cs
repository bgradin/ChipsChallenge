using System.Runtime.InteropServices;
using Translator.IR;
using Win16_Runtime;

namespace Translator.CodeGen;

/// <summary>Inputs bound by the native FindMonster entry point for segmented data access.</summary>
public sealed class FindMonsterContext
{
    public FindMonsterContext(SegmentMemory memory, ushort dataSegment, ushort gameStatePointerAddress = 0x1680, ushort gameStateMonsterListLengthOffset = 0x0928, ushort gameStateMonsterListPointerOffset = 0x092E, ushort monsterSize = 11, ushort monsterXOffset = 1, ushort monsterYOffset = 3)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        DataSegment = dataSegment;
        GameStatePointerAddress = gameStatePointerAddress;
        GameStateMonsterListLengthOffset = gameStateMonsterListLengthOffset;
        GameStateMonsterListPointerOffset = gameStateMonsterListPointerOffset;
        MonsterSize = monsterSize;
        MonsterXOffset = monsterXOffset;
        MonsterYOffset = monsterYOffset;
    }

    public SegmentMemory Memory
    {
        get;
    }
    public ushort DataSegment
    {
        get;
    }
    public ushort GameStatePointerAddress
    {
        get;
    }
    public ushort GameStateMonsterListLengthOffset
    {
        get;
    }
    public ushort GameStateMonsterListPointerOffset
    {
        get;
    }
    public ushort MonsterSize
    {
        get;
    }
    public ushort MonsterXOffset
    {
        get;
    }
    public ushort MonsterYOffset
    {
        get;
    }
}

/// <summary>Entry backed by the same AMD64 artifact used in standalone executables.</summary>
public delegate short NativeFindMonster(FindMonsterContext context, short x, short y);
/// <summary>Lowers FindMonster instruction IR into a native AMD64 loop.</summary>
public static class FindMonsterCodeGenerator
{
    public const string RuntimeHelperSymbol = "__win16_read_word";

    public static NativeFindMonster Generate(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("FindMonster native lowering currently targets AMD64 hosts.");
        }

        if (!function.Name.Equals("FindMonster", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected the FindMonster function.", nameof(function));
        }

        _ = GenerateArtifact(function, X64Abi.Windows);
        return (context, x, y) =>
        {
            var bindings = new Dictionary<string, int>
            {
                ["GameStatePtr"] = context.GameStatePointerAddress,
                ["MonsterListLen"] = context.GameStateMonsterListLengthOffset,
                ["MonsterListPtr+FarPtr.Off"] = context.GameStateMonsterListPointerOffset,
                ["MonsterListPtr+FarPtr.Seg"] = context.GameStateMonsterListPointerOffset + 2,
                ["Monster.x"] = context.MonsterXOffset,
                ["Monster.y"] = context.MonsterYOffset,
                ["Monster_size"] = context.MonsterSize
            };
            using var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(function, X64Abi.Windows, bindings));
            // Use the same segmented memory and Win16 stack boundary as packaged code.
            var memory = context.Memory;
            ushort stackSegment = 0xFFFE;
            while (memory.TryGetSegment(stackSegment, out _))
            {
                if (--stackSegment == 0)
                {
                    throw new InvalidOperationException("No free selector for the FindMonster stack.");
                }
            }

            memory.Map(new Win16Segment(stackSegment));
            try
            {
                var state = new MachineState
                {
                    DS = context.DataSegment,
                    SS = stackSegment,
                    SP = 0xFF00,
                    CS = 0x3000
                };
                var calls = new Win16CallDispatcher();
                body.Register(calls, memory, "FindMonster", new(0x3000, 0x100), function.EntryDistance);
                Win16Stack.Push(ref state, memory, unchecked((ushort)y));
                Win16Stack.Push(ref state, memory, unchecked((ushort)x));
                calls.Call(ref state, memory, "FindMonster", function.EntryDistance);
                return unchecked((short)state.AX);
            }
            finally
            {
                memory.Unmap(stackSegment);
            }
        };
    }

    /// <summary>Emits native FindMonster control flow with relocations for selector-based word reads.</summary>
    public static MachineCodeArtifact GenerateArtifact(IrFunction function, X64Abi abi)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!function.Name.Equals("FindMonster", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected the FindMonster function.", nameof(function));
        }

        if (abi != X64Abi.Windows)
        {
            throw new NotSupportedException("Relocatable FindMonster artifacts currently use the Windows AMD64 ABI.");
        }

        return new Win16BodyCodeGenerator().Generate(function, abi);
    }
}
