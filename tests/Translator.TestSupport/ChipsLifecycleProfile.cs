namespace Translator.ChipsChallenge;

/// <summary>Game-specific, binary-identity-pinned selection; no game decisions live in the native owner.</summary>
public static class ChipsLifecycleProfile
{
    public const string ExecutableSha256 = SupportedChipsSource.Sha256;
    public const string Root = "0009:0028"; // BitmapSize, called by LoadDigits; original far cdecl helper.
    public const uint ExpectedDxAx = 276; // independently: ceil(17*4/32)*4 bytes/row * 23 rows.
    public static ushort[] Arguments => [17, 23];

    public static void Validate(ReadOnlySpan<byte> original)
    {
        SupportedChipsSource.Validate(original);
    }
}
