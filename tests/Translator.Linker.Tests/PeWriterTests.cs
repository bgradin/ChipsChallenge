using Translator.CodeGen;
using System.Buffers.Binary;
using Xunit;

namespace Translator.Linker.Tests;

public class PeWriterTests
{
    [Fact]
    public void RelativeAddressAndDataReferencesAreExplicitAndBoundsChecked()
    {
        var data = new PeFunctionBody("data", new byte[] { 1, 2, 3, 4 });
        var entry = new PeFunctionBody("entry", new byte[] { 0x48, 0x8d, 0x0d, 0, 0, 0, 0, 0xc3 }, new[] { new PeRelativeCallFixup(3, "data", PeRelativeReferenceKind.RipRelativeAddress) });
        byte[] image = new Pe32PlusWriter().WriteProgram(entry, new[] { data });
        int at = 0x300;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, image.AsSpan(at + 7 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(at + 3)), 4).ToArray());
        Assert.Throws<InvalidDataException>(() => new Pe32PlusWriter().WriteProgram(entry with { RelativeCallFixups = new[] { new PeRelativeCallFixup(3, "data") } }, new[] { data }));
        var table = new PeFunctionBody("table", new byte[4], new[] { new PeRelativeCallFixup(0, "data", PeRelativeReferenceKind.DataRelative32) });
        Assert.True(Pe32PlusWriter.IsAmd64Pe32Plus(new Pe32PlusWriter().WriteProgram(table, new[] { data })));
        Assert.Throws<InvalidDataException>(() => new Pe32PlusWriter().WriteProgram(table with { RelativeCallFixups = new[] { new PeRelativeCallFixup(1, "data", PeRelativeReferenceKind.DataRelative32) } }, new[] { data }));
    }

    [Fact]
    public void WritesAmd64Pe32Plus()
    {
        var image = new Pe32PlusWriter().WriteExecutable(new byte[] { 0xC3 });
        Assert.True(Pe32PlusWriter.IsAmd64Pe32Plus(image));
    }

    [Fact]
    public void EntryPointCallsPayloadAndChecksItsOutputsBeforeExiting()
    {
        byte[] payload =
        {
            0x90,
            0xC3
        };
        byte[] image = new Pe32PlusWriter().WriteExecutable(payload);
        int entryOffset = 0x200;
        int callOffset = Find(image, entryOffset, new byte[] { 0xE8 });
        int callTarget = callOffset + 5 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(callOffset + 1, 4));
        Assert.Equal(0x300, callTarget);
        Assert.Equal(payload, image.AsSpan(callTarget, payload.Length).ToArray());
        Assert.True(image.AsSpan().IndexOf("ExitProcess"u8) >= 0);
        int firstCompare = Find(image, 0x200, new byte[] { 0x66, 0x83, 0x3D });
        int secondCompare = Find(image, firstCompare + 1, new byte[] { 0x66, 0x83, 0x3D });
        Assert.Equal(0x302, firstCompare + 8 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(firstCompare + 3, 4)));
        Assert.Equal(0x304, secondCompare + 8 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(secondCompare + 3, 4)));
        int[] failures = FindAll(image, 0x200, new byte[] { 0x0F, 0x85 });
        Assert.Equal(2, failures.Length);
        int firstFailureTarget = failures[0] + 6 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(failures[0] + 2, 4));
        int secondFailureTarget = failures[1] + 6 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(failures[1] + 2, 4));
        Assert.Equal(firstFailureTarget, secondFailureTarget);
        Assert.Equal((byte)0xB9, image[firstFailureTarget]);
    }

    [Fact]
    public void LinksMultipleNamedFunctionBodiesAndResolvesInternalRel32Calls()
    {
        var entryArtifact = new MachineCodeArtifact(new byte[] { 0xE8, 0, 0, 0, 0, 0xC3 }, "AMD64", X64Abi.Windows, "void TestEntry()", new[] { new MachineCodeRelocation(1, "FindMonster", MachineCodeRelocationKind.Relative32Call) });
        var entry = PeFunctionBody.FromArtifact("test_entry", entryArtifact);
        var findMonster = new PeFunctionBody("FindMonster", new byte[] { 0xB8, 0x01, 0, 0xC3 });
        var growArray = new PeFunctionBody("GrowArray", new byte[] { 0xB8, 0x02, 0, 0xC3 });
        byte[] image = new Pe32PlusWriter().WriteExecutable(entry, new[] { findMonster, growArray });
        Assert.True(Pe32PlusWriter.IsAmd64Pe32Plus(image));
        int callOffset = Find(image, 0x300, new byte[] { 0xE8 });
        int callTarget = callOffset + 5 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(callOffset + 1, 4));
        Assert.Equal(new byte[] { 0xB8, 0x01, 0, 0xC3 }, image.AsSpan(callTarget, 4).ToArray());
        Assert.True(Find(image, 0x300, new byte[] { 0xB8, 0x02, 0, 0xC3 }) >= 0);
    }

    [Fact]
    public void ImportsHaveSeparateDllDescriptorsTerminatedTablesAndRipRelativeStubs()
    {
        var imports = new[]
        {
            new PeNativeImport("GDI32.dll", "PatBlt", "native_PatBlt"),
            new PeNativeImport("GDI32.dll", "GdiFlush", "native_GdiFlush"),
            new PeNativeImport("USER32.dll", "GetDesktopWindow", "native_GetDesktopWindow")
        };
        var entry = new PeFunctionBody("entry", new byte[] { 0xE8, 0, 0, 0, 0, 0xC3 }, new[] { new PeRelativeCallFixup(1, "native_PatBlt") });
        byte[] image = new Pe32PlusWriter().WriteProgram(entry, Array.Empty<PeFunctionBody>(), imports);
        int opt = 0x98;

        int Raw(int rva) => rva - 0x1000 + 0x200;


        int Read32(int at) => BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(at));

        int directory = Raw(Read32(opt + 112 + 8));
        Assert.Equal(80, Read32(opt + 112 + 12));

        string Name(int rva)
        {
            int at = Raw(rva);
            return System.Text.Encoding.ASCII.GetString(image, at, Array.IndexOf(image, (byte)0, at) - at);
        }


        Assert.Equal("KERNEL32.dll", Name(Read32(directory + 12)));
        Assert.Equal("GDI32.dll", Name(Read32(directory + 32)));
        Assert.Equal("USER32.dll", Name(Read32(directory + 52)));
        Assert.All(image.AsSpan(directory + 60, 20).ToArray(), value => Assert.Equal((byte)0, value));
        int lookup = Raw(Read32(directory + 20)), iat = Raw(Read32(directory + 36));
        Assert.Equal("PatBlt", Name((int)BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(lookup)) + 2));
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(lookup + 16)));
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(iat + 16)));
        int call = 0x300;
        int stub = call + 5 + Read32(call + 1);
        Assert.Equal(new byte[] { 0xFF, 0x25 }, image.AsSpan(stub, 2).ToArray());
        Assert.Equal(iat, stub + 6 + Read32(stub + 2));
    }

    private static int Find(byte[] bytes, int start, byte[] pattern) => FindAll(bytes, start, pattern).FirstOrDefault(-1);

    private static int[] FindAll(byte[] bytes, int start, byte[] pattern)
    {
        var matches = new List<int>();
        for (int index = start; index <= bytes.Length - pattern.Length; index++)
        {
            if (bytes.AsSpan(index, pattern.Length).SequenceEqual(pattern))
            {
                matches.Add(index);
            }
        }

        return matches.ToArray();
    }
}
