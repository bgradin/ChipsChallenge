using System.Runtime.InteropServices;
using Xunit;

namespace Translator.Integration.Tests;

public sealed partial class NeNativeStartupTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] bytes, nuint size, out nuint read);
    private sealed record GuestSnapshot(byte[] Data, byte[] Game, ushort Pointer)
    {
        public ushort Word(int at) => BitConverter.ToUInt16(Game, at);

        public ushort Global(int at) => BitConverter.ToUInt16(Data, at);
        public int X => Word(0x808);
        public int Y => Word(0x80a);
        public int Death => Word(0x816);
        public ushort[] Inventory => Enumerable.Range(0, 8).Select(i => Global(0x1682 + i * 2)).ToArray();
    }

    private static Dictionary<ushort, (nint Address, int Size)> ReadSegmentMaps(ParitySession session)
    {
        var result = new Dictionary<ushort, (nint, int)>();
        using var file = new FileStream(Path.Combine(session.Directory, "execution.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(file);
        reader.BaseStream.Position = 96;
        while (reader.BaseStream.Position + 32 <= reader.BaseStream.Length)
        {
            uint[] row = Enumerable.Range(0, 8).Select(_ => reader.ReadUInt32()).ToArray();
            if (row[0] == 8)
            {
                result[(ushort)row[1]] = (unchecked((nint)(long)((ulong)row[3] << 32 | row[2])), checked((int)row[4]));
            }
        }

        return result;
    }

    private static GuestSnapshot Observe(ParitySession session)
    {
        ushort ds = (ushort)_package.Value.Manifest.Package!.Memory.InitialRegisters.DS;
        var maps = ReadSegmentMaps(session);
        Assert.True(maps.TryGetValue(ds, out var map), "Initial data segment was not described in the opt-in trace.");
        var data = new byte[map.Size];
        Assert.True(ReadProcessMemory(session.Process.Handle, map.Address, data, (nuint)data.Length, out var count));
        Assert.Equal((nuint)data.Length, count);
        ushort pointer = BitConverter.ToUInt16(data, 0x1680);
        Assert.InRange((int)pointer, 1, data.Length - 0xa42);
        return new(data, data.AsSpan(pointer, 0xa42).ToArray(), pointer);
    }
}
