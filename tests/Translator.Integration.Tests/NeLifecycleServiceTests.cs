using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeLifecycleServiceTests
{
    [Fact]
    public void InitTaskOwnsPspAndInstanceDataAndPreservesTheFarReturnFrame()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xcb);
        NeLoaderTests.W(bytes, 0x94, 0);
        NeLoaderTests.W(bytes, 0x90, 512);
        using var image = new NeProgramLoader().Load(bytes);
        var dispatcher = new Win16CallDispatcher();
        using var control = new Win16ProgramControl([], image.Memory, dispatcher);
        using var task = new Win16TaskMemory(image, dispatcher);
        using var kernel = new Win16TaskKernelServices(task, image.Memory, image.InitialState.DS);
        int waited = 0;
        using var lifecycle = new Win16TaskLifecycle(image, control, dispatcher, kernel, () =>
        {
            waited++;
            return true;
        }, "arg", 3);
        var state = image.InitialState;
        ushort old = state.SP;
        state.SP -= 4;
        old = state.SP;
        image.Memory.WriteDword(state.SS, old, 0x12345678);
        var before = state;
        Assert.Equal((ushort)0, lifecycle.Invoke("KERNEL.#91", ref state));
        Assert.Equal((ushort)1, state.AX);
        Assert.Equal((ushort)3, state.DX);
        Assert.Equal(before.DS, state.DI);
        Assert.Equal((ushort)0, state.SI);
        Assert.Equal((ushort)(old - 2), state.SP);
        Assert.Equal(0x12345678u, image.Memory.ReadDword(state.SS, state.SP));
        Assert.Equal((ushort)0, image.Memory.ReadWord(state.SS, (ushort)(old + 2)));
        Assert.Equal((ushort)0x81, state.BX);
        Assert.Equal((byte)3, image.Memory.ReadByte(state.ES, 0x80));
        Assert.Equal((byte)'a', image.Memory.ReadByte(state.ES, 0x81));
        Assert.NotEqual((ushort)0, image.Memory.ReadWord(state.ES, 0x2c));
        ushort psp = state.ES;
        image.Memory.WriteWord(state.SS, (ushort)(state.SP + 4), 0);
        lifecycle.Invoke("KERNEL.#30", ref state);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal(0, waited);
        lifecycle.Invoke("KERNEL.#30", ref state);
        Assert.Equal((ushort)1, state.AX);
        Assert.Equal(1, waited);
        lifecycle.PostEvent();
        lifecycle.Invoke("KERNEL.#30", ref state);
        Assert.Equal((ushort)0, state.AX);
        state.AX = 0x3000;
        lifecycle.Invoke("KERNEL.#102", ref state);
        Assert.Equal((ushort)0x0005, state.AX);
        Assert.Equal((ushort)0xff00, state.BX);
        state.AX = 0x3500;
        lifecycle.Invoke("KERNEL.#102", ref state);
        Assert.Equal((ushort)0, state.ES);
        Assert.Equal((ushort)0, state.BX);
        state.AX = 0x4c07;
        var exit = Assert.Throws<Win16TaskExitException>(() => lifecycle.Invoke("KERNEL.#102", ref state));
        Assert.Equal((ushort)7, exit.ExitCode);
        state.AX = 0x9900;
        Assert.Throws<NotSupportedException>(() => lifecycle.Invoke("KERNEL.#102", ref state));
        lifecycle.Dispose();
        Assert.False(image.Memory.TryGetSegment(psp, out _));
        Assert.Throws<ObjectDisposedException>(() => lifecycle.Invoke("KERNEL.#91", ref state));
    }

    [Fact]
    public void DosVectorAndInterruptFlagsAreExplicitAndFatalApisTerminate()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xcb);
        NeLoaderTests.W(bytes, 0x94, 0);
        NeLoaderTests.W(bytes, 0x90, 512);
        using var p = new NeWholeProgramControlProgram(bytes, initializeKernelServices: true);
        using var image = new NeProgramLoader().Load(bytes);
        var dispatcher = new Win16CallDispatcher();
        using var task = new Win16TaskMemory(image, dispatcher);
        using var kernel = new Win16TaskKernelServices(task, image.Memory, image.InitialState.DS);
        using var control = new Win16ProgramControl(new[] { new Win16ProgramBinding(new(image.InitialState.CS, 0), Win16ProgramAddressKind.Code, "handler", (ref MachineState s) => 0) }, image.Memory, dispatcher);
        using var lifecycle = new Win16TaskLifecycle(image, control, dispatcher, kernel);
        var state = image.InitialState;
        state.AX = 0x2500;
        state.DS = state.CS;
        state.DX = 0;
        lifecycle.Invoke("KERNEL.#102", ref state);
        Assert.Equal(new(state.CS, 0), lifecycle.InterruptVector(0));
        state.AX = 0x3500;
        lifecycle.Invoke("KERNEL.#102", ref state);
        Assert.Equal(state.CS, state.ES);
        state.AX = 0x3000;
        state.Flags |= 1;
        var before = state;
        dispatcher.Interrupt(ref state, image.Memory, 0x21);
        Assert.Equal((ushort)5, state.AX);
        Assert.Equal(before.SP, state.SP);
        Assert.Equal((ushort)(before.Flags & ~1), state.Flags);
        image.Memory.WriteWord(state.SS, (ushort)(state.SP + 4), 9);
        var exit = Assert.Throws<Win16TaskExitException>(() => lifecycle.Invoke("KERNEL.#1", ref state));
        Assert.Equal((ushort)9, exit.ExitCode);
    }
}
