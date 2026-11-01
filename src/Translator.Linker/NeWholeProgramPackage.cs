using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Translator.Analysis;
using Translator.Binary;
using Translator.CodeGen;
using Win16_Runtime;

namespace Translator.Linker;

public sealed record NePackageResource(string Module, int Index, NeResourceIdentity Type, NeResourceIdentity Name, int SourceOffset, int Bytes, string Sha256, string Conversion, int DerivedBytes, string? DerivedSha256, int DescriptorPayloadOffset, int DerivedOffset, string NativeSmokePolicy);
public sealed record NePackageManifest(string Format, string ExecutableSha256, string DescriptorSha256, string NativePayloadSha256, int MachineStateAbiVersion, string BootstrapPolicy, bool OriginalStartupEntered, bool OriginalExecutableRequiredAtRuntime, bool UtilityRequiredAtRuntime, NeLoadedImageReport Memory, NeControlFlowReport Control, IReadOnlyList<NePackageResource> Resources, IReadOnlyList<PeNativeImport> NativeImports, IReadOnlyList<PeSectionEvidence> Sections, int UnwindRanges, string RelocationPolicy, IReadOnlyDictionary<int, string> ExitCodes, NeUtilityDependencyAudit UtilityDependency)
{
    public int PackageAbiVersion { get; init; } = 1;
    public string DisplayPolicy { get; init; } = "Host GDI capabilities are unchanged; original code selects display resources and mode.";
    public bool OriginalStartupConfigured
    {
        get; init;
    }
    public string TracePolicy { get; init; } = "Optional WIN16_TRACE_FILE selects a fresh binary lifecycle trace; no sidecar, trace or launch argument is required. Trace I/O failures are explicit status 60.";
    public IReadOnlyList<string> DynamicNativeDependencies { get; init; } = ["System WINMM.dll through the five approved MMSYSTEM procedure contracts", "System HHCTRL.OCX HtmlHelpW for input-derived help conversion", "System USER32.dll CharUpperBuffW for keyword case normalization"];
    public IReadOnlyList<string> OriginalExternalAssets { get; init; } = ["CHIPS.DAT (149 original levels)", "Original WAV and MID assets for sound/music", "Referenced HC30 HLP companions, preserved relative names and output-basename HLP/CHM aliases for native system HTML Help"];
}

/// <summary>Whole-program static linking only. No authored state or selected-function packaging.</summary>
public static class NeWholeProgramPackager
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static (byte[] Executable, NePackageManifest Manifest) Build(byte[] source, byte[] utility, bool enterOriginalStartup = false)
    {
        var dependency = NeUtilityDependencyAuditBuilder.Build(utility);
        var artifact = NeWholeProgramControlCompiler.Compile(source);
        if (!artifact.Report.Summary.AllStaticTargetsResolved)
        {
            throw new InvalidDataException("Unproven static transfers prevent packaging.");
        }

        var imports = artifact.Report.Imports.ToDictionary(i => i.Import.Identity, StringComparer.Ordinal);
        foreach (var i in imports.Values.Where(i => i.Kind != "Constant"))
        {
            if (!Win16TaskServiceBindings.All.TryGetValue(i.Import.Identity, out var b) || !Win16TaskServiceBindings.IsNativeBound(b, NativeWholeTaskRuntime.Exports))
            {
                throw new NotSupportedException("Missing native package service: " + i.Import.Identity);
            }
        }

        using var image = new NeProgramLoader().Load(source, importResolver: i => imports[i.Identity].RuntimeAddress, constantResolver: i => imports[i.Identity].ConstantValue);
        var compiled = artifact.Code.Bytes.ToList();
        var relocations = artifact.Code.Relocations!.ToList();
        var bindings = artifact.Report.Entries.Select(e => (Address: e.RuntimeAddress, Kind: (ushort)1, Offset: e.NativeOffset)).ToList();
        foreach (var import in imports.Values.Where(i => i.Kind != "Constant").OrderBy(i => i.Import.Identity, StringComparer.Ordinal))
        {
            var b = Win16TaskServiceBindings.All[import.Import.Identity];
            int offset = compiled.Count;
            compiled.AddRange([0x41, 0xb8]);
            compiled.AddRange(BitConverter.GetBytes((uint)b.NativeModule));
            compiled.AddRange([0x41, 0xb9]);
            compiled.AddRange(BitConverter.GetBytes((uint)b.Ordinal));
            compiled.Add(0xe9);
            relocations.Add(new(compiled.Count, Win16TaskServiceBindings.Dispatcher, MachineCodeRelocationKind.Relative32Call));
            compiled.AddRange(new byte[4]);
            bindings.Add((import.RuntimeAddress, 2, offset));
        }

        var resources = new List<NePackageResource>();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        using var mainCatalog = new NeResourceCatalog(source);
        using var utilityCatalog = new NeResourceCatalog(utility);
        writer.Write(0x374b5457u);
        writer.Write(enterOriginalStartup
            ? 2u
            : 1u);
        writer.Write((uint)MachineState.AbiSize);
        writer.Write((uint)image.Report.Segments.Count);
        writer.Write((uint)bindings.Count);
        writer.Write((uint)mainCatalog.Resources.Count);
        writer.Write((uint)utilityCatalog.Resources.Count);
        writer.Write(checked((ushort)image.Report.InitialRegisters.HeapOffset!.Value));
        writer.Write(image.Report.InitialRegisters.HeapSize);
        var state = image.InitialState;
        writer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref state, 1)));
        writer.Write((uint)state.CS << 16 | state.IP);
        foreach (var s in image.Report.Segments)
        {
            writer.Write(s.Selector);
            writer.Write((ushort)((s.CanRead
                ? 1
                : 0) | (s.CanWrite
                ? 2
                : 0) | (!s.IsData
                ? 4
                : 0)));
            writer.Write((uint)s.AllocatedLength);
            // Zero BSS is described by allocation length. Initialized bytes already contain all fixups.
            writer.Write((uint)s.InitializedLength);
            writer.Write(image.Memory.ReadBytes(s.Selector, 0, s.InitializedLength));
        }

        foreach (var b in bindings.OrderBy(b => b.Address.Segment).ThenBy(b => b.Address.Offset))
        {
            writer.Write(b.Address.Segment);
            writer.Write(b.Address.Offset);
            writer.Write(b.Kind);
            writer.Write((ushort)0);
            writer.Write((uint)b.Offset);
        }

        Catalog("CHIPS", mainCatalog);
        Catalog("WEP4UTIL", utilityCatalog);
        writer.Flush();
        byte[] descriptor = stream.ToArray();
        byte[] entry = [0x48, 0x8d, 0x0d, 0, 0, 0, 0, 0xba, 0, 0, 0, 0, 0x4c, 0x8d, 0x05, 0, 0, 0, 0, 0x41, 0xb9, 0, 0, 0, 0, 0xe9, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), descriptor.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(21), compiled.Count);
        var entryBody = new PeFunctionBody("package_entry", entry, [new(3, "task_image", PeRelativeReferenceKind.RipRelativeAddress), new(15, "original_code", PeRelativeReferenceKind.RipRelativeAddress), new(26, "__win16_package_bootstrap")]);
        var payload = PeFunctionBody.FromArtifact("whole_task_payload", new(NativeWholeTaskRuntime.Bytes, "AMD64", X64Abi.Windows, "MachineState ABI v2/44", NativeWholeTaskRuntime.TargetFixups.Select(f => new MachineCodeRelocation(f.Value, f.Key, MachineCodeRelocationKind.Relative32Call)).ToArray())) with
        {
            Exports = NativeWholeTaskRuntime.Exports
        };
        var code = PeFunctionBody.FromArtifact("original_code", artifact.Code with
        {
            Bytes = compiled.ToArray(),
            Relocations = relocations
        });
        var unwind = NativeWholeTaskRuntime.Unwind.Select(u => new PeUnwindRange(payload.Name, u.Start, u.End, u.Info)).ToList();
        int[] offsets = artifact.Report.Entries.Select(e => e.NativeOffset).Distinct().Order().ToArray();
        for (int i = 0; i < offsets.Length; i++)
        {
            int end = i + 1 < offsets.Length
                ? offsets[i + 1]
                : artifact.Code.Bytes.Length;
            if (!artifact.Code.Bytes.AsSpan(offsets[i], Win16BodyCodeGenerator.EntryPrologue.Length).SequenceEqual(Win16BodyCodeGenerator.EntryPrologue))
            {
                throw new InvalidDataException("Unrecognized original-code native prolog.");
            }

            unwind.Add(new(code.Name, offsets[i], end, [1, 8, 3, 0, 8, 0x62, 4, 0xd0, 2, 0xc0, 0, 0]));
        }

        var nativeImports = NativeWholeTaskRuntime.Imports.Select(i => new PeNativeImport(i.Module, i.Name, i.Symbol)).ToArray();
        var pe = new Pe32PlusWriter().WriteWholeProgram(entryBody, [payload, code], [new("task_image", descriptor)], nativeImports, unwind);
        var manifest = new NePackageManifest("NeWholeProgramPackage.v1", Hash(pe.Bytes), Hash(descriptor), Hash(payload.Bytes), MachineState.AbiVersion, enterOriginalStartup
            ? "Development application: initialize NE environment and enter original initial CS:IP; original code owns windows, game state, message loop and shutdown."
            : "Development package: initialize original NE mappings, local heap, all native services and approved compiled-address map; validate native resource objects; exit. Original startup is the 7.8 gate.", false, false, false, image.Report, artifact.Report, resources, new[] { new PeNativeImport("KERNEL32.dll", "ExitProcess", "__launcher_exit") }.Concat(nativeImports).ToArray(), pe.Sections, unwind.Count + 1, "Entire image uses RIP-relative references and RVA import/unwind directories. ASLR and NX enabled; relocation directory contains only ABSOLUTE padding, with no absolute image addresses to patch.", new Dictionary<int, string> { { 0, enterOriginalStartup
            ? "Original task terminated normally and native cleanup passed"
            : "Package bootstrap, native resources and cleanup passed; original startup not entered" }, { 10, "Invalid package/ABI/map/conversion" }, { 20, "Native initialization/allocation failure" }, { 30, "Native compatibility error" }, { 4000, "CHIPS native resource failure: exit code minus 4000 is the one-based resource index" }, { 8000, "WEP native resource failure: exit code minus 8000 is the one-based resource index" }, { 50, "Native cleanup failure" }, { 60, "Requested lifecycle trace could not be created/written" } }, dependency)
        {
            OriginalStartupConfigured = enterOriginalStartup,
            PackageAbiVersion = enterOriginalStartup
                ? 2
                : 1
        };
        return (pe.Bytes, manifest);

        void Catalog(string module, NeResourceCatalog catalog)
        {
            for (int i = 1; i <= catalog.Resources.Count; i++)
            {
                var r = catalog.Extract(i);
                var (conversion, derived) = ConvertResource(r);
                writer.Write((ushort)(r.Metadata.Type.Id ?? 0));
                writer.Write((ushort)(r.Metadata.Identity.Id ?? 0));
                Name(r.Metadata.Type.Name);
                Name(r.Metadata.Identity.Name);
                writer.Write((uint)r.Bytes.Length);
                int at = (int)stream.Position;
                writer.Write(r.Bytes);
                writer.Write((uint)derived.Length);
                int derivedAt = (int)stream.Position;
                writer.Write(derived);
                resources.Add(new(module, i, r.Metadata.Type, r.Metadata.Identity, r.Metadata.FileOffset, r.Bytes.Length, r.PayloadSha256, conversion, derived.Length, derived.Length == 0
                    ? null
                    : Hash(derived), at, derivedAt, module == "WEP4UTIL" && r.Metadata.Type.Id == 5 && r.Metadata.Identity.Id is 200 or 300
                    ? "Preserved and converted; unimported Hall-of-Fame utility dialogs require original custom classes. None of the five imported utility contracts uses these templates."
                    : r.Metadata.Type.Id is 2 or 4 or 5 or 9 or 12 or 14
                        ? "Validated through real Windows APIs by the resource-smoke bootstrap; application loads follow original code; owned objects released at teardown."
                        : "Raw catalog bytes/provenance verified independently; images/strings are consumed through their group/resource contracts."));
            }
        }


        void Name(string? name)
        {
            byte[] b = name is null
                ? []
                : Encoding.Latin1.GetBytes(name + '\0');
            writer.Write((uint)b.Length);
            writer.Write(b);
        }
    }

    internal static (string, byte[]) ConvertResource(NeExtractedResource r)
    {
        if (r.Metadata.Type.Id == 5)
        {
            return ("Win16 dialog to Win32 DLGTEMPLATE; native conversion compared byte-for-byte at bootstrap", Win16TaskGuiResources.ConvertDialogTemplate(r.Bytes));
        }

        if (r.Metadata.Type.Id == 9)
        {
            var result = new List<byte>();
            bool ended = false;
            for (int at = 0; at <= r.Bytes.Length - 5; at += 5)
            {
                byte flags = r.Bytes[at];
                result.Add((byte)(flags & 0x7f));
                result.Add(0);
                result.AddRange(r.Bytes.AsSpan(at + 1, 4).ToArray());
                if ((flags & 0x80) != 0)
                {
                    ended = true;
                    break;
                }
            }

            if (!ended)
            {
                throw new InvalidDataException("Unterminated Win16 accelerator resource.");
            }

            return ("Win16 packed 5-byte entries to Win32 6-byte ACCEL; native owned accelerator table", result.ToArray());
        }

        return (r.Metadata.Type.Id switch
        {
            2 => "Original DIB to owned CreateDIBitmap object",
            4 => "Original ANSI menu tree to owned Win32 Unicode menu objects",
            1 or 3 => "Original cursor/icon image retained; group loader creates native object",
            12 or 14 => "Original group directory resolves embedded cursor/icon images",
            6 => "Original ANSI length-prefixed strings read through resource contracts",
            _ => "Original payload retained in catalog; no native format consumer required by observed contracts"
        }, []);
    }
}
