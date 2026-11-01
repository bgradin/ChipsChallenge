using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeSystemServiceTests
{
    [Fact]
    public void VersionsAndPathsUseTheConfiguredTaskEnvironmentAndOriginalWidthResults()
    {
        var memory = new SegmentMemory();
        using var data = memory.CreateSegment(1, 2048);
        memory.Map(data);
        using var service = new Win16TaskSystem(memory, 1, "C:\\APP\\TEST.EXE", "C:\\WINDOWS");
        var state = new MachineState
        {
            SS = 1,
            DS = 1,
            SP = 1024,
            DX = 0x7777
        };

        ushort Call(string identity, params uint[] args)
        {
            var c = Win16ExportContracts.Get(identity);
            foreach (var a in c.Arguments)
            {
                ushort at = (ushort)(state.SP + 4 + a.StackWord * 2);
                uint value = args[int.Parse(a.Name.AsSpan(3))];
                if (a.Bytes == 4)
                {
                    memory.WriteDword(1, at, value);
                }
                else
                {
                    memory.WriteWord(1, at, (ushort)value);
                }
            }

            return service.Invoke(identity, ref state);
        }


        Assert.Equal((ushort)0, Call("KERNEL.#3"));
        Assert.Equal((ushort)0x0a03, state.AX);
        Assert.Equal((ushort)0x0500, state.DX);
        Assert.Equal((ushort)6, Call("KERNEL.#134", 0x10040, 64));
        Assert.Equal((ushort)10, state.AX);
        Assert.Equal("C:\\WINDOWS", Win16TaskKernelServices.ReadString(memory, 0x10040));
        memory.WriteWord(1, 128, 0x7777);
        Call("KERNEL.#134", 0x10080, 2);
        Assert.Equal((ushort)11, state.AX);
        Assert.Equal((ushort)0x7777, memory.ReadWord(1, 128));
        Assert.Equal((ushort)8, Call("KERNEL.#49", 1, 0x10080, 64));
        Assert.Equal((ushort)15, state.AX);
        Assert.Equal("C:\\APP\\TEST.EXE", Win16TaskKernelServices.ReadString(memory, 0x10080));
        Call("KERNEL.#49", 0, 0x10080, 4);
        Assert.Equal((ushort)3, state.AX);
        Assert.Equal("C:\\", Win16TaskKernelServices.ReadString(memory, 0x10080));
        Call("KERNEL.#49", 0x7777, 0, 64);
        Assert.Equal((ushort)0, state.AX);
        Call("KERNEL.#49", 1, 0, 0xffff);
        Assert.Equal((ushort)0, state.AX);
        Assert.Throws<InvalidDataException>(() => Call("KERNEL.#49", 1, 0x107ff, 64));
        service.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Call("KERNEL.#3"));
    }
}
