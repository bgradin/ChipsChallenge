using System.Buffers.Binary;

namespace Translator.CodeGen;

/// <summary>Applies symbol fixups to a position-independent AMD64 artifact at a chosen load address.</summary>
public static class MachineCodeArtifactRelocator
{
    public static byte[] Relocate(MachineCodeArtifact artifact, ulong loadAddress, IReadOnlyDictionary<string, ulong> symbols)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(symbols);
        if (artifact.Architecture != "AMD64")
        {
            throw new ArgumentException("Only AMD64 artifacts can be relocated.", nameof(artifact));
        }

        byte[] result = artifact.Bytes.ToArray();
        foreach (var relocation in artifact.Relocations ?? Array.Empty<MachineCodeRelocation>())
        {
            if (!symbols.TryGetValue(relocation.Symbol, out ulong target))
            {
                throw new KeyNotFoundException($"No address was supplied for relocation symbol '{relocation.Symbol}'.");
            }

            if (relocation.Offset < 0)
            {
                throw new InvalidDataException($"Relocation for '{relocation.Symbol}' has a negative offset.");
            }

            target = unchecked(target + (ulong)relocation.Addend);
            switch (relocation.Kind)
            {
                case MachineCodeRelocationKind.Relative32Call:
                    {
                        if (relocation.Offset < 1 || relocation.Offset > result.Length - sizeof(int) || result[relocation.Offset - 1] is not (0xE8 or 0xE9))
                        {
                            throw new InvalidDataException($"rel32 relocation for '{relocation.Symbol}' does not identify a CALL/JMP displacement within the code body.");
                        }

                        long nextInstruction = checked((long)(loadAddress + (ulong)relocation.Offset + sizeof(int)));
                        long delta = checked((long)target - nextInstruction);
                        if (delta is < int.MinValue or > int.MaxValue)
                        {
                            throw new InvalidDataException($"rel32 target '{relocation.Symbol}' is outside the AMD64 call range.");
                        }

                        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(relocation.Offset), (int)delta);
                        break;
                    }

                case MachineCodeRelocationKind.Absolute64:
                    if (relocation.Offset > result.Length - sizeof(ulong))
                    {
                        throw new InvalidDataException($"absolute64 relocation for '{relocation.Symbol}' exceeds the code body.");
                    }

                    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(relocation.Offset), target);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(relocation.Kind), relocation.Kind, "Unknown AMD64 relocation kind.");
            }
        }

        return result;
    }
}
