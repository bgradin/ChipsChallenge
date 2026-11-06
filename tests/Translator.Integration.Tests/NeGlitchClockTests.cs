using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeGlitchClockTests
{
    [WindowsSuppliedNeFact]
    public void GlitchLongFirstSecondSkipsZeroAndWaitsSixteenTicksBetweenCountdowns()
    {
        string original = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        string levels = Path.Combine(Path.GetDirectoryName(original)!, "CHIPS.DAT");
        var program = NeApplicationProgram.Build(File.ReadAllBytes(original),
            new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true,
                SceneComposition: true, KeyboardInput: true, AuthenticLevel: true,
                PlayerMovement: true, GameClock: true),
            levelData: File.ReadAllBytes(levels), clockGlitches: true);
        Assert.Equal(5, program.Manifest.Input!.Cases.Count(c => c.Name.StartsWith("GlitchLongFirstSecond", StringComparison.Ordinal)));
        string diagnostic = Path.Combine(Path.GetTempPath(), "glitch-clock-" + Guid.NewGuid().ToString("N") + ".txt");
        int exit = NeApplicationTests.Execute(program.Executable, originalPath: original,
            additionalArguments: ["--levels", levels, "--diagnostics", diagnostic]);
        Assert.True(exit == 0, File.Exists(diagnostic) ? File.ReadAllText(diagnostic) : "Clock exit: " + exit);
        File.Delete(diagnostic);
    }
}
