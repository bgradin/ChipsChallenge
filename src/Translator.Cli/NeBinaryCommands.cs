using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Translator.Analysis;
using Translator.ChipsChallenge;
using Translator.Assembly;
using Translator.IR;
using Translator.Linker;
using Win16_Runtime;

internal static class NeBinaryCommands
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
    private sealed record MemoryValue(int? SegmentIndex, ushort? Selector, ushort Offset, string BytesHex);
    private sealed record FixtureSegment(ushort Selector, int Size, string? BytesHex);
    private sealed record Fixture(ushort[] Arguments, Dictionary<string, uint>? Registers, FixtureSegment[]? Segments, MemoryValue[]? Writes, Dictionary<string, uint>? ExpectedRegisters, MemoryValue[]? ExpectedMemory);
    public static int Run(string command, string[] args, string input, string? output)
    {
        string? Option(string key)
        {
            int i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length
                ? args[i + 1]
                : null;
        }

        var runtimeOptions = NeInspection.RuntimeOptions(args);
        byte[] bytes = File.ReadAllBytes(input);
        if (args.Contains("--application"))
        {
            if (args.Contains("--indirect"))
            {
                throw new ArgumentException("--indirect selects audited binary function workflows; gameplay application integration is a later milestone.");
            }

            if (args.Contains("--startup") && Option("--startup") is null)
            {
                throw new ArgumentException("--startup requires a mode.");
            }

            string startupMode = Option("--startup") ?? "standalone";
            if (command != "translate" || startupMode is not ("standalone" or "lifecycle-check" or "window-check" or "counter-paint" or "scene-check" or "input-check" or "level-check" or "movement-check" or "clock-check" or "tile-check" or "hazard-check" or "completion-check") || output is null)
            {
                throw new ArgumentException("Application checkpoints require translate --application --startup <standalone|lifecycle-check|window-check|counter-paint|scene-check|input-check|level-check|movement-check|clock-check|tile-check|hazard-check|completion-check> --output <exe>.");
            }

            if (new[]
            {
                "--root",
                "--function",
                "--state",
                "--compare-assembly",
                "--comparison-bindings"
            }.Any(args.Contains))
            {
                throw new ArgumentException("Application checkpoints have fixed audited startup paths; custom roots, fixtures and comparisons are not supported.");
            }

            bool gameplay = args.Contains("--gameplay");
            if (gameplay && startupMode != "standalone")
            {
                throw new ArgumentException("--gameplay uses the standalone application target; do not combine it with a checkpoint --startup.");
            }

            bool completion = gameplay || startupMode == "completion-check";
            bool hazards = completion || startupMode == "hazard-check";
            bool tiles = hazards || startupMode == "tile-check";
            bool clock = tiles || startupMode == "clock-check";
            bool movement = clock || startupMode == "movement-check";
            bool level = movement || startupMode == "level-check";
            if (!level && args.Contains("--level-data"))
            {
                throw new ArgumentException("--level-data requires --startup level-check.");
            }

            bool standalone = startupMode == "standalone";
            bool keyboard = movement || standalone || startupMode == "input-check";
            bool scene = level || keyboard || Option("--startup") == "scene-check";
            bool painting = scene || Option("--startup") == "counter-paint";
            if (painting
                ? Option("--assets") != "external"
                : args.Contains("--assets"))
            {
                throw new ArgumentException("Counter painting and scene composition require --assets external; other checkpoints do not accept an asset mode.");
            }

            ChipsLifecycleProfile.Validate(bytes);
            NeVerifiedSymbols? profileSymbols = Option("--symbols") is { } applicationProfile
                ? NeSymbolProfileReader.Read(applicationProfile, bytes)
                : null;
            var application = NeApplicationProgram.Build(bytes, startupMode is "standalone" or "window-check" or "counter-paint" or "scene-check" or "input-check" or "level-check" or "movement-check" or "clock-check" or "tile-check" or "hazard-check" or "completion-check"
                ? new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: painting, SceneComposition: scene, KeyboardInput: keyboard, StandaloneDistribution: standalone, AuthenticLevel: level, PlayerMovement: movement, GameClock: clock, TileInteractions: tiles, Hazards: hazards, Completion: completion, Gameplay: gameplay)
                : new(ChipsLifecycleProfile.Root, ChipsLifecycleProfile.Arguments, ChipsLifecycleProfile.ExpectedDxAx), profileSymbols, runtimeOptions, level
                ? File.ReadAllBytes(Option("--level-data") ?? (gameplay
                    ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, "CHIPS.DAT")
                    : throw new ArgumentException("--level-data requires original CHIPS.DAT for level-check.")))
                : null);
            File.WriteAllBytes(output, application.Executable);
            File.WriteAllText(output + ".json", JsonSerializer.Serialize(application.Manifest, _jsonOptions));
            if (tiles)
            {
                File.WriteAllText(output + ".tiles.json", JsonSerializer.Serialize(application.Manifest.Tiles, _jsonOptions));
            }

            if (standalone)
            {
                NeApplicationPackage.WriteSidecars(output, application.Manifest);
            }

            if (gameplay)
            {
                Console.WriteLine($"Wrote standalone original gameplay package to {output}; launch instructions: {output}.README.txt.");
            }
            else if (standalone)
            {
                Console.WriteLine($"Wrote standalone original UI package to {output}; launch instructions: {output}.README.txt. Arrow input buffers state; player movement remains unsupported.");
            }
            else
            {
                Console.WriteLine(application.Manifest.Startup.WindowMessages
                    ? $"Wrote native original-message window to {output}; startup/painting limits are in {output}.json."
                    : $"Wrote CLR-free GUI lifecycle checkpoint to {output}. Original BitmapSize runs across bounded thread messages; no game window is created at milestone 5.2. Startup bypasses are listed in {output}.json.");
            }

            return 0;
        }

        if (args.Contains("--gameplay"))
        {
            throw new ArgumentException("--gameplay requires translate --application --assets external.");
        }

        if (Option("--startup") is not null)
        {
            throw new ArgumentException("--startup requires --application.");
        }

        string root = Option("--root") ?? Option("--function") ?? throw new ArgumentException("Binary workflows require --root <name-or-segment:offset>.");
        string? profile = Option("--symbols");
        var symbols = profile is null
            ? null
            : NeSymbolProfileReader.Read(profile, bytes);
        IrModule? reconstruction = null;
        IReadOnlyDictionary<string, int>? comparisonBindings = null;
        if (Option("--compare-assembly") is { } sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new InvalidDataException("--compare-assembly must name an existing reconstruction file.");
            }

            var graph = new NeProgramAnalyzer().Analyze(bytes, root, symbols, resolveIndirectTransfers: args.Contains("--indirect"));
            var parsed = new AssemblyParser().ParseSelected(sourcePath, graph.Functions.Select(f => f.Name).ToArray());
            if (parsed.Diagnostics.Count != 0)
            {
                throw new InvalidDataException(string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Source.File}:{d.Source.Line}: {d.Message}")));
            }

            reconstruction = parsed.Module;
            if (Option("--comparison-bindings") is { } bindingPath)
            {
                if (!File.Exists(bindingPath))
                {
                    throw new InvalidDataException("Missing comparison bindings file.");
                }

                try
                {
                    comparisonBindings = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(bindingPath)) ?? throw new InvalidDataException("Empty comparison bindings.");
                }
                catch (JsonException error)
                {
                    throw new InvalidDataException("Invalid comparison binding JSON: " + error.Message, error);
                }
            }
        }
        else if (Option("--comparison-bindings") is not null)
        {
            throw new ArgumentException("--comparison-bindings requires --compare-assembly.");
        }

        void Json(object value)
        {
            string text = JsonSerializer.Serialize(value, _jsonOptions);
            if (output is null)
            {
                Console.WriteLine(text);
            }
            else
            {
                File.WriteAllText(output, text);
            }
        }

        if (command == "dump-ir")
        {
            var graph = new NeProgramAnalyzer().Analyze(bytes, root, symbols, resolveIndirectTransfers: args.Contains("--indirect"));
            var module = NeNativeTranslation.Normalize(new NeBinaryIrBuilder().Build(bytes, graph));
            if (reconstruction is not null)
            {
                foreach (var function in module.Functions)
                {
                    var other = reconstruction.Functions.SingleOrDefault(f => f.Name == function.Name) ?? throw new InvalidDataException("Missing comparison function: " + function.Name);
                    var comparison = NeAssemblyEquivalence.Compare(function, NeNativeTranslation.Normalize(reconstruction).Functions.Single(f => f.Name == other.Name), comparisonBindings);
                    if (!comparison.Equivalent)
                    {
                        throw new InvalidDataException("Reconstruction/binary mismatch: " + JsonSerializer.Serialize(comparison));
                    }
                }
            }

            Json(module);
            return 0;
        }

        if (command == "translate")
        {
            if (args.Contains("--pe"))
            {
                if (output is null)
                {
                    throw new ArgumentException("Standalone translation requires --output.");
                }

                string path = Option("--state") ?? throw new ArgumentException("Standalone translation requires --state <fixture.json> with explicit expected results.");
                var standalone = NeStandaloneProgram.Build(bytes, root, File.ReadAllText(path), symbols, reconstruction, comparisonBindings, runtimeOptions, resolveIndirectTransfers: args.Contains("--indirect"));
                File.WriteAllBytes(output, standalone.Executable);
                File.WriteAllText(output + ".json", JsonSerializer.Serialize(standalone.Manifest, _jsonOptions));
                Console.WriteLine($"Wrote standalone NE-driven PE32+ with {standalone.Manifest.Translation.Functions.Count} translated functions to {output}; provenance and exit codes are in {output}.json.");
                return 0;
            }

            if (output is null)
            {
                throw new ArgumentException("Binary translation requires --output.");
            }

            using var runtime = NeRuntimeProgram.Load(bytes, new(AllowIncompleteRelocations: true), runtimeOptions);
            var artifact = NeNativeTranslation.Compile(bytes, root, runtime.Image, symbols, reconstruction, comparisonBindings, resolveIndirectTransfers: args.Contains("--indirect"));
            File.WriteAllBytes(output, artifact.LinkedCode.Bytes);
            File.WriteAllText(output + ".json", JsonSerializer.Serialize(artifact, _jsonOptions));
            Console.WriteLine($"Wrote {artifact.Functions.Count} linked binary-derived AMD64 functions to {output}; helper relocations and provenance are in {output}.json.");
            return 0;
        }

        string fixturePath = Option("--state") ?? throw new ArgumentException("Binary native test requires --state <JSON fixture with inputs and expected results>.");
        if (!File.Exists(fixturePath))
        {
            throw new InvalidDataException("--state must name an existing JSON fixture.");
        }

        Fixture fixture;
        try
        {
            fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath)) ?? throw new InvalidDataException("Empty native test fixture.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Invalid native test fixture: " + e.Message, e);
        }

        if (fixture.Arguments is null || fixture.Arguments.Length > 256 || (fixture.ExpectedRegisters?.Count ?? 0) + (fixture.ExpectedMemory?.Length ?? 0) == 0)
        {
            throw new InvalidDataException("Fixture requires Arguments (at most 256 words) and at least one expected register or memory assertion.");
        }

        using var program = NeNativeProgram.Create(bytes, root, symbols, reconstruction, comparisonBindings, runtimeOptions, resolveIndirectTransfers: args.Contains("--indirect"));
        var memory = program.Runtime.Image.Memory;
        var owned = new List<ushort>();
        try
        {
            var extra = fixture.Segments ?? Array.Empty<FixtureSegment>();
            if (extra.Length > 128 || extra.Any(s => s is null) || extra.Sum(s => (long)s.Size) > 8 * 1024 * 1024)
            {
                throw new InvalidDataException("Fixture segment allocation limit exceeded.");
            }

            foreach (var segment in extra)
            {
                if (segment.Selector == 0 || segment.Size is < 1 or > 65536 || memory.TryGetSegment(segment.Selector, out _))
                {
                    throw new InvalidDataException("Invalid or colliding fixture segment.");
                }

                memory.Map(memory.CreateSegment(segment.Selector, segment.Size));
                owned.Add(segment.Selector);
                if (segment.BytesHex is not null)
                {
                    memory.WriteBytes(segment.Selector, 0, Convert.FromHexString(segment.BytesHex));
                }
            }

            ushort Selector(MemoryValue value) => value is null
                ? throw new InvalidDataException("Null memory fixture entry.")
                : value.SegmentIndex is int index && value.Selector is null
                    ? program.Runtime.Image.SelectorFor(index)
                    : value.SegmentIndex is null && value.Selector is ushort selector
                        ? selector
                        : throw new InvalidDataException("Memory fixture requires exactly one of SegmentIndex/Selector.");

            foreach (var write in fixture.Writes ?? Array.Empty<MemoryValue>())
            {
                memory.WriteBytes(Selector(write), write.Offset, Convert.FromHexString(write.BytesHex));
            }

            var state = program.CreateState();
            object boxed = state;
            foreach (var value in fixture.Registers ?? new())
            {
                Register(value.Key).Set(boxed, value.Value);
            }

            state = (MachineState)boxed;
            program.Invoke(ref state, fixture.Arguments);
            var errors = new List<string>();
            foreach (var expected in fixture.ExpectedRegisters ?? new())
            {
                if (Register(expected.Key).Read(state) != expected.Value)
                {
                    errors.Add($"Register {expected.Key}: expected {expected.Value}, actual {Register(expected.Key).Read(state)}");
                }
            }

            foreach (var expected in fixture.ExpectedMemory ?? Array.Empty<MemoryValue>())
            {
                byte[] wanted = Convert.FromHexString(expected.BytesHex);
                byte[] actual = memory.ReadBytes(Selector(expected), expected.Offset, wanted.Length);
                if (!wanted.AsSpan().SequenceEqual(actual))
                {
                    errors.Add($"Memory {Selector(expected):X4}:{expected.Offset:X4}: expected {expected.BytesHex}, actual {Convert.ToHexString(actual)}");
                }
            }

            Json(new
            {
                Passed = errors.Count == 0,
                Errors = errors,
                State = state,
                ExecutableSha256 = program.Artifact.ExecutableSha256,
                Functions = program.Artifact.Functions.Select(f => new { f.Address, f.Name, f.NativeSymbol }),
                program.Artifact.ReconstructionComparisons,
                program.Initialization
            });
            return errors.Count == 0
                ? 0
                : 1;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Binary native test failed: " + e.Message + (e.InnerException is null
                ? ""
                : ": " + e.InnerException.Message), e);
        }
        finally
        {
            foreach (ushort selector in owned)
            {
                var segment = memory.GetSegment(selector);
                memory.Unmap(selector);
                segment.Dispose();
            }
        }
    }

    private static (Action<object, uint> Set, Func<MachineState, uint> Read) Register(string name)
    {
        var field = typeof(MachineState).GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field?.FieldType == typeof(ushort))
        {
            return ((state, value) => field.SetValue(state, checked((ushort)value)), state => (ushort)field.GetValue(state)!);
        }

        var property = typeof(MachineState).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType == typeof(uint) && property.CanWrite)
        {
            return ((state, value) => property.SetValue(state, value), state => (uint)property.GetValue(state)!);
        }

        throw new InvalidDataException($"Unknown MachineState register '{name}'.");
    }
}
