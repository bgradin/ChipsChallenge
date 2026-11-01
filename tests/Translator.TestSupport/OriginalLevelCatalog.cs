using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Translator.TestSupport;

public sealed record OriginalLevel(int Number, int TimeLimit, int Chips, string Title, string Password, byte[] Upper, byte[] Lower, IReadOnlyDictionary<byte, byte[]> Fields, string RecordSha256);
/// <summary>Independent test-only DAT decoder. It neither initializes nor controls guest state.</summary>
public static class OriginalLevelCatalog
{
    public const string SourceSha256 = "b0a68f21642447385512e0ed9386a4a13a4f7e9a38f909da772104d8e6b5c1fb";
    public static IReadOnlyList<OriginalLevel> Read(byte[] data)
    {
        if (!Convert.ToHexString(SHA256.HashData(data)).Equals(SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Parity oracle requires the pinned original 149-level DAT.");
        }

        int at = 0;

        void Need(int n, int limit)
        {
            if (n < 0 || at > limit - n)
            {
                throw new InvalidDataException("Truncated DAT record.");
            }
        }


        ushort Word(int limit)
        {
            Need(2, limit);
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            at += 2;
            return v;
        }

        if (Word(data.Length) != 0xaaac || Word(data.Length) != 2 || Word(data.Length) != 149)
        {
            throw new InvalidDataException("Unexpected DAT header.");
        }

        var result = new List<OriginalLevel>();
        for (int ordinal = 1; ordinal <= 149; ordinal++)
        {
            int size = Word(data.Length), start = at;
            Need(size, data.Length);
            int end = at + size;
            int number = Word(end), time = Word(end), chips = Word(end);
            Word(end);

            byte[] Layer()
            {
                int bytes = Word(end);
                Need(bytes, end);
                int stop = at + bytes;
                var layer = new List<byte>();
                while (at < stop)
                {
                    byte tile = data[at++];
                    int count = 1;
                    if (tile == 0xff)
                    {
                        Need(2, stop);
                        count = data[at++];
                        tile = data[at++];
                        if (count == 0)
                        {
                            throw new InvalidDataException("Empty DAT run.");
                        }
                    }

                    if (layer.Count + count > 1024)
                    {
                        throw new InvalidDataException("DAT layer exceeds 32 by 32.");
                    }

                    layer.AddRange(Enumerable.Repeat(tile, count));
                }

                if (layer.Count != 1024)
                {
                    throw new InvalidDataException("DAT layer is not 32 by 32.");
                }

                return layer.ToArray();
            }


            byte[] upper = Layer(), lower = Layer();
            int extra = Word(end);
            Need(extra, end);
            if (at + extra != end)
            {
                throw new InvalidDataException("DAT optional-field size mismatch.");
            }

            var fields = new Dictionary<byte, byte[]>();
            while (at < end)
            {
                Need(2, end);
                byte id = data[at++], length = data[at++];
                Need(length, end);
                fields.Add(id, data.AsSpan(at, length).ToArray());
                at += length;
            }

            string Text(byte id, bool password = false)
            {
                if (!fields.TryGetValue(id, out var bytes) || bytes.Length == 0 || bytes[^1] != 0)
                {
                    throw new InvalidDataException("Missing DAT title/password terminator.");
                }

                return Encoding.ASCII.GetString(bytes[..^1].Select(b => password
                    ? (byte)(b ^ 0x99)
                    : b).ToArray());
            }

            if (number != ordinal)
            {
                throw new InvalidDataException("DAT level sequence mismatch.");
            }

            result.Add(new(number, time, chips, Text(3), Text(6, true), upper, lower, fields, Convert.ToHexString(SHA256.HashData(data.AsSpan(start, size))).ToLowerInvariant()));
        }

        if (at != data.Length)
        {
            throw new InvalidDataException("Unaccounted DAT bytes.");
        }

        return result;
    }
}
