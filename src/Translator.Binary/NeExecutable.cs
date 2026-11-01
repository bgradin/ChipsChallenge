namespace Translator.Binary;

public sealed record NeHeader(ushort LinkerVersion, ushort EntrySegment, ushort EntryOffset, ushort SegmentCount, ushort ModuleReferenceCount, uint SegmentTableOffset);
public sealed record NeDosHeader(int FileOffset, ushort LastPageBytes, ushort PageCount, ushort RelocationCount, ushort HeaderParagraphs, ushort MinimumAllocation, ushort MaximumAllocation, ushort InitialSs, ushort InitialSp, ushort Checksum, ushort InitialIp, ushort InitialCs, ushort RelocationTableOffset, ushort Overlay, string ReservedBytesHex, uint NewHeaderOffset);
public sealed record NeMetadata(int FileOffset, ushort LinkerVersion, uint Checksum, ushort Flags, ushort AutoDataSegment, ushort HeapSize, ushort StackSize, ushort InitialCs, ushort InitialIp, ushort InitialSs, ushort InitialSp, ushort SegmentCount, ushort ModuleReferenceCount, ushort EntryTableOffset, ushort EntryTableLength, ushort SegmentTableOffset, ushort ResourceTableOffset, ushort ResidentNameTableOffset, ushort ModuleReferenceTableOffset, ushort ImportedNameTableOffset, uint NonResidentNameTableOffset, ushort NonResidentNameTableLength, ushort MovableEntryCount, ushort AlignmentShift, ushort ResourceSegmentCount, byte TargetOs, byte OtherFlags, ushort ReturnThunkOffset, ushort SegmentReferenceOffset, ushort MinimumSwapSize, ushort ExpectedWindowsVersion);
public sealed record NeSegment(int Index, int SourceFileOffset, ushort RawSector, ushort RawLength, ushort Flags, ushort RawMinimumAllocation, int FileOffset, int FileLength, int MinimumAllocation, int MemoryLength, int AlignmentShift)
{
    public bool IsData => (Flags & 1) != 0;
    public bool IsReadable => IsData || (Flags & 0x80) == 0;
    public bool IsWritable => IsData && (Flags & 0x80) == 0;
    public bool HasRelocations => (Flags & 0x100) != 0;
}

public sealed record NeEntry(int Ordinal, int SourceFileOffset, string Kind, byte Flags, int? SegmentIndex, ushort? Offset, ushort? MovableSignature)
{
    public bool Exported => (Flags & 1) != 0;
}

public sealed record NeName(int SourceFileOffset, string Name, ushort Ordinal);
public sealed record NeImportedName(int SourceFileOffset, int TableOffset, string Name);
public sealed record NeModuleReference(int Index, int SourceFileOffset, ushort NameOffset, string Name)
{
    public string NormalizedName => Name.ToUpperInvariant();
}

public sealed record NeResourceIdentity(ushort RawValue, int? Id, string? Name);
public sealed record NeResource(int SourceFileOffset, NeResourceIdentity Type, NeResourceIdentity Identity, ushort RawOffset, ushort RawLength, int AlignmentShift, int FileOffset, int Length, ushort Flags, ushort Handle, ushort Usage, uint TypeReserved);
public sealed record NeExecutable(int FileSize, string Sha256, NeDosHeader DosHeader, NeMetadata NeHeader, IReadOnlyList<NeSegment> Segments, IReadOnlyList<NeEntry> EntryPoints, IReadOnlyList<NeName> ResidentNames, IReadOnlyList<NeName> NonResidentNames, IReadOnlyList<NeImportedName> ImportedNames, IReadOnlyList<NeModuleReference> ModuleReferences, IReadOnlyList<NeResource> Resources)
{
    public IReadOnlyList<NeRelocation> Relocations { get; init; } = Array.Empty<NeRelocation>();
}
