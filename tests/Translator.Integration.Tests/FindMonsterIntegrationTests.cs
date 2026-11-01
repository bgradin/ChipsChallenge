using Translator.Assembly;
using Translator.CodeGen;
using Translator.Linker;
using System.Buffers.Binary;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class FindMonsterIntegrationTests
{
    private static string Source => File.ReadAllText(Path.GetFullPath("../../../../../tests/Fixtures/Compiler/Acceptance.asm"));

    [Theory]
    [InlineData(3, 8, 1)]
    [InlineData(4, 9, -1)]
    [InlineData(1, 2, 0)]
    public void GeneratedNativeFindMonsterSearchesAcrossSegmentedRecords(short x, short y, short expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Source);
            var parsed = new AssemblyParser().ParseSelected(path, new[] { "FindMonster" });
            Assert.Empty(parsed.Diagnostics);
            var function = Assert.Single(parsed.Module.Functions);
            NativeFindMonster native = FindMonsterCodeGenerator.Generate(function);
            const ushort dataSegment = 0x1000, monsterSegment = 0x2000;
            var memory = new SegmentMemory();
            memory.Map(new Win16Segment(dataSegment));
            memory.Map(new Win16Segment(monsterSegment));
            memory.WriteWord(dataSegment, 0x1680, 0x3000); // GameStatePtr
            memory.WriteWord(dataSegment, 0x3000 + 0x0928, 3); // MonsterListLen
            memory.WriteFarPointer(dataSegment, 0x3000 + 0x092E, new FarPointer(0x0100, monsterSegment));
            WriteMonster(memory, monsterSegment, 0x0100, 1, 2);
            WriteMonster(memory, monsterSegment, 0x010B, 3, 8);
            WriteMonster(memory, monsterSegment, 0x0116, 6, 7);
            short actual = native(new FindMonsterContext(memory, dataSegment), x, y);
            Assert.Equal(expected, actual);
            Assert.Equal(ReferenceFind(memory, dataSegment, x, y), actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GeneratedNativeFindMonsterReturnsMinusOneForEmptyList()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Source);
            var function = Assert.Single(new AssemblyParser().ParseSelected(path, new[] { "FindMonster" }).Module.Functions);
            var memory = new SegmentMemory();
            memory.Map(new Win16Segment(0x1000));
            memory.WriteWord(0x1000, 0x1680, 0x3000);
            memory.WriteWord(0x1000, 0x3928, 0);
            Assert.Equal((short)-1, FindMonsterCodeGenerator.Generate(function)(new(memory, 0x1000), 1, 2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindMonsterArtifactCarriesAResolvableRuntimeCallRelocation()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Source);
            var function = Assert.Single(new AssemblyParser().ParseSelected(path, new[] { "FindMonster" }).Module.Functions);
            var artifact = FindMonsterCodeGenerator.GenerateArtifact(function, X64Abi.Windows);
            Assert.NotEmpty(artifact.Relocations!);
            var relocation = artifact.Relocations![0];
            Assert.Equal(GrowArrayCodeGenerator.WriteWordHelperSymbol, relocation.Symbol);
            Assert.Equal(MachineCodeRelocationKind.Relative32Call, relocation.Kind);
            Assert.True(artifact.Bytes.Length > 200); // artifact contains the loop and comparison logic
            byte[] code = MachineCodeArtifactRelocator.Relocate(artifact, 0x100000, new Dictionary<string, ulong> { [GrowArrayCodeGenerator.ReadWordHelperSymbol] = 0x101000, [GrowArrayCodeGenerator.WriteWordHelperSymbol] = 0x102000, [GrowArrayCodeGenerator.CallServiceHelperSymbol] = 0x103000, ["__win16_read_dword"] = 0x104000 });
            Assert.Equal(0x102000L - (0x100000L + relocation.Offset + 4), BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(relocation.Offset, 4)));
            byte[] image = new Pe32PlusWriter().WriteExecutable(PeFunctionBody.FromArtifact("FindMonster", artifact), new[] { new PeFunctionBody(FindMonsterCodeGenerator.RuntimeHelperSymbol, new byte[] { 0xC3 }), new PeFunctionBody(GrowArrayCodeGenerator.WriteWordHelperSymbol, new byte[] { 0xC3 }), new PeFunctionBody("__win16_read_dword", new byte[] { 0xC3 }) });
            Assert.True(Pe32PlusWriter.IsAmd64Pe32Plus(image));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteMonster(SegmentMemory memory, ushort segment, ushort offset, ushort x, ushort y)
    {
        memory.WriteWord(segment, unchecked((ushort)(offset + 1)), x);
        memory.WriteWord(segment, unchecked((ushort)(offset + 3)), y);
    }

    // Independent reference for differential validation of the generated native entry point.
    private static short ReferenceFind(SegmentMemory memory, ushort dataSegment, short x, short y)
    {
        ushort state = memory.ReadWord(dataSegment, 0x1680);
        short length = unchecked((short)memory.ReadWord(dataSegment, unchecked((ushort)(state + 0x928))));
        if (length <= 0)
        {
            return -1;
        }

        var list = memory.ReadFarPointer(dataSegment, unchecked((ushort)(state + 0x92E)));
        for (short i = 0; i < length; i++)
        {
            ushort record = unchecked((ushort)(list.Offset + i * 11));
            if (memory.ReadWord(list.Segment, unchecked((ushort)(record + 1))) == unchecked((ushort)x) && memory.ReadWord(list.Segment, unchecked((ushort)(record + 3))) == unchecked((ushort)y))
            {
                return i;
            }
        }

        return -1;
    }
}
