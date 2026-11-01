using System.Numerics;
using Translator.IR;

namespace Win16_Runtime;

/// <summary>Complex CPU operations invoked individually from statically emitted native instructions.</summary>
public static class Win16InstructionServices
{
    public static void String(ref MachineState state, SegmentMemory memory, ushort operation, RepeatPrefix prefix)
    {
        if (operation > 7 || !Enum.IsDefined(prefix))
        {
            throw new NotSupportedException("Unknown string helper operation/prefix.");
        }

        bool repeat = prefix != RepeatPrefix.None;
        int width = operation is 1 or 3 or 7
            ? 2
            : 1;
        while (!repeat || state.CX != 0)
        {
            int step = (state.Flags & 0x400) != 0
                ? -width
                : width;
            switch (operation)
            {
                case 0:
                    state.AX = (ushort)((state.AX & 0xFF00) | memory.ReadByte(state.DS, state.SI));
                    break;
                case 1:
                    state.AX = memory.ReadWord(state.DS, state.SI);
                    break;
                case 2:
                    memory.WriteByte(state.ES, state.DI, memory.ReadByte(state.DS, state.SI));
                    break;
                case 3:
                    memory.WriteWord(state.ES, state.DI, memory.ReadWord(state.DS, state.SI));
                    break;
                case 4:
                    memory.WriteByte(state.ES, state.DI, (byte)state.AX);
                    break;
                case 5:
                case 6:
                case 7:
                    uint left = operation == 5
                        ? (byte)state.AX
                        : width == 1
                            ? memory.ReadByte(state.DS, state.SI)
                            : memory.ReadWord(state.DS, state.SI);
                    uint right = width == 1
                        ? memory.ReadByte(state.ES, state.DI)
                        : memory.ReadWord(state.ES, state.DI);
                    uint mask = width == 1
                        ? 255u
                        : 65535u, sign = width == 1
                        ? 128u
                        : 32768u;
                    uint result = (left - right) & mask;
                    ushort flags = (ushort)((left < right
                        ? 1
                        : 0) | ((BitOperations.PopCount(result & 255) & 1) == 0
                        ? 4
                        : 0) | (((left ^ right ^ result) & 16) != 0
                        ? 16
                        : 0) | (result == 0
                        ? 64
                        : 0) | ((result & sign) != 0
                        ? 128
                        : 0) | (((left ^ right) & (left ^ result) & sign) != 0
                        ? 2048
                        : 0));
                    state.Flags = (ushort)((state.Flags & ~0x8D5) | flags);
                    break;
            }

            if (operation < 4 || operation >= 6)
            {
                state.SI = unchecked((ushort)(state.SI + step));
            }

            if (operation >= 2)
            {
                state.DI = unchecked((ushort)(state.DI + step));
            }

            if (!repeat)
            {
                break;
            }

            state.CX--;
            if (operation >= 5 && ((prefix == RepeatPrefix.Repne) == ((state.Flags & 64) != 0)))
            {
                break;
            }
        }
    }

    public static void Divide(ref MachineState state, uint divisor, ushort form)
    {
        int width = form & 255;
        if (width is not (8 or 16 or 32))
        {
            throw new NotSupportedException("Unsupported division width.");
        }

        ulong mask = width == 32
            ? uint.MaxValue
            : (1UL << width) - 1;
        divisor &= (uint)mask;
        ulong dividend = width switch
        {
            8 => state.AX,
            16 => (uint)state.AX | ((uint)state.DX << 16),
            _ => state.EAX | ((ulong)state.EDX << 32)
        };
        ulong quotient, remainder;
        if ((form & 0x100) == 0)
        {
            if (divisor == 0)
            {
                throw new DivideByZeroException();
            }

            quotient = dividend / divisor;
            remainder = dividend % divisor;
            if (quotient > mask)
            {
                throw new OverflowException("Win16 divide quotient overflow.");
            }
        }
        else
        {
            long signedDividend = width switch
            {
                8 => (short)dividend,
                16 => (int)dividend,
                _ => unchecked((long)dividend)
            };
            long signedDivisor = width switch
            {
                8 => (sbyte)divisor,
                16 => (short)divisor,
                _ => (int)divisor
            };
            if (signedDivisor == 0)
            {
                throw new DivideByZeroException();
            }

            if (signedDividend == long.MinValue && signedDivisor == -1)
            {
                throw new OverflowException("Win16 divide quotient overflow.");
            }

            long q = signedDividend / signedDivisor, r = signedDividend % signedDivisor;
            if (q < -(1L << (width - 1)) || q >= (1L << (width - 1)))
            {
                throw new OverflowException("Win16 divide quotient overflow.");
            }

            quotient = unchecked((ulong)q);
            remainder = unchecked((ulong)r);
        }

        if (width == 8)
        {
            state.AX = (ushort)(((remainder & 255) << 8) | (quotient & 255));
        }
        else if (width == 16)
        {
            state.AX = (ushort)quotient;
            state.DX = (ushort)remainder;
        }
        else
        {
            state.EAX = (uint)quotient;
            state.EDX = (uint)remainder;
        }
        // Division flags are undefined on x86. Preserve them deterministically.
    }
}
