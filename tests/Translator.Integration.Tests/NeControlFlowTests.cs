using Translator.Binary;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeControlFlowTests
{
    private static byte[] Fixture(params byte[] code)
    {
        byte[] bytes = NeDiscoveryTests.Fixture(code);
        NeLoaderTests.W(bytes, 0x94, 0);
        return bytes;
    }

    [Fact]
    public void CompiledBlocksFollowNearCallsBranchesAndOriginalReturns()
    {
        byte[] bytes = Fixture(0xe8, 13, 0, 0xb8, 1, 0, 0xcb);
        new byte[]
        {
            0xba,
            2,
            0,
            0xc3
        }.CopyTo(bytes, 0x210);
        using var program = new NeWholeProgramControlProgram(bytes);
        var state = program.InitialState;
        var before = state;
        program.Invoke(ref state, new(1, 0));
        Assert.Equal((ushort)1, state.AX);
        Assert.Equal((ushort)2, state.DX);
        Assert.Equal(before.SP, state.SP);
        Assert.Equal(before.CS, state.CS);
        Assert.Equal(before.IP, state.IP);
        Assert.True(program.Artifact.Report.Summary.AllStaticTargetsResolved);
        Assert.Contains(program.Artifact.Report.Entries, e => e.Original == new NeBinaryAddress(1, 6));
    }

    [Fact]
    public void IndirectCallsAndFarPointerTablesUseApprovedOriginalAddresses()
    {
        byte[] bytes = Fixture(0x2e, 0xff, 0x1e, 0x20, 0, 0xcb); // CALL FAR CS:[20h]
        new byte[]
        {
            0xb8,
            7,
            0,
            0xcb
        }.CopyTo(bytes, 0x218);
        // Original pointer has supported relocation and chain terminator.
        NeLoaderTests.W(bytes, 0xc4, 0x100);
        NeLoaderTests.W(bytes, 0x240, 1);
        bytes[0x242] = 3;
        NeLoaderTests.W(bytes, 0x244, 0x20);
        NeLoaderTests.W(bytes, 0x246, 1);
        NeLoaderTests.W(bytes, 0x248, 0x18);
        NeLoaderTests.W(bytes, 0x220, 0xffff);
        using var program = new NeWholeProgramControlProgram(bytes);
        var state = program.InitialState;
        program.Invoke(ref state, new(1, 0));
        Assert.Equal((ushort)7, state.AX);
        Assert.Throws<InvalidOperationException>(() => program.Control.Resolve(new(state.DS, 0)));
        Assert.Throws<InvalidOperationException>(() => program.Control.Resolve(new(state.CS, 0x19))); // middle of MOV
    }

    [Fact]
    public void NativeBodiesExecuteWithoutReadingOriginalInstructionStreams()
    {
        byte[] bytes = Fixture(0xb8, 7, 0, 0xcb);
        using var program = new NeWholeProgramControlProgram(bytes);
        var old = program.Memory.GetSegment(program.InitialState.CS);
        using var changed = new Win16Segment(old.Selector, old.Size, backing: old.Memory.ToArray());
        program.Memory.Unmap(old.Selector);
        program.Memory.Map(changed);
        try
        {
            program.Memory.WriteByte(old.Selector, 1, 9);
            var state = program.InitialState;
            program.Invoke(ref state, new(1, 0));
            Assert.Equal((ushort)7, state.AX);
        }
        finally
        {
            program.Memory.Unmap(old.Selector);
            program.Memory.Map(old);
        }

        bytes[0x201] = 9;
        using var rebuilt = new NeWholeProgramControlProgram(bytes);
        var mutation = rebuilt.InitialState;
        rebuilt.Invoke(ref mutation, new(1, 0));
        Assert.Equal((ushort)9, mutation.AX);
    }

    [Fact]
    public void PushCsNearCallFarReturnUsesOriginalStackRatherThanAnInferredFunctionConvention()
    {
        byte[] bytes = Fixture(0x0e, 0xe8, 12, 0, 0xcb);
        new byte[]
        {
            0xbd,
            0x22,
            0x22,
            0xb8,
            0x42,
            0,
            0xcb
        }.CopyTo(bytes, 0x210);
        using var program = new NeWholeProgramControlProgram(bytes);
        var state = program.InitialState;
        ushort sp = state.SP;
        program.Invoke(ref state, new(1, 0));
        Assert.Equal((ushort)0x42, state.AX);
        Assert.Equal((ushort)0x2222, state.BP);
        Assert.Equal(sp, state.SP); // no forced preservation of registers by the native dispatcher
    }

    [Fact]
    public void ConditionalBranchesAndLoopPreserveOriginalFlagsAndWrappingCounterSemantics()
    {
        byte[] bytes = Fixture(0x83, 0xf8, 7, 0x74, 5, 0xba, 1, 0, 0xeb, 3, 0xba, 2, 0, 0xcb);
        using (var program = new NeWholeProgramControlProgram(bytes))
        {
            foreach (ushort ax in new ushort[]
            {
                6,
                7,
                8
            }

            )
            {
                var state = program.InitialState;
                state.AX = ax;
                program.Invoke(ref state, new(1, 0));
                Assert.Equal((ushort)(ax == 7
                    ? 2
                    : 1), state.DX);
                Assert.Equal(ax == 7, (state.Flags & 64) != 0);
            }
        }

        bytes = Fixture(0xb9, 3, 0, 0x40, 0xe2, 0xfd, 0xcb);
        using var loop = new NeWholeProgramControlProgram(bytes);
        var result = loop.InitialState;
        result.AX = 0;
        result.Flags = 1;
        loop.Invoke(ref result, new(1, 0));
        Assert.Equal((ushort)3, result.AX);
        Assert.Equal((ushort)0, result.CX);
        Assert.Equal(1, result.Flags & 1);
    }

    [Fact]
    public void SharedEpiloguesAndInteriorInstructionEntriesHaveRealNativePrologueThunks()
    {
        byte[] bytes = Fixture(0xb8, 1, 0, 0xeb, 11); // jump to shared INC/RETF at 16
        new byte[]
        {
            0x40,
            0xcb
        }.CopyTo(bytes, 0x210);
        new byte[]
        {
            0xb8,
            2,
            0,
            0xeb,
            0xdb
        }.CopyTo(bytes, 0x230);
        using var program = new NeWholeProgramControlProgram(bytes);
        foreach (var (offset, expected) in new (ushort, ushort)[]
        {
            (0, 2),
            (48, 3),
            (17, 0)
        }

        )
        {
            var state = program.InitialState;
            state.AX = 0;
            program.Invoke(ref state, new(1, offset));
            Assert.Equal(expected, state.AX);
        }

        foreach (var entry in program.Artifact.Report.Entries)
        {
            Assert.Equal(Translator.CodeGen.Win16BodyCodeGenerator.EntryPrologue, program.Artifact.Code.Bytes.AsSpan(entry.NativeOffset, Translator.CodeGen.Win16BodyCodeGenerator.EntryPrologue.Length).ToArray());
        }
    }

    [Fact]
    public void VerifiedJumpTablesRejectOtherApprovedEntriesAndOddIndices()
    {
        byte[] bytes = NeIndirectTranslationTests.TableFixture();
        NeLoaderTests.W(bytes, 0x94, 0);
        using var program = new NeWholeProgramControlProgram(bytes);
        foreach (ushort index in new ushort[]
        {
            0,
            1,
            2
        }

        )
        {
            var state = program.InitialState;
            state.AX = index;
            program.Invoke(ref state, new(1, 0));
            Assert.Equal((ushort)(index == 0
                ? 0x1111
                : index == 1
                    ? 0x2222
                    : 0xffff), state.DX);
        }

        var old = program.Memory.GetSegment(program.InitialState.CS);
        using var writable = new Win16Segment(old.Selector, old.Size, backing: old.Memory.ToArray());
        program.Memory.Unmap(old.Selector);
        program.Memory.Map(writable);
        try
        {
            program.Memory.WriteWord(old.Selector, 0x20, 0x10); // real native entry, outside this site's verified target set
            var state = program.InitialState;
            state.AX = 0;
            Assert.Throws<InvalidOperationException>(() => program.Invoke(ref state, new(1, 0)));
            program.Memory.WriteWord(old.Selector, 0x20, 0x1c);
            state = program.InitialState;
            state.AX = 0;
            program.Invoke(ref state, new(1, 0));
            Assert.Equal((ushort)0x2222, state.DX);
            state = program.InitialState;
            state.BX = 1;
            Assert.Throws<InvalidOperationException>(() => program.Invoke(ref state, new(1, 8)));
        }
        finally
        {
            program.Memory.Unmap(old.Selector);
            program.Memory.Map(old);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeSelectedRegisterCallsAndJumpsResolveOnlyApprovedBoundaries(bool jump)
    {
        byte[] bytes = Fixture(0xff, jump
            ? (byte)0xe3
            : (byte)0xd3, 0xcb);
        new byte[]
        {
            0xb8,
            0x42,
            0,
            jump
                ? (byte)0xcb
                : (byte)0xc3
        }.CopyTo(bytes, 0x210);
        NeLoaderTests.W(bytes, 0x12d, 16);
        using var program = new NeWholeProgramControlProgram(bytes);
        var state = program.InitialState;
        state.BX = 16;
        program.Invoke(ref state, new(1, 0));
        Assert.Equal((ushort)0x42, state.AX);
        state = program.InitialState;
        state.BX = 17;
        Assert.Throws<InvalidOperationException>(() => program.Invoke(ref state, new(1, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectFarTransfersPreserveCrossSegmentIdentity(bool jump)
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 0, 1, 2, 0));
        bytes.AsSpan(0xc0, 16).CopyTo(bytes.AsSpan(0x160));
        NeLoaderTests.W(bytes, 0xa2, 0xe0);
        NeLoaderTests.W(bytes, 0x9c, 3);
        NeLoaderTests.W(bytes, 0x94, 0);
        NeLoaderTests.W(bytes, 0x8e, 3);
        NeLoaderTests.W(bytes, 0x9a, 3);
        NeLoaderTests.W(bytes, 0x16c, 0); // second segment is code
        NeLoaderTests.W(bytes, 0x170, 0x50);
        NeLoaderTests.W(bytes, 0x172, 32);
        NeLoaderTests.W(bytes, 0x174, 1);
        NeLoaderTests.W(bytes, 0x176, 64);
        bytes.AsSpan(0x200, 64).Fill(0x90);
        new byte[]
        {
            jump
                ? (byte)0xea
                : (byte)0x9a,
            0xff,
            0xff,
            0,
            0,
            0xcb
        }.CopyTo(bytes, 0x200);
        new byte[]
        {
            0xb8,
            9,
            0,
            0xcb
        }.CopyTo(bytes, 0x400);
        NeLoaderTests.W(bytes, 0x12d, 48);
        bytes[0x230] = 0xcb;
        using var program = new NeWholeProgramControlProgram(bytes);
        var state = program.InitialState;
        var before = state;
        program.Invoke(ref state, new(1, 0));
        Assert.Equal((ushort)9, state.AX);
        Assert.Equal(before.SP, state.SP);
        Assert.Equal(before.CS, state.CS);
    }

    [Fact]
    public void ImportIdentityResolutionDoesNotInventAContractAndAdaptersCleanOriginalFrames()
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 1, 4, 1, 7));
        NeLoaderTests.W(bytes, 0x94, 0);
        bytes.AsSpan(0x200, 64).Fill(0x90);
        new byte[]
        {
            0x68,
            0x34,
            0x12,
            0x9a,
            0xff,
            0xff,
            0,
            0,
            0xcb
        }.CopyTo(bytes, 0x200);
        using var program = new NeWholeProgramControlProgram(bytes);
        var import = Assert.Single(program.Artifact.Report.Imports);
        Assert.Equal(Win16ProgramAddressKind.Import, program.Control.Resolve(import.RuntimeAddress).Kind);
        Assert.Throws<InvalidOperationException>(() => program.Control.Resolve(import.RuntimeAddress, callback: true));
        var state = program.InitialState;
        Assert.Throws<InvalidOperationException>(() => program.Invoke(ref state, new(1, 0)));
        Assert.Throws<InvalidOperationException>(() => program.RegisterImport("KERNEL.#8", (ref MachineState _, SegmentMemory _) => 0));
        // Synthetic adapter: its explicit test contract consumes one word and returns AX.
        program.RegisterImport("KERNEL.#7", (ref MachineState s, SegmentMemory m) =>
        {
            s.AX = m.ReadWord(s.SS, unchecked((ushort)(s.SP + 4)));
            s.Flags |= 1;
            return 2;
        });
        state = program.InitialState;
        ushort sp = state.SP;
        program.Invoke(ref state, new(1, 0));
        Assert.Equal((ushort)0x1234, state.AX);
        Assert.Equal(sp, state.SP);
        Assert.Equal(1, state.Flags & 1);
    }

    [Fact]
    public void FarPointerTablesAndInstalledCallbacksSelectNativeEntriesFromCurrentDataValues()
    {
        byte[] bytes = Fixture(0xff, 0x1f, 0xcb); // CALL FAR [BX]
        new byte[]
        {
            0xb8,
            1,
            0,
            0xcb
        }.CopyTo(bytes, 0x210);
        new byte[]
        {
            0xb8,
            2,
            0,
            0xcb
        }.CopyTo(bytes, 0x220);
        NeLoaderTests.W(bytes, 0xcc, 0x101);
        NeLoaderTests.W(bytes, 0x410, 2);
        foreach (int n in new[]
        {
            0,
            1
        }

        )
        {
            int record = 0x412 + n * 8;
            bytes[record] = 3;
            NeLoaderTests.W(bytes, record + 2, (ushort)(n * 4));
            NeLoaderTests.W(bytes, record + 4, 1);
            NeLoaderTests.W(bytes, record + 6, (ushort)(16 + n * 16));
            NeLoaderTests.W(bytes, 0x400 + n * 4, 0xffff);
        }

        using var program = new NeWholeProgramControlProgram(bytes);
        foreach (var (slot, expected) in new (ushort, ushort)[]
        {
            (0, 1),
            (4, 2)
        }

        )
        {
            var state = program.InitialState;
            state.BX = slot;
            program.Invoke(ref state, new(1, 0));
            Assert.Equal(expected, state.AX);
        }

        ushort ds = program.InitialState.DS, cs = program.InitialState.CS;
        program.Memory.WriteDword(ds, 0, ((uint)cs << 16) | 32); // dynamically installed handler
        var changed = program.InitialState;
        changed.BX = 0;
        program.Invoke(ref changed, new(1, 0));
        Assert.Equal((ushort)2, changed.AX);
        var callback = program.Control.Resolve(new(cs, 32), callback: true);
        Assert.Equal(Win16ProgramAddressKind.Code, callback.Kind);
        program.Control.Invoke(ref changed, callback.Address);
        Assert.Equal((ushort)2, changed.AX);
    }

    [Fact]
    public void ImportAdaptersCanDeliverNestedOriginalCallbacksWithoutLosingOuterContinuation()
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 1, 1, 1, 7));
        NeLoaderTests.W(bytes, 0x94, 0);
        bytes.AsSpan(0x200, 64).Fill(0x90);
        new byte[]
        {
            0x9a,
            0xff,
            0xff,
            0,
            0,
            0xcb
        }.CopyTo(bytes, 0x200);
        NeLoaderTests.W(bytes, 0x12d, 48);
        new byte[]
        {
            0xb9,
            0x34,
            0x12,
            0xcb
        }.CopyTo(bytes, 0x230);
        using var program = new NeWholeProgramControlProgram(bytes);
        program.RegisterImport("KERNEL.#7", (ref MachineState s, SegmentMemory _) =>
        {
            ushort sp = s.SP, cs = s.CS, ip = s.IP;
            program.Invoke(ref s, new(1, 48));
            Assert.Equal(sp, s.SP);
            Assert.Equal(cs, s.CS);
            Assert.Equal(ip, s.IP);
            s.AX = 0x7777;
            return 0;
        });
        var state = program.InitialState;
        ushort originalSp = state.SP;
        program.Invoke(ref state, new(1, 0));
        Assert.Equal((ushort)0x7777, state.AX);
        Assert.Equal((ushort)0x1234, state.CX);
        Assert.Equal(originalSp, state.SP);
    }

    [Fact]
    public void ImportedFarTailConsumesTheExistingOriginalFrameWithoutAddingAReturnAddress()
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 1, 1, 1, 7));
        NeLoaderTests.W(bytes, 0x94, 0);
        bytes.AsSpan(0x200, 64).Fill(0x90);
        new byte[]
        {
            0xea,
            0xff,
            0xff,
            0,
            0
        }.CopyTo(bytes, 0x200);
        NeLoaderTests.W(bytes, 0x12d, 48);
        bytes[0x230] = 0xcb;
        using var program = new NeWholeProgramControlProgram(bytes);
        var state = program.InitialState;
        ushort sp = state.SP;
        program.RegisterImport("KERNEL.#7", (ref MachineState s, SegmentMemory m) =>
        {
            Assert.Equal(unchecked((ushort)(sp - 6)), s.SP);
            s.AX = m.ReadWord(s.SS, unchecked((ushort)(s.SP + 4)));
            return 2;
        });
        program.Invoke(ref state, new(1, 0), arguments: new ushort[] { 0x1234 });
        Assert.Equal((ushort)0x1234, state.AX);
        Assert.Equal(sp, state.SP);
    }

    [Fact]
    public void ResolverRejectsDataHostDuplicatesAndOversizedBindingTables()
    {
        var memory = new SegmentMemory();
        var dispatcher = new Win16CallDispatcher();
        var code = new Win16ProgramBinding(new(0x1000, 0), Win16ProgramAddressKind.Code, "code", (ref MachineState _) => 0);
        foreach (var kind in new[]
        {
            Win16ProgramAddressKind.Data,
            Win16ProgramAddressKind.NativeHost
        }

        )
        {
            Assert.Throws<InvalidDataException>(() => new Win16ProgramControl([code with { Kind = kind, Entry = null }], memory, dispatcher));
        }

        Assert.Throws<InvalidDataException>(() => new Win16ProgramControl([code, code], memory, dispatcher));
        Assert.Throws<InvalidDataException>(() => new Win16ProgramControl([code, code with { Address = new(0x1000, 1) }], memory, dispatcher, maximumEntries: 1));
        using var control = new Win16ProgramControl([code], memory, dispatcher);
        Assert.Throws<InvalidOperationException>(() => new Win16ProgramControl([code], memory, dispatcher));
        Assert.Throws<InvalidOperationException>(() => control.Resolve(new(0xfff8, 0xffff)));
    }

    [Fact]
    public void TransferBudgetAndDisposalRejectExecutionWithoutNativeStackRecursion()
    {
        byte[] bytes = Fixture(0xeb, 0xfe);
        var program = new NeWholeProgramControlProgram(bytes, maximumTransfers: 20);
        var state = program.InitialState;
        Assert.Throws<InvalidOperationException>(() => program.Invoke(ref state, new(1, 0)));
        program.Dispose();
        Assert.Throws<ObjectDisposedException>(() => program.Invoke(ref state, new(1, 0)));
        Assert.Throws<ObjectDisposedException>(() => program.Control.Resolve(new(0x1000, 0)));
    }

    [Fact]
    public void UnprovenStaticFarSelectorsRemainDiagnosticAndCannotAuthorizeExecution()
    {
        byte[] bytes = Fixture(0x9a, 0x10, 0, 0x34, 0x12, 0xcb); // raw 1234 is not an NE segment/relocation
        var artifact = NeWholeProgramControlCompiler.Compile(bytes);
        Assert.False(artifact.Report.Summary.AllStaticTargetsResolved);
        Assert.Equal(1, artifact.Report.Summary.UnsupportedTransfers);
        Assert.Contains(artifact.Report.Sites, s => s.Site == new NeBinaryAddress(1, 0) && !s.Resolved && s.Resolution == "RejectedUnprovenStaticTarget");
        Assert.Throws<InvalidDataException>(() => new NeWholeProgramControlProgram(bytes));
    }

    [Fact]
    public void CliWritesDeterministicNativeCodeAndSourceAddressMappingsAndRejectsConflictingModes()
    {
        string file = Path.Combine(Path.GetTempPath(), "control-cli-" + Guid.NewGuid().ToString("N") + ".exe"), output = file + ".json";
        byte[] bytes = Fixture(0xb8, 7, 0, 0xcb);
        try
        {
            File.WriteAllBytes(file, bytes);
            string[] args = ["analyze", "--input", file, "--whole-program", "--control-flow", "--output", output];
            var first = CliIntegrationTests.Run(args);
            Assert.True(first.Code == 0, first.Output);
            byte[] report = File.ReadAllBytes(output), native = File.ReadAllBytes(output + ".amd64.bin");
            var second = CliIntegrationTests.Run(args);
            Assert.True(second.Code == 0, second.Output);
            Assert.Equal(report, File.ReadAllBytes(output));
            Assert.Equal(native, File.ReadAllBytes(output + ".amd64.bin"));
            Assert.NotEqual(0, CliIntegrationTests.Run("analyze", "--input", file, "--control-flow").Code);
            Assert.NotEqual(0, CliIntegrationTests.Run(args.Concat(new[] { "--instruction-coverage" }).ToArray()).Code);
            Assert.NotEqual(0, CliIntegrationTests.Run("analyze", "--input", file, "--whole-program", "--control-flow", "--output", file).Code);
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }
        finally
        {
            File.Delete(file);
            File.Delete(output);
            File.Delete(output + ".amd64.bin");
        }
    }

    [SuppliedNeFact]
    public void OriginalWholeImageLinksEveryDiscoveredStaticTargetWithoutSymbols()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        using var program = new NeWholeProgramControlProgram(bytes);
        var report = program.Artifact.Report;
        Assert.True(report.Summary.AllStaticTargetsResolved);
        Assert.Equal(0, report.Summary.UnsupportedTransfers);
        Assert.Equal(report.Entries.Count, report.Summary.NativeEntries);
        Assert.Equal(24, report.Summary.IndirectTransferSites);
        Assert.Equal(7, report.Summary.VerifiedTargetSets);
        Assert.Equal(17, report.Summary.UnresolvedTargetSets);
        Assert.Equal(123, report.Imports.Count);
        foreach (var site in report.Sites.Where(s => s.Kind == "Direct"))
        {
            Assert.True(site.Resolved);
            Assert.All(site.OriginalTargets, t => Assert.Contains(report.Entries, e => e.Original == t));
        }

        Assert.Equal(179, report.NativeDispatchTables.Sum(t => t.Slots.Count));
        Assert.All(report.NativeHelperFixups, f => Assert.True(NativeNeRuntime.Exports.ContainsKey(f.Symbol)));
        Assert.All(report.NativeDispatchTables.SelectMany(t => t.Slots), slot => Assert.Contains(report.Entries, e => e.Original == slot.Target && e.NativeOffset == slot.NativeOffset));
        Assert.False(report.Summary.WholeProgramExecutionSupported);
    }
}
