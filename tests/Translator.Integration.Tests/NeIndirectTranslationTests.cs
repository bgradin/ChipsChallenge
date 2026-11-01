using System.Text.Json;
using Translator.Analysis;
using Translator.CodeGen;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeIndirectTranslationTests
{
    internal static byte[] TableFixture()
    {
        var bytes = NeDiscoveryTests.Fixture(0x83, 0xf8, 1, 0x77, 0x0b, 0xd1, 0xe0, 0x93, 0x2e, 0xff, 0xa7, 0x20, 0);
        new byte[]
        {
            0xba,
            0xff,
            0xff,
            0xcb
        }.CopyTo(bytes, 0x210);
        new byte[]
        {
            0xba,
            0x11,
            0x11,
            0xcb,
            0xba,
            0x22,
            0x22,
            0xcb
        }.CopyTo(bytes, 0x218);
        NeLoaderTests.W(bytes, 0x220, 0x18);
        NeLoaderTests.W(bytes, 0x222, 0x1c);
        return bytes;
    }

    [Fact]
    public void NativeTableDispatchPreservesOriginalRegistersFlagsAndStackForAllEntriesAndDefaults()
    {
        using var program = NeNativeProgram.Create(TableFixture(), "1:0", resolveIndirectTransfers: true);
        Assert.True(program.Artifact.Graph.ClosureComplete);
        var dispatch = program.Artifact.Module.Functions.Single().Instructions.Single(i => i.Operation == "DISPATCH");
        Assert.Equal(2, dispatch.IndirectTransfer!.Targets.Count);
        Assert.Equal("JMP", dispatch.OriginalMnemonic);
        Assert.Contains(program.Artifact.Module.Functions.Single().BasicBlocks!, b => b.Successors.Any(e => e.Kind == "indirect"));
        foreach (ushort input in new ushort[]
        {
            0,
            1,
            2,
            0x8000,
            0xffff
        }

        )
        {
            var s = program.CreateState();
            ushort sp = s.SP, cs = s.CS, ds = s.DS;
            s.AX = input;
            s.BX = 0x7777;
            s.CX = 0x1234;
            s.SI = 0x3456;
            s.DI = 0x4567;
            s.Flags = 0x202;
            program.Invoke(ref s, []);
            Assert.Equal(input == 0
                ? 0x1111
                : input == 1
                    ? 0x2222
                    : 0xffff, s.DX);
            Assert.Equal(sp, s.SP);
            Assert.Equal(cs, s.CS);
            Assert.Equal(ds, s.DS);
            Assert.Equal(0x1234, s.CX);
            Assert.Equal(0x3456, s.SI);
            Assert.Equal(0x4567, s.DI);
            if (input < 2)
            {
                Assert.Equal(0x7777, s.AX);
                Assert.Equal(input * 2, s.BX);
                Assert.Equal(input == 0
                    ? 0x44
                    : 0, s.Flags & 0x8c5);
            }
        }
    }

    [Fact]
    public void RuntimeTableMutationToUnapprovedAddressFailsButSharedApprovedTargetsWork()
    {
        byte[] bytes = TableFixture();
        using var program = NeNativeProgram.Create(bytes, "1:0", resolveIndirectTransfers: true);
        var memory = program.Runtime.Image.Memory;
        ushort selector = program.Runtime.Image.SelectorFor(1);
        byte[] code = memory.ReadBytes(selector, 0, 64);
        var original = memory.GetSegment(selector);
        memory.Unmap(selector);
        using var mutable = memory.CreateSegment(selector, 64, initialized: code);
        memory.Map(mutable);
        try
        {
            memory.WriteWord(selector, 0x20, 0x1c);
            var s = program.CreateState();
            program.Invoke(ref s, []);
            Assert.Equal(0x2222, s.DX);
            foreach (ushort bad in new ushort[]
            {
                0xffff,
                0x19,
                0x10
            }

            )
            {
                memory.WriteWord(selector, 0x20, bad);
                s = program.CreateState();
                Assert.Throws<InvalidOperationException>(() => program.Invoke(ref s, []));
            }
        }
        finally
        {
            memory.Unmap(selector);
            memory.Map(original);
        }
    }

    [Fact]
    public void NativeDispatchRejectsOddAndOutOfRangeIndicesBeforeReadingTable()
    {
        using var program = NeNativeProgram.Create(TableFixture(), "1:0", resolveIndirectTransfers: true);
        var original = program.Artifact.Module.Functions.Single();
        // Enter the typed dispatch directly to test defense beyond its original guard.
        var isolated = original with
        {
            Instructions = original.Instructions.Where(i => i.Address >= 8).ToArray()
        };
        using var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(isolated));
        foreach (ushort index in new ushort[]
        {
            1,
            4,
            0xffff
        }

        )
        {
            var state = program.CreateState();
            state.BX = index;
            state.DX = 0x9876;
            Assert.Throws<InvalidOperationException>(() => body.Invoke(ref state, program.Runtime.Image.Memory, program.Runtime.Dispatcher));
            Assert.Equal(0x9876, state.DX);
        }
    }

    [Fact]
    public void ForgedTargetSetAndInstructionInteriorCannotAuthorizeNativeDispatch()
    {
        byte[] bytes = TableFixture();
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0", resolveIndirectTransfers: true);
        var f = graph.Functions.Single();
        var altered = f with
        {
            Dependencies = f.Dependencies.Where(d => d.Target?.Offset != 0x18).ToArray()
        };
        Assert.Throws<NotSupportedException>(() => new NeBinaryIrBuilder().Build(bytes, graph with { Functions = [altered] }));
        NeLoaderTests.W(bytes, 0x220, 0x1d);
        Assert.Throws<InvalidDataException>(() => NeNativeProgram.Create(bytes, "1:0", resolveIndirectTransfers: true));
    }

    [Fact]
    public void RelocatedFarIndirectCallChecksPointerAndPreservesAuthenticReturnAddress()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0x2e, 0xff, 0x1e, 0x20, 0, 0xcb);
        // POP AX (original IP), POP DX (original CS), restore both and RETF.
        new byte[]
        {
            0x58,
            0x5a,
            0x52,
            0x50,
            0xcb
        }.CopyTo(bytes, 0x218);
        NeLoaderTests.W(bytes, 0xc4, 0x100);
        NeLoaderTests.W(bytes, 0x240, 1);
        bytes[0x242] = 3;
        NeLoaderTests.W(bytes, 0x244, 0x20);
        NeLoaderTests.W(bytes, 0x246, 1);
        NeLoaderTests.W(bytes, 0x248, 0x18);
        NeLoaderTests.W(bytes, 0x220, 0xffff);
        using var program = NeNativeProgram.Create(bytes, "1:0", resolveIndirectTransfers: true);
        var s = program.CreateState();
        ushort sp = s.SP, cs = s.CS;
        program.Invoke(ref s, []);
        Assert.Equal(5, s.AX);
        Assert.Equal(cs, s.DX);
        Assert.Equal(sp, s.SP);
        Assert.Single(program.Artifact.Module.Functions.First().Instructions.Single(i => i.Operation == "CALL").IndirectTransfer!.Targets);
        var memory = program.Runtime.Image.Memory;
        ushort selector = program.Runtime.Image.SelectorFor(1);
        var original = memory.GetSegment(selector);
        var copy = memory.ReadBytes(selector, 0, 64);
        memory.Unmap(selector);
        using var mutable = memory.CreateSegment(selector, 64, initialized: copy);
        memory.Map(mutable);
        try
        {
            // 1:0 is registered but not approved for this call site; reject before recursion.
            memory.WriteDword(selector, 0x20, (uint)selector << 16);
            s = program.CreateState();
            Assert.Throws<InvalidOperationException>(() => program.Invoke(ref s, []));
            memory.WriteDword(selector, 0x20, 0xffff0018);
            s = program.CreateState();
            Assert.Throws<InvalidOperationException>(() => program.Invoke(ref s, []));
        }
        finally
        {
            memory.Unmap(selector);
            memory.Map(original);
        }
    }

    [Fact]
    public void CrossFunctionNearTailTransferReusesOriginalFarReturnFrame()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xeb, 0x2e); // tail to separately exported 1:30
        new byte[]
        {
            0x58,
            0x5a,
            0x52,
            0x50,
            0xcb
        }.CopyTo(bytes, 0x230);
        using var program = NeNativeProgram.Create(bytes, "1:0", resolveIndirectTransfers: true);
        var s = program.CreateState();
        ushort sp = s.SP;
        program.Invoke(ref s, []);
        Assert.Equal(0, s.AX);
        Assert.Equal(sp, s.SP); // outer caller IP, not a new tail frame
        Assert.Contains(program.Artifact.Module.Functions.First().Instructions, i => i.Operation == "TAIL");
    }

    [Fact]
    public void IndexedNearCallsSelectApprovedBodiesAndReturnWithoutStackDrift()
    {
        byte[] bytes = TableFixture();
        bytes[0x20a] = 0x97; // CALL CS:[BX+20h]
        bytes[0x20d] = 0xcb;
        bytes[0x21b] = 0xc3;
        bytes[0x21f] = 0xc3;
        using var program = NeNativeProgram.Create(bytes, "1:0", resolveIndirectTransfers: true);
        foreach (ushort input in new ushort[]
        {
            0,
            1,
            2
        }

        )
        {
            var state = program.CreateState();
            state.AX = input;
            ushort sp = state.SP;
            program.Invoke(ref state, []);
            Assert.Equal(input == 0
                ? 0x1111
                : input == 1
                    ? 0x2222
                    : 0xffff, state.DX);
            Assert.Equal(sp, state.SP);
        }
    }

    [Fact]
    public void FarIndirectTailUsesRelocatedSelectorWithoutAddingReturnFrame()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0x2e, 0xff, 0x2e, 0x20, 0);
        new byte[]
        {
            0x58,
            0x5a,
            0x52,
            0x50,
            0xcb
        }.CopyTo(bytes, 0x218);
        NeLoaderTests.W(bytes, 0xc4, 0x100);
        NeLoaderTests.W(bytes, 0x240, 1);
        bytes[0x242] = 3;
        NeLoaderTests.W(bytes, 0x244, 0x20);
        NeLoaderTests.W(bytes, 0x246, 1);
        NeLoaderTests.W(bytes, 0x248, 0x18);
        NeLoaderTests.W(bytes, 0x220, 0xffff);
        using var program = NeNativeProgram.Create(bytes, "1:0", resolveIndirectTransfers: true);
        var state = program.CreateState();
        ushort sp = state.SP;
        program.Invoke(ref state, []);
        Assert.Equal(0, state.AX);
        Assert.Equal(sp, state.SP);
    }

    [Fact]
    public void FarJumpAcrossOriginalSegmentsPreservesCallerFrameAndTargetCs()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0x2e, 0xff, 0x2e, 0x20, 0);
        bytes.AsSpan(0xc0, 16).CopyTo(bytes.AsSpan(0x500, 16));
        NeLoaderTests.W(bytes, 0xa2, 0x480);
        NeLoaderTests.W(bytes, 0x9c, 3);
        NeLoaderTests.W(bytes, 0x510, 0x48);
        NeLoaderTests.W(bytes, 0x512, 16);
        NeLoaderTests.W(bytes, 0x516, 16);
        new byte[]
        {
            0x8c,
            0xca,
            0x58,
            0x50,
            0xcb
        }.CopyTo(bytes, 0x480);
        NeLoaderTests.W(bytes, 0x504, 0x100);
        NeLoaderTests.W(bytes, 0x240, 1);
        bytes[0x242] = 3;
        NeLoaderTests.W(bytes, 0x244, 0x20);
        NeLoaderTests.W(bytes, 0x246, 3);
        NeLoaderTests.W(bytes, 0x248, 0);
        NeLoaderTests.W(bytes, 0x220, 0xffff);
        using var program = NeNativeProgram.Create(bytes, "1:0", resolveIndirectTransfers: true);
        var state = program.CreateState();
        ushort sp = state.SP, cs = state.CS;
        program.Invoke(ref state, []);
        Assert.Equal(0, state.AX);
        Assert.Equal(program.Runtime.Image.SelectorFor(3), state.DX);
        Assert.Equal(cs, state.CS);
        Assert.Equal(sp, state.SP);
    }

    [WindowsFact]
    public void ClrFreePeExecutesDispatchCallsTailsAndDetectsWrongOracleAndRuntimeTargetMutation()
    {
        foreach (ushort input in new ushort[]
        {
            0,
            1,
            2,
            0xffff
        }

        )
        {
            string fixture = JsonSerializer.Serialize(new
            {
                Arguments = Array.Empty<ushort>(),
                Registers = new
                {
                    AX = input
                },
                ExpectedRegisters = new
                {
                    DX = input == 0
                ? 0x1111
                : input == 1
                    ? 0x2222
                    : 0xffff
                }
            });
            var first = NeStandaloneProgram.Build(TableFixture(), "1:0", fixture, resolveIndirectTransfers: true);
            var second = NeStandaloneProgram.Build(TableFixture(), "1:0", fixture, resolveIndirectTransfers: true);
            Assert.Equal(first.Executable, second.Executable);
            Assert.Equal(0, NeStandaloneTests.Execute(first.Executable));
            Assert.Equal(40, NeStandaloneTests.Execute(NeStandaloneProgram.Build(TableFixture(), "1:0", fixture.Replace("\"DX\":" + (input == 0
                ? 0x1111
                : input == 1
                    ? 0x2222
                    : 0xffff), "\"DX\":123"), resolveIndirectTransfers: true).Executable));
        }

        // Mutate only the descriptor's runtime table bytes after static translation.
        var checkedPe = NeStandaloneProgram.Build(TableFixture(), "1:0", """{"Arguments":[],"ExpectedRegisters":{"DX":4369}}""", resolveIndirectTransfers: true);
        byte[] damaged = checkedPe.Executable.ToArray();
        byte[] pattern = TableFixture().AsSpan(0x200, 64).ToArray();
        int tableAt = Enumerable.Range(0, damaged.Length - pattern.Length + 1).First(i => damaged.AsSpan(i, pattern.Length).SequenceEqual(pattern));
        damaged[tableAt + 0x20] = 0xff;
        damaged[tableAt + 0x21] = 0xff;
        Assert.Equal(30, NeStandaloneTests.Execute(damaged));
        byte[] tail = NeDiscoveryTests.Fixture(0xeb, 0x2e);
        new byte[]
        {
            0xb8,
            7,
            0,
            0xcb
        }.CopyTo(tail, 0x230);
        Assert.Equal(0, NeStandaloneTests.Execute(NeStandaloneProgram.Build(tail, "1:0", """{"Arguments":[],"ExpectedRegisters":{"AX":7}}""", resolveIndirectTransfers: true).Executable));
    }

    [WindowsSuppliedNeFact]
    public void OriginalBootTableExecutesInStandaloneNativePeWithoutClr()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        foreach (var c in new[]
        {
            (Tile: 3, Boot: 0x168a),
            (Tile: 12, Boot: 0x168e),
            (Tile: 50, Boot: 0x1690)
        }

        )
        {
            string fixture = JsonSerializer.Serialize(new
            {
                Arguments = new ushort[] { (ushort)c.Tile },
                Writes = new[] { new { SegmentIndex = 10, Offset = c.Boot, BytesHex = "0100" } },
                ExpectedRegisters = new
                {
                    AX = 1
                }
            });
            Assert.Equal(0, NeStandaloneTests.Execute(NeStandaloneProgram.Build(bytes, "3:187C", fixture, resolveIndirectTransfers: true).Executable));
        }
    }
}
