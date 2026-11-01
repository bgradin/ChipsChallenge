namespace Translator.Core;

/// <summary>Input-derived keyword routing filenames, not content/authenticity hashes.
/// Keep the UTF-16 FNV-1a encoding in sync with Native/task_wep.h.</summary>
public static class HelpKeywordRoute
{
    public static string Normalize(string keyword) => keyword.ToUpperInvariant();

    public static string File(string keyword)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char value in Normalize(keyword))
        {
            hash = unchecked((hash ^ (byte)value) * 1099511628211UL);
            hash = unchecked((hash ^ (byte)(value >> 8)) * 1099511628211UL);
        }

        return $"k{hash:x16}.htm";
    }
}
