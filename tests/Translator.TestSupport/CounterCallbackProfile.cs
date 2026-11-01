using Translator.IR;
using Win16_Runtime;

namespace Translator.ChipsChallenge;

/// <summary>Authored synthetic window-procedure fixture; tests the default-message path and arithmetic lowering.</summary>
public static class CounterCallbackProfile
{
    public static Win16CodeAddress Entry { get; } = new(0x4000, 0x0300);
    public static IReadOnlyDictionary<string, int> Symbols
    {
        get;
    } = new Dictionary<string, int>
    {
        ["_HiWord"] = 2,
        ["_LoWord"] = 0
    };
    public static IReadOnlyDictionary<string, Win16CodeAddress> Targets
    {
        get;
    } = new Dictionary<string, Win16CodeAddress>
    {
        ["USER.DefWindowProc"] = new(0xF000, 0x60)
    };

    public static IrFunction Configure(IrFunction function)
    {
        if (function.Name != "SAMPLEWNDPROC")
        {
            throw new ArgumentException("Expected synthetic SAMPLEWNDPROC.");
        }

        if (function.CleanupBytes != 10)
        {
            throw new InvalidDataException("Synthetic callback must declare ten bytes of callee cleanup.");
        }

        return function;
    }
}
