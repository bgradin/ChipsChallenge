using System.Buffers.Binary;
using Translator.CodeGen;

namespace Translator.Linker;

public enum PeSubsystem
{
    WindowsGui = 2,
    WindowsConsole = 3
}

public enum PeRelativeReferenceKind
{
    Branch,
    RipRelativeAddress,
    DataRelative32
}

public sealed record PeRelativeCallFixup(int DisplacementOffset, string TargetFunction, PeRelativeReferenceKind Kind = PeRelativeReferenceKind.Branch);
public sealed record PeFunctionBody(string Name, byte[] Bytes, IReadOnlyList<PeRelativeCallFixup>? RelativeCallFixups = null, IReadOnlyDictionary<string, int>? Exports = null)
{
    public static PeFunctionBody FromArtifact(string name, MachineCodeArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Architecture != "AMD64" || artifact.Abi != X64Abi.Windows)
        {
            throw new ArgumentException("Only AMD64 artifacts can be added to PE32+ images.", nameof(artifact));
        }

        var fixups = (artifact.Relocations ?? Array.Empty<MachineCodeRelocation>()).Select(relocation => relocation.Kind switch
        {
            MachineCodeRelocationKind.Relative32Call when relocation.Addend == 0 => new PeRelativeCallFixup(relocation.Offset, relocation.Symbol),
            _ => throw new NotSupportedException($"PE32+ function body relocation kind {relocation.Kind} is not supported.")
        }).ToArray();
        return new(name, artifact.Bytes.ToArray(), fixups);
    }
}

public sealed partial class Pe32PlusWriter
{
    private const int _headerSize = 0x200, _fileAlign = 0x200, _sectionAlign = 0x1000, _optionalHeaderSize = 0xF0;

    public byte[] WriteExecutable(ReadOnlySpan<byte> x64EntryCode, ulong imageBase = 0x140000000) => WriteExecutable(new PeFunctionBody("__entry", x64EntryCode.ToArray()), Array.Empty<PeFunctionBody>(), imageBase);

    /// <summary>Writes an entry body plus named native functions, resolving declared rel32 CALL fixups within .text.</summary>
    public byte[] WriteExecutable(PeFunctionBody entryFunction, IReadOnlyList<PeFunctionBody> functions, ulong imageBase = 0x140000000) => WriteImage(entryFunction, functions, imageBase, false);

    /// <summary>Calls a no-argument Win64 entry and passes its integer result to ExitProcess.</summary>
    public byte[] WriteProgram(PeFunctionBody entryFunction, IReadOnlyList<PeFunctionBody> functions, ulong imageBase = 0x140000000) => WriteImage(entryFunction, functions, imageBase, true);

    public byte[] WriteProgram(PeFunctionBody entryFunction, IReadOnlyList<PeFunctionBody> functions, IReadOnlyList<PeNativeImport> imports, ulong imageBase = 0x140000000) => WriteImage(entryFunction, functions, imageBase, true, imports);

    /// <summary>GUI subsystem with the same native return-to-ExitProcess contract.</summary>
    public byte[] WriteGuiProgram(PeFunctionBody entryFunction, IReadOnlyList<PeFunctionBody> functions, IReadOnlyList<PeNativeImport> imports, ulong imageBase = 0x140000000) => WriteImage(entryFunction, functions, imageBase, true, imports, PeSubsystem.WindowsGui);
    private byte[] WriteImage(PeFunctionBody entryFunction, IReadOnlyList<PeFunctionBody> functions, ulong imageBase, bool exitCodeEntry, IReadOnlyList<PeNativeImport>? imports = null, PeSubsystem subsystem = PeSubsystem.WindowsConsole)
    {
        ArgumentNullException.ThrowIfNull(entryFunction);
        ArgumentNullException.ThrowIfNull(functions);
        if (entryFunction.Bytes.Length == 0)
        {
            throw new ArgumentException("Entry code must not be empty.", nameof(entryFunction));
        }

        imports ??= Array.Empty<PeNativeImport>();
        var nativeImports = new[]
        {
            new PeNativeImport("KERNEL32.dll", "ExitProcess", "__launcher_exit")
        }.Concat(imports).ToArray();
        var importStubs = imports.Select(i => new PeFunctionBody(i.Symbol, new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }));
        var allFunctions = new[]
        {
            entryFunction
        }.Concat(functions).Concat(importStubs).ToArray();
        if (allFunctions.Any(f => string.IsNullOrWhiteSpace(f.Name) || f.Bytes is null || f.Bytes.Length == 0))
        {
            throw new ArgumentException("Every native function must have a name and non-empty body.", nameof(functions));
        }

        if (allFunctions.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != allFunctions.Length)
        {
            throw new ArgumentException("Native function names must be unique.", nameof(functions));
        }

        const int payloadOffset = 0x100;
        var functionOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        int cursor = payloadOffset;
        foreach (var function in allFunctions)
        {
            cursor = Align(cursor, 0x10);
            functionOffsets.Add(function.Name, cursor);
            foreach (var export in function.Exports ?? new Dictionary<string, int>())
            {
                if (export.Value < 0 || export.Value >= function.Bytes.Length || !functionOffsets.TryAdd(export.Key, cursor + export.Value))
                {
                    throw new InvalidDataException($"Invalid or duplicate export '{export.Key}'.");
                }
            }

            cursor = checked(cursor + function.Bytes.Length);
        }

        int outputOffset = Align(cursor, 2);
        int importOffset = Align(outputOffset + 4, 0x10);
        var importLayout = PeImportLayout.Build(nativeImports, importOffset, _sectionAlign);
        int iatOffset = importLayout.Slots["__launcher_exit"];
        byte[] launcher = exitCodeEntry
            ? BuildProgramLauncher(payloadOffset, iatOffset)
            : BuildLauncher(payloadOffset, outputOffset, iatOffset);
        int virtualSize = importOffset + importLayout.Bytes.Length;
        int rawSize = Align(virtualSize, _fileAlign), imageSize = Align(_sectionAlign + virtualSize, _sectionAlign);
        byte[] image = new byte[_headerSize + rawSize];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        WriteInt32(image, 0x3C, 0x80);
        // DOS header -> PE signature
        "This program requires Windows.\r\n$"u8.CopyTo(image.AsSpan(0x40));
        int pe = 0x80;
        "PE\0\0"u8.CopyTo(image.AsSpan(pe));
        int coff = pe + 4, opt = coff + 20, section = opt + _optionalHeaderSize;
        WriteUInt16(image, coff, 0x8664);
        WriteUInt16(image, coff + 2, 1);
        WriteUInt16(image, coff + 16, _optionalHeaderSize);
        WriteUInt16(image, coff + 18, 0x0022);
        WriteUInt16(image, opt, 0x20B);
        image[opt + 2] = 1;
        WriteInt32(image, opt + 4, rawSize);
        WriteInt32(image, opt + 16, _sectionAlign);
        WriteInt32(image, opt + 20, _sectionAlign);
        WriteUInt64(image, opt + 24, imageBase);
        WriteInt32(image, opt + 32, _sectionAlign);
        WriteInt32(image, opt + 36, _fileAlign);
        WriteUInt16(image, opt + 40, 6);
        WriteUInt16(image, opt + 48, 6);
        WriteUInt16(image, opt + 50, 0);
        WriteInt32(image, opt + 56, imageSize);
        WriteInt32(image, opt + 60, _headerSize);
        WriteUInt16(image, opt + 68, (int)subsystem);
        WriteUInt16(image, opt + 70, 0x8160);
        WriteUInt64(image, opt + 72, 0x100000);
        WriteUInt64(image, opt + 80, 0x1000);
        WriteUInt64(image, opt + 88, 0x100000);
        WriteUInt64(image, opt + 96, 0x1000);
        WriteInt32(image, opt + 108, 16);
        WriteInt32(image, opt + 112 + 8, _sectionAlign + importOffset);
        WriteInt32(image, opt + 112 + 12, (nativeImports.Select(i => i.Module).Distinct(StringComparer.OrdinalIgnoreCase).Count() + 1) * 20);
        // import directory
        WriteInt32(image, opt + 112 + 12 * 8, _sectionAlign + importLayout.IatOffset);
        WriteInt32(image, opt + 112 + 12 * 8 + 4, importLayout.IatSize);
        // IAT directory
        ".text\0\0\0"u8.CopyTo(image.AsSpan(section));
        WriteInt32(image, section + 8, virtualSize);
        WriteInt32(image, section + 12, _sectionAlign);
        WriteInt32(image, section + 16, rawSize);
        WriteInt32(image, section + 20, _headerSize);
        WriteInt32(image, section + 36, unchecked((int)0xE0000060));
        int code = _headerSize;
        launcher.CopyTo(image.AsSpan(code));
        foreach (var function in allFunctions)
        {
            function.Bytes.CopyTo(image.AsSpan(code + functionOffsets[function.Name]));
        }

        int raw = _headerSize;
        foreach (var function in allFunctions)
        {
            foreach (var fixup in function.RelativeCallFixups ?? Array.Empty<PeRelativeCallFixup>())
            {
                int at = fixup.DisplacementOffset;
                if (at < 0 || at > function.Bytes.Length - 4)
                {
                    throw new InvalidDataException($"Relative fixup is outside '{function.Name}'.");
                }

                bool validReference = fixup.Kind switch
                {
                    PeRelativeReferenceKind.Branch => at >= 1 && function.Bytes[at - 1] is 0xE8 or 0xE9,
                    PeRelativeReferenceKind.RipRelativeAddress => at >= 3 && function.Bytes[at - 3] is 0x48 or 0x4c && function.Bytes[at - 2] == 0x8d && (function.Bytes[at - 1] & 0xc7) == 5,
                    PeRelativeReferenceKind.DataRelative32 => true,
                    _ => false
                };
                if (!validReference)
                {
                    throw new InvalidDataException($"Invalid {fixup.Kind} rel32 fixup in '{function.Name}'.");
                }

                if (!functionOffsets.TryGetValue(fixup.TargetFunction, out int targetOffset))
                {
                    throw new InvalidDataException($"Function '{function.Name}' calls unresolved target '{fixup.TargetFunction}'.");
                }

                int nextRva = _sectionAlign + functionOffsets[function.Name] + fixup.DisplacementOffset + 4;
                int targetRva = _sectionAlign + targetOffset;
                WriteInt32(image, raw + functionOffsets[function.Name] + fixup.DisplacementOffset, checked(targetRva - nextRva));
            }
        }

        importLayout.Bytes.CopyTo(image, raw + importOffset);
        foreach (var import in imports)
        {
            int stub = functionOffsets[import.Symbol];
            WriteInt32(image, raw + stub + 2, importLayout.Slots[import.Symbol] - (stub + 6));
        }

        return image;
    }

    private static byte[] BuildProgramLauncher(int payloadOffset, int iatOffset)
    {
        byte[] bytes =
        {
            0x48,
            0x83,
            0xEC,
            0x28,
            0xE8,
            0,
            0,
            0,
            0,
            0x89,
            0xC1,
            0xFF,
            0x15,
            0,
            0,
            0,
            0
        };
        WriteInt32(bytes, 5, payloadOffset - 9);
        WriteInt32(bytes, 13, iatOffset - 17);
        return bytes;
    }

    private static byte[] BuildLauncher(int payloadOffset, int outputOffset, int iatOffset)
    {
        var code = new List<byte>();
        var relativePatches = new List<(int DisplacementOffset, int TargetOffset)>();
        var dataPatches = new List<(int DisplacementOffset, int TargetRva, int InstructionTail)>();
        int failureLabel;
        int exitLabel;
        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 }); // align stack and reserve Windows x64 shadow space
        code.AddRange(new byte[] { 0xB9, 0xFF, 0xFF, 0xFF, 0xFF }); // x = -1
        code.AddRange(new byte[] { 0x31, 0xD2 }); // y = 0
        code.AddRange(new byte[] { 0x4C, 0x8D, 0x05 });
        dataPatches.Add((code.Count, _sectionAlign + outputOffset, 0));
        code.AddRange(new byte[4]); // r8 = &xOut
        code.AddRange(new byte[] { 0x4C, 0x8D, 0x0D });
        dataPatches.Add((code.Count, _sectionAlign + outputOffset + 2, 0));
        code.AddRange(new byte[4]); // r9 = &yOut
        code.Add(0xE8);
        relativePatches.Add((code.Count, _sectionAlign + payloadOffset));
        code.AddRange(new byte[4]); // call translated function
        code.AddRange(new byte[] { 0x66, 0x83, 0x3D });
        dataPatches.Add((code.Count, _sectionAlign + outputOffset, 1));
        code.AddRange(new byte[] { 0, 0, 0, 0, 0 }); // cmp word [xOut], 0
        code.AddRange(new byte[] { 0x0F, 0x85 });
        int failureJump = code.Count;
        code.AddRange(new byte[4]);
        code.AddRange(new byte[] { 0x66, 0x83, 0x3D });
        dataPatches.Add((code.Count, _sectionAlign + outputOffset + 2, 1));
        code.AddRange(new byte[] { 0, 0, 0, 0, 1 }); // cmp word [yOut], 1
        code.AddRange(new byte[] { 0x0F, 0x85 });
        int secondFailureJump = code.Count;
        code.AddRange(new byte[4]);
        code.AddRange(new byte[] { 0x31, 0xC9 }); // success exit code 0
        code.Add(0xE9);
        int successJump = code.Count;
        code.AddRange(new byte[4]);
        failureLabel = code.Count;
        code.AddRange(new byte[] { 0xB9, 1, 0, 0, 0 }); // failure exit code 1
        exitLabel = code.Count;
        code.AddRange(new byte[] { 0xFF, 0x15 });
        int exitIatPatch = code.Count;
        code.AddRange(new byte[4]); // call kernel32!ExitProcess
        PatchRelative(code, relativePatches);
        PatchRelative(code, new[] { (failureJump, _sectionAlign + failureLabel), (secondFailureJump, _sectionAlign + failureLabel), (successJump, _sectionAlign + exitLabel) });
        foreach ((int displacementOffset, int targetRva, int instructionTail) in dataPatches)
        {
            WriteInt32(code, displacementOffset, targetRva - (_sectionAlign + displacementOffset + 4 + instructionTail));
        }

        WriteInt32(code, exitIatPatch, (_sectionAlign + iatOffset) - (_sectionAlign + exitIatPatch + 4));
        return code.ToArray();
    }

    private static void PatchRelative(List<byte> code, IEnumerable<(int DisplacementOffset, int TargetOffset)> patches)
    {
        foreach ((int displacementOffset, int targetOffset) in patches)
        {
            WriteInt32(code, displacementOffset, targetOffset - (_sectionAlign + displacementOffset + 4));
        }
    }

    private static void WriteInt32(List<byte> buffer, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer).Slice(offset), value);

    public static bool IsAmd64Pe32Plus(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z')
        {
            return false;
        }

        uint pe = BinaryPrimitives.ReadUInt32LittleEndian(data[0x3C..]);
        if (pe > int.MaxValue || (ulong)pe + 0x1Au > (ulong)data.Length)
        {
            return false;
        }

        int offset = (int)pe;
        return data[offset] == 'P' && data[offset + 1] == 'E' && data[offset + 2] == 0 && data[offset + 3] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]) == 0x8664 && BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 24)..]) == 0x20B;
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static void WriteUInt16(byte[] buffer, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), unchecked((ushort)value));

    private static void WriteInt32(byte[] buffer, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), value);

    private static void WriteUInt64(byte[] buffer, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), value);
}
