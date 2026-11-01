using System.Runtime.InteropServices;
using Translator.IR;

namespace Translator.CodeGen;

/// <summary>Emits an executable AMD64 version of the documented TurnLeft function.</summary>
public sealed class TurnLeftCodeGenerator
{
    public MachineCodeArtifact Generate(IrFunction function, X64Abi abi)
    {
        ArgumentNullException.ThrowIfNull(function);
        var b = new List<byte>();
        // Keep output pointers before AX/y lowering uses RDX on System V,
        // including examples that do not reserve a local stack frame.
        if (abi == X64Abi.SystemV)
        {
            b.AddRange(new byte[] { 0x49, 0x89, 0xD0, 0x49, 0x89, 0xC9 });
        }

        var jumps = new List<(int Displacement, string Target)>();
        var labels = new Dictionary<string, int>();
        bool hasStackFrame = false;
        for (int index = 0; index < function.Instructions.Count; index++)
        {
            IrInstruction instruction = function.Instructions[index];
            string operation = instruction.Operation.ToUpperInvariant();
            if (instruction.Prefix != RepeatPrefix.None)
            {
                throw new UnsupportedInstructionException(function.Name, instruction, $"prefix {instruction.Prefix} has no AMD64 lowering in this generator");
            }

            if (instruction.Operands.Any(operand => operand.Memory?.HasExplicitSegment == true))
            {
                throw new UnsupportedInstructionException(function.Name, instruction, "explicit segment override has no AMD64 lowering in this generator");
            }

            switch (operation)
            {
                case "LABEL":
                    labels[Operand(instruction, 0).Text] = b.Count;
                    break;
                case "SUB" when IsRegister(instruction, 0, "SP") && IsImmediate(instruction, 1, 2) && index == 0:
                    // The source reserves a two-byte local frame. The translated body has no stack-relative locals,
                    // so reserve an ABI-aligned eight-byte frame and restore it before returning.
                    b.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x08 });
                    hasStackFrame = true;
                    break;
                case "MOV" when IsRegister(instruction, 0, "AX") && IsMemory(instruction, 1, "x"):
                    b.AddRange(abi == X64Abi.Windows
                        ? new byte[] { 0x0F, 0xBF, 0xC1 } // movsx eax, cx
                        : new byte[] { 0x0F, 0xBF, 0xC7 }); // movsx eax, di
                    break;
                case "SHL" when IsRegister(instruction, 0, "AX") && IsImmediate(instruction, 1, 1):
                    b.AddRange(new byte[] { 0x66, 0xD1, 0xE0 });
                    break;
                case "ADD" when IsRegister(instruction, 0, "AX") && IsMemory(instruction, 1, "y"):
                    b.AddRange(abi == X64Abi.Windows
                        ? new byte[] { 0x0F, 0xBF, 0xD2 } // movsx edx, dx
                        : new byte[] { 0x0F, 0xBF, 0xD6 }); // movsx edx, si
                    b.AddRange(new byte[] { 0x66, 0x01, 0xD0 }); // add ax, dx
                    break;
                case "CMP" when IsRegister(instruction, 0, "AX") && TryImmediate(instruction, 1, out short comparison):
                    b.AddRange(new byte[] { 0x66, 0x3D }); // cmp ax, imm16
                    b.AddRange(BitConverter.GetBytes(comparison));
                    break;
                case "INC" when IsRegister(instruction, 0, "AX"):
                    b.AddRange(new byte[] { 0x66, 0xFF, 0xC0 });
                    break;
                case "DEC" when IsRegister(instruction, 0, "AX"):
                    b.AddRange(new byte[] { 0x66, 0xFF, 0xC8 });
                    break;
                case "JZ" or "JE":
                    AddRelativeJump(new byte[] { 0x0F, 0x84 }, Operand(instruction, 0).Text);
                    break;
                case "JMP":
                    AddRelativeJump(new byte[] { 0xE9 }, Operand(instruction, 0).Text);
                    break;
                case "MOV" when IsRegister(instruction, 0, "BX") && IsMemory(instruction, 1, "xOut"):
                    b.AddRange(new byte[] { 0x4D, 0x89, 0xC2 }); // mov r10, r8
                    break;
                case "MOV" when IsRegister(instruction, 0, "BX") && IsMemory(instruction, 1, "yOut"):
                    b.AddRange(new byte[] { 0x4D, 0x89, 0xCA }); // mov r10, r9
                    break;
                case "MOV" when IsMemory(instruction, 0, "bx") && TryImmediate(instruction, 1, out short value):
                    b.AddRange(new byte[] { 0x66, 0x41, 0xC7, 0x02 }); // mov word [r10], imm16
                    b.AddRange(BitConverter.GetBytes(value));
                    break;
                case "NOP":
                    if (instruction.Operands.Count != 0)
                    {
                        throw new UnsupportedInstructionException(function.Name, instruction, "NOP must not have operands");
                    }

                    b.Add(0x90);
                    break;
                default:
                    throw new UnsupportedInstructionException(function.Name, instruction, "no AMD64 lowering is registered for this instruction form");
            }
        }

        if (hasStackFrame)
        {
            b.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x08 });
        }

        b.Add(0xC3);
        foreach (var j in jumps)
        {
            if (!labels.TryGetValue(j.Target, out int target))
            {
                throw new InvalidDataException($"Branch target '{j.Target}' was not defined in function '{function.Name}'.");
            }

            int rel = target - (j.Displacement + 4);
            var bytes = BitConverter.GetBytes(rel);
            for (int i = 0; i < 4; i++)
            {
                b[j.Displacement + i] = bytes[i];
            }
        }

        return new(b.ToArray(), "AMD64", abi, "void TurnLeft(short x, short y, short* xOut, short* yOut)");

        void AddRelativeJump(byte[] opcode, string target)
        {
            b.AddRange(opcode);
            int p = b.Count;
            b.AddRange(new byte[4]);
            jumps.Add((p, target));
        }

    }

    private static IrOperand Operand(IrInstruction instruction, int index) => index < instruction.Operands.Count
        ? instruction.Operands[index]
        : throw new InvalidDataException($"{instruction.Operation} is missing operand {index + 1}.");

    private static bool IsRegister(IrInstruction instruction, int index, string name) => index < instruction.Operands.Count && instruction.Operands[index].Kind == "Register" && instruction.Operands[index].Text.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsMemory(IrInstruction instruction, int index, string expression) => index < instruction.Operands.Count && instruction.Operands[index].Kind == "Memory" && instruction.Operands[index].Text.Trim('[', ']').Equals(expression, StringComparison.OrdinalIgnoreCase);

    private static bool IsImmediate(IrInstruction instruction, int index, short expected) => TryImmediate(instruction, index, out short value) && value == expected;

    private static bool TryImmediate(IrInstruction instruction, int index, out short value)
    {
        value = 0;
        if (index >= instruction.Operands.Count || instruction.Operands[index].Kind != "Immediate")
        {
            return false;
        }

        string text = instruction.Operands[index].Text.Trim();
        bool negative = text.StartsWith('-');
        text = text.TrimStart('+', '-');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!short.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
        }
        else if (text.EndsWith('h') && short.TryParse(text[..^1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
        }
        else if (!short.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (negative)
        {
            value = unchecked((short)-value);
        }

        return true;
    }

    public (short X, short Y) Execute(MachineCodeArtifact artifact, short x, short y)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("TurnLeft execution requires an x64 process.");
        }

        if (OperatingSystem.IsWindows() != (artifact.Abi == X64Abi.Windows))
        {
            throw new PlatformNotSupportedException("Generated ABI must match the host process ABI.");
        }

        IntPtr code = ExecutableMemory.Allocate(artifact.Bytes), output = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt16(output, 0, unchecked((short)0x5555));
            Marshal.WriteInt16(output, 2, unchecked((short)0x5555));
            Marshal.GetDelegateForFunctionPointer<TurnLeftDelegate>(code)(x, y, output, IntPtr.Add(output, 2));
            return (Marshal.ReadInt16(output), Marshal.ReadInt16(output, 2));
        }
        finally
        {
            ExecutableMemory.Free(code, artifact.Bytes.Length);
            Marshal.FreeHGlobal(output);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void TurnLeftDelegate(short x, short y, IntPtr xOut, IntPtr yOut);
}

internal static class ExecutableMemory
{
    private const int _pageReadWrite = 0x04;
    private const int _pageExecuteRead = 0x20;
    private const int _memCommit = 0x1000;
    private const int _memReserve = 0x2000;
    private const int _protRead = 0x1;
    private const int _protWrite = 0x2;
    private const int _protExecute = 0x4;

    public static IntPtr Allocate(byte[] bytes)
    {
        IntPtr p;
        if (OperatingSystem.IsWindows())
        {
            p = VirtualAlloc(IntPtr.Zero, (UIntPtr)bytes.Length, _memCommit | _memReserve, _pageReadWrite);
            if (p == IntPtr.Zero)
            {
                throw new InvalidOperationException("VirtualAlloc failed.");
            }

            Marshal.Copy(bytes, 0, p, bytes.Length);
            if (!VirtualProtect(p, (UIntPtr)bytes.Length, _pageExecuteRead, out _))
            {
                VirtualFree(p, UIntPtr.Zero, 0x8000);
                throw new InvalidOperationException("VirtualProtect failed.");
            }
        }
        else
        {
            p = mmap(IntPtr.Zero, (UIntPtr)bytes.Length, _protRead | _protWrite, 0x22, -1, IntPtr.Zero);
            if (p == new IntPtr(-1))
            {
                throw new InvalidOperationException("mmap failed.");
            }

            Marshal.Copy(bytes, 0, p, bytes.Length);
            if (mprotect(p, (UIntPtr)bytes.Length, _protRead | _protExecute) != 0)
            {
                munmap(p, (UIntPtr)bytes.Length);
                throw new InvalidOperationException("mprotect failed.");
            }
        }

        return p;
    }

    public static void Free(IntPtr p, int size)
    {
        if (OperatingSystem.IsWindows())
        {
            VirtualFree(p, UIntPtr.Zero, 0x8000);
        }
        else
        {
            munmap(p, (UIntPtr)size);
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr address, UIntPtr length, int prot, int flags, int fd, IntPtr offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr address, UIntPtr length, int prot);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr address, UIntPtr length);
}
