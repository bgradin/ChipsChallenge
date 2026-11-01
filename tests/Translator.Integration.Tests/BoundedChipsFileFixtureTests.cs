using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class BoundedChipsFileFixtureTests
{
    [Fact]
    public void ReadOnlyFileContractsHandleReadsSeeksCloseAndPreservePascalStack()
    {
        var memory = new SegmentMemory();
        using var data = memory.CreateSegment(0x6000, 4096);
        memory.Map(data);
        var dispatcher = new Win16CallDispatcher();
        var files = new BoundedChipsFileFixture([1, 2, 3, 4]);
        files.Register(dispatcher);
        memory.WriteBytes(0x6000, 0x100, "CHIPS.DAT\0"u8);

        uint Invoke(string name, params ushort[] args)
        {
            var state = new MachineState
            {
                SS = 0x6000,
                DS = 0x6000,
                SP = 0xf00
            };
            foreach (var arg in args.Reverse())
            {
                Win16Stack.Push(ref state, memory, arg);
            }

            dispatcher.Call(ref state, memory, "KERNEL." + name, CallDistance.Far);
            Assert.Equal(0xf00, state.SP);
            return (uint)state.DX << 16 | state.AX;
        }


        Assert.Equal(1u, Invoke("OpenFile", 0x20, 0x200, 0x6000, 0x100, 0x6000));
        Assert.True(files.IsOpen);
        Assert.Equal(3u, Invoke("_lread", 3, 0x300, 0x6000, 1));
        Assert.Equal(new byte[] { 1, 2, 3 }, memory.ReadBytes(0x6000, 0x300, 3));
        Assert.Equal(1u, Invoke("_llseek", 1, 0xfffe, 0xffff, 1));
        Assert.Equal(3u, Invoke("_lread", 8, 0x300, 0x6000, 1));
        Assert.Equal(uint.MaxValue, Invoke("_llseek", 0, 0xffff, 0xffff, 1));
        Assert.Equal(0u, Invoke("_lclose", 1));
        Assert.False(files.IsOpen);
        Assert.Equal(0xffffu, Invoke("_lread", 1, 0x300, 0x6000, 1));
    }

    [Fact]
    public void AnsiStringCopyPreservesEncryptedHighBytesAndRejectsUnterminatedInput()
    {
        var memory = new SegmentMemory();
        using var data = memory.CreateSegment(0x6000, 4096);
        memory.Map(data);
        var dispatcher = new Win16CallDispatcher();
        new BoundedChipsFileFixture([]).Register(dispatcher);
        byte[] text = [0xdb, 0xdd, 0xd1, 0xc9, 0];
        memory.WriteBytes(0x6000, 0x100, text);
        var state = new MachineState
        {
            SS = 0x6000,
            SP = 0xf00
        };
        foreach (ushort arg in new ushort[]
        {
            0x6000,
            0x200,
            0x6000,
            0x100
        }

        )
        {
            Win16Stack.Push(ref state, memory, arg);
        }

        dispatcher.Call(ref state, memory, "KERNEL.lstrcpy", CallDistance.Far);
        Assert.Equal(text, memory.ReadBytes(0x6000, 0x200, text.Length));
        Assert.Equal(0x200, state.AX);
        Assert.Equal(0x6000, state.DX);
        Assert.Equal(0xf00, state.SP);
        memory.WriteBytes(0x6000, 0x100, Enumerable.Repeat((byte)65, 256).ToArray());
        state.SP = 0xf00;
        foreach (ushort arg in new ushort[]
        {
            0x6000,
            0x200,
            0x6000,
            0x100
        }

        )
        {
            Win16Stack.Push(ref state, memory, arg);
        }

        Assert.Throws<InvalidDataException>(() => dispatcher.Call(ref state, memory, "KERNEL.lstrcpy", CallDistance.Far));
    }
}
