using Translator.IR;
using Win16_Runtime;

namespace Translator.ChipsChallenge;

/// <summary>Synthetic fixture symbols and caller-cleaned synthetic UI signatures, not instruction rewrites.</summary>
public static class GraphicsTargetProfile
{
    public static bool Supports(string name) => name is "InvertViewportTile" or "IsPointVisible";
    public static IReadOnlyDictionary<string, int> Symbols
    {
        get;
    } = new Dictionary<string, int>
    {
        ["FixtureStatePtr"] = 0x0040,
        ["ViewportX"] = 0x00,
        ["ViewportY"] = 0x02,
        ["ViewportWidth"] = 0x04,
        ["ViewportHeight"] = 0x06,
        ["PaddingX"] = 0x08,
        ["PaddingY"] = 0x0A,
        ["TileShift"] = 5,
        ["TileWidth"] = 32,
        ["TileHeight"] = 32
    };
    public static IReadOnlyDictionary<string, Win16CodeAddress> Targets
    {
        get;
    } = new Dictionary<string, Win16CodeAddress>
    {
        ["IsPointVisible"] = new(0x4000, 0x0100),
        ["InvertViewportTile"] = new(0x4000, 0x0200),
        ["GDI.PatBlt"] = new(0xF000, 0x0050)
    };

    public static IrFunction Configure(IrFunction function) => function.Name switch
    {
        "IsPointVisible" => function with
        {
            CleanupBytes = 0
        },
        "InvertViewportTile" => function with
        {
            CleanupBytes = 0,
            Arguments = new[]
            {
                new IrArgument("hdc", RegisterWidth.Word, 0),
                new IrArgument("x", RegisterWidth.Word, 2),
                new IrArgument("y", RegisterWidth.Word, 4)
            }
        },
        _ => throw new ArgumentException("No graphics target profile for " + function.Name)
    };
}
