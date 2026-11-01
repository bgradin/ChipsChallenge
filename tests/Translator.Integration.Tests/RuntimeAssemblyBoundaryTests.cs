using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class RuntimeAssemblyBoundaryTests
{
    [Fact]
    public void ProductionRuntimeDoesNotContainAcceptancePayloadsOrCheckpointContracts()
    {
        var runtime = typeof(MachineState).Assembly;
        Assert.Same(runtime, typeof(NativeWholeTaskRuntime).Assembly);
        foreach (var fixture in new[]
        {
            typeof(NativeAcceptanceRuntime),
            typeof(NativeApplicationRuntime),
            typeof(NativeGraphicsRuntime),
            typeof(NativeInputRuntime),
            typeof(NativeNeRuntime),
            typeof(NativePaintRuntime),
            typeof(NativeSceneRuntime),
            typeof(NativeWindowRuntime),
            typeof(NativeWindowsCallbackRuntime),
            typeof(BoundedChipsFileFixture),
            typeof(Win16WindowContracts)
        }

        )
        {
            Assert.NotSame(runtime, fixture.Assembly);
            Assert.Null(runtime.GetType(fixture.FullName!));
        }

        Assert.DoesNotContain(runtime.GetManifestResourceNames(), name => name.Contains("Win16WindowApis") || name.Contains("Win16SceneApis") || name.Contains("Win16PaintApis") || name.Contains("Win16InputApis") || name.Contains("Win16TileApis") || name.Contains("Win16HazardApis") || name.Contains("Win16CompletionApis"));
        Assert.NotSame(runtime, typeof(Translator.Analysis.NeImportContractAuditBuilder).Assembly);
    }
}
