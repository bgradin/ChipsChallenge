using System.Text;
using Translator.Binary;
using Translator.Assembly;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeImportTests
{
    internal static byte[] MemoryImportDataFixture()
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 1, 4, 1, 15), (3, 1, 12, 1, 16), (3, 1, 20, 1, 18), (3, 1, 28, 1, 19));
        NeLoaderTests.W(bytes, 0xc4, 0); // code has no fixups; pointers reside in data instead
        NeLoaderTests.W(bytes, 0xca, 64);
        NeLoaderTests.W(bytes, 0xcc, 0x101);
        NeLoaderTests.W(bytes, 0xce, 64);
        bytes.AsSpan(0x240, 34).CopyTo(bytes.AsSpan(0x440));
        foreach (int offset in new[]
        {
            4,
            12,
            20,
            28
        }

        )
        {
            NeLoaderTests.W(bytes, 0x400 + offset, 0xffff);
        }

        return bytes;
    }

    private static byte[] NameFixture(string name)
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 2, 4, 1, 7));
        bytes.AsSpan(0x120, 16).CopyTo(bytes.AsSpan(0x150));
        NeLoaderTests.W(bytes, 0x84, 0xd0);
        bytes.AsSpan(0x119, 0x27).Clear();
        bytes[0x119] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0x11a);
        return bytes;
    }

    [Fact]
    public void VerifiedOrdinalsMatchTheExistingRegistryWithoutAnotherSignatureTable()
    {
        foreach (var (module, name, ordinal) in new (string, string, ushort)[]
        {
            ("KERNEL", "GlobalAlloc", 15),
            ("KERNEL", "GlobalReAlloc", 16),
            ("KERNEL", "GlobalLock", 18),
            ("KERNEL", "GlobalUnlock", 19),
            ("GDI", "PatBlt", 29),
            ("USER", "DefWindowProc", 107)
        }

        )
        {
            Assert.Same(Win16ApiRegistry.Find($"{module}.{name}"), Win16ApiRegistry.Find(module, null, ordinal));
            Assert.Equal(ordinal, Win16ApiRegistry.Find(module, name, null)!.Ordinal);
        }

        Assert.Null(Win16ApiRegistry.Find("KERNEL", "#15", null));
    }

    [Fact]
    public void PreservesRawOrdinalAndNameReferencesAndClassifiesKnownUnsupportedAndUnknown()
    {
        var ordinal = Assert.Single(NeImportResolver.Discover(new NeExecutableReader().Read(NeLoaderTests.Fixture((3, 1, 4, 1, 15)))));
        var name = Assert.Single(NeImportResolver.Discover(new NeExecutableReader().Read(NameFixture("GlobalAlloc"))));
        Assert.Equal(NeImportSupport.SupportedRuntimeService, ordinal.Support);
        Assert.Null(ordinal.Original.Name);
        Assert.Equal((ushort?)15, ordinal.Original.Ordinal);
        Assert.Null(name.Original.Ordinal);
        Assert.Equal("GlobalAlloc", name.Original.Name);
        Assert.Same(ordinal.Api, name.Api);
        Assert.Equal(1, Assert.Single(ordinal.Sources).SegmentIndex);
        Assert.Equal(4, Assert.Single(ordinal.Sources).Offset);
        Assert.Equal(0x242, Assert.Single(ordinal.Sources).RelocationFileOffset);
        var free = NeImportResolver.Classify(new(1, "KERNEL", null, 17));
        Assert.Equal(NeImportSupport.KnownUnsupportedService, free.Support);
        Assert.Equal("KERNEL.GlobalFree", free.ResolvedIdentity);
        Assert.Null(free.Api);
        Assert.Contains("identity source", free.Diagnostic);
        var unknown = NeImportResolver.Classify(new(1, "WEP4UTIL", null, 999));
        Assert.Equal(NeImportSupport.UnknownImport, unknown.Support);
        Assert.Null(unknown.ResolvedIdentity);
        Assert.Equal(NeImportSupport.UnknownImport, NeImportResolver.Classify(new(1, "KERNEL", "#15", null)).Support);
        Assert.Equal(NeImportSupport.SupportedRuntimeService, NeImportResolver.Classify(new(1, "kernel", "globalalloc", null)).Support);
    }

    [Fact]
    public void InvalidIdentitiesAndTamperedModuleAssociationsStayInvalid()
    {
        foreach (var invalid in new NeImportReference[]
        {
            new(0, "KERNEL", null, 15),
            new(1, "", null, 15),
            new(1, "KERNEL", "GlobalAlloc", 15),
            new(1, "KERNEL", null, null),
            new(1, "KERNEL", null, 0)
        }

        )
        {
            Assert.Equal(NeImportSupport.InvalidImportReference, NeImportResolver.Classify(invalid).Support);
        }

        var executable = new NeExecutableReader().Read(NeLoaderTests.Fixture((3, 1, 4, 1, 15)));
        var relocation = executable.Relocations.Single();
        var tampered = executable with
        {
            Relocations = new[]
            {
                relocation with
                {
                    Import = relocation.Import!with
                    {
                        Module = "GDI"
                    }
                }
            }
        };
        Assert.Equal(NeImportSupport.InvalidImportReference, Assert.Single(NeImportResolver.Discover(tampered)).Support);
        foreach (var mismatch in new[]
        {
            relocation with
            {
                RawTarget1 = 2
            },
            relocation with
            {
                TargetKind = NeRelocationTargetKind.InternalSegment
            }
        }

        )
        {
            Assert.Equal(NeImportSupport.InvalidImportReference, Assert.Single(NeImportResolver.Discover(executable with
            {
                Relocations = new[] { mismatch }
            })).Support);
        }
    }

    [Fact]
    public void BindsOnlyRegistryBackedServiceRegistrationsAndGuardsBindingLifetime()
    {
        var executable = new NeExecutableReader().Read(NeLoaderTests.Fixture((3, 1, 4, 1, 15)));
        var dispatcher = new Win16CallDispatcher();
        dispatcher.Register("KERNEL.GlobalAlloc", new(0x9000, 0), CallDistance.Far, (ref MachineState state, SegmentMemory memory, Win16CallDispatcher calls, Win16CallContext call) => 0);
        var bindings = new NeProcedureBindings(executable, dispatcher);
        Assert.Null(bindings.Resolve(executable.Relocations[0].Import!));
        Assert.Equal("RuntimeServiceUnavailable", bindings.Imports[0].BindingStatus);
        bindings.Dispose();
        Assert.Throws<ObjectDisposedException>(() => bindings.Resolve(executable.Relocations[0].Import!));
    }

    [Fact]
    public void AutomaticallyBoundDataPointersExecuteExistingGlobalServicesWithWin16StackEffects()
    {
        using var runtime = NeRuntimeProgram.Load(MemoryImportDataFixture());
        Assert.True(runtime.Report.FullyRelocated);
        Assert.All(runtime.Report.Imports, i => Assert.Equal("Bound", i.BindingStatus));
        var image = runtime.Image;
        var state = image.InitialState;
        ushort initialSp = state.SP;
        var pointers = new[]
        {
            4,
            12,
            20,
            28
        }.Select(offset => image.Memory.ReadFarPointer(state.DS, (ushort)offset)).ToArray();
        Assert.Equal(new FarPointer(16, 0xf000), pointers[0]);

        void Call(int index)
        {
            var p = pointers[index];
            runtime.Dispatcher.Call(ref state, image.Memory, new Win16CodeAddress(p.Segment, p.Offset), CallDistance.Far);
            Assert.Equal(initialSp, state.SP);
            Assert.Equal(image.InitialState.CS, state.CS);
            Assert.Equal(image.InitialState.DS, state.DS);
        }


        // Pascal GlobalAlloc(flags, bytes) through the relocated pointer stored in original data.
        Win16Stack.Push(ref state, image.Memory, 0x42);
        Win16Stack.Push(ref state, image.Memory, 0);
        Win16Stack.Push(ref state, image.Memory, 12);
        Call(0);
        ushort handle = state.AX;
        Assert.NotEqual(0, handle);
        Win16Stack.Push(ref state, image.Memory, handle);
        Call(2);
        ushort allocated = state.DX;
        Assert.NotEqual(0, allocated);
        Assert.NotEqual(handle, allocated);
        image.Memory.WriteWord(allocated, 0, 0x1234);
        Win16Stack.Push(ref state, image.Memory, handle);
        Call(3);
        Win16Stack.Push(ref state, image.Memory, handle);
        Win16Stack.Push(ref state, image.Memory, 0);
        Win16Stack.Push(ref state, image.Memory, 24);
        Win16Stack.Push(ref state, image.Memory, 0x42);
        Call(1);
        Assert.Equal(handle, state.AX);
        Win16Stack.Push(ref state, image.Memory, handle);
        Call(2);
        Assert.Equal(24, image.Memory.GetSegment(state.DX).Size);
        Assert.Equal(0x1234, image.Memory.ReadWord(state.DX, state.AX));
        var memory = image.Memory;
        runtime.Dispose();
        runtime.Dispose();
        Assert.Empty(memory.Segments);
        Assert.Throws<ObjectDisposedException>(() => runtime.Dispatcher);
    }

    [Fact]
    public void NativeIndirectCallConsumesAutomaticallyRelocatedNeDataPointers()
    {
        using var runtime = NeRuntimeProgram.Load(MemoryImportDataFixture());
        var parsed = new AssemblyParser().ParseText("func_nods F\npush byte 0x42\npush byte 0\npush byte 12\ncall far dword [4]\npush ax\ncall far dword [20]\nendfunc");
        Assert.Empty(parsed.Diagnostics);
        using var native = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(Assert.Single(parsed.Module.Functions)));
        native.Register(runtime.Dispatcher, runtime.Image.Memory, "F", new(0x6000, 0x100), CallDistance.Far);
        var state = runtime.Image.InitialState;
        runtime.Dispatcher.Call(ref state, runtime.Image.Memory, "F", CallDistance.Far);
        Assert.Equal(runtime.Image.InitialState.SP, state.SP);
        Assert.Equal(12, runtime.Image.Memory.GetSegment(state.DX).Size);
        Assert.All(runtime.Image.Memory.ReadBytes(state.DX, state.AX, 12), b => Assert.Equal(0, b));
    }

    [Fact]
    public void NameImportLoadsWithoutManualDeclarationsAndOrdinalMutationFails()
    {
        using var named = NeRuntimeProgram.Load(NameFixture("GlobalAlloc"));
        Assert.True(named.Report.FullyRelocated);
        byte[] changed = MemoryImportDataFixture();
        NeLoaderTests.W(changed, 0x448, 999);
        Assert.Throws<InvalidDataException>(() => NeRuntimeProgram.Load(changed));
        using var incomplete = NeRuntimeProgram.Load(changed, new(AllowIncompleteRelocations: true));
        Assert.False(incomplete.Report.FullyRelocated);
        Assert.Contains(incomplete.Report.Imports, i => i.Original.Ordinal == 999 && i.Support == NeImportSupport.UnknownImport);
    }

    [Fact]
    public void GraphicsContractsAndHostAvailabilityAreReportedSeparately()
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 1, 4, 1, 29));
        // Reuse the sole module's raw string storage for GDI, retaining ordinal identity.
        bytes[0x112] = 3;
        Encoding.ASCII.GetBytes("GDI").CopyTo(bytes, 0x113);
        bytes.AsSpan(0x116, 3).Clear();
        using var runtime = NeRuntimeProgram.Load(bytes, new(AllowIncompleteRelocations: true));
        var import = Assert.Single(runtime.Report.Imports);
        Assert.Equal(NeImportSupport.SupportedRuntimeService, import.Support);
        Assert.Equal(OperatingSystem.IsWindows()
            ? "Bound"
            : "RuntimeServiceUnavailable", import.BindingStatus);
        Assert.Equal(OperatingSystem.IsWindows(), runtime.Report.FullyRelocated);
    }
}
