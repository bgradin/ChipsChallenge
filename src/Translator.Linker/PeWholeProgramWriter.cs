namespace Translator.Linker;

public sealed record PeUnwindRange(string Function, int Start, int End, byte[] Info);
public sealed record PeSectionEvidence(string Name, int Rva, int VirtualSize, int FileOffset, int RawSize, uint Characteristics);
public sealed record PeWholeProgramImage(byte[] Bytes, IReadOnlyDictionary<string, int> Symbols, IReadOnlyList<PeSectionEvidence> Sections);
public sealed partial class Pe32PlusWriter
{
    /// <summary>Production layout. No writable executable sections or absolute code/data pointers.
    /// A relocation block of ABSOLUTE entries permits rebasing this entirely RIP-relative image.</summary>
    public PeWholeProgramImage WriteWholeProgram(PeFunctionBody entry, IReadOnlyList<PeFunctionBody> functions, IReadOnlyList<PeFunctionBody> readOnlyData, IReadOnlyList<PeNativeImport> imports, IReadOnlyList<PeUnwindRange> unwind)
    {
        const int header = 0x400, textRva = 0x1000;
        var nativeImports = new[]
        {
            new PeNativeImport("KERNEL32.dll", "ExitProcess", "__launcher_exit")
        }.Concat(imports).ToArray();
        var bodies = new[]
        {
            entry
        }.Concat(functions).Concat(imports.Select(i => new PeFunctionBody(i.Symbol, [0xff, 0x25, 0, 0, 0, 0]))).ToArray();
        var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
        int cursor = 0x20;
        foreach (var body in bodies)
        {
            cursor = Align(cursor, 16);
            Add(body, textRva + cursor);
            cursor = checked(cursor + body.Bytes.Length);
        }

        byte[] text = new byte[cursor];
        int roRva = Align(textRva + text.Length, 0x1000);
        cursor = 0;
        foreach (var data in readOnlyData)
        {
            cursor = Align(cursor, 16);
            Add(data, roRva + cursor);
            cursor = checked(cursor + data.Bytes.Length);
        }

        byte[] ro = new byte[cursor];
        int dataRva = Align(roRva + ro.Length, 0x1000);
        var importLayout = PeImportLayout.Build(nativeImports, 0, dataRva);
        // Launcher reserves Win64 shadow space, calls the declared bootstrap, exits with its status.
        byte[] launcher = [0x48, 0x83, 0xec, 0x28, 0xe8, 0, 0, 0, 0, 0x89, 0xc1, 0xff, 0x15, 0, 0, 0, 0];
        WriteInt32(launcher, 5, symbols[entry.Name] - (textRva + 9));
        WriteInt32(launcher, 13, dataRva + importLayout.Slots["__launcher_exit"] - (textRva + 17));
        launcher.CopyTo(text, 0);
        foreach (var body in bodies)
        {
            body.Bytes.CopyTo(text, symbols[body.Name] - textRva);
            Patch(body, text, textRva);
        }

        foreach (var body in readOnlyData)
        {
            body.Bytes.CopyTo(ro, symbols[body.Name] - roRva);
            Patch(body, ro, roRva);
        }

        foreach (var import in imports)
        {
            WriteInt32(text, symbols[import.Symbol] - textRva + 2, dataRva + importLayout.Slots[import.Symbol] - (symbols[import.Symbol] + 6));
        }

        int pdataRva = Align(dataRva + importLayout.Bytes.Length, 0x1000);
        var ranges = new[]
        {
            new PeUnwindRange("__launcher", 0, launcher.Length, [1, 4, 1, 0, 4, 0x42, 0, 0])
        }.Concat(unwind).Select(u => (Start: u.Function == "__launcher"
            ? textRva
            : symbols[u.Function] + u.Start, End: u.Function == "__launcher"
            ? textRva + u.End
            : symbols[u.Function] + u.End, u.Info)).OrderBy(u => u.Start).ToArray();
        byte[] pdata = new byte[ranges.Length * 12];
        int xdataRva = Align(pdataRva + pdata.Length, 0x1000);
        using var xs = new MemoryStream();
        int previousEnd = textRva;
        for (int i = 0; i < ranges.Length; i++)
        {
            var u = ranges[i];
            if (u.Start < previousEnd || u.End <= u.Start || u.End > textRva + text.Length || u.Info.Length < 4 || (u.Info.Length & 3) != 0 || u.Info[0] != 1)
            {
                throw new InvalidDataException("Invalid or overlapping AMD64 unwind range.");
            }

            previousEnd = u.End;
            WriteInt32(pdata, i * 12, u.Start);
            WriteInt32(pdata, i * 12 + 4, u.End);
            WriteInt32(pdata, i * 12 + 8, xdataRva + (int)xs.Length);
            xs.Write(u.Info);
        }

        byte[] xdata = xs.ToArray();
        int relocRva = Align(xdataRva + xdata.Length, 0x1000);
        byte[] reloc = new byte[12];
        WriteInt32(reloc, 0, textRva);
        WriteInt32(reloc, 4, 12); // two IMAGE_REL_BASED_ABSOLUTE padding entries
        var parts = new[]
        {
            (".text", textRva, text, 0x60000020u),
            (".rdata", roRva, ro, 0x40000040u),
            (".data", dataRva, importLayout.Bytes, 0xc0000040u),
            (".pdata", pdataRva, pdata, 0x40000040u),
            (".xdata", xdataRva, xdata, 0x40000040u),
            (".reloc", relocRva, reloc, 0x42000040u)
        };
        int raw = header;
        var sections = new List<PeSectionEvidence>();
        foreach (var (name, rva, bytes, flags) in parts)
        {
            sections.Add(new(name, rva, bytes.Length, raw, Align(bytes.Length, 0x200), flags));
            raw += Align(bytes.Length, 0x200);
        }

        byte[] image = new byte[raw];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        WriteInt32(image, 0x3c, 0x80);
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        int coff = 0x84, opt = coff + 20, table = opt + 0xf0;
        WriteUInt16(image, coff, 0x8664);
        WriteUInt16(image, coff + 2, parts.Length);
        WriteUInt16(image, coff + 16, 0xf0);
        WriteUInt16(image, coff + 18, 0x22);
        WriteUInt16(image, opt, 0x20b);
        WriteInt32(image, opt + 4, sections[0].RawSize);
        WriteInt32(image, opt + 8, sections.Skip(1).Sum(s => s.RawSize));
        WriteInt32(image, opt + 16, textRva);
        WriteInt32(image, opt + 20, textRva);
        WriteUInt64(image, opt + 24, 0x140000000);
        WriteInt32(image, opt + 32, 0x1000);
        WriteInt32(image, opt + 36, 0x200);
        WriteUInt16(image, opt + 40, 6);
        WriteUInt16(image, opt + 48, 6);
        WriteInt32(image, opt + 56, Align(relocRva + reloc.Length, 0x1000));
        WriteInt32(image, opt + 60, header);
        WriteUInt16(image, opt + 68, 2);
        WriteUInt16(image, opt + 70, 0x8160);
        WriteUInt64(image, opt + 72, 0x400000);
        WriteUInt64(image, opt + 80, 0x4000);
        WriteUInt64(image, opt + 88, 0x100000);
        WriteUInt64(image, opt + 96, 0x1000);
        WriteInt32(image, opt + 108, 16);
        Directory(1, dataRva, (nativeImports.Select(i => i.Module).Distinct(StringComparer.OrdinalIgnoreCase).Count() + 1) * 20);
        Directory(3, pdataRva, pdata.Length);
        Directory(5, relocRva, reloc.Length);
        Directory(12, dataRva + importLayout.IatOffset, importLayout.IatSize);
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            System.Text.Encoding.ASCII.GetBytes(s.Name).CopyTo(image, table + i * 40);
            WriteInt32(image, table + i * 40 + 8, s.VirtualSize);
            WriteInt32(image, table + i * 40 + 12, s.Rva);
            WriteInt32(image, table + i * 40 + 16, s.RawSize);
            WriteInt32(image, table + i * 40 + 20, s.FileOffset);
            WriteInt32(image, table + i * 40 + 36, unchecked((int)s.Characteristics));
            parts[i].Item3.CopyTo(image, s.FileOffset);
        }

        return new(image, symbols, sections);

        void Directory(int index, int rva, int size)
        {
            WriteInt32(image, opt + 112 + index * 8, rva);
            WriteInt32(image, opt + 116 + index * 8, size);
        }


        void Add(PeFunctionBody body, int rva)
        {
            if (body.Bytes.Length == 0 || !symbols.TryAdd(body.Name, rva))
            {
                throw new InvalidDataException("Empty or duplicate package symbol: " + body.Name);
            }

            foreach (var e in body.Exports ?? new Dictionary<string, int>())
            {
                if (e.Value < 0 || e.Value >= body.Bytes.Length || !symbols.TryAdd(e.Key, rva + e.Value))
                {
                    throw new InvalidDataException("Invalid package export: " + e.Key);
                }
            }
        }

        void Patch(PeFunctionBody body, byte[] dest, int baseRva)
        {
            foreach (var f in body.RelativeCallFixups ?? [])
            {
                int at = f.DisplacementOffset;
                if (at < 0 || at > body.Bytes.Length - 4 || !symbols.TryGetValue(f.TargetFunction, out int target))
                {
                    throw new InvalidDataException("Invalid/unresolved package reference: " + f.TargetFunction);
                }

                bool valid = f.Kind switch
                {
                    PeRelativeReferenceKind.Branch => at > 0 && body.Bytes[at - 1] is 0xe8 or 0xe9,
                    PeRelativeReferenceKind.RipRelativeAddress => at >= 3 && body.Bytes[at - 2] == 0x8d && (body.Bytes[at - 1] & 0xc7) == 5,
                    PeRelativeReferenceKind.DataRelative32 => true,
                    _ => false
                };
                if (!valid)
                {
                    throw new InvalidDataException("Invalid package rel32 instruction.");
                }

                WriteInt32(dest, symbols[body.Name] - baseRva + at, checked(target - (symbols[body.Name] + at + 4)));
            }
        }
    }
}
