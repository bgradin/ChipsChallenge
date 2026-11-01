using System.Text;

namespace Translator.TestSupport;

/// <summary>External level-file inputs for normal original-loader tests, never guest-memory patches.</summary>
public sealed class ParityScenarioLevel
{
    public byte[] Upper { get; } = Enumerable.Repeat((byte)1, 1024).ToArray();
    public byte[] Lower { get; } = new byte[1024];
    public ushort TimeLimit { get; set; } = 0;
    public ushort Chips
    {
        get; set;
    }
    public List<(byte Tag, byte[] Bytes)> Fields { get; } = [];

    public ParityScenarioLevel()
    {
        for (int y = 10; y <= 20; y++)
        {
            for (int x = 10; x <= 20; x++)
            {
                Upper[y * 32 + x] = 0;
            }
        }

        Upper[15 * 32 + 15] = 0x6c;
    }

    public void Tile(int x, int y, byte tile, byte lower = 0)
    {
        Upper[y * 32 + x] = tile;
        Lower[y * 32 + x] = lower;
    }

    public static string Password(int index) => "T" + (char)('A' + index / 676 % 26) + (char)('A' + index / 26 % 26) + (char)('A' + index % 26);

    public static string Title(int index) => $"SCENARIO {index + 1:D3}";

    public static byte[] Build(IReadOnlyList<ParityScenarioLevel> levels)
    {
        if (levels.Count is < 1 or > 149)
        {
            throw new ArgumentOutOfRangeException(nameof(levels));
        }

        using var output = new MemoryStream();
        using var w = new BinaryWriter(output, Encoding.ASCII, true);
        w.Write(0x0002aaacu);
        w.Write((ushort)levels.Count);
        for (int i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            using var record = new MemoryStream();
            using var r = new BinaryWriter(record, Encoding.ASCII, true);
            r.Write((ushort)(i + 1));
            r.Write(level.TimeLimit);
            r.Write(level.Chips);
            r.Write((ushort)1);
            foreach (var layer in new[]
            {
                level.Upper,
                level.Lower
            }

            )
            {
                if (layer.Length != 1024 || layer.Contains((byte)255))
                {
                    throw new InvalidDataException("Scenario layer must contain 1024 literal tile bytes.");
                }

                r.Write((ushort)layer.Length);
                r.Write(layer);
            }

            using var optional = new MemoryStream();
            using var f = new BinaryWriter(optional, Encoding.ASCII, true);

            void Field(byte tag, byte[] value)
            {
                if (value.Length > 255)
                {
                    throw new InvalidDataException("Optional field too long.");
                }

                f.Write(tag);
                f.Write((byte)value.Length);
                f.Write(value);
            }


            Field(3, Encoding.ASCII.GetBytes(Title(i) + "\0"));
            Field(6, Encoding.ASCII.GetBytes(Password(i)).Select(b => (byte)(b ^ 0x99)).Append((byte)0).ToArray());
            foreach (var field in level.Fields)
            {
                Field(field.Tag, field.Bytes);
            }

            r.Write((ushort)optional.Length);
            r.Write(optional.ToArray());
            w.Write((ushort)record.Length);
            w.Write(record.ToArray());
        }

        return output.ToArray();
    }
}
