namespace Translator.CodeGen;

/// <summary>Shared native service ABI symbols, independent of regression generators.</summary>
public static class Win16HelperSymbols
{
    public const string ReadWordHelperSymbol = "__win16_read_word";
    public const string WriteWordHelperSymbol = "__win16_write_word";
    public const string CallServiceHelperSymbol = "__win16_call_service";
}
