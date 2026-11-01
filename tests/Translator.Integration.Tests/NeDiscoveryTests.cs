using System.Security.Cryptography;
using System.Text.Json;
using Translator.Analysis;
using Translator.Binary;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeDiscoveryTests
{
    internal static byte[] Fixture(params byte[] code)
    {
        byte[] bytes = NeLoaderTests.Fixture();
        bytes.AsSpan(0x200, 64).Fill(0x90);
        code.CopyTo(bytes, 0x200);
        bytes[0x230] = 0xcb;
        NeLoaderTests.W(bytes, 0x12d, 48); // exported entry is separate from root's body
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void SplitWindowProcedureStoresRequireMatchingSlotsAndSelectorRelocation()
    {
        byte[] bytes = Fixture(0xc7, 0x46, 0x98, 0x10, 0, 0xc7, 0x46, 0x9a, 0xff, 0xff, 0xcb);
        bytes[0x210] = 0xcb;
        NeLoaderTests.W(bytes, 0xc4, 0x100);
        NeLoaderTests.W(bytes, 0x240, 1);
        bytes[0x242] = 2;
        NeLoaderTests.W(bytes, 0x244, 8);
        NeLoaderTests.W(bytes, 0x246, 1);
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.Contains(graph.Dependencies, d => d.Kind == "CallbackOrProcedureReference" && d.Target == new NeBinaryAddress(1, 16));
        bytes[0x207] = 0x9c; // selector no longer follows the offset word
        Assert.DoesNotContain(new NeProgramAnalyzer().Analyze(bytes, "1:0").Dependencies, d => d.Kind == "CallbackOrProcedureReference");
    }

    [Fact]
    public void DiagnosticLoweringCannotAuthorizeAnIncompleteClosureForExecution()
    {
        byte[] bytes = Fixture(0xff, 0xd0, 0xcb);
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.False(graph.ClosureComplete);
        _ = new NeBinaryIrBuilder().AuditFunction(bytes, graph, graph.Root);
        Assert.Throws<InvalidDataException>(() => new NeBinaryIrBuilder().Build(bytes, graph));
        bytes[0x200] ^= 1;
        Assert.Throws<InvalidDataException>(() => new NeBinaryIrBuilder().AuditFunction(bytes, graph, graph.Root));
    }

    [Fact]
    public void BinaryAddressesNeverImplyRuntimeSelectors()
    {
        var address = NeBinaryAddress.Parse("000A:1234");
        Assert.Equal(10, address.SegmentIndex);
        Assert.Equal(0x1234, address.Offset);
        Assert.Equal("000A:1234", address.Identity);
        Assert.Throws<ArgumentException>(() => NeBinaryAddress.Parse("0:0"));
    }

    [Fact]
    public void DiscoversNearCallsAndFunctionsWithoutAnyReconstruction()
    {
        byte[] bytes = Fixture(0xe8, 13, 0, 0xcb);
        bytes[0x210] = 0xc3;
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.True(graph.ClosureComplete);
        Assert.Equal(new ushort[] { 0, 16 }, graph.Functions.Select(f => f.Address.Offset));
        Assert.Contains(graph.Dependencies, e => e.Kind == "DirectNearCall" && e.Target == new NeBinaryAddress(1, 16));
        Assert.Null(graph.SymbolVerification);
        Assert.All(graph.Functions, f => Assert.Equal("NotValidated", f.TranslationStatus));
        Assert.Equal(JsonSerializer.Serialize(graph), JsonSerializer.Serialize(new NeProgramAnalyzer().Analyze(bytes, "1:0")));
    }

    [Fact]
    public void MultipleReturnsAndLoopsUseReachableControlFlowInsteadOfFirstRetBoundaries()
    {
        var returns = new NeProgramAnalyzer().Analyze(Fixture(0x85, 0xc0, 0x74, 1, 0xcb, 0xcb), "1:0");
        Assert.True(returns.ClosureComplete);
        Assert.Equal(2, returns.Functions.Single().Instructions.Count(i => i.FlowControl == "Return"));
        var loop = new NeProgramAnalyzer().Analyze(Fixture(0xe2, 0xfe, 0xcb), "1:0");
        Assert.True(loop.ClosureComplete);
        Assert.Equal(2, loop.Functions.Single().Instructions.Count);
    }

    [Fact]
    public void FarCallsResolveRelocationsRatherThanTreatingChainWordsAsSegments()
    {
        byte[] bytes = Fixture(0x9a, 16, 0, 0xff, 0xff, 0xcb);
        bytes[0x210] = 0xcb;
        NeLoaderTests.W(bytes, 0xc4, 0x100);
        NeLoaderTests.W(bytes, 0x240, 1);
        bytes[0x242] = 2;
        bytes[0x243] = 0;
        NeLoaderTests.W(bytes, 0x244, 3);
        NeLoaderTests.W(bytes, 0x246, 1);
        NeLoaderTests.W(bytes, 0x248, 0);
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.True(graph.ClosureComplete);
        Assert.Equal(2, graph.Functions.Count);
        Assert.Contains(graph.Dependencies, e => e.Kind == "DirectFarCall" && e.Target == new NeBinaryAddress(1, 16));
        NeLoaderTests.W(bytes, 0xc4, 0);
        var missing = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.False(missing.ClosureComplete);
        Assert.Contains(missing.Dependencies, e => e.Kind == "UnresolvedFarTransfer" && !e.Resolved);
    }

    [Fact]
    public void ImportedCallsMatchTheExistingRegistryAndUnsupportedApisPreventCompleteClosure()
    {
        byte[] bytes = NeLoaderTests.Fixture((3, 1, 1, 1, 15));
        bytes.AsSpan(0x200, 6).Clear();
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
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.True(graph.ClosureComplete);
        Assert.Contains(graph.Dependencies, e => e.Import?.Ordinal == 15 && e.Classification == "SupportedRuntimeService");
        NeLoaderTests.W(bytes, 0x248, 17);
        var free = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.False(free.ClosureComplete);
        Assert.Contains(free.Dependencies, e => e.Import?.Ordinal == 17 && e.Detail.Contains("GlobalFree") && !e.Resolved);
    }

    [Fact]
    public void IndirectFarCallUsesDataRelocationEvidenceAndSegmentState()
    {
        byte[] bytes = NeImportTests.MemoryImportDataFixture();
        NeLoaderTests.W(bytes, 0x12d, 48);
        new byte[]
        {
            0xff,
            0x1e,
            4,
            0,
            0xcb
        }.CopyTo(bytes, 0x200);
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.True(graph.ClosureComplete);
        Assert.Contains(graph.Dependencies, d => d.Kind == "IndirectFarCall" && d.Import?.Ordinal == 15);
        // Two paths reach the same call, one changes DS to an unknown selector.
        new byte[]
        {
            0x85,
            0xc0,
            0x74,
            2,
            0x8e,
            0xd8,
            0xff,
            0x1e,
            4,
            0,
            0xcb
        }.CopyTo(bytes, 0x200);
        var changedDs = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.False(changedDs.ClosureComplete);
        Assert.Contains(changedDs.Dependencies, d => d.Kind == "IndirectCall" && !d.Resolved);
    }

    [Fact]
    public void IndirectNearCallsRequireMatchingOffsetEvidenceAndFunctionByteOverlapIsRejected()
    {
        byte[] bytes = NeImportTests.MemoryImportDataFixture();
        NeLoaderTests.W(bytes, 0x12d, 48);
        new byte[]
        {
            0xff,
            0x16,
            4,
            0,
            0xcb
        }.CopyTo(bytes, 0x200);
        bytes[0x210] = 0xc3;
        NeLoaderTests.W(bytes, 0x440, 1);
        bytes[0x442] = 5;
        bytes[0x443] = 0;
        NeLoaderTests.W(bytes, 0x446, 1);
        NeLoaderTests.W(bytes, 0x448, 16);
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.True(graph.ClosureComplete);
        Assert.Equal(2, graph.Functions.Count);
        Assert.Contains(graph.Dependencies, d => d.Kind == "IndirectNearCall" && d.Target == new NeBinaryAddress(1, 16));
        // A direct call into the middle of a different decoded instruction is not a valid boundary.
        var overlap = new NeProgramAnalyzer().Analyze(Fixture(0xb8, 0xc3, 0xcb, 0xe8, 0xfb, 0xff, 0xcb), "1:0");
        Assert.False(overlap.ClosureComplete);
        Assert.Contains(overlap.Diagnostics, d => d.Code == "OverlappingFunctionInstructions");
    }

    [Fact]
    public void RelocatedDataPointersAreNotMisclassifiedAsCallbacks()
    {
        byte[] bytes = NeImportTests.MemoryImportDataFixture();
        NeLoaderTests.W(bytes, 0x12d, 48);
        new byte[]
        {
            0xc4,
            0x1e,
            4,
            0,
            0xcb
        }.CopyTo(bytes, 0x200); // LES BX,[4]
        NeLoaderTests.W(bytes, 0x440, 1);
        bytes[0x442] = 3;
        bytes[0x443] = 0;
        NeLoaderTests.W(bytes, 0x446, 2);
        NeLoaderTests.W(bytes, 0x448, 0);
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.True(graph.ClosureComplete);
        Assert.Single(graph.Functions);
        Assert.Contains(graph.Dependencies, d => d.Kind == "DataPointerReference" && d.Classification == "ExternalData");
        NeLoaderTests.W(bytes, 0x446, 1);
        NeLoaderTests.W(bytes, 0x448, 16);
        bytes[0x210] = 0xcb;
        var callback = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.True(callback.ClosureComplete);
        Assert.Equal(2, callback.Functions.Count);
        Assert.Contains(callback.Dependencies, d => d.Kind == "CallbackOrProcedureReference" && d.Target == new NeBinaryAddress(1, 16));
    }

    [Fact]
    public void DynamicCallsAndJumpTablesRemainExplicitlyUnresolved()
    {
        foreach (var code in new[]
        {
            new byte[]
            {
                0xff,
                0xd0,
                0xcb
            },
            new byte[]
            {
                0xff,
                0xe0
            }
        }

        )
        {
            var graph = new NeProgramAnalyzer().Analyze(Fixture(code), "1:0");
            Assert.False(graph.ClosureComplete);
            Assert.Contains(graph.Dependencies, e => e.Classification == "UnresolvedDynamicTarget" && !e.Resolved);
        }
    }

    [Fact]
    public void TailTransfersAliasesAndDiscoveryLimitsAreAuditable()
    {
        byte[] bytes = Fixture(0xeb, 14);
        bytes[0x210] = 0xcb;
        NeLoaderTests.W(bytes, 0x12d, 16);
        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0");
        Assert.True(graph.ClosureComplete);
        Assert.Contains(graph.Dependencies, d => d.Kind == "TailTransfer" && d.Target == new NeBinaryAddress(1, 16));
        var limited = new NeProgramAnalyzer().Analyze(bytes, "1:0", limits: new(MaximumFunctions: 1));
        Assert.False(limited.ClosureComplete);
        Assert.Contains(limited.Diagnostics, d => d.Code == "FunctionLimit");
        var profile = new NeVerifiedSymbols(Hash(bytes), bytes.Length, "test-verified", new[] { new NeProfileSymbol("Alias", new(1, 0), 2, "test"), new NeProfileSymbol("Root", new(1, 0), 2, "test") });
        var aliased = new NeProgramAnalyzer().Analyze(bytes, "Alias", profile);
        Assert.Equal(new[] { "Alias", "Root" }, aliased.Functions.First().Aliases);
        Assert.Throws<InvalidDataException>(() => new NeProgramAnalyzer().Analyze(bytes, "Alias", profile with { ExecutableSha256 = "wrong" }));
    }

    [Fact]
    public void InvalidBoundariesOverlappingInstructionsAndSharedFallthroughDoNotPass()
    {
        foreach (var code in new[]
        {
            new byte[]
            {
                0xb8,
                0x90,
                0x90,
                0xeb,
                0xfc
            },
            new byte[]
            {
                0xe9,
                0xff,
                0x7f
            }
        }

        )
        {
            var graph = new NeProgramAnalyzer().Analyze(Fixture(code), "1:0");
            Assert.False(graph.ClosureComplete);
            Assert.NotEmpty(graph.Diagnostics);
        }

        var shared = new NeProgramAnalyzer().Analyze(Fixture(), "1:0");
        Assert.False(shared.ClosureComplete);
        Assert.Contains(shared.Diagnostics, d => d.Code == "SharedFallthrough");
        Assert.Throws<InvalidDataException>(() => new NeProgramAnalyzer().Analyze(Fixture(), "2:0"));
        Assert.Throws<InvalidDataException>(() => new NeProgramAnalyzer().Analyze(Fixture(), "NotVerified"));
    }

    [Fact]
    public void FixedDataAddressMutationsCannotClaimCompleteDependencies()
    {
        var valid = new NeProgramAnalyzer().Analyze(Fixture(0xa1, 0, 0, 0xcb), "1:0");
        Assert.True(valid.ClosureComplete);
        var invalid = new NeProgramAnalyzer().Analyze(Fixture(0xa1, 0xff, 0x7f, 0xcb), "1:0");
        Assert.False(invalid.ClosureComplete);
        Assert.Contains(invalid.Dependencies, d => d.Classification == "InvalidDataReference");
        Assert.Contains(invalid.Diagnostics, d => d.Code == "UnresolvedDependency");
    }

    [Fact]
    public void SymbolProfilesRejectExecutableAddressAndRangeMutations()
    {
        byte[] bytes = Fixture(0xcb);
        var original = new NeProfileSymbol("Root", new(1, 0), 32, Hash(bytes.AsSpan(0x200, 32).ToArray()));
        var profile = new NeSymbolProfile(1, bytes.Length, Hash(bytes), [original]);
        Assert.Single(NeSymbolProfileReader.VerifyPackagedEntries(profile, bytes).Symbols);
        foreach (var invalid in new[]
        {
            profile with
            {
                Version = 2
            },
            profile with
            {
                ExecutableSize = bytes.Length - 1
            },
            profile with
            {
                ExecutableSha256 = "wrong"
            },
            profile with
            {
                Symbols = [original, original]
            },
            profile with
            {
                Symbols = [null!]
            },
            profile with
            {
                Symbols = [original with
                {
                    Name = ""
                }

                ]
            },
            profile with
            {
                Symbols = [original with
                {
                    Address = new(1, 1)
                }

                ]
            },
            profile with
            {
                Symbols = [original with
                {
                    Address = new(9, 0)
                }

                ]
            },
            profile with
            {
                Symbols = [original with
                {
                    VerifiedRangeLength = 0
                }

                ]
            },
            profile with
            {
                Symbols = [original with
                {
                    VerifiedRangeLength = bytes.Length
                }

                ]
            },
            profile with
            {
                Symbols = [original with
                {
                    BytesSha256 = "wrong"
                }

                ]
            }
        }

        )
        {
            Assert.Throws<InvalidDataException>(() => NeSymbolProfileReader.VerifyPackagedEntries(invalid, bytes));
        }

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(profile));
            Assert.Single(NeSymbolProfileReader.Read(path, bytes).Symbols);
            byte[] changed = (byte[])bytes.Clone();
            changed[0x200] ^= 1;
            Assert.Throws<InvalidDataException>(() => NeSymbolProfileReader.Read(path, changed));
            File.WriteAllText(path, "{");
            Assert.Throws<InvalidDataException>(() => NeSymbolProfileReader.Read(path, bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
