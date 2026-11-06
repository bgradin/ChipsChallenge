using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Translator.Analysis;
using Translator.ChipsChallenge;
using Translator.Binary;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Linker;

/// <summary>Native GUI image/task owner. Window checkpoints retain original procedure bodies and explicit deferred imports.</summary>
public sealed class NeApplicationProgram
{
    public byte[] Executable
    {
        get;
    }
    public byte[] Descriptor
    {
        get;
    }
    public NeApplicationManifest Manifest
    {
        get;
    }
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

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static NeApplicationProgram Build(ReadOnlySpan<byte> bytes, NeApplicationStartup startup, NeVerifiedSymbols? symbols = null, NeRuntimeOptions? runtimeOptions = null, byte[]? levelData = null, bool clockGlitches = false)
    {
        ArgumentNullException.ThrowIfNull(startup);
        if (startup.Gameplay)
        {
            if (startup.Root != "0002:296A" || startup.Arguments is null || startup.Arguments.Length != 5 || startup.Arguments.Any(w => w != 0) || startup.InitialLevel != 1)
            {
                throw new ArgumentException("Gameplay requires the original application root and runtime level selection.");
            }

            var selected = startup with
            {
                Gameplay = false,
                StandaloneDistribution = true,
                WindowMessages = true,
                CounterPainting = true,
                SceneComposition = true,
                KeyboardInput = true,
                AuthenticLevel = true,
                PlayerMovement = true,
                GameClock = true,
                TileInteractions = true,
                Hazards = true,
                Completion = true
            };
            var first = Build(bytes, selected, symbols, runtimeOptions, levelData);
            var second = Build(bytes, selected with
            {
                InitialLevel = 2
            }, symbols, runtimeOptions, levelData);
            return new(first, second);
        }

        if (startup.InitialLevel is not (1 or 2) || startup.InitialLevel == 2 && !startup.Completion)
        {
            throw new ArgumentException("InitialLevel is reserved for the two audited gameplay launch profiles.");
        }

        if (string.IsNullOrWhiteSpace(startup.Root) || startup.Arguments is null || startup.Arguments.Length > 256 || startup.Invocations is < 2 or > 100 || startup.IntervalMilliseconds is < 10 or > 1000)
        {
            throw new ArgumentException("Lifecycle startup requires a root, at most 256 argument words, 2–100 invocations and a 10–1000 ms interval.");
        }

        if (runtimeOptions?.MemoryBackend == NeMemoryBackend.Managed)
        {
            throw new NotSupportedException("A native application requires the Native memory backend.");
        }

        if (startup.Callbacks is { } callbacks && (!startup.WindowMessages || callbacks.MaximumDepth is < 1 or > 8 || callbacks.StackBytes is < 1024 or > 65536 || callbacks.StackBytes % 16 != 0))
        {
            throw new ArgumentException("Callback options require window mode, depth 1–8 and a 16-byte-aligned 1–64 KiB stack.");
        }

        if (startup.SceneComposition)
        {
            startup = startup with
            {
                Callbacks = startup.Callbacks ?? new(4, 4096)
            };
            if (startup.Callbacks.MaximumDepth > 4 || startup.Callbacks.StackBytes > 4096)
            {
                throw new ArgumentException("Scene near-pointer ABI requires at most four 4 KiB stacks in automatic DS.");
            }
        }

        // Snapshot mutable caller input before constructing the image or its manifest.
        startup = startup with
        {
            Arguments = startup.Arguments.ToArray()
        };
        if (startup.CounterPainting && (!startup.WindowMessages || startup.Root != "0002:296A"))
        {
            throw new ArgumentException("Counter painting requires its verified original WNDPROC root.");
        }

        if (startup.SceneComposition && !startup.CounterPainting)
        {
            throw new ArgumentException("Scene composition requires original counter painting.");
        }

        if (startup.StandaloneDistribution && !startup.KeyboardInput)
        {
            throw new ArgumentException("Standalone distribution requires the audited original input scene.");
        }

        if (startup.KeyboardInput && !startup.SceneComposition)
        {
            throw new ArgumentException("Keyboard input requires the original scene.");
        }

        NeLevelState? level = null;
        if (startup.AuthenticLevel)
        {
            if (!startup.SceneComposition || (startup.KeyboardInput && !startup.PlayerMovement) || levelData is null)
            {
                throw new ArgumentException("Authentic level checkpoint requires scene composition and original CHIPS.DAT.");
            }

            level = NeLevelInitialization.Load(bytes.ToArray(), levelData, (startup.Hazards && !startup.Completion || startup.InitialLevel == 2)
                ? (ushort)2
                : (ushort)1);
            if ((startup.Hazards && !startup.Completion || startup.InitialLevel == 2)
                ? level.BoardSha256 != ChipsHazardProfile.BoardHash || level.PlayerX != 21 || level.PlayerY != 12 || level.TimeLimit != 100 || level.Chips != 4 || level.Password != "JXMJ"
                : level.BoardSha256 != ChipsLevelProfile.BoardHash || level.PlayerX != 15 || level.PlayerY != 14 || level.TimeLimit != 100 || level.Chips != 11 || level.Password != "BDHP")
            {
                throw new InvalidDataException("Original level decoder disagrees with independent LESSON 1 oracle.");
            }
        }

        if (startup.PlayerMovement && (!startup.AuthenticLevel || !startup.KeyboardInput))
        {
            throw new ArgumentException("Movement requires authentic level and keyboard input.");
        }

        if (startup.GameClock && !startup.PlayerMovement)
        {
            throw new ArgumentException("Original game clock requires movement and authentic initialization.");
        }

        if (startup.Hazards && !startup.TileInteractions)
        {
            throw new ArgumentException("Hazards require the original tile application.");
        }

        if (startup.TileInteractions && !startup.GameClock)
        {
            throw new ArgumentException("Tile interactions require the original game clock.");
        }

        var input = startup.KeyboardInput
            ? ChipsInputProfile.Inspect()
            : null;
        var scene = startup.SceneComposition
            ? ChipsSceneProfile.Inspect(bytes)
            : null;
        if (level is not null)
        {
            scene = LevelScene(bytes, scene!, level);
        }

        if (startup.PlayerMovement)
        {
            scene = scene! with
            {
                PlayerMovement = true
            };
            byte[] source = bytes.ToArray();
            var referenceScene = scene!;
            input = startup.Completion && startup.InitialLevel == 1
                ? ChipsCompletionProfile.Create(level!.State, NeLevelInitialization.Load(bytes.ToArray(), levelData!, 2).State, (state, inventory) => MovementPixels(source, referenceScene, state, 0, inventory))
                : startup.Hazards
                    ? ChipsHazardProfile.Create(level!.State, (state, inventory) => MovementPixels(source, referenceScene, state, 0, inventory))
                    : startup.TileInteractions
                        ? ChipsTileProfile.Create(level!.State, (state, inventory) => MovementPixels(source, referenceScene, state, 0, inventory))
                        : startup.GameClock
                            ? ChipsClockProfile.Create(level!.State, (state, yellow) => MovementPixels(source, referenceScene, state, yellow), includeGlitches: clockGlitches)
                            : ChipsMovementProfile.Create(level!.State, (state, yellow) => MovementPixels(source, referenceScene, state, yellow));
        }

        if (startup.Completion && startup.InitialLevel == 2)
        {
            input = input! with
            {
                InitialWrites = [.. input!.InitialWrites, new(0x13c6, [0, 0], "Original music-disabled configuration")]
            };
        }

        var graphics = startup.CounterPainting
            ? ChipsCounterGraphics.Inspect(bytes)
            : null;
        var contracts = startup.KeyboardInput
            ? Win16WindowContracts.InputByImportIdentity
            : startup.SceneComposition
                ? Win16WindowContracts.SceneByImportIdentity
                : startup.CounterPainting
                    ? Win16WindowContracts.PaintByImportIdentity
                    : startup.WindowMessages
                        ? Win16WindowContracts.ByImportIdentity
                        : null;
        if (level is not null)
        {
            contracts = contracts!.Concat(BoundedChipsFileFixture.Contracts).Append(new KeyValuePair<string, Win16ApiMetadata>("USER.#10", LevelTimerContract)).ToDictionary(p => p.Key, p => p.Value);
        }

        if (startup.PlayerMovement)
        {
            contracts = contracts!.Concat(MovementContracts).ToDictionary(p => p.Key, p => p.Value);
        }

        if (startup.GameClock)
        {
            contracts = contracts!.Append(new KeyValuePair<string, Win16ApiMetadata>("USER.#12", KillTimerContract)).ToDictionary(p => p.Key, p => p.Value);
        }

        if (startup.TileInteractions)
        {
            contracts = contracts!.Concat(Win16WindowContracts.TileByImportIdentity).ToDictionary(p => p.Key, p => p.Value);
        }

        if (startup.Completion)
        {
            contracts = contracts!.Concat(Win16WindowContracts.CompletionByImportIdentity).ToDictionary(p => p.Key, p => p.Value);
        }

        if (startup.Hazards)
        {
            contracts = contracts!.Concat(Win16WindowContracts.HazardByImportIdentity).ToDictionary(p => p.Key, p => p.Value);
        }

        var nativeBindings = contracts?.ToDictionary(p => p.Key, p => new Win16CodeAddress(0xf000, p.Value.EntryOffset));
        var selectedFunctions = input is null
            ? ChipsSceneProfile.Functions
            : ChipsInputProfile.Functions;
        if (level is not null)
        {
            selectedFunctions = selectedFunctions.Concat(NeLevelInitialization.Functions).Concat(new[] { "4:01FC", "2:16FA" }).Distinct().ToArray();
        }

        if (startup.PlayerMovement)
        {
            selectedFunctions = selectedFunctions.Concat(ChipsMovementProfile.Functions).Distinct().ToArray();
        }

        if (startup.GameClock)
        {
            selectedFunctions = selectedFunctions.Concat(ChipsClockProfile.Functions).Distinct().ToArray();
        }

        if (startup.TileInteractions)
        {
            selectedFunctions = selectedFunctions.Concat(ChipsTileProfile.Functions).Distinct().ToArray();
        }

        if (startup.Completion)
        {
            selectedFunctions = selectedFunctions.Concat(ChipsCompletionProfile.Functions).Distinct().ToArray();
        }

        if (startup.Hazards)
        {
            selectedFunctions = selectedFunctions.Concat(ChipsHazardProfile.Functions).Distinct().ToArray();
        }

        var included = scene is null
            ? null
            : selectedFunctions.Select(NeBinaryAddress.Parse).ToHashSet();
        var rejectedImports = new Dictionary<string, Win16CodeAddress>();
        if (included is not null)
        {
            byte[] sceneBytes = bytes.ToArray();
            var graphs = new[]
            {
                startup.Root
            }.Concat(selectedFunctions).Select(root => new NeProgramAnalyzer().Analyze(sceneBytes, root, symbols, resolveIndirectTransfers: level is not null)).ToArray();
            var missing = graphs.SelectMany(g => g.Functions).Where(f => included.Contains(f.Address)).SelectMany(f => f.Dependencies).Where(d => d.Import is not null && !contracts!.ContainsKey(d.Import.Identity) && NeImportResolver.Classify(d.Import).Api is null).Select(d => d.Import!.Identity).Distinct().Order(StringComparer.Ordinal).ToArray();
            for (int i = 0; i < missing.Length; i++)
            {
                var target = new Win16CodeAddress(0xf000, checked((ushort)(0x8000 + i)));
                rejectedImports.Add(missing[i], target);
                nativeBindings!.Add(missing[i], target);
            }
        }

        using var runtime = NeRuntimeProgram.Load(bytes, new(AllowIncompleteRelocations: true), runtimeOptions, nativeBindings);
        var artifact = NeNativeTranslation.Compile(bytes, startup.Root, runtime.Image, symbols, nativeContracts: contracts, additionalRoots: scene is not null
            ? selectedFunctions
            : graphics is null
                ? null
                : new[] { graphics.Startup, graphics.Shutdown }, includedFunctions: included, rejectedImports: rejectedImports, resolveIndirectTransfers: level is not null, rejectedIndirectSites: startup.PlayerMovement
            ? ChipsMovementProfile.FatalIndirectSites.Select(NeBinaryAddress.Parse).ToHashSet()
            : null);
        return new(artifact, runtime, startup, graphics, scene, input, level);
    }

    // Both launch descriptors bind the same compiled bodies. Only initialization
    // metadata and independent acceptance oracles differ; DAT stays external.
    private NeApplicationProgram(NeApplicationProgram first, NeApplicationProgram second)
    {
        var one = first.Functions.Where(f => f.Name != "application_image").ToArray();
        var two = second.Functions.Where(f => f.Name != "application_image").ToArray();
        if (one.Length != two.Length || !first.Imports.SequenceEqual(second.Imports) || one.Zip(two).Any(pair => pair.First.Name != pair.Second.Name || !pair.First.Bytes.SequenceEqual(pair.Second.Bytes) || !(pair.First.RelativeCallFixups ?? []).SequenceEqual(pair.Second.RelativeCallFixups ?? [])))
        {
            throw new InvalidDataException("Gameplay launch profiles must share identical linked code and procedure bindings.");
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(0x3647504eu);
            writer.Write(1u);
            writer.Write(2u);
            foreach (var profile in new[]
            {
                first,
                second
            }

            )
            {
                writer.Write((uint)profile.Manifest.Startup.InitialLevel);
                writer.Write((uint)profile.Descriptor.Length);
                writer.Write(profile.Descriptor);
            }
        }

        Descriptor = stream.ToArray();
        var entry = first.Entry.Bytes.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), Descriptor.Length);
        Entry = first.Entry with
        {
            Bytes = entry
        };
        Functions = first.Functions.Select(f => f.Name == "application_image"
            ? f with
            {
                Bytes = Descriptor
            }
            : f).ToArray();
        Imports = first.Imports;
        Executable = new Pe32PlusWriter().WriteGuiProgram(Entry, Functions, Imports);
        Manifest = first.Manifest with
        {
            Format = "NE standalone original gameplay PE32+ v1",
            PeSha256 = Hash(Executable),
            DescriptorSha256 = Hash(Descriptor),
            Startup = first.Manifest.Startup with
            {
                Gameplay = true
            },
            Gameplay = new
            {
                DefaultLevel = 1,
                SupportedInitialLevels = new[]
                {
                    1,
                    2
                },
                Profiles = new[]
                {
                    first,
                    second
                }.Select(p => new { Level = p.Manifest.Startup.InitialLevel, DescriptorSha256 = Hash(p.Descriptor), AcceptanceCases = p.Manifest.Input!.Cases.Count, Initialization = p.Manifest.Level }).ToArray(),
                Policy = "One statically linked original engine; native selection chooses an audited initialization descriptor, never decodes levels or implements game rules"
            },
            Distribution = new
            {
                Platform = "Windows 10 or newer, AMD64",
                ClrRequired = false,
                RuntimeSidecarsRequired = false,
                Launch = "<generated.exe> --original <CHIPS.EXE> [--levels <CHIPS.DAT>] [--level 1|2] [--diagnostics <new-log-file>]",
                Assets = "External hash-validated CHIPS.EXE and CHIPS.DAT; default DAT location is adjacent to the supplied executable; neither asset is included",
                Code = "Statically translated original bodies; asset executable is never executed or dynamically loaded",
                Behavior = "Original level initialization, input, ticks, collisions, inventory, bugs, death/restart, completion/scoring and next-level progression within the certified subset",
                Verification = "--lifecycle-test selects independent native acceptance for the requested launch level (99 LESSON 1 stages or 21 LESSON 2 stages)",
                Progress = "Process-local INI compatibility store; saves do not persist between processes"
            },
            DeferredCapabilities = ["Only initial levels 1 and 2 and the documented acceptance paths are certified; later levels, blocks, slipping, other monster AI and last-level animation retain explicit fatal boundaries", "Music is disabled; progress is process-local; completion uses native presentation of original-computed text"]
        };
    }

    private static IReadOnlyDictionary<string, Win16ApiMetadata> MovementContracts => new[]
    {
        new Win16ApiMetadata(420, "USER", "ScrollWindow", 61, Win16ImportKind.Procedure, Win16ApiConvention.Pascal, Win16ArgumentOrder.LeftToRight, Win16ValueType.Bool, Win16Marshalling.StackValues, true, true, 0x318, [new("hwnd", Win16ValueType.Handle, 6), new("dx", Win16ValueType.SignedWord, 5), new("dy", Win16ValueType.SignedWord, 4), new("rect", Win16ValueType.FarPointer, 2), new("clip", Win16ValueType.FarPointer, 0)]),
        new Win16ApiMetadata(421, "USER", "ValidateRect", 127, Win16ImportKind.Procedure, Win16ApiConvention.Pascal, Win16ArgumentOrder.LeftToRight, Win16ValueType.Bool, Win16Marshalling.StackValues, true, true, 0x31c, [new("hwnd", Win16ValueType.Handle, 2), new("rect", Win16ValueType.FarPointer, 0)])
    }.ToDictionary(a => $"USER.#{a.Ordinal}");

    private static IReadOnlyList<ScenePixels> MovementPixels(byte[] original, ChipsScenePlan scene, byte[] state, ushort yellow, IReadOnlyList<ushort>? inventory = null)
    {
        using var catalog = new NeResourceCatalog(original);

        NeDecodedBitmap Bitmap(string name) => NeBitmapDecoder.Decode(catalog.Extract(catalog.Resources.ToList().FindIndex(r => r.Type.Id == 2 && r.Identity.Name == name) + 1).Bytes);

        var atlas = Bitmap("OBJ32_4");
        var info = Bitmap("INFOWND");
        return scene.Pixels.Select(p => p with { Sha256 = Hash(ChipsSceneProfile.Expected(atlas, info, state, p.Window, p.Width, p.Height, p.Left, p.Top, p.Right, p.Bottom, true, yellow, inventory)) }).ToArray();
    }

    private static Win16ApiMetadata KillTimerContract => new(422, "USER", "KillTimer", 12, Win16ImportKind.Procedure, Win16ApiConvention.Pascal, Win16ArgumentOrder.LeftToRight, Win16ValueType.Bool, Win16Marshalling.StackValues, true, true, 0x320, [new("hwnd", Win16ValueType.Handle, 1), new("id", Win16ValueType.Word, 0)]);
    private static Win16ApiMetadata LevelTimerContract => new(410, "USER", "SetTimer", 10, Win16ImportKind.Procedure, Win16ApiConvention.Pascal, Win16ArgumentOrder.LeftToRight, Win16ValueType.Word, Win16Marshalling.StackValues, true, true, 0x314, [new("hwnd", Win16ValueType.Handle, 4), new("id", Win16ValueType.Word, 3), new("milliseconds", Win16ValueType.Word, 2), new("procedure", Win16ValueType.FarPointer, 0)]);

    private static ChipsScenePlan LevelScene(ReadOnlySpan<byte> bytes, ChipsScenePlan scene, NeLevelState level)
    {
        var writes = scene.Writes.Where(w => w.Offset != 0x4000 && !(w.Offset >= 0x1682 && w.Offset <= 0x1690)).ToArray();
        var state = level.State.ToArray();
        BitConverter.GetBytes((ushort)(level.PlayerX - 4)).CopyTo(state, 0xa24);
        BitConverter.GetBytes((ushort)(level.PlayerY - 4)).CopyTo(state, 0xa26);
        BitConverter.GetBytes((ushort)9).CopyTo(state, 0xa28);
        BitConverter.GetBytes((ushort)9).CopyTo(state, 0xa2a);
        using var catalog = new NeResourceCatalog(bytes);

        NeDecodedBitmap Bitmap(string name) => NeBitmapDecoder.Decode(catalog.Extract(catalog.Resources.ToList().FindIndex(r => r.Type.Id == 2 && r.Identity.Name == name) + 1).Bytes);

        var atlas = Bitmap("OBJ32_4");
        var info = Bitmap("INFOWND");
        var pixels = scene.Pixels.Select(p => p with { Sha256 = Hash(ChipsSceneProfile.Expected(atlas, info, state, p.Window, p.Width, p.Height, p.Left, p.Top, p.Right, p.Bottom, true)) }).ToArray();
        var windows = scene.Windows.Select((w, i) => i == 3
            ? w with
            {
                Value = (ushort)BitConverter.ToUInt16(level.State, 0x800)
            }
            : i == 4
                ? w with
                {
                    Value = 100
                }
                : i == 5
                    ? w with
                    {
                        Value = (ushort)level.Chips
                    }
                    : w).ToArray();
        return scene with
        {
            Writes = writes,
            Windows = windows,
            Pixels = pixels,
            AuthenticLevel = true
        };
    }

    private NeApplicationProgram(NeNativeArtifact artifact, NeRuntimeProgram runtime, NeApplicationStartup startup, CounterGraphicsPlan? graphics, ChipsScenePlan? scene, ChipsInputPlan? input, NeLevelState? level)
    {
        // Lifecycle mode requires an import-free helper. Window mode uses the explicit native
        // contract policy above; deferred painting imports remain rejected, never fake successes.
        if (!startup.WindowMessages && artifact.Graph.Dependencies.Any(d => d.Import is not null))
        {
            throw new NotSupportedException("Lifecycle-check startup requires an import-free original helper; application API adapters require a different startup mode.");
        }

        if (artifact.Functions.Any(f => f.Distance != CallDistance.Far))
        {
            throw new NotSupportedException("Lifecycle-check procedure bindings currently require verified far-return functions.");
        }

        var helperBytes = startup.KeyboardInput
            ? NativeInputRuntime.Bytes
            : startup.SceneComposition
                ? NativeSceneRuntime.Bytes
                : startup.CounterPainting
                    ? NativePaintRuntime.Bytes
                    : startup.WindowMessages
                        ? NativeWindowRuntime.Bytes
                        : NativeApplicationRuntime.Bytes;
        var helperExports = startup.KeyboardInput
            ? NativeInputRuntime.Exports
            : startup.SceneComposition
                ? NativeSceneRuntime.Exports
                : startup.CounterPainting
                    ? NativePaintRuntime.Exports
                    : startup.WindowMessages
                        ? NativeWindowRuntime.Exports
                        : NativeApplicationRuntime.Exports;
        var helperFixups = startup.KeyboardInput
            ? NativeInputRuntime.TargetFixups
            : startup.SceneComposition
                ? NativeSceneRuntime.TargetFixups
                : startup.CounterPainting
                    ? NativePaintRuntime.TargetFixups
                    : startup.WindowMessages
                        ? NativeWindowRuntime.TargetFixups
                        : NativeApplicationRuntime.TargetFixups;
        var helperImports = startup.KeyboardInput
            ? NativeInputRuntime.Imports
            : startup.SceneComposition
                ? NativeSceneRuntime.Imports
                : startup.CounterPainting
                    ? NativePaintRuntime.Imports
                    : startup.WindowMessages
                        ? NativeWindowRuntime.Imports
                        : NativeApplicationRuntime.Imports;
        if (startup.WindowMessages && (rootCleanup(artifact) != 10 || startup.Arguments.Length != 5))
        {
            throw new NotSupportedException("Window message startup requires a far Pascal WNDPROC with ten argument bytes.");
        }

        static ushort? rootCleanup(NeNativeArtifact a) => a.Module.Functions.Single(f => f.OriginalSegmentIndex == a.Graph.Root.SegmentIndex && f.OriginalOffset == a.Graph.Root.Offset).CleanupBytes;

        if ((artifact.LinkedCode.Relocations ?? []).Any(r => !helperExports.ContainsKey(r.Symbol)))
        {
            throw new NotSupportedException("A required native application helper is unavailable.");
        }

        var root = artifact.Module.Functions.Single(f => f.OriginalSegmentIndex == artifact.Graph.Root.SegmentIndex && f.OriginalOffset == artifact.Graph.Root.Offset);
        if (root.CleanupBytes is not ushort cleanup || cleanup != 0 && cleanup != startup.Arguments.Length * 2)
        {
            throw new NotSupportedException("Startup requires an unambiguous caller-clean or exact Pascal argument cleanup contract.");
        }

        var initial = runtime.Image.InitialState;
        var registers = runtime.Initialization.InitialRegisters;
        int stackBottom = registers.StackBottom ?? throw new NotSupportedException("Application startup requires a declared original stack.");
        if (initial.DS == 0 || initial.SP < stackBottom + startup.Arguments.Length * 2 + 64)
        {
            throw new NotSupportedException("Application startup requires automatic DS and sufficient declared stack space.");
        }

        const ushort taskSelector = 0xe000;
        if (runtime.Image.Memory.TryGetSegment(taskSelector, out _))
        {
            throw new NotSupportedException("Private task selector collides with the original image.");
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var segments = runtime.Image.Memory.Segments.OrderBy(s => s.Selector).ToArray();
            var limits = runtime.Initialization.RuntimeLimits;
            if (limits.NativeLimits!.MaximumAllocations > 32768 || limits.NativeLimits.MaximumBytes > 1024L * 1024 * 1024 || limits.GlobalLimits!.MaxAllocations > 32768)
            {
                throw new NotSupportedException("Application runtime limits exceed the native descriptor policy.");
            }

            writer.Write(0x3541504eu);
            writer.Write(startup.Completion
                ? 11u
                : startup.Hazards
                    ? 10u
                    : startup.TileInteractions
                        ? 9u
                        : startup.GameClock
                            ? 8u
                            : startup.PlayerMovement
                                ? 7u
                                : startup.AuthenticLevel
                                    ? 6u
                                    : startup.KeyboardInput
                                        ? 5u
                                        : startup.SceneComposition
                                            ? 4u
                                            : startup.CounterPainting
                                                ? 3u
                                                : startup.WindowMessages
                                                    ? 2u
                                                    : 1u);
            writer.Write((uint)MachineState.AbiVersion);
            writer.Write((uint)MachineState.AbiSize);
            writer.Write((uint)segments.Length);
            writer.Write((uint)artifact.Functions.Count);
            writer.Write((uint)artifact.Functions.ToList().FindIndex(f => f.Address == artifact.Graph.Root));
            writer.Write((uint)startup.Arguments.Length);
            writer.Write((uint)(startup.Arguments.Length * 2 - cleanup));
            writer.Write(startup.ExpectedDxAx);
            writer.Write(startup.ExpectedResultStep);
            writer.Write((uint)startup.Invocations);
            writer.Write((uint)startup.IntervalMilliseconds);
            writer.Write((uint)limits.NativeLimits.MaximumAllocations);
            writer.Write((ulong)limits.NativeLimits.MaximumBytes);
            writer.Write((uint)limits.GlobalLimits.MaxAllocations);
            writer.Write((ulong)limits.GlobalLimits.MaxBytes);
            writer.Write((uint)stackBottom);
            writer.Write((uint)initial.SP);
            writer.Write((uint)(registers.HeapOffset ?? 0));
            writer.Write((uint)registers.HeapSize);
            writer.Write(taskSelector);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write(1u); // Explicit empty ANSI Win16 command line; native control args are not application args.
            writer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref initial, 1)));
            writer.Write((byte)0);
            foreach (var segment in segments)
            {
                var mapping = artifact.SegmentMappings.Single(s => s.Selector == segment.Selector);
                writer.Write(segment.Selector);
                writer.Write((ushort)((segment.CanRead
                    ? 1
                    : 0) | (segment.CanWrite
                    ? 2
                    : 0) | (!mapping.IsData
                    ? 4
                    : 0)));
                // Reserve separate fixture storage beyond the original data/stack/heap extent.
                writer.Write((uint)(scene is not null && segment.Selector == initial.DS
                    ? Math.Max(segment.Size, 0x9000)
                    : segment.Size));
                byte[] data = segment.Memory.ToArray();
                int length = data.Length;
                while (length > 0 && data[length - 1] == 0)
                {
                    length--;
                }

                writer.Write((uint)length);
                writer.Write(data, 0, length);
            }

            foreach (var function in artifact.Functions)
            {
                writer.Write(runtime.Image.SelectorFor(function.Address.SegmentIndex));
                writer.Write(function.Address.Offset);
                writer.Write(1u);
            }

            foreach (ushort argument in startup.Arguments)
            {
                writer.Write(argument);
            }

            if (startup.WindowMessages)
            {
                var callbacks = startup.Callbacks ?? new();
                writer.Write((uint)callbacks.MaximumDepth);
                writer.Write((uint)callbacks.StackBytes);
                writer.Write(callbacks.TimerResult.HasValue
                    ? 1u
                    : 0u);
                writer.Write(callbacks.TimerResult ?? 0u);
                if (graphics is not null)
                {
                    writer.Write((uint)graphics.OriginalSize);
                    writer.Write(Convert.FromHexString(graphics.OriginalSha256));
                    writer.Write((uint)graphics.ResourceOffset);
                    writer.Write((uint)graphics.ResourceLength);
                    writer.Write(Convert.FromHexString(graphics.ResourceSha256));

                    uint Procedure(string address)
                    {
                        var a = NeBinaryAddress.Parse(address);
                        return (uint)runtime.Image.SelectorFor(a.SegmentIndex) << 16 | a.Offset;
                    }


                    writer.Write(Procedure(graphics.Startup));
                    writer.Write(Procedure(graphics.Shutdown));
                    writer.Write(ChipsCounterGraphics.MainInstanceOffset);
                    writer.Write(ChipsCounterGraphics.ColorModeOffset);
                    writer.Write(ChipsCounterGraphics.ResourceHandleOffset);
                    writer.Write(ChipsCounterGraphics.DigitDataOffset);
                    writer.Write(ChipsCounterGraphics.DigitArrayOffset);
                    writer.Write((ushort)200);
                    writer.Write((uint)graphics.Cases.Count);
                    foreach (var c in graphics.Cases)
                    {
                        writer.Write(c.Value);
                        writer.Write(c.Flags);
                        writer.Write(c.ColorMode);
                        writer.Write(c.Width);
                        writer.Write(c.Height);
                        writer.Write(c.ClipLeft);
                        writer.Write(c.ClipTop);
                        writer.Write(c.ClipRight);
                        writer.Write(c.ClipBottom);
                        writer.Write(c.Background);
                        writer.Write(Convert.FromHexString(c.BgrxSha256));
                    }

                    if (scene is not null)
                    {
                        writer.Write((uint)scene.Resources.Count);
                        foreach (var resource in scene.Resources)
                        {
                            writer.Write((uint)resource.Offset);
                            writer.Write((uint)resource.Length);
                            writer.Write(Convert.FromHexString(resource.Sha256));
                            writer.Write((ushort)resource.Width);
                            writer.Write((ushort)resource.Height);
                            writer.Write(System.Text.Encoding.ASCII.GetBytes(resource.Name.PadRight(16, '\0')));
                        }

                        writer.Write((uint)scene.Writes.Count);
                        foreach (var write in scene.Writes)
                        {
                            writer.Write(write.Offset);
                            writer.Write(checked((ushort)write.Bytes.Length));
                            writer.Write(write.Bytes);
                        }

                        writer.Write((uint)scene.Windows.Count);
                        foreach (var window in scene.Windows)
                        {
                            writer.Write((short)window.Parent);
                            writer.Write((ushort)0);
                            writer.Write(window.Procedure.Length == 0
                                ? 0u
                                : Procedure(window.Procedure));
                            writer.Write(window.X);
                            writer.Write(window.Y);
                            writer.Write(window.Width);
                            writer.Write(window.Height);
                            writer.Write(window.Style);
                            writer.Write(window.Value);
                            writer.Write(window.Flags);
                        }

                        writer.Write((uint)scene.Pixels.Count);
                        foreach (var pixel in scene.Pixels)
                        {
                            writer.Write((ushort)pixel.Window);
                            writer.Write(pixel.Width);
                            writer.Write(pixel.Height);
                            writer.Write(pixel.Left);
                            writer.Write(pixel.Top);
                            writer.Write(pixel.Right);
                            writer.Write(pixel.Bottom);
                            writer.Write(Convert.FromHexString(pixel.Sha256));
                        }

                        if (level is not null)
                        {
                            writer.Write(108569u);
                            writer.Write(Convert.FromHexString(NeLevelInitialization.DataHash));
                            writer.Write(Convert.FromHexString(level.BoardSha256));
                            writer.Write((ushort)0x4000);
                            writer.Write((ushort)0x800);
                            writer.Write(System.Text.Encoding.ASCII.GetBytes("CHIPS.DAT".PadRight(16, '\0')));
                            var calls = ChipsLevelProfile.Calls;
                            if ((startup.Hazards && !startup.Completion || startup.InitialLevel == 2))
                            {
                                calls = calls.Select(c => c.Procedure == "4:00D8"
                                    ? c with
                                    {
                                        Arguments = [2]
                                    }
                                    : c).ToArray();
                            }

                            writer.Write((uint)calls.Length);
                            foreach (var call in calls)
                            {
                                writer.Write(Procedure(call.Procedure));
                                writer.Write((uint)call.Arguments.Length);
                                foreach (var arg in call.Arguments)
                                {
                                    writer.Write(arg);
                                }
                            }

                            writer.Write(7u);
                            for (ushort i = 0; i < 7; i++)
                            {
                                writer.Write((ushort)(0x10 + i * 2));
                                writer.Write(i);
                            }

                            foreach (var writes in new[]
                            {
                                (startup.Hazards && !startup.Completion || startup.InitialLevel == 2)
                                    ? ChipsHazardProfile.Viewport
                                    : ChipsLevelProfile.Viewport,
                                (startup.Hazards && !startup.Completion || startup.InitialLevel == 2)
                                    ? ChipsHazardProfile.LevelChecks
                                    : ChipsLevelProfile.Checks
                            }

                            )
                            {
                                writer.Write((uint)writes.Length);
                                foreach (var w in writes)
                                {
                                    writer.Write(w.Offset);
                                    writer.Write((ushort)w.Bytes.Length);
                                    writer.Write(w.Bytes);
                                }
                            }
                        }

                        if (input is not null)
                        {
                            void Writes(IReadOnlyList<SceneWrite> writes)
                            {
                                writer.Write((uint)writes.Count);
                                foreach (var w in writes)
                                {
                                    writer.Write(w.Offset);
                                    writer.Write(checked((ushort)w.Bytes.Length));
                                    writer.Write(w.Bytes);
                                }
                            }

                            writer.Write(Procedure(input.Procedure));
                            writer.Write(input.Window);
                            writer.Write(input.FocusWindow);
                            writer.Write((uint)input.WindowBindings.Count);
                            foreach (var b in input.WindowBindings)
                            {
                                writer.Write(b.Offset);
                                writer.Write(b.Window);
                            }

                            writer.Write((uint)input.Keys.Count);
                            foreach (var key in input.Keys)
                            {
                                writer.Write(key);
                            }

                            Writes(input.InitialWrites);
                            writer.Write((uint)input.Cases.Count);
                            foreach (var c in input.Cases)
                            {
                                Writes(c.InitialWrites);

                                void Checks(IReadOnlyList<InputCheck> checks)
                                {
                                    writer.Write((uint)checks.Count);
                                    foreach (var check in checks)
                                    {
                                        writer.Write(check.Offset);
                                        writer.Write((ushort)check.Bytes.Length);
                                        writer.Write(check.Bytes);
                                    }
                                }


                                void FarChecks(IReadOnlyList<InputFarCheck> checks)
                                {
                                    writer.Write((uint)checks.Count);
                                    foreach (var check in checks)
                                    {
                                        writer.Write(check.PointerOffset);
                                        writer.Write((ushort)check.Bytes.Length);
                                        writer.Write(check.Bytes);
                                    }
                                }

                                if (startup.Hazards)
                                {
                                    writer.Write(c.Modal is null
                                        ? 0u
                                        : 1u);
                                    if (c.Modal is { } modal)
                                    {
                                        var text = System.Text.Encoding.ASCII.GetBytes(modal.Text);
                                        writer.Write((uint)text.Length);
                                        writer.Write(text);
                                        Checks(modal.Checks);
                                        FarChecks(modal.FarChecks);
                                        writer.Write((uint)modal.Pixels.Count);
                                        foreach (var pixel in modal.Pixels)
                                        {
                                            writer.Write((ushort)pixel.Window);
                                            writer.Write(pixel.Width);
                                            writer.Write(pixel.Height);
                                            writer.Write(pixel.Left);
                                            writer.Write(pixel.Top);
                                            writer.Write(pixel.Right);
                                            writer.Write(pixel.Bottom);
                                            writer.Write(Convert.FromHexString(pixel.Sha256));
                                        }
                                    }
                                }

                                if (startup.Completion)
                                {
                                    writer.Write((uint)(c.Completion?.Count ?? 0));
                                    if (c.Completion is { } messages)
                                    {
                                        foreach (var message in messages)
                                        {
                                            var text = System.Text.Encoding.ASCII.GetBytes(message);
                                            writer.Write((uint)text.Length);
                                            writer.Write(text);
                                        }
                                    }
                                }

                                writer.Write((uint)c.Events.Count);
                                foreach (var e in c.Events)
                                {
                                    if (startup.GameClock)
                                    {
                                        writer.Write((ushort)e.Operation);
                                    }

                                    if (startup.PlayerMovement)
                                    {
                                        writer.Write(e.Window);
                                    }

                                    writer.Write(e.Message);
                                    writer.Write(e.Key);
                                    if (startup.GameClock && e.Operation == InputDriverOperation.CallOriginal)
                                    {
                                        writer.Write(Procedure($"{e.Lparam >> 16:X4}:{e.Lparam & 0xffff:X4}"));
                                    }
                                    else
                                    {
                                        writer.Write(e.Lparam);
                                    }
                                }

                                writer.Write((uint)c.Checks.Count);
                                foreach (var check in c.Checks)
                                {
                                    writer.Write(check.Offset);
                                    writer.Write(checked((ushort)check.Bytes.Length));
                                    writer.Write(check.Bytes);
                                }

                                if (startup.Hazards)
                                {
                                    FarChecks(c.FarChecks);
                                }

                                if (startup.TileInteractions)
                                {
                                    writer.Write((uint)c.WindowChecks.Count);
                                    foreach (var check in c.WindowChecks)
                                    {
                                        writer.Write(check.Window);
                                        writer.Write(check.Index);
                                        writer.Write(check.Value);
                                    }
                                }

                                if (startup.PlayerMovement)
                                {
                                    writer.Write((uint)c.Pixels.Count);
                                    foreach (var pixel in c.Pixels)
                                    {
                                        writer.Write((ushort)pixel.Window);
                                        writer.Write(pixel.Width);
                                        writer.Write(pixel.Height);
                                        writer.Write(pixel.Left);
                                        writer.Write(pixel.Top);
                                        writer.Write(pixel.Right);
                                        writer.Write(pixel.Bottom);
                                        writer.Write(Convert.FromHexString(pixel.Sha256));
                                    }
                                }
                            }

                            if (startup.GameClock)
                            {
                                var snapshot = startup.Completion
                                    ? ChipsCompletionProfile.Snapshot
                                    : startup.Hazards
                                        ? ChipsHazardProfile.Snapshot
                                        : startup.TileInteractions
                                            ? ChipsTileProfile.Snapshot
                                            : ChipsClockProfile.Snapshot;
                                writer.Write(1u);
                                writer.Write((uint)snapshot.Length);
                                foreach (var field in snapshot)
                                {
                                    writer.Write(field.Offset);
                                    writer.Write((ushort)2);
                                    writer.Write(System.Text.Encoding.ASCII.GetBytes(field.Name.PadRight(16, '\0')));
                                }

                                if (startup.TileInteractions)
                                {
                                    writer.Write(Procedure("2:2BBE"));
                                    writer.Write(Convert.FromHexString(ChipsTileProfile.HintTextHash));
                                    writer.Write(System.Text.Encoding.ASCII.GetBytes(ChipsTileProfile.HintText.PadRight(128, '\0')));
                                }

                                if (startup.Hazards)
                                {
                                    writer.Write(Procedure("4:0240"));
                                    writer.Write((ushort)0x71);
                                    writer.Write((ushort)0x26);
                                    if (startup.Completion)
                                    {
                                        writer.Write(Procedure("6:03C6"));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        Descriptor = stream.ToArray();
        byte[] entry = [0x48, 0x8d, 0x0d, 0, 0, 0, 0, 0x48, 0x8d, 0x15, 0, 0, 0, 0, 0x41, 0xb8, 0, 0, 0, 0, 0x41, 0xb9, 0, 0, 0, 0, 0xe9, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), Descriptor.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(22), artifact.Functions.Count);
        Entry = new("application_entry", entry, [new(3, "application_image", PeRelativeReferenceKind.RipRelativeAddress), new(10, "application_procedures", PeRelativeReferenceKind.RipRelativeAddress), new(27, "ne_application_entry")]);
        var helper = new MachineCodeArtifact(helperBytes, "AMD64", X64Abi.Windows, "Persistent native application lifecycle; MachineState ABI v2/44", helperFixups.Select(f => new MachineCodeRelocation(f.Value, f.Key, MachineCodeRelocationKind.Relative32Call)).ToArray());
        Functions = [PeFunctionBody.FromArtifact("application_runtime", helper)with
        {
            Exports = helperExports
        }, PeFunctionBody.FromArtifact("application_bodies", artifact.LinkedCode)with
        {
            Exports = artifact.EntryPoints
        }, new("application_image", Descriptor), new("application_procedures", new byte[artifact.Functions.Count * 4], artifact.Functions.Select((f, i) => new PeRelativeCallFixup(i * 4, f.NativeSymbol, PeRelativeReferenceKind.DataRelative32)).ToArray())];
        Imports = helperImports.Select(i => new PeNativeImport(i.Module, i.Name, i.Symbol)).ToArray();
        Executable = new Pe32PlusWriter().WriteGuiProgram(Entry, Functions, Imports);
        Manifest = new("NE application lifecycle PE32+ v1", artifact.ExecutableSha256, Hash(Executable), Hash(Descriptor), Hash(helper.Bytes), "Windows GUI (2); CLR-free", startup, artifact, runtime.Initialization, new[] { new PeNativeImport("KERNEL32.dll", "ExitProcess", "__launcher_exit") }.Concat(Imports).ToArray(), new
        {
            Instance = 1,
            PreviousInstance = 0,
            ShowState = "STARTUPINFOW.wShowWindow when STARTF_USESHOWWINDOW is set; otherwise SW_SHOWNORMAL (1)",
            TaskSelector = taskSelector,
            CommandLineOffset = 16,
            CommandLine = "",
            InstancePolicy = "Private word identity, distinct from native HMODULE and selectors",
            ThreadPolicy = "Owning UI thread only",
            Lifetime = "One image/state/heap/procedure table retained through startup, timer messages, WM_QUIT and shutdown",
            LocalHeap = "Original declared region reserved, no Win16 local-heap allocator metadata or APIs initialized"
        }, ["Full CRT/WinMain/CreateClasses/CreateWindowsAndInitGame and WEP4UTIL validation bypassed explicitly", "Original resource initialization (LoadDigits), MainInstance game-global fixture and graphics require the painting startup mode", "Native top-level window and Win16 message conversion require the window startup mode", "Persistent WNDPROC callbacks/reentrancy are outside this lifecycle fixture", "Original settings, audio, levels, menus, game-state and input are not initialized", "External asset path/hash validation at runtime is outside this lifecycle fixture; no original resources embedded"], new Dictionary<int, string> { [0] = "All original helper invocations and lifecycle invariants verified; timer, procedures, image/task mappings and native heap released", [10] = "Invalid descriptor, task/stack/image metadata or ABI", [20] = "Native runtime initialization/allocation failed", [30] = "Translated code/runtime failure", [40] = "Original result or stack/state invariant failed", [50] = "Cleanup/ownership failure", [60] = "Message-loop/thread/lifetime failure or premature WM_QUIT", [70] = "Unsupported runtime arguments" });
        if (startup.WindowMessages)
        {
            Manifest = Manifest with
            {
                Format = "NE window/message PE32+ v1",
                WindowCompatibility = new
                {
                    ClassName = startup.SceneComposition
                        ? "Win16OriginalScene"
                        : "Win16TranslatedCounter",
                    Title = startup.SceneComposition
                        ? "Chip's Challenge - original scene"
                        : "Chip's Challenge - original message dispatch",
                    Procedure = artifact.Graph.Root,
                    Contracts = startup.KeyboardInput
                        ? Win16WindowContracts.InputByImportIdentity.Values.ToArray()
                        : startup.SceneComposition
                            ? Win16WindowContracts.SceneByImportIdentity.Values.ToArray()
                            : startup.CounterPainting
                                ? Win16WindowContracts.Paint
                                : Win16WindowContracts.All,
                    ExecutionPolicy = startup.KeyboardInput
                        ? "Original MAINWNDPROC -> MoveChip buffered-input branch; declared arrow down/up messages, native focus and DC leases; original scene painting; no movement or timer claim"
                        : startup.SceneComposition
                            ? "Complete original board/info/counter/inventory bodies; explicit fatal boundaries for deferred original routines and imports; native main container only"
                            : startup.CounterPainting
                                ? "Original LoadDigits/counter/DrawDigit/FreeDigits execute; external graphics verified with independent pixel hashes"
                                : "Complete original bodies; deferred painting imports trap. Conservative graph remains incomplete. No painting or gameplay claim.",
                    CallbackPolicy = "Owning UI thread; bounded nested entry with a private stack, automatic DS and context per depth",
                    CallbackSupport = new
                    {
                        MaximumDepth = (startup.Callbacks ?? new()).MaximumDepth,
                        StackBytes = (startup.Callbacks ?? new()).StackBytes,
                        TimerResult = startup.Callbacks?.TimerResult,
                        StackPolicy = startup.SceneComposition
                            ? "Private 4 KiB stack slice per depth in automatic DS; SS=DS preserves original near-pointer ABI; canaries and bounded pushes"
                            : "Preallocated selector per depth, canaries and SP/SS checks",
                        CpuPolicy = "Restore all 44 MachineState bytes; retain original data/window-word mutations",
                        LifetimePolicy = "Validate live class binding, owning thread, far code target and generation before entry; detach before freeing"
                    },
                    AllowedMessages = startup.SceneComposition
                        ? "WM_CREATE/DESTROY/CLOSE/SIZE/KEYDOWN/KEYUP/CHAR/COMMAND/TIMER/PAINT; WM_PAINT executes each component's original board/info/counter/inventory procedure; WM_ERASEBKGND handled by host default procedure"
                        : startup.CounterPainting
                            ? "WM_CREATE/DESTROY/CLOSE/SIZE/KEYDOWN/KEYUP/CHAR/COMMAND/TIMER/PAINT; WM_PAINT executes original COUNTERWNDPROC; WM_ERASEBKGND handled by host default procedure"
                            : "WM_CREATE/DESTROY/CLOSE/SIZE/KEYDOWN/KEYUP/CHAR/COMMAND/TIMER; WM_PAINT/ERASEBKGND handled by host default procedure",
                    AutomationOracle = "Every original default-path invocation must call DefWindowProc with the converted arguments and return its LONG unchanged",
                    HandlePolicy = "Word IDs are separate from HWND/HMODULE/native callback pointers; no recycling during owner lifetime"
                },
                DeferredCapabilities = ["Full original startup, menus, sound, settings and game initialization bypassed", "Original painting/LoadDigits/GDI resources require the painting startup mode; denied imports fail deterministically", "Board composition, original game input effects and external assets are outside this window fixture"]
            };
        }

        if (graphics is not null)
        {
            Manifest = Manifest with
            {
                Format = "NE counter painting PE32+ v1",
                Graphics = graphics,
                ExitCodes = new Dictionary<int, string>(Manifest.ExitCodes)
                {
                    [80] = "Original executable path missing or file unavailable",
                    [81] = "Original executable size, read or whole-file identity mismatch",
                    [82] = "Original resource bounds, identity or bitmap layout mismatch",
                    [83] = "Cannot create or write a new diagnostics file; existing files are never overwritten"
                },
                DeferredCapabilities = ["Full startup, board/inventory composition, gameplay and sound remain deferred", "Resource/GDI services are the observed counter subset, not general Win16 graphics"]
            };
        }

        if (scene is not null)
        {
            Manifest = Manifest with
            {
                Format = "NE original scene PE32+ v1",
                Scene = scene,
                DeferredCapabilities = ["Controlled fixture bypasses full original main-window startup, level loading, menus, sound and gameplay", "Original timer, pause, placard and end-game paths terminate at explicit fatal boundaries", "Keyboard interaction requires the input startup mode"]
            };
        }

        if (input is not null)
        {
            Manifest = Manifest with
            {
                Format = "NE original input PE32+ v1",
                Input = input,
                DeferredCapabilities = ["Original main-window callback is installed only for declared arrow key down/up messages; other lifecycle remains native bootstrap", "Original MoveChip buffered-input branch only; no movement, timers, level loading or gameplay", "Full original initialization, menus, audio and general Win16 services remain unsupported"]
            };
        }

        if (level is not null)
        {
            Manifest = Manifest with
            {
                Format = "NE authentic level initialization PE32+ v1",
                Level = new
                {
                    Number = (startup.Hazards && !startup.Completion || startup.InitialLevel == 2)
                        ? 2
                        : 1,
                    level.Title,
                    level.PlayerX,
                    level.PlayerY,
                    level.TimeLimit,
                    level.Chips,
                    level.BoardSha256,
                    DataSha256 = NeLevelInitialization.DataHash,
                    DataSize = 108569,
                    Policy = "Runtime original ReadLevelDataOrDie/ReadLevelData/RLE/DecodeLevelFields/InitBoard/ResetInventory/ResetTimerAndChipCount; pinned external CHIPS.DAT; original timer installed but Var22 pauses gameplay. Host owns GameState reservation and UI viewport only."
                },
                DeferredCapabilities = ["CRT/WinMain/full load-and-start/UI/settings/music bypassed explicitly", "Viewport UI bootstrap (11,10,9,9) remains authored", "Only original LESSON 1 accepted; gameplay ticks/input/movement remain deferred", "GameState DS:4000-4A41, loader stack DS:7000-7FFF used before callbacks; no original local-heap allocator claim"]
            };
        }

        if (startup.PlayerMovement)
        {
            Manifest = Manifest with
            {
                Format = "NE original player movement PE32+ v1",
                Input = input,
                Level = new
                {
                    Number = (startup.Hazards && !startup.Completion || startup.InitialLevel == 2)
                        ? 2
                        : 1,
                    level!.Title,
                    level.PlayerX,
                    level.PlayerY,
                    level.TimeLimit,
                    level.Chips,
                    level.BoardSha256,
                    DataSha256 = NeLevelInitialization.DataHash,
                    Policy = "Original MAINWNDPROC/MoveChip/BOARDWNDPROC/DoTick/ChipCanEnterTile/UpdateChip/ScrollViewport; original sound-disabled configuration; exact queued timer messages in automation and native timer in preview"
                },
                DeferredCapabilities = ["Complete tile/item/hazard/monster/death/completion closures remain unsupported and fatal", "Hint and collectible paths are outside this checkpoint; floor route left/up/down/right is verified", "Timer determinism and lifecycle coverage require the clock startup mode"],
                WindowCompatibility = new
                {
                    ClassName = "Win16OriginalScene",
                    Title = "Chip's Challenge - original level",
                    Contracts = contractsForManifest(),
                    Policy = "Owning UI thread; keyboard handlers never author player positions; seeded fixtures run in automation only"
                }
            };
        }

        if (startup.GameClock)
        {
            Manifest = Manifest with
            {
                Format = "NE original game clock PE32+ v1",
                Clock = new
                {
                    BoardTimerMilliseconds = 110,
                    SecondaryTimerMilliseconds = 220,
                    CountdownTickDivisor = 10,
                    Policy = "Original BOARDWNDPROC/DoTick own game timing. Test-only exact ticks and elapsed milliseconds use registered timer periods. Pause/resume use original routines; nested timer delivery is coalesced and queued after active callbacks. Native Windows timers may coalesce naturally.",
                    Snapshot = (startup.Completion
                        ? ChipsCompletionProfile.Snapshot
                        : startup.Hazards
                            ? ChipsHazardProfile.Snapshot
                            : startup.TileInteractions
                                ? ChipsTileProfile.Snapshot
                                : ChipsClockProfile.Snapshot).Select(f => new { f.Offset, f.Name })
                },
                DeferredCapabilities = ["Monster/sliding/hazard/death/completion dependencies remain explicit fatal boundaries", "Only the supported original level and basic movement/idle/countdown paths are certified"]
            };
        }

        if (startup.TileInteractions)
        {
            Manifest = Manifest with
            {
                Format = "NE original tile interactions PE32+ v1",
                Tiles = ChipsTileProfile.Matrix(runtime.Image.Memory.ReadBytes(initial.DS, 0x66c, 112 * 6), runtime.Image.Memory.ReadBytes(runtime.Image.SelectorFor(3), artifact.Module.Functions.Single(f => f.OriginalSegmentIndex == 3 && f.OriginalOffset == 0x1a56).Instructions.Single(i => i.Operation == "DISPATCH").IndirectTransfer!.TableOffset!.Value, 48 * 2), input!.Cases, startup.Hazards, startup.Completion),
                DeferredCapabilities = ["Exit eligibility is supported; EndLevel completion dialog/progression requires the completion startup mode", "Nonempty monster lists, unprotected hazards, sliding, blocks and button machinery remain uncertified/fatal; see tile matrix"]
            };
        }

        if (startup.Hazards)
        {
            Manifest = Manifest with
            {
                Format = "NE original monster death restart PE32+ v1",
                Hazards = new
                {
                    AcceptanceLevel = 2,
                    BoardHash = ChipsHazardProfile.BoardHash,
                    Policy = "Original bugs, water/fire/monster/timeout death and LoadAndStartLevel restart; no host AI/death/reset rules",
                    EndGameAudit = "0007:0A74 is the last-level ending animation, not the observed death path; remains fatal outside the certified LESSON 1/2 subset"
                },
                DeferredCapabilities = ["Completion/progression, pushing blocks, sliding, other monster AI and levels remain uncertified/fatal"]
            };
        }

        if (startup.Completion)
        {
            Manifest = Manifest with
            {
                Format = "NE original completion progression PE32+ v1",
                Completion = new
                {
                    InitialLevel = 1,
                    NextLevel = 2,
                    Policy = "Original EndLevel and COMPLETEMSGPROC own exit, score and progression; host notification only displays original computed text",
                    ProgressStorage = "Process-local bounded INI; no persistent save-file writes",
                    AcceptanceScore = 1310
                },
                DeferredCapabilities = ["Last-level ending animation, blocks, sliding and other monster AI remain uncertified/fatal"]
            };
        }

        IReadOnlyList<Win16ApiMetadata> contractsForManifest() => Win16WindowContracts.InputByImportIdentity.Values.Concat(BoundedChipsFileFixture.Contracts.Values).Append(LevelTimerContract).Concat(startup.GameClock
            ? new[] { KillTimerContract }
            : Array.Empty<Win16ApiMetadata>()).Concat(MovementContracts.Values).Concat(startup.TileInteractions
            ? Win16WindowContracts.TileByImportIdentity.Values
            : Array.Empty<Win16ApiMetadata>()).Concat(startup.Hazards
            ? Win16WindowContracts.HazardByImportIdentity.Values
            : Array.Empty<Win16ApiMetadata>()).Concat(startup.Completion
            ? Win16WindowContracts.CompletionByImportIdentity.Values
            : Array.Empty<Win16ApiMetadata>()).OrderBy(a => a.Module, StringComparer.Ordinal).ThenBy(a => a.Ordinal).ToArray();

        if (startup.StandaloneDistribution)
        {
            Manifest = Manifest with
            {
                Format = "NE standalone original UI PE32+ v1",
                Distribution = new
                {
                    Platform = "Windows 10 or newer, AMD64",
                    ClrRequired = false,
                    RuntimeSidecarsRequired = false,
                    Launch = "<generated.exe> --original <path-to-CHIPS.EXE> [--diagnostics <new-log-file>]",
                    Verification = "Add --lifecycle-test for bounded native input/graphics acceptance and no modal error dialog",
                    Assets = "External original executable required only for validated bitmap resources; no original resource pixels included in package",
                    Code = "Statically linked translated original bodies execute from this PE; supplied asset file is never executed or dynamically loaded as code",
                    Diagnostics = "Normal failures show a specific native dialog and debugger message. Optional new-file-only ASCII report records stage, exit, runtime and cleanup codes after teardown",
                    Behavior = "Original scene plus original buffered arrow input; no player movement, game timer, level loading, menus or sound",
                    Completion = "Application distribution checkpoint; acceptance covers the distribution checkpoint only"
                }
            };
        }
    }
}
