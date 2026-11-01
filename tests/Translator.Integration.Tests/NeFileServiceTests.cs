using System.Text;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeFileServiceTests
{
    [Fact]
    public void ActualFilesUseIndependentHandlesBoundsAndSignedSeekWithDosFailures()
    {
        string directory = Path.Combine(Path.GetTempPath(), "p7f-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "DATA.BIN"), new byte[] { 1, 2, 3, 4, 5 });
            var memory = new SegmentMemory();
            using var data = memory.CreateSegment(1, 4096);
            memory.Map(data);
            var state = new MachineState
            {
                SS = 1,
                DS = 1,
                SP = 1024,
                DX = 0xabcd
            };
            using var files = new Win16TaskFiles(memory, directory);

            ushort Call(string identity, params uint[] args)
            {
                var contract = Win16ExportContracts.Get(identity);
                foreach (var a in contract.Arguments)
                {
                    ushort offset = (ushort)(state.SP + 4 + a.StackWord * 2);
                    uint value = args[int.Parse(a.Name.AsSpan(3))];
                    if (a.Bytes == 4)
                    {
                        memory.WriteDword(1, offset, value);
                    }
                    else
                    {
                        memory.WriteWord(1, offset, (ushort)value);
                    }
                }

                return files.Invoke(identity, ref state);
            }


            uint name = 0x10040, of = 0x10100, buffer = 0x10200;
            memory.WriteBytes(1, 64, Encoding.ASCII.GetBytes("DATA.BIN\0"));
            Assert.Equal((ushort)10, Call("KERNEL.#74", name, of, 0x20));
            ushort handle = state.AX;
            Assert.NotEqual((ushort)0xffff, handle);
            Assert.Equal((byte)136, memory.ReadByte(1, 256));
            Assert.Equal((ushort)0, memory.ReadWord(1, 258));
            Call("KERNEL.#74", name, of, 0x20);
            ushort second = state.AX;
            Assert.NotEqual(handle, second);
            Assert.Equal(2, files.OpenCount);
            Assert.Equal((ushort)8, Call("KERNEL.#82", handle, buffer, 3));
            Assert.Equal((ushort)3, state.AX);
            Assert.Equal(new byte[] { 1, 2, 3 }, memory.ReadBytes(1, 512, 3));
            Call("KERNEL.#82", second, buffer, 2);
            Assert.Equal(new byte[] { 1, 2 }, memory.ReadBytes(1, 512, 2));
            Assert.Equal((ushort)8, Call("KERNEL.#84", handle, unchecked((uint)-1), 1));
            Assert.Equal((ushort)2, state.AX);
            Assert.Equal((ushort)0, state.DX);
            Call("KERNEL.#82", handle, buffer, 10);
            Assert.Equal((ushort)3, state.AX);
            Assert.Equal(new byte[] { 3, 4, 5 }, memory.ReadBytes(1, 512, 3));
            Call("KERNEL.#84", handle, 100, 0);
            Assert.Equal((ushort)100, state.AX);
            Call("KERNEL.#82", handle, buffer, 10);
            Assert.Equal((ushort)0, state.AX);
            Call("KERNEL.#84", handle, unchecked((uint)-1), 0);
            Assert.Equal((ushort)0xffff, state.AX);
            Assert.Equal((ushort)0xffff, state.DX);
            Call("KERNEL.#84", handle, 0, 0);
            Assert.Throws<InvalidDataException>(() => Call("KERNEL.#82", handle, 0x10fff, 10));
            Call("KERNEL.#82", handle, buffer, 1);
            Assert.Equal((byte)1, memory.ReadByte(1, 512));
            Assert.Equal((ushort)2, Call("KERNEL.#81", handle));
            Assert.Equal((ushort)0, state.AX);
            Call("KERNEL.#81", handle);
            Assert.Equal((ushort)0xffff, state.AX);
            memory.WriteBytes(1, 64, Encoding.ASCII.GetBytes("MISSING.BIN\0"));
            Call("KERNEL.#74", name, of, 0x20);
            Assert.Equal((ushort)0xffff, state.AX);
            Assert.Equal((ushort)2, memory.ReadWord(1, 258));
            Call("KERNEL.#74", name, of, 1);
            Assert.Equal((ushort)0xffff, state.AX);
            Assert.Equal((ushort)5, memory.ReadWord(1, 258));
            files.Dispose();
            Assert.Equal(0, files.OpenCount);
            Assert.Throws<ObjectDisposedException>(() => Call("KERNEL.#81", second));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
