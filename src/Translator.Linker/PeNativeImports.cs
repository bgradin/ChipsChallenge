using System.Buffers.Binary;
using System.Text;

namespace Translator.Linker;

public sealed record PeNativeImport(string Module, string Name, string Symbol);
internal sealed record PeImportLayout(byte[] Bytes, IReadOnlyDictionary<string, int> Slots, int IatOffset, int IatSize)
{
    private const int _descriptorSize = 20;
    private const int _thunkSize = 8;
    private const int _hintSize = 2;

    public static PeImportLayout Build(IReadOnlyList<PeNativeImport> imports, int offset, int sectionRva)
    {
        foreach (var entry in imports)
        {
            if (string.IsNullOrWhiteSpace(entry.Symbol) || new[]
            {
                entry.Module,
                entry.Name
            }.Any(s => string.IsNullOrWhiteSpace(s) || s.Any(c => c < 33 || c > 126)))
            {
                throw new ArgumentException("Native imports require nonempty symbols and ASCII DLL/function names.");
            }
        }

        if (imports.Select(i => i.Symbol).Distinct(StringComparer.Ordinal).Count() != imports.Count)
        {
            throw new ArgumentException("Native import symbols must be unique.");
        }

        var groups = imports.GroupBy(i => i.Module, StringComparer.OrdinalIgnoreCase).ToArray();
        int cursor = checked(offset + (groups.Length + 1) * _descriptorSize);
        cursor = (cursor + 7) & ~7;
        var lookupTableOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            lookupTableOffsets.Add(group.Key, cursor);
            cursor = checked(cursor + (group.Count() + 1) * _thunkSize);
        }

        int iatOffset = cursor;
        var addressTableOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            addressTableOffsets.Add(group.Key, cursor);
            cursor = checked(cursor + (group.Count() + 1) * _thunkSize);
        }

        int iatSize = cursor - iatOffset;
        var moduleNameOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var functionNameOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            moduleNameOffsets.Add(group.Key, cursor);
            cursor = checked(cursor + group.Key.Length + 1);
            foreach (var entry in group)
            {
                cursor = (cursor + 1) & ~1;
                functionNameOffsets.Add(entry.Symbol, cursor);
                cursor = checked(cursor + _hintSize + entry.Name.Length + 1);
            }
        }

        var bytes = new byte[cursor - offset];
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);

        void WriteInt32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at - offset), value);


        void WriteInt64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at - offset), value);

        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            int descriptor = offset + groupIndex * _descriptorSize;
            WriteInt32(descriptor, sectionRva + lookupTableOffsets[group.Key]);
            WriteInt32(descriptor + 12, sectionRva + moduleNameOffsets[group.Key]);
            WriteInt32(descriptor + 16, sectionRva + addressTableOffsets[group.Key]);
            Encoding.ASCII.GetBytes(group.Key).CopyTo(bytes, moduleNameOffsets[group.Key] - offset);
            int importIndex = 0;
            foreach (var entry in group)
            {
                int slot = addressTableOffsets[group.Key] + importIndex * _thunkSize;
                slots.Add(entry.Symbol, slot);
                WriteInt64(lookupTableOffsets[group.Key] + importIndex * _thunkSize, sectionRva + functionNameOffsets[entry.Symbol]);
                WriteInt64(slot, sectionRva + functionNameOffsets[entry.Symbol]);
                Encoding.ASCII.GetBytes(entry.Name).CopyTo(bytes, functionNameOffsets[entry.Symbol] - offset + _hintSize);
                importIndex++;
            }
        }

        return new(bytes, slots, iatOffset, iatSize);
    }
}
