using System.Globalization;

namespace Translator.Binary;

/// <summary>Original NE identity, independent of runtime selectors and native addresses.</summary>
public readonly record struct NeBinaryAddress(int SegmentIndex, ushort Offset) : IComparable<NeBinaryAddress>
{
    public string Identity => $"{SegmentIndex:X4}:{Offset:X4}";

    public override string ToString() => Identity;
    public int CompareTo(NeBinaryAddress other) => SegmentIndex != other.SegmentIndex
        ? SegmentIndex.CompareTo(other.SegmentIndex)
        : Offset.CompareTo(other.Offset);

    public static NeBinaryAddress Parse(string text)
    {
        string[] parts = text.Split(':');
        if (parts.Length != 2 || !ushort.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var segment) || segment == 0 || !ushort.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset))
        {
            throw new ArgumentException($"Invalid NE address '{text}'; use hexadecimal segment:offset.");
        }

        return new(segment, offset);
    }
}
