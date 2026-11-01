using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class WindowsSuppliedNeFactAttribute : FactAttribute
{
    public WindowsSuppliedNeFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires execution on 64-bit Windows.";
        }
        else if (!File.Exists(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")))
        {
            Skip = "Set CHIPS_NE_INPUT to the supplied original executable.";
        }
    }
}

public sealed class NeStandaloneTests
{
    private const string _fixture = """{"Arguments":[],"ExpectedRegisters":{"AX":7}}""";

    private static byte[] Binary() => NeDiscoveryTests.Fixture(0xb8, 7, 0, 0xcb);

    private static NeStandaloneProgram Build(string fixture = _fixture) => NeStandaloneProgram.Build(Binary(), "1:0", fixture);

    [Fact]
    public void PeIsDeterministicAmd64WithoutClrAndCarriesSourceAndInitializationIdentity()
    {
        var first = Build();
        var second = Build();
        Assert.Equal(first.Executable, second.Executable);
        Assert.Equal(first.Descriptor, second.Descriptor);
        using var reader = new PEReader(new MemoryStream(first.Executable));
        Assert.False(reader.HasMetadata);
        Assert.Null(reader.PEHeaders.CorHeader);
        Assert.Equal(Machine.Amd64, reader.PEHeaders.CoffHeader.Machine);
        Assert.Equal(PEMagic.PE32Plus, reader.PEHeaders.PEHeader!.Magic);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Binary())).ToLowerInvariant(), first.Manifest.ExecutableSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first.Executable)).ToLowerInvariant(), first.Manifest.PeSha256);
        Assert.Equal(44, first.Manifest.Initialization.MachineStateAbiSize);
        Assert.Single(first.Manifest.Translation.Functions);
        // Indexed input addresses are checked by the native runtime, not guessed by discovery.
        Assert.NotEmpty(NeStandaloneProgram.Build(NeDiscoveryTests.Fixture(0xbb, 0xff, 0xff, 0x8b, 0x07, 0xcb), "1:0", _fixture).Executable);
        Assert.Equal(4, first.Imports.Count);
        Assert.Equal(5, first.Manifest.NativeImports.Count);
        Assert.Contains(first.Manifest.NativeImports, i => i.Name == "ExitProcess");
        Assert.All(first.Imports, i => Assert.Equal("KERNEL32.dll", i.Module));
        Assert.DoesNotContain("CLR", first.Imports.Select(i => i.Module));
    }

    [Fact]
    public void RejectsMissingOracleUnknownRegistersInvalidSegmentsAndManagedBackend()
    {
        foreach (string fixture in new[]
        {
            "{}",
            "{",
            """{"Arguments":[]} """,
            """{"Arguments":[],"ExpectedRegisters":{"Unknown":7}}""",
            """{"Arguments":[],"ExpectedRegisters":{"AX":65536}}""",
            """{"Arguments":[],"Segments":[{"Selector":4096,"Size":8}],"ExpectedRegisters":{"AX":7}}"""
        }

        )
        {
            Assert.Throws<InvalidDataException>(() => Build(fixture));
        }

        Assert.Throws<NotSupportedException>(() => NeStandaloneProgram.Build(Binary(), "1:0", _fixture, runtimeOptions: new(NeMemoryBackend.Managed)));
    }

    [WindowsFact]
    public void StandaloneExecutesNativeBodiesAndDetectsResultCodeDataAndCallMutations()
    {
        Assert.Equal(0, Execute(Build().Executable));
        Assert.Equal(40, Execute(Build(_fixture.Replace(":7", ":8")).Executable));
        byte[] changed = Binary();
        changed[0x201] = 8;
        Assert.Equal(40, Execute(NeStandaloneProgram.Build(changed, "1:0", _fixture).Executable));
        byte[] calls = NeDiscoveryTests.Fixture(0xe8, 13, 0, 0xcb);
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
        }.CopyTo(calls, 0x210);
        Assert.Equal(0, Execute(NeStandaloneProgram.Build(calls, "1:0", _fixture).Executable));
        calls[0x201] = 17;
        Assert.Equal(40, Execute(NeStandaloneProgram.Build(calls, "1:0", _fixture).Executable));
        const string data = """{"Arguments":[],"ExpectedMemory":[{"SegmentIndex":2,"Offset":0,"BytesHex":"80"}]}""";
        Assert.Equal(0, Execute(Build(data).Executable));
        changed = Binary();
        changed[0x400] ^= 1;
        Assert.Equal(40, Execute(NeStandaloneProgram.Build(changed, "1:0", data).Executable));
        // Corrupt the embedded descriptor's explicit version, without touching generated code.
        var broken = Build();
        byte[] pe = broken.Executable.ToArray();
        int at = Find(pe, broken.Descriptor);
        Assert.True(at >= 0);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(at + 4), 99);
        Assert.Equal(10, Execute(pe));
        var fault = NeStandaloneProgram.Build(NeDiscoveryTests.Fixture(0xbb, 0xff, 0xff, 0x8b, 0x07, 0xcb), "1:0", _fixture);
        Assert.Equal(30, Execute(fault.Executable));
        var constrained = NeStandaloneProgram.Build(Binary(), "1:0", _fixture, runtimeOptions: new(NativeLimits: new(MaximumBytes: 128)));
        Assert.Equal(20, Execute(constrained.Executable));
    }

    [WindowsSuppliedNeFact]
    public void SuppliedOriginalFiveFunctionClosureAndDynamicServicesRunWithoutSourceFileOrClr()
    {
        byte[] original = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        foreach (var (root, name) in new[]
        {
            ("0003:045E", "DeleteMonsterAt.compact"),
            ("0003:0000", "FindMonster.empty"),
            ("0003:01A4", "GrowArray.allocate")
        }

        )
        {
            string fixture = File.ReadAllText(Path.GetFullPath($"../../../../../tests/Fixtures/NativeExecution/{name}.json"));
            var program = NeStandaloneProgram.Build(original, root, fixture);
            Assert.Equal(root == "0003:045E"
                ? 5
                : 1, program.Manifest.Translation.Functions.Count);
            Assert.Equal(0, Execute(program.Executable));
            if (root == "0003:045E")
            {
                var changed = System.Text.Json.Nodes.JsonNode.Parse(fixture)!;
                changed["ExpectedRegisters"]!["BP"] = 1;
                Assert.Equal(40, Execute(NeStandaloneProgram.Build(original, root, changed.ToJsonString()).Executable));
            }

            // A damaged descriptor must be rejected before entering translated code.
            var invalid = program.Executable.ToArray();
            int at = Find(invalid, program.Descriptor);
            Assert.True(at >= 0);
            invalid[at] ^= 1;
            Assert.Equal(10, Execute(invalid));
        }
    }

    private static int Find(byte[] bytes, byte[] pattern)
    {
        for (int i = 0; i <= bytes.Length - pattern.Length; i++)
        {
            if (bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }

    internal static int Execute(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ne-standalone-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(path, bytes);
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() })!;
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("NE standalone executable timed out.");
            }

            return process.ExitCode;
        }
        finally
        {
            File.Delete(path);
        }
    }
}
