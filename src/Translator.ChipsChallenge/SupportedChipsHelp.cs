namespace Translator.ChipsChallenge;

/// <summary>Converts the supplied WinHelp file; no prepared help content is embedded.</summary>
public static class SupportedChipsHelp
{
    public static byte[] Convert(ReadOnlySpan<byte> original)
    {
        try
        {
            return HtmlHelpWriter.Build(WinHelpReader.Read(original.ToArray()));
        }
        catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("Invalid WinHelp file: a record is outside its declared bounds.", error);
        }
    }
}
