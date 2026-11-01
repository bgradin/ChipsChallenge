using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeInstanceServiceTests
{
    [Fact]
    public void ProcedureInstancesUseApprovedEntriesCaptureAxAndRetireFreedPointers()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0x89, 0x06, 0, 0, 0xcb);
        NeLoaderTests.W(bytes, 0x94, 0);
        using var program = new NeWholeProgramControlProgram(bytes, initializeKernelServices: true);
        program.TaskMemory.GrowDataSegment(65536);
        using var instance = program.Memory.CreateSegment(0x6000, 128);
        program.Memory.Map(instance);
        var state = program.InitialState;
        state.SP = 1024;
        ushort ds = state.DS;

        ushort Call(string identity, params uint[] args)
        {
            var c = Win16ExportContracts.Get(identity);
            foreach (var a in c.Arguments)
            {
                uint value = args[int.Parse(a.Name.AsSpan(3))];
                ushort at = (ushort)(state.SP + 4 + a.StackWord * 2);
                if (a.Bytes == 4)
                {
                    program.Memory.WriteDword(state.SS, at, value);
                }
                else
                {
                    program.Memory.WriteWord(state.SS, at, (ushort)value);
                }
            }

            return program.InstanceServices!.Invoke(identity, ref state);
        }


        uint target = (uint)state.CS << 16;
        Assert.Equal((ushort)6, Call("KERNEL.#51", target, instance.Selector));
        var thunk = new Win16CodeAddress(state.DX, state.AX);
        Assert.NotEqual((ushort)0, thunk.Segment);
        program.Memory.ResizeDataSegment(instance.Selector, 256);
        var before = state;
        program.Control.Invoke(ref state, thunk);
        Assert.Equal(instance.Selector, program.Memory.ReadWord(ds, 0));
        Assert.Equal((ushort)0, program.Memory.ReadWord(instance.Selector, 0));
        Assert.Equal(ds, state.DS);
        Assert.Equal(before.SP, state.SP);
        Assert.Equal(before.CS, state.CS);
        uint pointer = (uint)thunk.Segment << 16 | thunk.Offset;
        Assert.Equal((ushort)4, Call("KERNEL.#52", pointer));
        Assert.Throws<InvalidOperationException>(() => program.Control.Resolve(thunk, callback: true));
        Call("KERNEL.#51", target, instance.Selector);
        var fresh = new Win16CodeAddress(state.DX, state.AX);
        Assert.NotEqual(thunk, fresh);
        Call("KERNEL.#51", 0x77770000, instance.Selector);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal((ushort)0, state.DX);
        Call("KERNEL.#51", target, 0);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal((ushort)0, state.DX);
        program.Memory.Unmap(instance.Selector);
        Assert.Throws<InvalidOperationException>(() => program.Control.Invoke(ref state, fresh));
    }

    [Fact]
    public void BindingLimitsCannotBeBypassedByCreatingInstanceThunks()
    {
        var memory = new SegmentMemory();
        using var data = memory.CreateSegment(1, 128);
        memory.Map(data);
        using var control = new Win16ProgramControl(new[] { new Win16ProgramBinding(new(0x4000, 0), Win16ProgramAddressKind.Code, "compiled", (ref MachineState state) => 0) }, memory, new(), maximumEntries: 1);
        Assert.Equal(default, control.CreateInstanceThunk(new(0x4000, 0), data.Selector));
    }
}
