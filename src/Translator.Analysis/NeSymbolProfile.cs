using System.Security.Cryptography;
using System.Text.Json;
using Translator.Binary;

namespace Translator.Analysis;

public sealed record NeProfileSymbol(string Name, NeBinaryAddress Address, int VerifiedRangeLength, string BytesSha256);
public sealed record NeSymbolProfile(int Version, int ExecutableSize, string ExecutableSha256, IReadOnlyList<NeProfileSymbol> Symbols);
public sealed record NeVerifiedSymbols(string ExecutableSha256, int ExecutableSize, string Verification, IReadOnlyList<NeProfileSymbol> Symbols);
/// <summary>Reviewed entry metadata bound to original executable identity and byte ranges.</summary>
public static class NeSymbolProfileReader
{
    public static NeVerifiedSymbols Read(string path, ReadOnlySpan<byte> image)
    {
        NeSymbolProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<NeSymbolProfile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty symbol profile.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid symbol profile JSON.", error);
        }

        return VerifyPackagedEntries(profile, image);
    }

    public static NeVerifiedSymbols VerifyPackagedEntries(NeSymbolProfile profile, ReadOnlySpan<byte> image)
    {
        string hash = Hash(image);
        if (profile.Version != 1 || profile.ExecutableSize != image.Length || profile.ExecutableSha256 != hash || profile.Symbols is null)
        {
            throw new InvalidDataException("Packaged entry evidence does not match the original executable.");
        }

        var executable = new NeExecutableReader().Read(image);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in profile.Symbols)
        {
            if (symbol is null)
            {
                throw new InvalidDataException("Null entry in symbol profile.");
            }

            var segment = executable.Segments.FirstOrDefault(s => s.Index == symbol.Address.SegmentIndex);
            if (string.IsNullOrWhiteSpace(symbol.Name) || !names.Add(symbol.Name) || segment is null || segment.IsData || symbol.VerifiedRangeLength < 1 || symbol.Address.Offset > segment.FileLength - symbol.VerifiedRangeLength || Hash(image.Slice(segment.FileOffset + symbol.Address.Offset, symbol.VerifiedRangeLength)) != symbol.BytesSha256)
            {
                throw new InvalidDataException("Invalid packaged original-byte entry evidence: " + symbol.Name);
            }
        }

        return new(hash, image.Length, "Original executable identity and entry byte ranges verified; names and entry boundaries are reviewed metadata", profile.Symbols.ToArray());
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
