using Translator.ChipsChallenge;
using Win16_Runtime;

namespace Translator.Linker;

/// <summary>Configuration for an explicit lifecycle/window checkpoint or the certified gameplay application.</summary>
public sealed record NeWindowCallbackOptions(int MaximumDepth = 8, int StackBytes = 16384, uint? TimerResult = null);
public sealed record NeApplicationStartup(string Root, ushort[] Arguments, uint ExpectedDxAx, uint ExpectedResultStep = 0, int Invocations = 3, int IntervalMilliseconds = 100, bool WindowMessages = false, NeWindowCallbackOptions? Callbacks = null, bool CounterPainting = false, bool SceneComposition = false, bool KeyboardInput = false, bool StandaloneDistribution = false, bool AuthenticLevel = false, bool PlayerMovement = false, bool GameClock = false, bool TileInteractions = false, bool Hazards = false, bool Completion = false, bool Gameplay = false, ushort InitialLevel = 1);
public sealed record NeApplicationManifest(string Format, string ExecutableSha256, string PeSha256, string DescriptorSha256, string NativeHelperSha256, string Subsystem, NeApplicationStartup Startup, NeNativeArtifact Translation, NeRuntimeInitialization Initialization, IReadOnlyList<PeNativeImport> NativeImports, object TaskState, IReadOnlyList<string> DeferredCapabilities, IReadOnlyDictionary<int, string> ExitCodes)
{
    public object? WindowCompatibility
    {
        get; init;
    }
    public object? Distribution
    {
        get; init;
    }
    public object? Level
    {
        get; init;
    }
    public object? Clock
    {
        get; init;
    }
    public object? Tiles
    {
        get; init;
    }
    public object? Hazards
    {
        get; init;
    }
    public object? Completion
    {
        get; init;
    }
    public object? Gameplay
    {
        get; init;
    }
    public ChipsInputPlan? Input
    {
        get; init;
    }
    public ChipsScenePlan? Scene
    {
        get; init;
    }
    public CounterGraphicsPlan? Graphics
    {
        get; init;
    }
}
