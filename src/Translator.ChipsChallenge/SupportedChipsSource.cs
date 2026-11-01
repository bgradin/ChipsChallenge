using System.Security.Cryptography;

namespace Translator.ChipsChallenge;

/// <summary>Supported executable identity only. Code entries are discovered from the binary.</summary>
public static class SupportedChipsSource
{
    public const int Size = 267776;
    public const string Sha256 = "8e26acd67cf120bd5b512de4b4e78b80aca1579413cd04f3b2b68909a375866c";

    public static void Validate(ReadOnlySpan<byte> executable)
    {
        if (executable.Length != Size || !Convert.ToHexString(SHA256.HashData(executable)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unsupported source: production translation requires the verified original CHIPS.EXE size/SHA-256.");
        }
    }
}
