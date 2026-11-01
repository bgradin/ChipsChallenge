using Translator.IR;

namespace Translator.Core;

public sealed record CpuFlags(bool CF, bool PF, bool AF, bool ZF, bool SF, bool OF);
public sealed record TranslationDiagnostic(string Code, string Stage, string Message, SourceLocation? Source = null, string? OriginalText = null);
/// <summary>
/// 16-bit arithmetic policy: logically undefined AF is cleared, and undefined flags from MUL/IMUL
/// and multi-bit shifts retain their incoming values. This makes differential runs deterministic
/// without promising behavior the processor did not define.
/// </summary>
public static class X86Semantics
{
    public static (ushort Result, CpuFlags Flags) Add16(ushort left, ushort right) => AddWithCarry16(left, right, false);
    public static (ushort Result, CpuFlags Flags) AddWithCarry16(ushort left, ushort right, bool carryIn)
    {
        uint wide = (uint)left + right + (carryIn
            ? 1u
            : 0u);
        ushort result = unchecked((ushort)wide);
        long signed = (short)left + (short)right + (carryIn
            ? 1
            : 0);
        bool overflow = signed < short.MinValue || signed > short.MaxValue;
        bool auxiliary = (left & 0xF) + (right & 0xF) + (carryIn
            ? 1
            : 0) > 0xF;
        return (result, ArithmeticFlags(result, wide > ushort.MaxValue, auxiliary, overflow));
    }

    public static (ushort Result, CpuFlags Flags) Sub16(ushort left, ushort right) => SubtractWithBorrow16(left, right, false);
    public static (ushort Result, CpuFlags Flags) SubtractWithBorrow16(ushort left, ushort right, bool borrowIn)
    {
        ushort result = unchecked((ushort)(left - right - (borrowIn
            ? 1
            : 0)));
        long signed = (short)left - (short)right - (borrowIn
            ? 1
            : 0);
        bool borrow = (uint)left < (uint)right + (borrowIn
            ? 1u
            : 0u);
        bool auxiliary = (left & 0xF) < (right & 0xF) + (borrowIn
            ? 1
            : 0);
        return (result, ArithmeticFlags(result, borrow, auxiliary, signed < short.MinValue || signed > short.MaxValue));
    }

    public static (ushort Result, CpuFlags Flags) Increment16(ushort value, CpuFlags previous)
    {
        ushort result = unchecked((ushort)(value + 1));
        return (result, ArithmeticFlags(result, previous.CF, (value & 0xF) == 0xF, value == 0x7FFF));
    }

    public static (ushort Result, CpuFlags Flags) Decrement16(ushort value, CpuFlags previous)
    {
        ushort result = unchecked((ushort)(value - 1));
        return (result, ArithmeticFlags(result, previous.CF, (value & 0xF) == 0, value == 0x8000));
    }

    public static (ushort Result, CpuFlags Flags) Negate16(ushort value) => (unchecked((ushort)-value), ArithmeticFlags(unchecked((ushort)-value), value != 0, (value & 0xF) != 0, value == 0x8000));

    public static ushort Not16(ushort value) => (ushort)~value;

    public static (ushort Result, CpuFlags Flags) And16(ushort left, ushort right) => Logical((ushort)(left & right));

    public static (ushort Result, CpuFlags Flags) Or16(ushort left, ushort right) => Logical((ushort)(left | right));

    public static (ushort Result, CpuFlags Flags) Xor16(ushort left, ushort right) => Logical((ushort)(left ^ right));

    /// <summary>Unsigned one-operand MUL. Undefined flags retain their prior deterministic values.</summary>
    public static (ushort Low, ushort High, CpuFlags Flags) MultiplyUnsigned16(ushort accumulator, ushort operand, CpuFlags previous)
    {
        uint product = (uint)accumulator * operand;
        bool overflow = (product >> 16) != 0;
        return ((ushort)product, (ushort)(product >> 16), previous with
        {
            CF = overflow,
            OF = overflow
        });
    }

    /// <summary>Signed one-operand IMUL. Undefined flags retain their prior deterministic values.</summary>
    public static (ushort Low, ushort High, CpuFlags Flags) MultiplySigned16(ushort accumulator, ushort operand, CpuFlags previous)
    {
        int product = (short)accumulator * (short)operand;
        ushort low = unchecked((ushort)product), high = unchecked((ushort)(product >> 16));
        bool overflow = product < short.MinValue || product > short.MaxValue;
        return (low, high, previous with
        {
            CF = overflow,
            OF = overflow
        });
    }

    public static (ushort Quotient, ushort Remainder) DivideUnsigned16(uint dividend, ushort divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException("Win16 DIV divisor is zero.");
        }

        uint quotient = dividend / divisor;
        if (quotient > ushort.MaxValue)
        {
            throw new OverflowException("Win16 DIV quotient does not fit in AX.");
        }

        return ((ushort)quotient, (ushort)(dividend % divisor));
    }

    public static (ushort Quotient, ushort Remainder) DivideSigned16(int dividend, short divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException("Win16 IDIV divisor is zero.");
        }

        int quotient = dividend / divisor;
        if (quotient < short.MinValue || quotient > short.MaxValue)
        {
            throw new OverflowException("Win16 IDIV quotient does not fit in AX.");
        }

        int remainder = dividend % divisor;
        return (unchecked((ushort)(short)quotient), unchecked((ushort)(short)remainder));
    }

    public static short Cwd16(ushort ax) => (short)ax < 0
        ? (short)-1
        : (short)0;

    public static ushort MergeFlagsIntoWord(ushort previousWord, CpuFlags flags)
    {
        const ushort mask = (1 << 0) | (1 << 2) | (1 << 4) | (1 << 6) | (1 << 7) | (1 << 11);
        ushort result = (ushort)(previousWord & ~mask);
        if (flags.CF)
        {
            result |= 1 << 0;
        }

        if (flags.PF)
        {
            result |= 1 << 2;
        }

        if (flags.AF)
        {
            result |= 1 << 4;
        }

        if (flags.ZF)
        {
            result |= 1 << 6;
        }

        if (flags.SF)
        {
            result |= 1 << 7;
        }

        if (flags.OF)
        {
            result |= 1 << 11;
        }

        return result;
    }

    public static CpuFlags ReadFlagsFromWord(ushort word) => new(CF: (word & (1 << 0)) != 0, PF: (word & (1 << 2)) != 0, AF: (word & (1 << 4)) != 0, ZF: (word & (1 << 6)) != 0, SF: (word & (1 << 7)) != 0, OF: (word & (1 << 11)) != 0);

    public static (ushort Result, CpuFlags Flags) ShiftLeft16(ushort value, int count, CpuFlags previous) => Shift16(value, count, previous, ShiftKind.Left);

    public static (ushort Result, CpuFlags Flags) ShiftRight16(ushort value, int count, CpuFlags previous) => Shift16(value, count, previous, ShiftKind.LogicalRight);

    public static (ushort Result, CpuFlags Flags) ShiftArithmeticRight16(ushort value, int count, CpuFlags previous) => Shift16(value, count, previous, ShiftKind.ArithmeticRight);
    public static (ushort Result, CpuFlags Flags) RotateCarryLeft16(ushort value, int count, CpuFlags previous)
    {
        int effectiveCount = count & 0x1F;
        if (effectiveCount == 0)
        {
            return (value, previous);
        }

        ushort result = value;
        bool carry = previous.CF;
        for (int i = 0; i < effectiveCount; i++)
        {
            bool nextCarry = (result & 0x8000) != 0;
            result = unchecked((ushort)((result << 1) | (carry
                ? 1
                : 0)));
            carry = nextCarry;
        }

        bool overflow = effectiveCount == 1
            ? ((result & 0x8000) != 0) ^ carry
            : previous.OF;
        return (result, previous with
        {
            CF = carry,
            OF = overflow
        });
    }

    public static bool EvaluateCondition(BranchCondition condition, CpuFlags flags, ushort cx = 0) => condition switch
    {
        BranchCondition.Equal => flags.ZF,
        BranchCondition.NotEqual => !flags.ZF,
        BranchCondition.Below => flags.CF,
        BranchCondition.AboveOrEqual => !flags.CF,
        BranchCondition.BelowOrEqual => flags.CF || flags.ZF,
        BranchCondition.Above => !flags.CF && !flags.ZF,
        BranchCondition.Less => flags.SF != flags.OF,
        BranchCondition.LessOrEqual => flags.ZF || flags.SF != flags.OF,
        BranchCondition.GreaterOrEqual => flags.SF == flags.OF,
        BranchCondition.Greater => !flags.ZF && flags.SF == flags.OF,
        BranchCondition.Sign => flags.SF,
        BranchCondition.NotSign => !flags.SF,
        BranchCondition.Overflow => flags.OF,
        BranchCondition.NotOverflow => !flags.OF,
        BranchCondition.CounterZero => cx == 0,
        BranchCondition.Loop => throw new ArgumentException("LOOP decrements CX; use ExecuteLoop instead of a read-only condition check.", nameof(condition)),
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Not a conditional branch.")
    };

    public static bool ExecuteLoop(ref ushort cx)
    {
        cx = unchecked((ushort)(cx - 1));
        return cx != 0;
    }

    public static bool ExecuteJcxz(ushort cx) => cx == 0;

    public static CpuFlags ClearCarry(CpuFlags previous) => previous with
    {
        CF = false
    };

    public static CpuFlags SetCarry(CpuFlags previous) => previous with
    {
        CF = true
    };

    public static (T Left, T Right) Exchange<T>(T left, T right) => (right, left);
    private enum ShiftKind
    {
        Left,
        LogicalRight,
        ArithmeticRight
    }

    private static (ushort Result, CpuFlags Flags) Shift16(ushort value, int count, CpuFlags previous, ShiftKind kind)
    {
        int effectiveCount = count & 0x1F;
        if (effectiveCount == 0)
        {
            return (value, previous);
        }

        ushort result = value;
        bool carry = previous.CF;
        for (int i = 0; i < effectiveCount; i++)
        {
            if (kind == ShiftKind.Left)
            {
                carry = (result & 0x8000) != 0;
                result = unchecked((ushort)(result << 1));
            }
            else
            {
                carry = (result & 1) != 0;
                result = kind == ShiftKind.LogicalRight
                    ? (ushort)(result >> 1)
                    : unchecked((ushort)((short)result >> 1));
            }
        }

        bool overflow = effectiveCount == 1
            ? kind switch
            {
                ShiftKind.Left => ((result & 0x8000) != 0) ^ carry,
                ShiftKind.LogicalRight => (value & 0x8000) != 0,
                ShiftKind.ArithmeticRight => false,
                _ => previous.OF
            }
            : previous.OF;
        var arithmetic = ArithmeticFlags(result, carry, previous.AF, overflow);
        return (result, arithmetic);
    }

    private static (ushort Result, CpuFlags Flags) Logical(ushort result) => (result, ArithmeticFlags(result, carry: false, auxiliary: false, overflow: false));

    private static CpuFlags ArithmeticFlags(ushort result, bool carry, bool auxiliary, bool overflow)
    {
        byte low = (byte)result;
        bool parity = (System.Numerics.BitOperations.PopCount((uint)low) & 1) == 0;
        return new(carry, parity, auxiliary, result == 0, (result & 0x8000) != 0, overflow);
    }
}
