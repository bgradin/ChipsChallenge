using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Translator.Analysis;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Linker;

public sealed record NePeMemoryValue(int? SegmentIndex, ushort? Selector, ushort Offset, string BytesHex);
public sealed record NePeFixtureSegment(ushort Selector, int Size, string? BytesHex);
public sealed record NePeFixture(ushort[] Arguments, Dictionary<string, uint>? Registers, NePeFixtureSegment[]? Segments, NePeMemoryValue[]? Writes, Dictionary<string, uint>? ExpectedRegisters, NePeMemoryValue[]? ExpectedMemory);
public sealed record NeStandaloneManifest(string Format, string ExecutableSha256, string PeSha256, string FixtureSha256, string DescriptorSha256, string NativeHelperSha256, NeNativeArtifact Translation, NeRuntimeInitialization Initialization, IReadOnlyList<PeNativeImport> NativeImports, int EmbeddedImageBytes, string InputPolicy, IReadOnlyDictionary<int, string> ExitCodes);
/// <summary>Packages statically recompiled NE bodies with an owned heap, fixed-up image and independent fixture oracle.</summary>
public sealed class NeStandaloneProgram
{
    public PeFunctionBody Entry
    {
        get;
    }
    public IReadOnlyList<PeFunctionBody> Functions
    {
        get;
    }
    public IReadOnlyList<PeNativeImport> Imports
    {
        get;
    }
    public byte[] Descriptor
    {
        get;
    }
    public NeStandaloneManifest Manifest
    {
        get;
    }
    public byte[] Executable
    {
        get;
    }

    private static string Hash(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private NeStandaloneProgram(NeNativeArtifact artifact, NeRuntimeProgram runtime, NePeFixture fixture, string fixtureJson)
    {
        if (fixture.Arguments is null || fixture.Arguments.Length > 256 || (fixture.ExpectedRegisters?.Count ?? 0) + (fixture.ExpectedMemory?.Length ?? 0) == 0)
        {
            throw new InvalidDataException("Standalone execution requires Arguments (at most 256 words) and explicit expected register/memory assertions.");
        }

        if ((fixture.ExpectedRegisters?.Count ?? 0) > 64 || (fixture.ExpectedMemory?.Length ?? 0) > 4096)
        {
            throw new InvalidDataException("Standalone fixture assertion limit exceeded.");
        }

        // These are the four existing freestanding KERNEL adapters. Other registry entries are not implementations here.
        foreach (var import in artifact.Graph.Dependencies.Where(d => d.Import is not null).Select(d => d.Import!).Distinct())
        {
            var api = NeImportResolver.Classify(import).Api;
            if (api is null || api.Module != "KERNEL" || api.Name is not ("GlobalAlloc" or "GlobalReAlloc" or "GlobalLock" or "GlobalUnlock"))
            {
                throw new NotSupportedException($"Standalone runtime does not implement required import {import.Module}!{import.Name ?? import.Ordinal?.ToString()}.");
            }
        }

        var unknown = (artifact.LinkedCode.Relocations ?? Array.Empty<MachineCodeRelocation>()).Where(r => !NativeNeRuntime.Exports.ContainsKey(r.Symbol)).ToArray();
        if (unknown.Length != 0)
        {
            throw new NotSupportedException("Standalone native helper is unavailable: " + string.Join(", ", unknown.Select(r => r.Symbol).Distinct()));
        }

        var memory = runtime.Image.Memory;
        var owned = new List<Win16Segment>();
        byte[] descriptor;
        int imageBytes;
        try
        {
            var extras = fixture.Segments ?? Array.Empty<NePeFixtureSegment>();
            if (extras.Length > 128 || extras.Any(s => s is null) || extras.Sum(s => (long)s.Size) > 8 * 1024 * 1024)
            {
                throw new InvalidDataException("Standalone fixture segment budget exceeded.");
            }

            foreach (var extra in extras)
            {
                if (extra.Selector is 0 or 0xf000 || extra.Size is < 1 or > 65536 || memory.TryGetSegment(extra.Selector, out _))
                {
                    throw new InvalidDataException("Invalid or colliding standalone fixture segment.");
                }

                var segment = memory.CreateSegment(extra.Selector, extra.Size);
                owned.Add(segment);
                memory.Map(segment);
                if (extra.BytesHex is not null)
                {
                    Write(extra.Selector, 0, Convert.FromHexString(extra.BytesHex));
                }
            }

            ushort Selector(NePeMemoryValue value) => value is null
                ? throw new InvalidDataException("Null fixture memory record.")
                : value.SegmentIndex is int index && value.Selector is null
                    ? runtime.Image.SelectorFor(index)
                    : value.SegmentIndex is null && value.Selector is ushort selector && selector != 0
                        ? selector
                        : throw new InvalidDataException("Memory record requires exactly one SegmentIndex or nonzero Selector.");


            void Write(ushort selector, ushort offset, byte[] bytes)
            {
                var segment = memory.GetSegment(selector);
                if (bytes.Length > segment.Size || offset > segment.Size - bytes.Length)
                {
                    throw new InvalidDataException("Fixture write exceeds segment.");
                }

                memory.WriteBytes(selector, offset, bytes);
            }


            foreach (var write in fixture.Writes ?? Array.Empty<NePeMemoryValue>())
            {
                Write(Selector(write), write.Offset, Convert.FromHexString(write.BytesHex));
            }

            object state = runtime.Image.InitialState;
            foreach (var register in fixture.Registers ?? new())
            {
                var (lo, hi) = Register(register.Key);
                if (hi == 65535 && register.Value > 65535)
                {
                    throw new InvalidDataException("Word register override exceeds 65535.");
                }

                var field = typeof(MachineState).GetFields().Single(f => !f.IsStatic && Marshal.OffsetOf<MachineState>(f.Name).ToInt32() == lo);
                field.SetValue(state, (ushort)register.Value);
                if (hi != 65535)
                {
                    typeof(MachineState).GetField(field.Name + "High")!.SetValue(state, (ushort)(register.Value >> 16));
                }
            }

            var initial = (MachineState)state;
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write(0x34454e54u);
            w.Write(1u);
            w.Write((uint)MachineState.AbiVersion);
            w.Write((uint)MachineState.AbiSize);
            var segments = memory.Segments.OrderBy(s => s.Selector).ToArray();
            var functions = artifact.Functions.ToList();
            w.Write((uint)segments.Length);
            w.Write((uint)functions.Count);
            w.Write((uint)functions.FindIndex(f => f.Address == artifact.Graph.Root));
            w.Write((uint)fixture.Arguments.Length);
            w.Write((uint)(fixture.ExpectedRegisters?.Count ?? 0));
            w.Write((uint)(fixture.ExpectedMemory?.Length ?? 0));
            var limits = runtime.Initialization.RuntimeLimits;
            w.Write((uint)limits.NativeLimits!.MaximumAllocations);
            w.Write((ulong)limits.NativeLimits.MaximumBytes);
            w.Write((uint)limits.GlobalLimits!.MaxAllocations);
            w.Write((ulong)limits.GlobalLimits.MaxBytes);
            w.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref initial, 1)));
            imageBytes = 0;
            foreach (var segment in segments)
            {
                var mapping = artifact.SegmentMappings.SingleOrDefault(m => m.Selector == segment.Selector);
                w.Write(segment.Selector);
                w.Write((ushort)((segment.CanRead
                    ? 1
                    : 0) | (segment.CanWrite
                    ? 2
                    : 0) | (mapping is { IsData: false }
                    ? 4
                    : 0)));
                w.Write((uint)segment.Size);
                // Trim zero-filled tails; reconstruct them with the zero-initializing native allocator.
                byte[] data = segment.Memory.ToArray();
                int length = data.Length;
                while (length > 0 && data[length - 1] == 0)
                {
                    length--;
                }

                w.Write((uint)length);
                w.Write(data, 0, length);
                imageBytes += length;
            }

            foreach (var f in functions)
            {
                w.Write(runtime.Image.SelectorFor(f.Address.SegmentIndex));
                w.Write(f.Address.Offset);
                w.Write(f.Distance == CallDistance.Far
                    ? 1u
                    : 0u);
            }

            foreach (ushort word in fixture.Arguments)
            {
                w.Write(word);
            }

            foreach (var register in (fixture.ExpectedRegisters ?? new()).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var (lo, hi) = Register(register.Key);
                if (hi == 65535 && register.Value > 65535)
                {
                    throw new InvalidDataException("Word expectation exceeds 65535.");
                }

                w.Write(lo);
                w.Write(hi);
                w.Write(register.Value);
            }

            foreach (var expected in fixture.ExpectedMemory ?? Array.Empty<NePeMemoryValue>())
            {
                byte[] data = Convert.FromHexString(expected.BytesHex);
                if (data.Length is < 1 or > 65536 || (long)expected.Offset + data.Length > 65536)
                {
                    throw new InvalidDataException("Memory assertion exceeds Win16 segment.");
                }

                w.Write(Selector(expected));
                w.Write(expected.Offset);
                w.Write((uint)data.Length);
                w.Write(data);
            }

            descriptor = stream.ToArray();
        }
        finally
        {
            foreach (var segment in owned)
            {
                memory.Unmap(segment.Selector);
                segment.Dispose();
            }
        }

        Descriptor = descriptor;
        // RIP-relative LEAs and tail jump preserve the launcher's Win64 shadow space/alignment.
        byte[] entry =
        {
            0x48,
            0x8d,
            0x0d,
            0,
            0,
            0,
            0,
            0x48,
            0x8d,
            0x15,
            0,
            0,
            0,
            0,
            0x41,
            0xb8,
            0,
            0,
            0,
            0,
            0x41,
            0xb9,
            0,
            0,
            0,
            0,
            0xe9,
            0,
            0,
            0,
            0
        };
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), descriptor.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(22), artifact.Functions.Count);
        Entry = new("ne_program_entry", entry, new[] { new PeRelativeCallFixup(3, "ne_image", PeRelativeReferenceKind.RipRelativeAddress), new PeRelativeCallFixup(10, "ne_procedures", PeRelativeReferenceKind.RipRelativeAddress), new PeRelativeCallFixup(27, "ne_entry") });
        var helper = new MachineCodeArtifact(NativeNeRuntime.Bytes, "AMD64", X64Abi.Windows, "NE runtime ABI v2/44", NativeNeRuntime.TargetFixups.Select(f => new MachineCodeRelocation(f.Value, f.Key, MachineCodeRelocationKind.Relative32Call)).ToArray());
        var table = new PeFunctionBody("ne_procedures", new byte[artifact.Functions.Count * 4], artifact.Functions.Select((f, i) => new PeRelativeCallFixup(i * 4, f.NativeSymbol, PeRelativeReferenceKind.DataRelative32)).ToArray());
        Functions = new[]
        {
            PeFunctionBody.FromArtifact("ne_runtime", helper)with
            {
                Exports = NativeNeRuntime.Exports
            },
            PeFunctionBody.FromArtifact("ne_bodies", artifact.LinkedCode)with
            {
                Exports = artifact.EntryPoints
            },
            new PeFunctionBody("ne_image", descriptor),
            table
        };
        Imports = NativeNeRuntime.Imports.Select(i => new PeNativeImport(i.Module, i.Name, i.Symbol)).ToArray();
        Executable = new Pe32PlusWriter().WriteProgram(Entry, Functions, Imports);
        Manifest = new("NE-driven PE32+ v1", artifact.ExecutableSha256, Hash(Executable), Hash(Encoding.UTF8.GetBytes(fixtureJson)), Hash(descriptor), Hash(helper.Bytes), artifact, runtime.Initialization, new[] { new PeNativeImport("KERNEL32.dll", "ExitProcess", "__launcher_exit") }.Concat(Imports).ToArray(), imageBytes, "Local supplied executable: embedded fixed-up segments and authored fixture only; no extracted resources or runtime source-file dependency", new Dictionary<int, string> { [0] = "Verified; owned resources released", [10] = "Invalid descriptor/ABI", [20] = "Allocation/initialization failed", [30] = "Native runtime error", [40] = "Register/memory assertion failed", [50] = "Cleanup/ownership failure" });
    }

    private static (ushort Low, ushort High) Register(string name)
    {
        var field = typeof(MachineState).GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field?.FieldType == typeof(ushort))
        {
            return ((ushort)Marshal.OffsetOf<MachineState>(name).ToInt32(), 65535);
        }

        var property = typeof(MachineState).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType == typeof(uint) && name.StartsWith('E'))
        {
            string word = name[1..];
            return ((ushort)Marshal.OffsetOf<MachineState>(word).ToInt32(), (ushort)Marshal.OffsetOf<MachineState>(word + "High").ToInt32());
        }

        throw new InvalidDataException($"Unknown MachineState register '{name}'.");
    }

    public static NeStandaloneProgram Build(ReadOnlySpan<byte> bytes, string root, string fixtureJson, NeVerifiedSymbols? symbols = null, IrModule? reconstruction = null, IReadOnlyDictionary<string, int>? comparisonBindings = null, NeRuntimeOptions? runtimeOptions = null, bool resolveIndirectTransfers = false)
    {
        NePeFixture fixture;
        try
        {
            fixture = JsonSerializer.Deserialize<NePeFixture>(fixtureJson) ?? throw new InvalidDataException("Empty standalone fixture.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid standalone fixture JSON: " + error.Message, error);
        }

        if (runtimeOptions?.MemoryBackend == NeMemoryBackend.Managed)
        {
            throw new NotSupportedException("Standalone PE uses native heap storage; select --memory-backend Native.");
        }

        using var runtime = NeRuntimeProgram.Load(bytes, new(AllowIncompleteRelocations: true), runtimeOptions);
        var artifact = NeNativeTranslation.Compile(bytes, root, runtime.Image, symbols, reconstruction, comparisonBindings, resolveIndirectTransfers: resolveIndirectTransfers);
        try
        {
            return new(artifact, runtime, fixture, fixtureJson);
        }
        catch (Exception error) when (error is FormatException or OverflowException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Invalid standalone fixture: " + error.Message, error);
        }
    }
}
