using Xunit;

namespace Translator.Core.Tests;

public class ArithmeticTests
{
    [Theory]
    [InlineData(0, 0, 0, false, false)]
    [InlineData(32767, 1, 32768, false, true)]
    [InlineData(65535, 1, 0, true, false)]
    public void Add16CalculatesResultAndCarryOverflow(int a, int b, int result, bool cf, bool of)
    {
        var actual = X86Semantics.Add16((ushort)a, (ushort)b);
        Assert.Equal((ushort)result, actual.Result);
        Assert.Equal(cf, actual.Flags.CF);
        Assert.Equal(of, actual.Flags.OF);
    }

    [Fact]
    public void Sub16WrapsAndSetsBorrow()
    {
        var actual = X86Semantics.Sub16(0, 1);
        Assert.Equal(ushort.MaxValue, actual.Result);
        Assert.True(actual.Flags.CF);
    }

    [Fact]
    public void IncrementPreservesCarryAndSetsOverflow()
    {
        var previous = new CpuFlags(true, false, false, false, false, false);
        var actual = X86Semantics.Increment16(0x7FFF, previous);
        Assert.Equal((ushort)0x8000, actual.Result);
        Assert.True(actual.Flags.CF);
        Assert.True(actual.Flags.OF);
    }

    [Fact]
    public void LogicalOperationClearsCarryAndOverflow()
    {
        var actual = X86Semantics.Xor16(0x8000, 0x8000);
        Assert.Equal((ushort)0, actual.Result);
        Assert.False(actual.Flags.CF);
        Assert.False(actual.Flags.OF);
        Assert.True(actual.Flags.ZF);
    }

    [Fact]
    public void AddSetsAllDefinedFlagsAtSignedBoundary()
    {
        var actual = X86Semantics.Add16(0x7FFF, 1);
        Assert.True(actual.Flags.PF);
        Assert.True(actual.Flags.AF);
        Assert.False(actual.Flags.ZF);
        Assert.True(actual.Flags.SF);
        Assert.True(actual.Flags.OF);
    }
}
