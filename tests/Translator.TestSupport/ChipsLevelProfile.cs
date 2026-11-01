using System.Security.Cryptography;

namespace Translator.ChipsChallenge;

public sealed record LevelInitializationCall(string Procedure, ushort[] Arguments);
/// <summary>Pinned game-specific identity, original entry selection and independent oracles.
/// No decoding or gameplay algorithm is implemented here.</summary>
public static class ChipsLevelProfile
{
    public const int DataSize = 108569;
    public const string DataHash = "b0a68f21642447385512e0ed9386a4a13a4f7e9a38f909da772104d8e6b5c1fb";
    public const string BoardHash = "57fde52f2e19823f7c6fea53b2e19827646f96352b9810434aa7408e55629b31";
    public static readonly string[] Functions = ["4:00D8", "4:0320", "3:1734", "4:0A56", "4:0950", "4:09B4", "4:05FC", "4:06D8", "4:06B0", "3:054C"];
    public static LevelInitializationCall[] Calls => [new("4:0320", [1]), new("3:1734", [0]), new("4:00D8", [1]), new("3:054C", []), new("4:01FC", [])];

    private static SceneWrite Word(ushort offset, ushort value, string purpose) => new(offset, [(byte)value, (byte)(value >> 8)], purpose);
    public static SceneWrite[] Viewport => [Word(0x4a24, 11, "UI viewport X"), Word(0x4a26, 10, "UI viewport Y"), Word(0x4a28, 9, "UI viewport width"), Word(0x4a2a, 9, "UI viewport height")];

    public static SceneWrite[] Checks
    {
        get
        {
            var checks = new List<SceneWrite>
            {
                Word(0x4800, 1, "Level"),
                Word(0x4802, 149, "Level set size"),
                Word(0x4804, 100, "Initial time"),
                Word(0x4806, 11, "Initial chips"),
                Word(0x4808, 15, "Chip X"),
                Word(0x480a, 14, "Chip Y"),
                Word(0x1692, 11, "Remaining chips"),
                Word(0x1694, 100, "Remaining time")
            };
            foreach (ushort offset in new ushort[]
            {
                0x1682,
                0x1684,
                0x1686,
                0x1688,
                0x168a,
                0x168c,
                0x168e,
                0x1690,
                0x4816,
                0x481c,
                0x491e,
                0x4928,
                0x4932,
                0x493c,
                0x4946,
                0x4950,
                0x4a36
            }

            )
            {
                checks.Add(Word(offset, 0, "Empty inventory, original lists and inactive death/completion state"));
            }

            checks.Add(new(0x495a, "LESSON 1\0"u8.ToArray(), "Original title"));
            checks.Add(new(0x4a1a, "BDHP\0"u8.ToArray(), "Original decoded password"));
            return checks.ToArray();
        }
    }

    public static void ValidateData(byte[] data)
    {
        if (data.Length != DataSize || Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != DataHash)
        {
            throw new InvalidDataException("Original CHIPS.DAT identity mismatch.");
        }
    }
}
