using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Iced.Intel;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NePackagingUnwindTests
{
    [DllImport("ntdll.dll")]
    private static extern nint RtlVirtualUnwind(uint flags, ulong imageBase, ulong pc, nint function, nint context, out nint handler, out ulong frame, nint pointers);
    [WindowsSuppliedNeFact]
    public void WindowsUnwindsActualPayloadAndTranslatedPrologsAtEveryStackChange()
    {
        string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        var package = NeWholeProgramPackager.Build(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "WEP4UTIL.DLL")));
        using var pe = new PEReader(new MemoryStream(package.Executable));
        var header = pe.PEHeaders.PEHeader!;
        nint mapped = Marshal.AllocHGlobal(header.SizeOfImage), stack = Marshal.AllocHGlobal(65536), context = Marshal.AllocHGlobal(1232);
        try
        {
            foreach (var s in pe.PEHeaders.SectionHeaders)
            {
                Marshal.Copy(package.Executable, s.PointerToRawData, mapped + s.VirtualAddress, s.SizeOfRawData);
            }

            var directory = header.ExceptionTableDirectory;
            int checkedRanges = 0;
            for (int row = 0; row < directory.Size; row += 12)
            {
                nint function = mapped + directory.RelativeVirtualAddress + row;
                int start = Marshal.ReadInt32(function), info = Marshal.ReadInt32(function + 8);
                int prolog = Marshal.ReadByte(mapped + info + 1);
                byte[] code = new byte[prolog];
                Marshal.Copy(mapped + start, code, 0, code.Length);
                var decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
                decoder.IP = 0;
                ulong[] regs = Enumerable.Range(0, 16).Select(i => 0x1122334400000000ul + (uint)i).ToArray();
                ulong initialSp = (ulong)(stack + 60000);
                regs[4] = initialSp;
                var saved = new HashSet<int>();
                const ulong returnAddress = 0x1234567812345678;
                Marshal.WriteInt64((nint)initialSp, unchecked((long)returnAddress));
                Check(0);
                while (decoder.IP < (ulong)prolog)
                {
                    var instruction = decoder.Decode();
                    Assert.NotEqual(Code.INVALID, instruction.Code);
                    if (instruction.Mnemonic == Mnemonic.Push)
                    {
                        Assert.Equal(OpKind.Register, instruction.Op0Kind);
                        int reg = Reg(instruction.Op0Register);
                        saved.Add(reg);
                        regs[4] -= 8;
                        Marshal.WriteInt64((nint)regs[4], unchecked((long)regs[reg]));
                    }
                    else if (instruction.Mnemonic == Mnemonic.Sub && instruction.Op0Register == Register.RSP)
                    {
                        regs[4] -= instruction.GetImmediate(1);
                    }
                    else if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == Register.RSP && instruction.Op1Kind == OpKind.Register)
                    {
                        int reg = Reg(instruction.Op1Register);
                        Assert.InRange(reg, 0, 15);
                        saved.Add(reg);
                        Marshal.WriteInt64((nint)(regs[4] + instruction.MemoryDisplacement64), unchecked((long)regs[reg]));
                    }
                    else if (instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.RSP)
                    {
                        Assert.Fail("Unrepresented native RSP prolog operation: " + instruction);
                    }
                    // Register setup may overwrite saved nonvolatiles. Deliberately
                    // clobber them after their real push/store to test actual restoration.
                    else if (instruction.Op0Kind == OpKind.Register && instruction.Mnemonic is Mnemonic.Mov or Mnemonic.Lea)
                    {
                        int reg = Reg(instruction.Op0Register);
                        if (reg >= 0)
                        {
                            regs[reg] = 0x5566778800000000ul + (uint)reg;
                        }
                    }

                    Check((int)instruction.NextIP);
                }

                checkedRanges++;

                void Check(int offset)
                {
                    Marshal.Copy(new byte[1232], 0, context, 1232);
                    for (int i = 0; i < 16; i++)
                    {
                        Marshal.WriteInt64(context + 120 + i * 8, unchecked((long)regs[i]));
                    }

                    ulong pc = (ulong)mapped + (uint)start + (uint)offset;
                    Marshal.WriteInt64(context + 248, unchecked((long)pc));
                    RtlVirtualUnwind(0, (ulong)mapped, pc, function, context, out _, out _, 0);
                    Assert.Equal(returnAddress, unchecked((ulong)Marshal.ReadInt64(context + 248)));
                    Assert.Equal(initialSp + 8, unchecked((ulong)Marshal.ReadInt64(context + 152)));
                    foreach (int reg in new[]
                    {
                        3,
                        5,
                        6,
                        7,
                        12,
                        13,
                        14,
                        15
                    }

                    )
                    {
                        Assert.True(unchecked((ulong)Marshal.ReadInt64(context + 120 + reg * 8)) == (saved.Contains(reg)
                            ? 0x1122334400000000ul + (uint)reg
                            : regs[reg]), $"Unwind mismatch in range {start:x}, prolog offset {offset}, register {reg}");
                    }
                }

            }

            Assert.Equal(package.Manifest.UnwindRanges, checkedRanges);
            Assert.True(checkedRanges > 15000);
        }
        finally
        {
            Marshal.FreeHGlobal(context);
            Marshal.FreeHGlobal(stack);
            Marshal.FreeHGlobal(mapped);
        }
    }

    private static int Reg(Register r) => r switch
    {
        Register.RAX => 0,
        Register.RCX => 1,
        Register.RDX => 2,
        Register.RBX => 3,
        Register.RSP => 4,
        Register.RBP => 5,
        Register.RSI => 6,
        Register.RDI => 7,
        Register.R8 => 8,
        Register.R9 => 9,
        Register.R10 => 10,
        Register.R11 => 11,
        Register.R12 => 12,
        Register.R13 => 13,
        Register.R14 => 14,
        Register.R15 => 15,
        _ => -1
    };
}
