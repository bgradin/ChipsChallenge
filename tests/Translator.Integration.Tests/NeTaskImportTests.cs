using System.Text;
using Translator.Analysis;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeTaskImportTests
{
    private sealed class TaskFixture : IDisposable
    {
        public NeLoadedProgram Image { get; } = new NeProgramLoader().Load(NeLoaderTests.Fixture());
        public Win16TaskMemory Task
        {
            get;
        }
        public Win16TaskKernelServices Kernel
        {
            get;
        }

        public MachineState State;
        public SegmentMemory Memory => Image.Memory;

        public TaskFixture()
        {
            State = Image.InitialState;
            Task = new(Image, new());
            Kernel = new(Task, Memory, State.DS);
            Task.GrowDataSegment(65536);
            State.SP = 1024;
            State.DX = 0xabcd;
        }

        public ushort Call(string identity, params uint[] values)
        {
            var contract = Win16ExportContracts.Get(identity);
            foreach (var arg in contract.Arguments)
            {
                uint value = values[int.Parse(arg.Name.AsSpan(3))];
                ushort offset = (ushort)(State.SP + 4 + arg.StackWord * 2);
                if (arg.Bytes == 4)
                {
                    Memory.WriteDword(State.SS, offset, value);
                }
                else
                {
                    Memory.WriteWord(State.SS, offset, (ushort)value);
                }
            }

            return Kernel.Invoke(identity, ref State);
        }

        public uint Pointer(ushort offset) => (uint)State.DS << 16 | offset;

        public void Dispose()
        {
            Kernel.Dispose();
            Task.Dispose();
            Image.Dispose();
        }
    }

    [Fact]
    public void EveryOriginalIdentityHasDistinctPinnedAbiEvidenceIncludingRegisterAndConstantExports()
    {
        Assert.Equal(123, Win16ExportContracts.All.Count);
        Assert.Equal(122, Win16ExportContracts.All.Count(c => c.Kind != "Constant"));
        Assert.Equal("Register", Win16ExportContracts.Get("KERNEL.#91").Convention);
        Assert.Equal("Register", Win16ExportContracts.Get("KERNEL.#102").Convention);
        var flags = Win16ExportContracts.Get("KERNEL.#178");
        Assert.Equal("Constant", flags.Kind);
        Assert.Equal((ushort)0x413, flags.ConstantValue);
        var print = Win16ExportContracts.Get("USER.#420");
        Assert.True(print.Variadic);
        Assert.Equal("Cdecl", print.Convention);
        Assert.Equal((ushort)0, print.CleanupBytes);
        Assert.Equal(new[] { 0, 2 }, print.Arguments.Select(a => a.StackWord));
        var create = Win16ExportContracts.Get("USER.#41");
        Assert.Equal(11, create.Arguments.Count);
        Assert.Equal((ushort)30, create.CleanupBytes);
        var global = Win16ExportContracts.Get("KERNEL.#15");
        Assert.Equal(new[] { 2, 0 }, global.Arguments.Select(a => a.StackWord));
        Assert.Equal((ushort)6, global.CleanupBytes);
    }

    [Fact]
    public void GlobalApiUsesOriginalPascalDwordArgumentsAndWordReturnsDoNotEraseDx()
    {
        using var f = new TaskFixture();
        var m = f.Memory;
        var s = f.State;
        m.WriteDword(s.SS, (ushort)(s.SP + 4), 65536);
        m.WriteWord(s.SS, (ushort)(s.SP + 8), 0x42);
        Assert.Equal((ushort)6, f.Kernel.Invoke("KERNEL.#15", ref f.State));
        ushort h = f.State.AX;
        Assert.NotEqual((ushort)0, h);
        Assert.Equal((ushort)0xabcd, f.State.DX);
        Assert.Equal((ushort)2, f.Call("KERNEL.#20", h));
        Assert.Equal((ushort)0, f.State.AX);
        Assert.Equal((ushort)1, f.State.DX);
        f.Call("KERNEL.#18", h);
        ushort selector = f.State.DX;
        Assert.NotEqual((ushort)0, selector);
        Assert.Equal((ushort)0, f.State.AX);
        f.Memory.WriteWord(selector, 0, 0xbeef);
        using var alias = m.CreateAlias(m.AllocateSelector(), selector);
        f.Call("KERNEL.#17", h);
        Assert.Equal((ushort)0, f.State.AX);
        Assert.True(alias.IsDisposed);
        Assert.False(m.TryGetSegment(selector, out _));
        f.Call("KERNEL.#18", h);
        Assert.Equal((ushort)0, f.State.AX);
        Assert.Equal((ushort)0, f.State.DX);
        f.Call("KERNEL.#20", h);
        Assert.Equal((ushort)0, f.State.AX);
        Assert.Equal((ushort)0, f.State.DX);
    }

    [Fact]
    public void GlobalResizeLockUnlockAndFailureKeepOriginalStorageAndCleanup()
    {
        using var f = new TaskFixture();
        f.Call("KERNEL.#15", 2, 16);
        ushort h = f.State.AX;
        f.Call("KERNEL.#18", h);
        ushort selector = f.State.DX;
        f.Memory.WriteWord(selector, 0, 0x1234);
        Assert.Equal((ushort)8, f.Call("KERNEL.#16", h, 32, 2));
        Assert.Equal(h, f.State.AX);
        Assert.Equal(32, f.Memory.GetSegment(selector).Size);
        f.Task.Global.FailNextReallocation = true;
        f.Call("KERNEL.#16", h, 64, 2);
        Assert.Equal((ushort)0, f.State.AX);
        Assert.Equal(32, f.Memory.GetSegment(selector).Size);
        Assert.Equal((ushort)0x1234, f.Memory.ReadWord(selector, 0));
        f.Call("KERNEL.#19", h);
        Assert.Equal((ushort)0, f.State.AX);
        f.Call("KERNEL.#17", h);
        Assert.Equal(0, f.Task.Global.AllocationCount);
        f.Call("KERNEL.#15", 0x100, 1);
        Assert.Equal((ushort)0, f.State.AX);
    }

    [Fact]
    public void LocalApiReturnsDsOffsetsAndPreservesContentsWhenItMoves()
    {
        using var f = new TaskFixture();
        Assert.Equal((ushort)4, f.Call("KERNEL.#5", 0x40, 7));
        ushort p = f.State.AX;
        Assert.Equal((ushort)0, f.Memory.ReadWord(f.State.DS, p));
        f.Memory.WriteWord(f.State.DS, p, 0xbeef);
        f.Call("KERNEL.#5", 0x20, 8);
        ushort neighbour = f.State.AX;
        f.Memory.WriteWord(f.State.DS, neighbour, 0x9999);
        Assert.Equal((ushort)6, f.Call("KERNEL.#6", p, 128, 0x62));
        ushort moved = f.State.AX;
        Assert.NotEqual(p, moved);
        Assert.Equal((ushort)0xbeef, f.Memory.ReadWord(f.State.DS, moved));
        Assert.Equal((ushort)0x9999, f.Memory.ReadWord(f.State.DS, neighbour));
        f.Call("KERNEL.#10", moved);
        Assert.True(f.State.AX >= 128);
        f.Call("KERNEL.#7", moved);
        Assert.Equal((ushort)0, f.State.AX);
        f.Call("KERNEL.#7", neighbour);
        Assert.Equal(0, f.Task.Local.AllocationCount);
        f.Call("KERNEL.#7", 123);
        Assert.Equal((ushort)123, f.State.AX);
        f.Call("KERNEL.#10", 123);
        Assert.Equal((ushort)0, f.State.AX);
        f.Call("KERNEL.#5", 2, 4);
        Assert.Equal((ushort)0, f.State.AX);
    }

    [Fact]
    public void SegmentLocksUseCurrentDsAndUnlockDoesNotInventAReturnValue()
    {
        using var f = new TaskFixture();
        Assert.Equal((ushort)2, f.Call("KERNEL.#23", 0xffff));
        Assert.Equal(f.State.DS, f.State.AX);
        f.State.AX = 0x7777;
        Assert.Equal((ushort)2, f.Call("KERNEL.#24", 0xffff));
        Assert.Equal((ushort)0x7777, f.State.AX);
        f.Call("KERNEL.#23", 0x7777);
        Assert.Equal((ushort)0, f.State.AX);
        f.Call("KERNEL.#15", 2, 8);
        ushort h = f.State.AX;
        f.Call("KERNEL.#23", h);
        Assert.Equal(h, f.State.AX);
        f.Call("KERNEL.#24", h);
        Assert.True(f.Task.Global.TryGetAllocation(h, out var block));
        Assert.Equal((ushort)0, block!.LockCount);
    }

    [Fact]
    public void StringsValidateEntireViewsAndCopyOverlapWithoutWideningPointers()
    {
        using var f = new TaskFixture();
        var m = f.Memory;
        uint src = f.Pointer(100), dst = f.Pointer(200);
        m.WriteBytes(f.State.DS, 100, Encoding.Latin1.GetBytes("abc\0"));
        Assert.Equal((ushort)8, f.Call("KERNEL.#88", dst, src));
        Assert.Equal((ushort)200, f.State.AX);
        Assert.Equal(f.State.DS, f.State.DX);
        Assert.Equal("abc", Win16TaskKernelServices.ReadString(m, dst));
        Assert.Equal((ushort)4, f.Call("KERNEL.#90", dst));
        Assert.Equal((ushort)3, f.State.AX);
        f.Call("KERNEL.#88", f.Pointer(101), src);
        Assert.Equal("abc", Win16TaskKernelServices.ReadString(m, f.Pointer(101)));
        m.WriteBytes(f.State.DS, 200, Encoding.ASCII.GetBytes("AbC\0"));
        f.Call("USER.#471", f.Pointer(101), dst);
        Assert.Equal((ushort)0, f.State.AX);
        f.Call("USER.#471", f.Pointer(101), f.Pointer(102));
        Assert.True(unchecked((short)f.State.AX) < 0);
        m.WriteWord(f.State.DS, 0xfffe, 0x7777);
        Assert.Throws<InvalidDataException>(() => f.Call("KERNEL.#88", f.Pointer(0xfffe), src));
        Assert.Equal((ushort)0x7777, m.ReadWord(f.State.DS, 0xfffe));
        Assert.Throws<InvalidDataException>(() => f.Call("KERNEL.#90", f.Pointer(0xfffe)));
        m.WriteBytes(f.State.DS, 100, Encoding.ASCII.GetBytes("abc\0"));
        ushort argumentSlot = (ushort)(f.State.SP + 8);
        f.Call("KERNEL.#88", f.Pointer(argumentSlot), src);
        Assert.Equal(argumentSlot, f.State.AX);
        Assert.Equal(f.State.DS, f.State.DX);
    }

    [Fact]
    public void EnvironmentAndErrorModeAreTaskOwnedAndNeverLeakHostPointers()
    {
        using var f = new TaskFixture();
        f.Call("KERNEL.#107", 0x1234);
        Assert.Equal((ushort)0, f.State.AX);
        f.Call("KERNEL.#107", 0);
        Assert.Equal((ushort)0x1234, f.State.AX);
        Assert.Equal((ushort)0xabcd, f.State.DX);
        Assert.Equal((ushort)0, f.Call("KERNEL.#131"));
        ushort selector = f.State.DX;
        Assert.NotEqual((ushort)0, selector);
        Assert.Equal((ushort)0, f.State.AX);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 0, 0, 0, 0 }, f.Memory.GetSegment(selector).Memory.ToArray());
        Assert.Throws<InvalidOperationException>(() => f.Memory.WriteByte(selector, 0, 1));
        f.Call("KERNEL.#131");
        Assert.Equal(selector, f.State.DX);
        f.Kernel.Dispose();
        Assert.False(f.Memory.TryGetSegment(selector, out _));
        Assert.Throws<ObjectDisposedException>(() => f.Call("KERNEL.#131"));
    }

    [Fact]
    public void LocalApiRejectsAnUnrelatedDsAndUnsupportedServicesFailExplicitly()
    {
        using var f = new TaskFixture();
        using var other = f.Memory.CreateSegment(0x7000, 1024);
        f.Memory.Map(other);
        f.State.DS = other.Selector;
        Assert.Throws<NotSupportedException>(() => f.Call("KERNEL.#5", 0, 8));
        Assert.Equal(0, f.Task.Local.AllocationCount);
        Assert.Throws<NotSupportedException>(() => f.Kernel.Invoke("USER.#41", ref f.State));
    }

    [SuppliedNeFact]
    public void OriginalInventoryCertifiesIdentitySeparatelyFromWholeTaskServiceReadiness()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var report = NeImportContractAuditBuilder.Build(bytes);
        Assert.Equal(123, report.TotalImports);
        Assert.Equal(123, report.VerifiedContracts);
        Assert.Equal(122, report.CallableImports);
        Assert.Equal(1, report.ConstantExports);
        Assert.Equal(0, report.UnknownIdentities);
        Assert.False(report.CompleteApplicationServiceCoverage);
        Assert.Equal(5, report.DynamicProcedureNames.Count);
        Assert.All(report.Imports, i => Assert.NotEmpty(i.Sources));
        Assert.Equal(Win16TaskServiceBindings.All.Count, report.ImplementedButNotExercised);
        string dll = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"))!, "WEP4UTIL.DLL");
        if (File.Exists(dll))
        {
            var data = File.ReadAllBytes(dll);
            Win16ExportContracts.VerifyWepImage(data);
            data[1088 + 0x16e3] ^= 1;
            Assert.Throws<InvalidDataException>(() => Win16ExportContracts.VerifyWepImage(data));
        }
    }
}
