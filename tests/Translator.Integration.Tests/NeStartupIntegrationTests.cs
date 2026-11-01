using System.Text.Json;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeStartupIntegrationTests
{
    [Fact]
    public void LifecycleRequiresExplicitKernelOwnership()
    {
        Assert.Throws<ArgumentException>(() => new NeWholeProgramControlProgram([], lifecycleOptions: new()));
    }

    [Fact]
    public void LoaderEntryDoesNotPushAProcedureFrameAndUnwindsItsExecutionContextOnExit()
    {
        var memory = new SegmentMemory();
        using var stack = memory.CreateSegment(0x1000, 2048);
        memory.Map(stack);
        var dispatcher = new Win16CallDispatcher();
        var state = new MachineState
        {
            CS = 0x4000,
            IP = 0x1000,
            SS = 0x1000,
            SP = 1024
        };
        memory.WriteDword(state.SS, 1020, 0x87654321);
        var binding = new Win16ProgramBinding(new(0x4000, 0x1000), Win16ProgramAddressKind.Code, "startup", (ref MachineState s) =>
        {
            Assert.Equal((ushort)1024, s.SP);
            Assert.Equal(0x87654321u, memory.ReadDword(s.SS, 1020));
            throw new Win16TaskExitException(9);
        });
        using var control = new Win16ProgramControl([binding], memory, dispatcher);
        Assert.Equal((ushort)9, Assert.Throws<Win16TaskExitException>(() => control.Enter(ref state, binding.Address)).ExitCode);
        Assert.Equal((ushort)1024, state.SP);
        // A second entry proves the suspended context was removed on exception.
        Assert.Throws<Win16TaskExitException>(() => control.Enter(ref state, binding.Address));
    }

    [SuppliedNeFact]
    public void OriginalStartupExercisesLifecycleAndPreservesTheNextUnwiredImportAsABlocker()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var first = NeStartupTraceBuilder.Run(bytes);
        Assert.True(first.LifecycleInitialized);
        Assert.NotEqual((ushort)0, first.PspSelector);
        Assert.Equal(new[] { "KERNEL.#91", "KERNEL.#102", "KERNEL.#30" }, first.LifecycleCalls);
        Assert.Equal("USER.#5", first.NextUnsupportedImport);
        Assert.False(first.OriginalStartupComplete);
        Assert.False(first.ApplicationAccepted);
        Assert.Equal(MachineState.AbiSize * 2, first.InitialCpuHex.Length);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(NeStartupTraceBuilder.Run(bytes)));
    }

    [SuppliedNeFact]
    public void StartupOptionIsExplicitAndRejectsChangedLoaderRegisters()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        using var program = new NeWholeProgramControlProgram(bytes, initializeKernelServices: true);
        Assert.Null(program.LifecycleServices);
        var state = program.InitialState;
        state.SP -= 2;
        Assert.Throws<ArgumentException>(() => program.EnterStartup(ref state));
        state = program.InitialState;
        var failure = Assert.Throws<InvalidOperationException>(() => program.EnterStartup(ref state));
        Assert.Equal("KERNEL.#91", failure.GetBaseException().Data[Win16ProgramControl.UnimplementedImportIdentityKey]);
    }
}
