using System.Buffers.Binary;
using Translator.Binary;
using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class SuppliedNeFactAttribute : FactAttribute
{
    public SuppliedNeFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")))
        {
            Skip = "Set CHIPS_NE_INPUT to the locally supplied original CHIPS.EXE; assets are not committed.";
        }
    }
}

public sealed class SuppliedNeTheoryAttribute : TheoryAttribute
{
    public SuppliedNeTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")))
        {
            Skip = "Set CHIPS_NE_INPUT to the locally supplied original CHIPS.EXE; assets are not committed.";
        }
    }
}

public sealed class SuppliedNeWindowsFactAttribute : FactAttribute
{
    public SuppliedNeWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
        else if (!File.Exists(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")))
        {
            Skip = "Set CHIPS_NE_INPUT to the locally supplied original CHIPS.EXE; assets are not committed.";
        }
    }
}

public sealed class NeOriginalImageTests
{
    [SuppliedNeFact]
    public void OriginalImportInventoryAndSupportedProcedurePointersResolveAutomatically()
    {
        byte[] original = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var executable = new NeExecutableReader().Read(original);
        using var runtime = NeRuntimeProgram.Load(original, new(AllowIncompleteRelocations: true));
        Assert.Equal(123, runtime.Report.Imports.Count);
        Assert.Equal(6, runtime.Report.Imports.Count(i => i.Support == NeImportSupport.SupportedRuntimeService));
        Assert.Equal(1, runtime.Report.Imports.Count(i => i.Support == NeImportSupport.KnownUnsupportedService));
        Assert.Equal(116, runtime.Report.Imports.Count(i => i.Support == NeImportSupport.UnknownImport));
        Assert.Equal(OperatingSystem.IsWindows()
            ? 6
            : 4, runtime.Report.Imports.Count(i => i.BindingStatus == "Bound"));
        Assert.False(runtime.Report.FullyRelocated);
        var state = runtime.Image.InitialState;
        var memory = runtime.Image.Memory;
        // Select the original far-pointer relocation, not an acceptance profile or declaration list.
        var allocation = executable.Relocations.First(r => r.Import is not null && NeImportResolver.Classify(r.Import).Api?.Name == "GlobalAlloc" && r.RawAddressType == 3);
        var pointer = memory.ReadFarPointer(runtime.Image.SelectorFor(allocation.SourceSegmentIndex), allocation.SourceOffsets[0]);
        Assert.Equal(new FarPointer(16, 0xf000), pointer);
        Win16Stack.Push(ref state, memory, 0x42);
        Win16Stack.Push(ref state, memory, 0);
        Win16Stack.Push(ref state, memory, 12);
        runtime.Dispatcher.Call(ref state, memory, new Win16CodeAddress(pointer.Segment, pointer.Offset), CallDistance.Far);
        Assert.NotEqual(0, state.AX);
        Assert.Equal(runtime.Image.InitialState.SP, state.SP);
        Assert.Contains(memory.Segments, segment => segment.Size == 12);
        runtime.Dispose();
        Assert.Empty(memory.Segments);
    }

    [SuppliedNeFact]
    public void OriginalImageLoadsAndAll965SitesMatchIndependentlyCalculatedBytes()
    {
        byte[] original = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var executable = new NeExecutableReader().Read(original);
        Assert.Equal("8e26acd67cf120bd5b512de4b4e78b80aca1579413cd04f3b2b68909a375866c", executable.Sha256);
        Assert.Equal(218, executable.Relocations.Count);
        Assert.Equal(965, executable.Relocations.Sum(r => r.SourceOffsets.Count));
        Assert.All(executable.Relocations, r => Assert.True(r.Supported, r.Diagnostic));
        // Only address bindings: no API names are inferred, no service implementations are supplied.
        var bindings = executable.Relocations.Where(r => r.Import is not null).Select(r => r.Import!).Distinct().Select((import, i) => (import, address: new Win16CodeAddress(0x7000, (ushort)(i * 8)))).ToDictionary(pair => pair.import, pair => pair.address);
        Assert.Equal(123, bindings.Count);
        var memory = new SegmentMemory();
        var program = new NeProgramLoader().Load(original, importResolver: import => bindings[import], memory: memory);
        Assert.True(program.Report.FullyRelocated);
        Assert.Equal(10, memory.Segments.Count);
        Assert.Equal(0x1048, program.InitialState.DS);
        Assert.Equal(0x3738, program.InitialState.SP);
        Assert.Equal(16184, memory.GetSegment(program.InitialState.DS).Size);
        Assert.Equal(original.AsSpan(executable.Segments[9].FileOffset, 5944).ToArray(), memory.ReadBytes(program.InitialState.DS, 0, 5944));
        Assert.All(memory.ReadBytes(program.InitialState.DS, 5944, 10240), b => Assert.Equal(0, b));
        Assert.Equal(0, memory.ReadByte(program.SelectorFor(6), 1883));
        foreach (var segment in executable.Segments)
        {
            var expected = new byte[program.Report.Segments.Single(s => s.SegmentIndex == segment.Index).AllocatedLength];
            original.AsSpan(segment.FileOffset, segment.FileLength).CopyTo(expected);
            foreach (var relocation in executable.Relocations.Where(r => r.SourceSegmentIndex == segment.Index))
            {
                Assert.False(relocation.Additive);
                var target = relocation.Import is not null
                    ? bindings[relocation.Import]
                    : new Win16CodeAddress(program.SelectorFor(relocation.TargetSegmentIndex!.Value), relocation.TargetOffset!.Value);
                foreach (ushort offset in relocation.SourceOffsets)
                {
                    if (relocation.RawAddressType is 3 or 5)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(offset), target.Offset);
                    }

                    if (relocation.RawAddressType is 2 or 3)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(offset + (relocation.RawAddressType == 3
                            ? 2
                            : 0)), target.Segment);
                    }
                }
            }

            Assert.Equal(expected, memory.GetSegment(program.SelectorFor(segment.Index)).Memory.ToArray());
        }

        // A changed authoritative initialized byte must change the loaded state and identity.
        byte[] changed = (byte[])original.Clone();
        changed[executable.Segments[9].FileOffset + 100] ^= 1;
        using var mutation = new NeProgramLoader().Load(changed, importResolver: import => bindings[import]);
        Assert.NotEqual(program.Report.Sha256, mutation.Report.Sha256);
        Assert.NotEqual(memory.ReadByte(program.InitialState.DS, 100), mutation.Memory.ReadByte(mutation.InitialState.DS, 100));
        program.Dispose();
        Assert.Empty(memory.Segments);
    }
}
