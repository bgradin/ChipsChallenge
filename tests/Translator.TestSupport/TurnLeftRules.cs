namespace Translator.ChipsChallenge;

/// <summary>Independent behavioral oracle for the 16-bit direction-code example.</summary>
public static class TurnLeftRules
{
    public static bool TryTurnLeft(short x, short y, out short xOut, out short yOut)
    {
        short directionCode = unchecked((short)(2 * (int)x + y));
        (xOut, yOut) = directionCode switch
        {
            -2 => ((short)0, (short)1),
            -1 => ((short)-1, (short)0),
            1 => ((short)1, (short)0),
            2 => ((short)0, (short)-1),
            _ => ((short)0, (short)0)
        };
        return directionCode is -2 or -1 or 1 or 2;
    }
}
