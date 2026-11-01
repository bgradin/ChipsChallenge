using System.Text.Json;
using Translator.Analysis;
using Translator.Binary;
using Translator.IR;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeInstructionCoverageTests
{
    private static byte[] Fixture(params byte[] code)
    {
        byte[] bytes = NeDiscoveryTests.Fixture(code);
        NeLoaderTests.W(bytes, 0x94, 0);
        return bytes;
    }

    private static NeInstructionCoverageReport Audit(byte[] bytes) => NeInstructionCoverageBuilder.Build(bytes, NeWholeProgramDiscoveryBuilder.Build(bytes));

    [Fact]
    public void ReportsCpuFormsAndServiceRequirementsWithoutCertifyingLinkageOrExecution()
    {
        byte[] bytes = Fixture(0xf3, 0xa6, 0xf3, 0xa7, 0xcd, 0x21, 0xcb);
        var report = Audit(bytes);
        Assert.True(report.AllAuditedInstructionsLowered);
        Assert.Equal(0, report.UnsupportedForms);
        Assert.False(report.WholeProgramExecutionSupported);
        foreach (string opcode in new[]
        {
            "Cmpsb_m8_m8",
            "Cmpsw_m16_m16"
        }

        )
        {
            var form = Assert.Single(report.Forms, f => f.Opcode == opcode);
            Assert.Equal("F3", form.PrefixesHex);
            Assert.Equal("Emitted", form.NativeLoweringStatus);
            Assert.Equal(2, form.MemoryEffects.Count);
            Assert.Contains("AF", form.Flags.Written);
            Assert.NotEmpty(form.Tests);
            Assert.Contains("__win16_string", form.NativeHelpers);
        }

        var interrupt = Assert.Single(report.Forms, f => f.Opcode == "Int_imm8");
        Assert.Contains(interrupt.LinkageRequirements, r => r.Contains("freestanding-adapter-unimplemented"));
        Assert.Contains("push FLAGS,CS,nextIP", interrupt.StackEffects);
        Assert.Contains("__win16_interrupt", interrupt.NativeHelpers);
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(Audit(bytes)));
    }

    [Fact]
    public void InstructionMutationExposesUnsupportedFormsRatherThanCountingSuccessfulParsingAsSupport()
    {
        byte[] bytes = Fixture(0xf3, 0xa6, 0xcb);
        Assert.True(Audit(bytes).AllAuditedInstructionsLowered);
        bytes[0x200] = 0x0f;
        bytes[0x201] = 0x31; // RDTSC is not observed in the authoritative binary
        var mutation = Audit(bytes);
        Assert.False(mutation.AllAuditedInstructionsLowered);
        Assert.Equal(1, mutation.UnsupportedForms);
        var form = Assert.Single(mutation.Forms, f => f.NativeLoweringStatus == "Unsupported");
        Assert.Equal("Rdtsc", form.Opcode);
        Assert.NotEmpty(form.Diagnostics);
    }

    [Fact]
    public void UndefinedFlagsAreSeparateFromDefinedSemanticResultsInTypedIrAndAudit()
    {
        byte[] bytes = Fixture(0xf7, 0xf1, 0x21, 0xd8, 0xcb); // DIV CX; AND AX,BX; RETF
        var discovery = NeWholeProgramDiscoveryBuilder.Build(bytes);
        var function = discovery.Graph.Functions.Single(f => f.Address == new NeBinaryAddress(1, 0));
        var div = new NeBinaryIrBuilder().AuditInstruction(bytes, discovery.Graph, function.Address, function.Instructions[0].Address);
        Assert.Empty(div.Semantics!.FlagsWritten);
        Assert.Equal(6, div.Semantics.UndefinedFlags.Count);
        var and = new NeBinaryIrBuilder().AuditInstruction(bytes, discovery.Graph, function.Address, function.Instructions[1].Address);
        Assert.Contains(CpuFlag.AuxiliaryCarry, and.Semantics!.UndefinedFlags);
        Assert.DoesNotContain(CpuFlag.AuxiliaryCarry, and.Semantics.FlagsWritten);
        var form = Assert.Single(NeInstructionCoverageBuilder.Build(bytes, discovery).Forms, f => f.Opcode == "Div_rm16");
        Assert.Contains("AF", form.Flags.Undefined);
        Assert.Equal("None", form.Flags.Written);
    }

    [Fact]
    public void CoverageRejectsChangedImagesAndForgedInstructionBytes()
    {
        byte[] bytes = Fixture(0xf3, 0xa6, 0xcb);
        var discovery = NeWholeProgramDiscoveryBuilder.Build(bytes);
        var modified = discovery with
        {
            Instructions = discovery.Instructions.Select(i => i.Original.Address == new NeBinaryAddress(1, 0)
                ? i with
                {
                    Original = i.Original with
                    {
                        BytesHex = "F3A7"
                    }
                }
                : i).ToArray()
        };
        Assert.Throws<InvalidDataException>(() => NeInstructionCoverageBuilder.Build(bytes, modified));
        Assert.Throws<InvalidDataException>(() => NeInstructionCoverageBuilder.Build(bytes, discovery with { Instructions = [] }));
        Assert.Throws<InvalidDataException>(() => NeInstructionCoverageBuilder.Build(bytes, discovery with { Instructions = discovery.Instructions.Select(i => i with { OverlapsAnotherInterpretation = true }).ToArray() }));
        bytes[0x201] ^= 1;
        Assert.Throws<InvalidDataException>(() => NeInstructionCoverageBuilder.Build(bytes, discovery));
    }

    [Fact]
    public void EveryFreestandingVariantExportsTheNewSemanticServiceBoundary()
    {
        foreach (var (exports, length) in new (IReadOnlyDictionary<string, int>, int)[]
        {
            (NativeAcceptanceRuntime.Exports, NativeAcceptanceRuntime.Bytes.Length),
            (NativeGraphicsRuntime.Exports, NativeGraphicsRuntime.Bytes.Length),
            (NativeWindowsCallbackRuntime.Exports, NativeWindowsCallbackRuntime.Bytes.Length),
            (NativeNeRuntime.Exports, NativeNeRuntime.Bytes.Length),
            (NativeApplicationRuntime.Exports, NativeApplicationRuntime.Bytes.Length),
            (NativeWindowRuntime.Exports, NativeWindowRuntime.Bytes.Length),
            (NativePaintRuntime.Exports, NativePaintRuntime.Bytes.Length),
            (NativeSceneRuntime.Exports, NativeSceneRuntime.Bytes.Length),
            (NativeInputRuntime.Exports, NativeInputRuntime.Bytes.Length)
        }

        )
        {
            Assert.InRange(exports["__win16_interrupt"], 0, length - 1);
            Assert.InRange(exports["__win16_string"], 0, length - 1);
        }
    }

    [Fact]
    public void CliRequiresWholeProgramAuditAndReturnsFailureForUnsupportedInstructionForms()
    {
        string file = Path.Combine(Path.GetTempPath(), $"instruction-coverage-{Guid.NewGuid():N}.exe");
        string output = file + ".json";
        try
        {
            File.WriteAllBytes(file, Fixture(0xf3, 0xa6, 0xcb));
            Assert.NotEqual(0, CliIntegrationTests.Run("analyze", "--input", file, "--instruction-coverage").Code);
            var good = CliIntegrationTests.Run("analyze", "--input", file, "--whole-program", "--instruction-coverage", "--output", output);
            Assert.True(good.Code == 0, good.Output);
            using (var report = JsonDocument.Parse(File.ReadAllText(output)))
            {
                Assert.True(report.RootElement.GetProperty("AllAuditedInstructionsLowered").GetBoolean());
            }

            File.WriteAllBytes(file, Fixture(0x0f, 0x31, 0xcb));
            Assert.Equal(1, CliIntegrationTests.Run("analyze", "--input", file, "--whole-program", "--instruction-coverage", "--output", output).Code);
        }
        finally
        {
            File.Delete(file);
            File.Delete(output);
        }
    }

    [SuppliedNeFact]
    public void EveryUnambiguousOriginalInstanceNormalizesAndEmitsWithExplicitUnresolvedLinkage()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var discovery = NeWholeProgramDiscoveryBuilder.Build(bytes);
        var report = NeInstructionCoverageBuilder.Build(bytes, discovery);
        Assert.True(report.AllAuditedInstructionsLowered);
        Assert.Equal(0, report.UnsupportedForms);
        Assert.Equal(discovery.Instructions.Count(i => !i.OverlapsAnotherInterpretation), report.AuditedInstructions);
        Assert.Equal(report.Forms.Count, report.ObservedForms);
        Assert.Equal(discovery.Instructions.Count(i => i.OverlapsAnotherInterpretation), report.ExcludedAmbiguousInstructions);
        Assert.Equal(discovery.Instructions.Where(i => !i.OverlapsAnotherInterpretation).Select(i => i.Original.Address).Order(), report.Forms.SelectMany(f => f.Sites).Order());
        Assert.Contains(report.AmbiguousInstructions, i => i.Mnemonic == "In");
        Assert.DoesNotContain(report.Forms, f => f.Opcode.StartsWith("In_"));
        Assert.Equal(report.AuditedInstructions, report.Forms.Sum(f => f.Instances));
        Assert.All(report.Forms, f =>
        {
            Assert.Equal("Represented", f.IrStatus);
            Assert.Equal("Emitted", f.NativeLoweringStatus);
            Assert.Empty(f.Diagnostics);
        });
        Assert.Contains(report.Forms.SelectMany(f => f.LinkageRequirements), r => r.StartsWith("Win16ImportContract"));
        Assert.False(report.WholeProgramExecutionSupported);
    }
}
