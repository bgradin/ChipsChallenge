using Translator.Assembly;
using Translator.ChipsChallenge;
using Translator.CodeGen;
using Xunit;

namespace Translator.Integration.Tests;

public class TurnLeftIntegrationTests
{
    [Theory]
    [InlineData("TurnLeft/TurnLeft.asm")]
    [InlineData("Compiler/Acceptance.asm")]
    public void SyntheticSampleMatchesIndependentReferenceFor49BoundaryPairs(string sample)
    {
        var path = Path.GetFullPath("../../../../../tests/Fixtures/" + sample);
        var parsed = new AssemblyParser().ParseSelected(path, new[] { "TurnLeft" });
        Assert.Empty(parsed.Diagnostics);
        var function = Assert.Single(parsed.Module.Functions);
        var generator = new TurnLeftCodeGenerator();
        var artifact = generator.Generate(function, OperatingSystem.IsWindows()
            ? X64Abi.Windows
            : X64Abi.SystemV);
        foreach (short x in new short[]
        {
            short.MinValue,
            -2,
            -1,
            0,
            1,
            2,
            short.MaxValue
        }

        )
        {
            foreach (short y in new short[]
            {
                short.MinValue,
                -2,
                -1,
                0,
                1,
                2,
                short.MaxValue
            }

            )
            {
                var expected = TurnLeftRules.TryTurnLeft(x, y, out short xo, out short yo)
                    ? (xo, yo)
                    : ((short)0x5555, (short)0x5555);
                Assert.Equal(expected, generator.Execute(artifact, x, y));
            }
        }
    }
}
