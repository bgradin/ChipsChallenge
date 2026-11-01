using Translator.Assembly;
using Translator.ChipsChallenge;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class GraphicsLoweringTests
{
    [Theory]
    [InlineData(3, 4, true)]
    [InlineData(4, 5, true)]
    [InlineData(2, 4, false)]
    [InlineData(7, 4, false)]
    [InlineData(3, 8, false)]
    public void SyntheticGraphicsClosureLowersAndMarshalsThePatBltSignature(short x, short y, bool visible)
    {
        string source = Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Ui.asm");
        var parsed = new AssemblyParser().ParseSelected(source, new[] { "InvertViewportTile", "IsPointVisible" });
        Assert.Empty(parsed.Diagnostics);
        var memory = new SegmentMemory();
        memory.Map(new(0x1000));
        memory.Map(new(0x2000));
        memory.WriteWord(0x1000, 0x0040, 0x3000);

        void Field(string name, ushort value) => memory.WriteWord(0x1000, (ushort)(0x3000 + GraphicsTargetProfile.Symbols[name]), value);

        Field("ViewportX", 3);
        Field("ViewportY", 4);
        Field("ViewportWidth", 4);
        Field("ViewportHeight", 4);
        Field("PaddingX", 1);
        Field("PaddingY", 2);
        var calls = new Win16CallDispatcher();
        var bodies = new List<NativeWin16Body>();
        uint[]? arguments = null;
        var api = Win16ApiRegistry.Find("GDI.PatBlt")!;
        // A recording compatibility adapter verifies marshalling; real GDI forwarding is milestone 3.4.
        calls.RegisterApi(api with
        {
            Supported = true
        }, 0xF000, (ref MachineState state, SegmentMemory m, Win16CallDispatcher _, Win16CallContext call) =>
        {
            var copy = state;
            arguments = api.Arguments.Select(a => api.ReadArgument(a.Name, m, copy, call)).ToArray();
            state.AX = 1;
            return 0;
        });
        try
        {
            foreach (var function in parsed.Module.Functions)
            {
                var configured = GraphicsTargetProfile.Configure(function);
                var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(configured, symbols: GraphicsTargetProfile.Symbols, module: parsed.Module, targets: GraphicsTargetProfile.Targets));
                bodies.Add(body);
                body.Register(calls, memory, function.Name, GraphicsTargetProfile.Targets[function.Name], CallDistance.Far);
            }

            var state = new MachineState
            {
                DS = 0x1000,
                SS = 0x2000,
                CS = 0x4000,
                SP = 0x8000,
                BP = 0x1234,
                IP = 0x50,
                SI = 0xAAAA,
                DI = 0xBBBB
            };
            Win16Stack.Push(ref state, memory, (ushort)y);
            Win16Stack.Push(ref state, memory, (ushort)x);
            Win16Stack.Push(ref state, memory, 7);
            calls.Call(ref state, memory, "InvertViewportTile", CallDistance.Far);
            Assert.Equal((ushort)0x7FFA, state.SP); // Both synthetic functions are caller-cleaned.
            state.SP += 6;
            Assert.Equal((ushort)0x8000, state.SP);
            Assert.Equal((ushort)0x1234, state.BP);
            Assert.Equal((ushort)0xAAAA, state.SI);
            Assert.Equal((ushort)0xBBBB, state.DI);
            if (visible)
            {
                Assert.Equal(new uint[] { 7, unchecked((ushort)((x - 4) * 32)), unchecked((ushort)((y - 6) * 32)), 32, 32, 0x550009 }, arguments);
            }
            else
            {
                Assert.Null(arguments);
            }
        }
        finally
        {
            foreach (var body in bodies)
            {
                body.Dispose();
            }
        }
    }
}
