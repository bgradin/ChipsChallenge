using Translator.Analysis;
using Translator.Binary;
using Translator.IR;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeBinaryTranslationTests
{
    [Fact]
    public void BinaryFramesMultipleReturnsAndCleanupExecuteWithoutReconstructedMacros()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0x85, 0xc0, 0x74, 4, 0xb8, 1, 0, 0xcb, 0xb8, 2, 0, 0xcb);
        using var program = NeNativeProgram.Create(bytes, "1:0");
        var state = program.CreateState();
        ushort sp = state.SP;
        program.Invoke(ref state, Array.Empty<ushort>());
        Assert.Equal(2, state.AX);
        Assert.Equal(sp, state.SP);
        state = program.CreateState();
        state.AX = 1;
        program.Invoke(ref state, Array.Empty<ushort>());
        Assert.Equal(1, state.AX);
        Assert.All(program.Artifact.Module.Functions, f => Assert.True(f.ExplicitFrame));
        using var cleanup = NeNativeProgram.Create(NeDiscoveryTests.Fixture(0xb8, 7, 0, 0xca, 6, 0), "1:0");
        state = cleanup.CreateState();
        sp = state.SP;
        cleanup.Invoke(ref state, new ushort[] { 1, 2, 3 });
        Assert.Equal(7, state.AX);
        Assert.Equal(sp, state.SP);
    }

    [Fact]
    public void MultiFunctionLinkingSupportsNearCallsAndAuthenticNextInstructionReturnAddresses()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xe8, 13, 0, 0xcb);
        // Return the original near return address through AX before RET consumes it.
        new byte[]
        {
            0x58,
            0x50,
            0xc3
        }.CopyTo(bytes, 0x210);
        using var program = NeNativeProgram.Create(bytes, "1:0");
        Assert.Equal(2, program.Artifact.EntryPoints.Count);
        Assert.Equal(2, program.Artifact.Functions.Count);
        var state = program.CreateState();
        ushort sp = state.SP;
        program.Invoke(ref state, Array.Empty<ushort>());
        Assert.Equal(3, state.AX);
        Assert.Equal(sp, state.SP);
        Assert.All(program.Artifact.LinkedCode.Relocations!, r => Assert.InRange(r.Offset, 0, program.Artifact.LinkedCode.Bytes.Length - 4));
    }

    [Fact]
    public void BinaryIrPreservesSourceWidthsPrefixesAndUnsupportedFormsFailExplicitly()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0x66, 0xb8, 0x78, 0x56, 0x34, 0x12, 0x66, 0xa3, 0, 0, 0xcb);
        using var program = NeNativeProgram.Create(bytes, "1:0");
        var state = program.CreateState();
        program.Invoke(ref state, Array.Empty<ushort>());
        Assert.Equal(0x12345678u, state.EAX);
        Assert.Equal(0x12345678u, program.Runtime.Image.Memory.ReadDword(state.DS, 0));
        var instruction = program.Artifact.Module.Functions[0].Instructions.First(i => i.BinarySource is not null);
        Assert.Equal("66", instruction.BinarySource!.PrefixesHex);
        Assert.Equal("66B878563412", instruction.BinarySource.BytesHex);
        Assert.Equal(RegisterWidth.Dword, instruction.Operands[0].Width);
        foreach (byte[] unsupported in new[]
        {
            new byte[]
            {
                0xd1,
                0xc8,
                0xcb
            },
            new byte[]
            {
                0x66,
                0xcb
            },
            new byte[]
            {
                0x67,
                0xa1,
                0,
                0,
                0,
                0,
                0xcb
            }
        }

        )
        {
            Assert.ThrowsAny<NotSupportedException>(() => NeNativeProgram.Create(NeDiscoveryTests.Fixture(unsupported), "1:0"));
        }

        bytes = NeDiscoveryTests.Fixture(0xe8, 13, 0, 0xcb);
        bytes[0x210] = 0xcb;
        Assert.Throws<InvalidDataException>(() => NeNativeProgram.Create(bytes, "1:0"));
    }

    [Fact]
    public void CallGraphMutationsAreCheckedAgainstOriginalBytesAndOwnedCodeDisposalIsGuarded()
    {
        byte[] bytes = NeDiscoveryTests.Fixture(0xe8, 13, 0, 0xe8, 14, 0, 0xcb);
        new byte[]
        {
            0xb8,
            7,
            0,
            0xc3,
            0xb8,
            8,
            0,
            0xc3
        }.CopyTo(bytes, 0x210);
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        var function = graph.Functions[0];
        var altered = function with
        {
            Dependencies = function.Dependencies.Select(d => d.Kind == "DirectNearCall" && d.Site.Offset == 0
                ? d with
                {
                    Target = new NeBinaryAddress(1, 20)
                }
                : d).ToArray()
        };
        var changed = graph with
        {
            Functions = graph.Functions.Select(f => f.Address == function.Address
                ? altered
                : f).ToArray()
        };
        Assert.Throws<InvalidDataException>(() => new NeBinaryIrBuilder().Build(bytes, changed));
        var program = NeNativeProgram.Create(bytes, "1:0");
        var memory = program.Runtime.Image.Memory;
        var state = program.CreateState();
        program.Dispose();
        program.Dispose();
        Assert.Empty(memory.Segments);
        Assert.Throws<ObjectDisposedException>(() => program.Invoke(ref state, Array.Empty<ushort>()));
    }
}
