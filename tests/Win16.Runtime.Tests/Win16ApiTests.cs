using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Win16.Runtime.Tests;

public class Win16ApiTests
{
    [Fact]
    public void RegistryHasStableIdsAndCompleteStackLayouts()
    {
        Assert.Equal(6, Win16ApiRegistry.All.Select(a => a.ServiceId).Distinct().Count());
        foreach (var api in Win16ApiRegistry.All)
        {
            var words = api.Arguments.SelectMany(a => Enumerable.Range(a.StackWord, a.Width / 16)).Order().ToArray();
            Assert.Equal(Enumerable.Range(0, api.CleanupBytes / 2), words);
            Assert.Same(api, Win16ApiRegistry.Find(api.Identity.CanonicalName.ToLowerInvariant()));
            Assert.Equal(api, Win16ApiRegistry.Get(api.ServiceId));
        }

        Assert.Throws<NotSupportedException>(() => Win16ApiRegistry.Get(99));
    }

    [Fact]
    public void InternalAndDeclaredUnknownTargetsAreNotMisclassifiedAsImports()
    {
        var module = new IrModule("test.asm", new[] { new IrFunction("FarInternal", Array.Empty<IrInstruction>()) }, new[] { "USER.Unknown" });
        Assert.Equal(Win16CallTargetKind.Internal, Win16ImportResolver.Resolve("FarInternal", module).Kind);
        var unresolved = Win16ImportResolver.Resolve("USER.Unknown", module);
        Assert.Equal(Win16CallTargetKind.Unresolved, unresolved.Kind);
        Assert.Contains("declared external", unresolved.Diagnostic);
        var resolved = Win16ImportResolver.Resolve("Alloc", module, new Dictionary<string, string> { ["Alloc"] = "KERNEL.GlobalAlloc" });
        Assert.Equal(Win16CallTargetKind.Imported, resolved.Kind);
        Assert.Equal("Alloc", resolved.OriginalIdentity);
    }

    [Fact]
    public void RegistryControlsCleanupAndPreservesCallState()
    {
        var memory = new SegmentMemory();
        memory.Map(new Win16Segment(0x2000, 65536));
        var dispatcher = new Win16CallDispatcher();
        var api = Win16ApiRegistry.Find("KERNEL.GlobalAlloc")!;
        dispatcher.RegisterApi(api, 0xF000, (ref MachineState state, SegmentMemory mem, Win16CallDispatcher _, Win16CallContext call) =>
        {
            Assert.Equal(0x12345678u, api.ReadArgument("bytes", mem, state, call));
            Assert.Equal(0x42u, api.ReadArgument("flags", mem, state, call));
            Assert.Equal(api.EntryOffset, state.IP);
            state.AX = 9;
            state.BP = 0;
            state.DS = 0;
            return 99; // The registry owns cleanup, not an implementation-specific return count.
        });
        var state = new MachineState
        {
            SS = 0x2000,
            SP = 0x1000,
            CS = 0x4000,
            IP = 8,
            BP = 7,
            DS = 6
        };
        Win16Stack.Push(ref state, memory, 0x42);
        Win16Stack.Push(ref state, memory, 0x1234);
        Win16Stack.Push(ref state, memory, 0x5678);
        dispatcher.Call(ref state, memory, api.Identity.CanonicalName, CallDistance.Far);
        Assert.Equal((ushort)0x1000, state.SP);
        Assert.Equal((ushort)9, state.AX);
        Assert.Equal((ushort)7, state.BP);
        Assert.Equal((ushort)6, state.DS);
        Assert.Equal((ushort)0x4000, state.CS);
        Assert.Equal((ushort)8, state.IP);
    }
}
