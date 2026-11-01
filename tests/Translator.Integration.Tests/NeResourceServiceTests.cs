using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeResourceServiceTests
{
    [Fact]
    public void ResourceReferencesDataHandlesAndFarViewsHaveSeparateOwnership()
    {
        byte[] source = NeParserTests.Fixture();
        var memory = new SegmentMemory();
        using var segment = memory.CreateSegment(0x1000, 2048);
        memory.Map(segment);
        using var globals = new Win16GlobalMemoryServices(memory, new(), wholeProgram: true);
        using var resources = new Win16TaskResources(source, memory, globals, segment.Selector);
        var state = new MachineState
        {
            SS = segment.Selector,
            DS = segment.Selector,
            SP = 512,
            DX = 0xabcd
        };

        ushort Call(string id, params uint[] args)
        {
            var c = Win16ExportContracts.Get(id);
            foreach (var a in c.Arguments)
            {
                ushort offset = (ushort)(state.SP + 4 + a.StackWord * 2);
                uint value = args[int.Parse(a.Name.AsSpan(3))];
                if (a.Bytes == 4)
                {
                    memory.WriteDword(state.SS, offset, value);
                }
                else
                {
                    memory.WriteWord(state.SS, offset, (ushort)value);
                }
            }

            return resources.Invoke(id, ref state);
        }


        memory.WriteBytes(segment.Selector, 64, new byte[] { (byte)'p', (byte)'i', (byte)'c', 0 });
        uint name = (uint)segment.Selector << 16 | 64;
        Assert.Equal((ushort)10, Call("KERNEL.#60", 0, name, 2));
        ushort reference = state.AX;
        Assert.NotEqual((ushort)0, reference);
        Assert.Equal((ushort)0xabcd, state.DX);
        Call("KERNEL.#60", 0, name, 3);
        Assert.Equal((ushort)0, state.AX);
        Call("KERNEL.#60", 0x7777, name, 2);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal((ushort)4, Call("KERNEL.#61", 0, reference));
        ushort data = state.AX;
        Assert.NotEqual(reference, data);
        Call("KERNEL.#61", 0, reference);
        Assert.Equal(data, state.AX);
        Assert.Equal(1, globals.AllocationCount);
        Assert.Equal((ushort)2, Call("KERNEL.#62", data));
        ushort selector = state.DX;
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal(source.AsSpan(0x300, 16).ToArray(), memory.GetSegment(selector).Memory.ToArray());
        using var alias = memory.CreateAlias(memory.AllocateSelector(), selector);
        Call("KERNEL.#63", data);
        Assert.Equal((ushort)0, state.AX);
        Assert.True(memory.TryGetSegment(selector, out _));
        Call("KERNEL.#63", data);
        Assert.Equal((ushort)0, state.AX);
        Assert.False(memory.TryGetSegment(selector, out _));
        Assert.True(alias.IsDisposed);
        Assert.Equal(0, globals.AllocationCount);
        Call("KERNEL.#62", data);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal((ushort)0, state.DX);
        Call("KERNEL.#61", 0, 0xffff);
        Assert.Equal((ushort)0, state.AX);
        globals.FailNextAllocation = true;
        Call("KERNEL.#61", 0, reference);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal(0, globals.AllocationCount);
        Call("KERNEL.#61", 0, reference);
        Assert.NotEqual((ushort)0, state.AX);
        resources.Dispose();
        Assert.Equal(0, globals.AllocationCount);
    }
}
