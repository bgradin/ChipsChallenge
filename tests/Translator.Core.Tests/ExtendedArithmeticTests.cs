using Translator.IR;
using Xunit;

namespace Translator.Core.Tests;

public sealed class ExtendedArithmeticTests
{
    private static CpuFlags Flags(bool cf = false, bool pf = false, bool af = false, bool zf = false, bool sf = false, bool of = false) => new(cf, pf, af, zf, sf, of);

    [Fact]
    public void AdcAndSbbConsumeCarryAndSetWidthCorrectFlags()
    {
        var adc = X86Semantics.AddWithCarry16(0x7FFF, 0, true);
        Assert.Equal((ushort)0x8000, adc.Result);
        Assert.True(adc.Flags.OF);
        Assert.True(adc.Flags.SF);
        var sbb = X86Semantics.SubtractWithBorrow16(0, 0, true);
        Assert.Equal(ushort.MaxValue, sbb.Result);
        Assert.True(sbb.Flags.CF);
        Assert.True(sbb.Flags.AF);
        Assert.Equal((ushort)0, X86Semantics.SubtractWithBorrow16(0x8000, 0x7FFF, true).Result);
        Assert.True(X86Semantics.SubtractWithBorrow16(0x8000, 0x7FFF, true).Flags.OF);
    }

    [Theory]
    [InlineData(0, 0, false, false)]
    [InlineData(1, 0xFFFF, true, false)]
    [InlineData(0x8000, 0x8000, true, true)]
    public void NegateSetsCarryAndOverflowForBoundaryInputs(int input, int result, bool carry, bool overflow)
    {
        var actual = X86Semantics.Negate16((ushort)input);
        Assert.Equal((ushort)result, actual.Result);
        Assert.Equal(carry, actual.Flags.CF);
        Assert.Equal(overflow, actual.Flags.OF);
    }

    [Fact]
    public void MultiplySplitsDxAxAndKeepsUndefinedFlagsDeterministic()
    {
        var before = Flags(cf: true, pf: true, af: true, zf: true, sf: true, of: true);
        var unsignedProduct = X86Semantics.MultiplyUnsigned16(0xFFFF, 2, before);
        Assert.Equal((ushort)0xFFFE, unsignedProduct.Low);
        Assert.Equal((ushort)1, unsignedProduct.High);
        Assert.True(unsignedProduct.Flags.CF);
        Assert.True(unsignedProduct.Flags.OF);
        Assert.True(unsignedProduct.Flags.ZF);
        var signedProduct = X86Semantics.MultiplySigned16(0x8000, 2, before);
        Assert.Equal((ushort)0, signedProduct.Low);
        Assert.Equal((ushort)0xFFFF, signedProduct.High);
        Assert.True(signedProduct.Flags.OF);
        Assert.True(signedProduct.Flags.PF);
        var noOverflow = X86Semantics.MultiplySigned16(0xFFFE, 3, before);
        Assert.Equal((ushort)0xFFFA, noOverflow.Low);
        Assert.Equal((ushort)0xFFFF, noOverflow.High);
        Assert.False(noOverflow.Flags.OF);
    }

    [Fact]
    public void DivisionDetectsZeroAndQuotientOverflow()
    {
        Assert.Equal(((ushort)100, (ushort)3), X86Semantics.DivideUnsigned16(1003, 10));
        Assert.Equal(((ushort)0xFFFD, (ushort)0xFFFF), X86Semantics.DivideSigned16(-10, 3));
        Assert.Throws<DivideByZeroException>(() => X86Semantics.DivideUnsigned16(10, 0));
        Assert.Throws<OverflowException>(() => X86Semantics.DivideUnsigned16(0x1_0000, 1));
        Assert.Throws<OverflowException>(() => X86Semantics.DivideSigned16(int.MinValue, -1));
        Assert.Throws<OverflowException>(() => X86Semantics.DivideSigned16(32768, 1));
    }

    [Fact]
    public void ShiftsRotatesAndZeroCountsPreserveDefinedBehavior()
    {
        var prior = Flags(cf: true, pf: true, af: true, zf: true, sf: true, of: true);
        var left = X86Semantics.ShiftLeft16(0x8001, 1, prior);
        Assert.Equal((ushort)2, left.Result);
        Assert.True(left.Flags.CF);
        Assert.True(left.Flags.OF);
        Assert.False(left.Flags.ZF);
        var right = X86Semantics.ShiftRight16(0x8001, 1, prior);
        Assert.Equal((ushort)0x4000, right.Result);
        Assert.True(right.Flags.CF);
        Assert.True(right.Flags.OF);
        var arithmetic = X86Semantics.ShiftArithmeticRight16(0x8001, 1, prior);
        Assert.Equal((ushort)0xC000, arithmetic.Result);
        Assert.True(arithmetic.Flags.CF);
        Assert.False(arithmetic.Flags.OF);
        var rotate = X86Semantics.RotateCarryLeft16(0x8000, 1, Flags(cf: true));
        Assert.Equal((ushort)1, rotate.Result);
        Assert.True(rotate.Flags.CF);
        Assert.True(rotate.Flags.OF);
        Assert.Equal(((ushort)0x1234, prior), X86Semantics.ShiftLeft16(0x1234, 32, prior));
        Assert.Equal(((ushort)0x1234, prior), X86Semantics.RotateCarryLeft16(0x1234, 0, prior));
    }

    [Fact]
    public void BranchConditionsUseSignedUnsignedAndCounterRules()
    {
        var flags = Flags(cf: true, zf: false, sf: true, of: false);
        Assert.True(X86Semantics.EvaluateCondition(BranchCondition.Below, flags));
        Assert.True(X86Semantics.EvaluateCondition(BranchCondition.Less, flags));
        Assert.False(X86Semantics.EvaluateCondition(BranchCondition.Above, flags));
        Assert.True(X86Semantics.ExecuteJcxz(0));
        Assert.False(X86Semantics.ExecuteJcxz(1));
        ushort cx = 1;
        Assert.False(X86Semantics.ExecuteLoop(ref cx));
        Assert.Equal((ushort)0, cx);
        Assert.Throws<ArgumentException>(() => X86Semantics.EvaluateCondition(BranchCondition.Loop, flags));
    }

    [Fact]
    public void ModelsClcStcCwdXchgAndNot()
    {
        var flags = Flags(cf: true, zf: true);
        Assert.False(X86Semantics.ClearCarry(flags).CF);
        Assert.True(X86Semantics.SetCarry(flags).CF);
        Assert.Equal((short)-1, X86Semantics.Cwd16(0x8000));
        Assert.Equal((short)0, X86Semantics.Cwd16(0x7FFF));
        Assert.Equal(("right", "left"), X86Semantics.Exchange("left", "right"));
        Assert.Equal((ushort)0xEDCB, X86Semantics.Not16(0x1234));
    }

    [Fact]
    public void ConvertsArithmeticFlagsToAndFromThe16BitFlagsWord()
    {
        var flags = Flags(cf: true, pf: true, af: false, zf: true, sf: false, of: true);
        ushort merged = X86Semantics.MergeFlagsIntoWord(0x0042, flags);
        Assert.Equal((ushort)0x0847, merged);
        Assert.Equal(flags, X86Semantics.ReadFlagsFromWord(merged));
        Assert.Equal((ushort)0xF72A, X86Semantics.MergeFlagsIntoWord(0xFFFF, Flags()));
    }
}
