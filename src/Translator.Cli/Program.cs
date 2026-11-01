using System.Text.Json;
using Translator.Binary;
using Translator.Analysis;
using System.Text.Json.Serialization;
using Translator.Assembly;
using Translator.ChipsChallenge;
using Translator.CodeGen;
using Translator.Linker;
using Translator.Transform;
using Translator.IR;
using Win16_Runtime;

if (args.Length < 2 || args[0] is "--help" or "-h")
{
    Console.WriteLine("ChipShift64 <inspect|extract-resource|analyze|dump-ir|translate|test|validate> --input <assembly-or-ne-or-pe> [--output <file>] [--pe] [--application [--gameplay] [--startup <standalone|lifecycle-check|window-check|counter-paint|scene-check|input-check|level-check|movement-check|clock-check|tile-check|hazard-check|completion-check>] [--assets external] [--level-data <CHIPS.DAT>]] [--memory-backend <Native|Managed>] [--initialization] [--state <native-test.json>] [--compare-assembly <file> --comparison-bindings <json>] [--root <name-or-segment:offset> --symbols <profile>] [--whole-program [--instruction-coverage | --control-flow | --memory-audit | --import-contracts | --startup-trace]] [--ne-inventory] [--indirect] [--resources --resource-index <n>] [--imports] [--load [--allow-incomplete] [--import-bindings <json>]] [--function <name> | --acceptance [--graphics-input <UI assembly> [--callbacks --callback-fixtures <assembly>]] | --callbacks [--callback-input <UI assembly>]]");
    return args.Length < 2
        ? 2
        : 0;
}

string command = args[0];
string? input = Option(args, "--input");
string? output = Option(args, "--output");
if (command == "validate")
{
    if (input is null || !File.Exists(input))
    {
        Console.Error.WriteLine("Error: --input must name an existing executable.");
        return 2;
    }

    try
    {
        bool valid = Pe32PlusWriter.IsAmd64Pe32Plus(File.ReadAllBytes(input));
        Console.WriteLine(valid
            ? "Valid AMD64 PE32+ image."
            : "Invalid or unsupported executable format.");
        return valid
            ? 0
            : 1;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Error: " + error.Message);
        return 1;
    }
}

if (input is null || !File.Exists(input))
{
    Console.Error.WriteLine("Error: --input must name an existing input file.");
    return 2;
}

try
{
    if (args.Contains("--gameplay") && (command != "translate" || !args.Contains("--application")))
    {
        throw new ArgumentException("--gameplay requires translate --application with the original CHIPS.EXE.");
    }

    if (args.Contains("--startup-trace") && (command != "analyze" || !args.Contains("--whole-program") || args.Any(a => a is "--symbols" or "--instruction-coverage" or "--control-flow" or "--memory-audit" or "--import-contracts")))
    {
        throw new ArgumentException("--startup-trace requires analyze --whole-program without symbol profiles or other report modes.");
    }

    if (args.Contains("--import-contracts") && (command != "analyze" || !args.Contains("--whole-program") || args.Contains("--control-flow") || args.Contains("--memory-audit") || args.Contains("--instruction-coverage")))
    {
        throw new ArgumentException("--import-contracts requires analyze --whole-program without other report modes.");
    }

    if (args.Contains("--memory-audit") && (command != "analyze" || !args.Contains("--whole-program") || args.Contains("--control-flow") || args.Contains("--instruction-coverage")))
    {
        throw new ArgumentException("--memory-audit requires analyze --whole-program without other report modes.");
    }

    if (args.Contains("--control-flow") && (command != "analyze" || !args.Contains("--whole-program") || args.Contains("--instruction-coverage")))
    {
        throw new ArgumentException("--control-flow requires analyze --whole-program without --instruction-coverage.");
    }

    if (args.Contains("--instruction-coverage") && (command != "analyze" || !args.Contains("--whole-program")))
    {
        throw new ArgumentException("--instruction-coverage requires analyze --whole-program.");
    }

    if (args.Contains("--whole-program") && (command != "analyze" || args.Contains("--root")))
    {
        throw new ArgumentException("--whole-program requires analyze without --root.");
    }

    if (command == "extract-resource")
    {
        return NeResourceCommands.Extract(args, input, output);
    }

    if (command is "dump-ir" or "translate" or "test")
    {
        using var stream = File.OpenRead(input);
        if (stream.ReadByte() == 'M' && stream.ReadByte() == 'Z')
        {
            return NeBinaryCommands.Run(command, args, input, output);
        }
    }

    if (args.Contains("--gameplay"))
    {
        throw new ArgumentException("--gameplay requires the original NE CHIPS.EXE, not an assembly input.");
    }

    if (command == "inspect" && args.Contains("--ne-inventory"))
    {
        var inventory = NeExecutableInventoryBuilder.Build(File.ReadAllBytes(input));
        string json = JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output ?? "NeExecutableInventory.json", json + "\n");
        return 0;
    }

    if (command == "analyze")
    {
        string? root = Option(args, "--root");
        byte[] bytes = File.ReadAllBytes(input);
        string? profile = Option(args, "--symbols");
        var symbols = profile is null
            ? null
            : NeSymbolProfileReader.Read(profile, bytes);
        if (args.Contains("--whole-program"))
        {
            if (args.Contains("--startup-trace"))
            {
                SupportedChipsSource.Validate(bytes);
                if (output is not null && Path.GetFullPath(output).Equals(Path.GetFullPath(input), OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
                {
                    throw new ArgumentException("Startup trace must not overwrite its original executable.");
                }

                var trace = NeStartupTraceBuilder.Run(bytes);
                string traceJson = JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true });
                if (output is null)
                {
                    Console.WriteLine(traceJson);
                }
                else
                {
                    File.WriteAllText(output, traceJson + "\n");
                }

                return 0; // A diagnostic checkpoint succeeded; application startup remains incomplete.
            }

            if (args.Contains("--import-contracts"))
            {
                string? utility = Option(args, "--utility-input");
                var audit = NeImportContractAuditBuilder.Build(bytes, utility is null
                    ? null
                    : File.ReadAllBytes(utility));
                string importJson = JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
                if (output is null)
                {
                    Console.WriteLine(importJson);
                }
                else
                {
                    if (Path.GetFullPath(output).Equals(Path.GetFullPath(input), OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Import audit must not overwrite its authoritative executable.");
                    }

                    File.WriteAllText(output, importJson + "\n");
                }

                return audit.UnknownIdentities == 0
                    ? 0
                    : 1;
            }

            if (args.Contains("--control-flow"))
            {
                var control = NeWholeProgramControlCompiler.Compile(bytes, symbols);
                string controlJson = JsonSerializer.Serialize(control.Report, new JsonSerializerOptions { WriteIndented = true });
                if (output is null)
                {
                    Console.WriteLine(controlJson);
                }
                else
                {
                    var comparison = OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal;
                    if (Path.GetFullPath(output).Equals(Path.GetFullPath(input), comparison) || Path.GetFullPath(output + ".amd64.bin").Equals(Path.GetFullPath(input), comparison))
                    {
                        throw new ArgumentException("Control-flow output must not overwrite the authoritative input executable.");
                    }

                    File.WriteAllText(output, controlJson + "\n");
                    File.WriteAllBytes(output + ".amd64.bin", control.Code.Bytes);
                }

                return control.Report.Summary.AllStaticTargetsResolved
                    ? 0
                    : 1;
            }

            var discovery = NeWholeProgramDiscoveryBuilder.Build(bytes, symbols);
            if (args.Contains("--memory-audit"))
            {
                var audit = NeMemoryAuditBuilder.Build(bytes, discovery);
                string auditJson = JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true });
                if (output is null)
                {
                    Console.WriteLine(auditJson);
                }
                else
                {
                    if (Path.GetFullPath(output).Equals(Path.GetFullPath(input), OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Memory audit must not overwrite the authoritative executable.");
                    }

                    File.WriteAllText(output, auditJson + "\n");
                }

                return audit.StaticResolutionRequired
                    ? 1
                    : 0;
            }

            var coverage = args.Contains("--instruction-coverage")
                ? NeInstructionCoverageBuilder.Build(bytes, discovery)
                : null;
            string discoveryJson = JsonSerializer.Serialize((object?)coverage ?? discovery, new JsonSerializerOptions { WriteIndented = true });
            if (output is null)
            {
                Console.WriteLine(discoveryJson);
            }
            else
            {
                File.WriteAllText(output, discoveryJson + "\n");
            }

            return discovery.Summary.WorklistExhausted && (coverage is null || coverage.AllAuditedInstructionsLowered)
                ? 0
                : 1;
        }

        var graph = new NeProgramAnalyzer().Analyze(bytes, root ?? throw new ArgumentException("analyze requires --root <name-or-segment:offset>."), symbols, resolveIndirectTransfers: args.Contains("--indirect"));
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        string json = JsonSerializer.Serialize(graph, jsonOptions);
        if (output is null)
        {
            Console.WriteLine(json);
        }
        else
        {
            File.WriteAllText(output, json);
        }

        return graph.ClosureComplete
            ? 0
            : 1;
    }

    if (command == "inspect")
    {
        byte[] bytes = File.ReadAllBytes(input);
        if (bytes.Length >= 2 && bytes[0] == 'M' && bytes[1] == 'Z' || Path.GetExtension(input).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            object report;
            bool load = args.Contains("--load");
            bool explicitBindings = args.Contains("--import-bindings");
            if (explicitBindings && (args.Contains("--initialization") || args.Contains("--memory-backend")))
            {
                throw new ArgumentException("--initialization and --memory-backend require automatic runtime import resolution; omit --import-bindings.");
            }

            var loadOptions = new NeLoadOptions(AllowIncompleteRelocations: args.Contains("--allow-incomplete"));
            using var runtime = load && !explicitBindings
                ? NeRuntimeProgram.Load(bytes, loadOptions, NeInspection.RuntimeOptions(args))
                : null;
            using var loaded = load && explicitBindings
                ? new NeProgramLoader().Load(bytes, loadOptions, NeInspection.ReadImportBindings(Option(args, "--import-bindings")))
                : null;
            if ((args.Contains("--resources") || args.Contains("--resource-index")) && load)
            {
                throw new ArgumentException("Inspect resources and loaded runtime initialization in separate commands.");
            }

            if (args.Contains("--initialization") && !load)
            {
                throw new ArgumentException("--initialization requires --load.");
            }

            if (runtime is not null)
            {
                report = args.Contains("--initialization")
                    ? runtime.Initialization
                    : runtime.Report;
            }
            else if (loaded is not null)
            {
                report = loaded.Report;
            }
            else
            {
                if (args.Contains("--allow-incomplete") || explicitBindings)
                {
                    throw new ArgumentException("--allow-incomplete and --import-bindings require --load.");
                }

                var executable = new NeExecutableReader().Read(bytes);
                report = args.Contains("--resources") || args.Contains("--resource-index")
                    ? NeResourceCommands.Inspect(args, bytes)
                    : args.Contains("--imports")
                        ? NeImportResolver.Discover(executable)
                        : executable;
            }

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
            string json = JsonSerializer.Serialize(report, jsonOptions);
            if (output is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                File.WriteAllText(output, json);
            }

            return report is NeResourceReport resources && resources.Resources.Any(r => r.DecodeStatus == "Invalid")
                ? 1
                : 0;
        }
    }

    string? selectedFunction = Option(args, "--function");
    bool acceptance = args.Contains("--acceptance");
    if (Option(args, "--graphics-input") is not null && !acceptance)
    {
        throw new ArgumentException("--graphics-input requires --acceptance.");
    }

    var parser = new AssemblyParser();
    var result = acceptance
        ? parser.ParseSelected(input, new[] { "TurnLeft", "FindMonster", "GrowArray" })
        : selectedFunction is not null
            ? parser.ParseSelected(input, new[] { selectedFunction })
            : parser.Parse(input);
    foreach (var d in result.Diagnostics)
    {
        Console.Error.WriteLine($"{d.Code}: {d.Source.File}:{d.Source.Line}: {d.Message} [{d.Text}]");
    }

    if (result.Diagnostics.Count != 0)
    {
        return 1;
    }

    if (args.Contains("--callbacks") && !acceptance && command == "test")
    {
        IrModule? original = null;
        string? callbackInput = Option(args, "--callback-input");
        if (callbackInput is not null)
        {
            if (!File.Exists(callbackInput))
            {
                throw new InvalidDataException("--callback-input must name the original UI assembly.");
            }

            var parsed = parser.ParseSelected(callbackInput, new[] { "SAMPLEWNDPROC" });
            foreach (var diagnostic in parsed.Diagnostics)
            {
                Console.Error.WriteLine($"{diagnostic.Source.File}:{diagnostic.Source.Line}: {diagnostic.Message}");
            }

            if (parsed.Diagnostics.Count != 0)
            {
                return 1;
            }

            original = parsed.Module;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIPPED Windows-only native callback execution on this platform.");
            return 0;
        }

        CallbackAcceptanceHarness.Run(result.Module, original);
        Console.WriteLine("Windows callback acceptance passed: enumeration, signed window return, state preservation" + (original is null
            ? "."
            : ", and synthetic SAMPLEWNDPROC non-paint path. Painting remains unsupported."));
        return 0;
    }

    if (command == "inspect")
    {
        foreach (var fn in result.Module.Functions)
        {
            Console.WriteLine($"{fn.Name}: {fn.Instructions.Count} instructions");
            if (fn.Name == "SAMPLEWNDPROC")
            {
                var artifact = new Win16BodyCodeGenerator().Generate(CounterCallbackProfile.Configure(fn), symbols: CounterCallbackProfile.Symbols, targets: CounterCallbackProfile.Targets);
                Console.WriteLine("  Callback reference: 4000:0300, WindowProcedure, callee cleanup 10; non-paint integration only.");
                Console.WriteLine("  Runtime helpers: " + string.Join(", ", artifact.Relocations!.Select(r => r.Symbol).Distinct()));
            }

            if (GraphicsTargetProfile.Supports(fn.Name))
            {
                var artifact = new Win16BodyCodeGenerator().Generate(GraphicsTargetProfile.Configure(fn), symbols: GraphicsTargetProfile.Symbols, module: result.Module, targets: GraphicsTargetProfile.Targets);
                Console.WriteLine("  Runtime helpers: " + string.Join(", ", artifact.Relocations!.Select(r => r.Symbol).Distinct()));
                Console.WriteLine("  Original UI profile: caller cleanup; GDI.PatBlt requires a registered Windows DC.");
            }

            foreach (var call in fn.Instructions.Where(i => i.Operation == "CALL"))
            {
                var binding = Win16ImportResolver.Resolve(call, result.Module);
                if (binding.Kind == Win16CallTargetKind.Unresolved && GraphicsTargetProfile.Supports(fn.Name) && GraphicsTargetProfile.Targets.TryGetValue(binding.OriginalIdentity, out var target))
                {
                    binding = new(Win16CallTargetKind.Internal, binding.OriginalIdentity, null, $"configured translated procedure {target.Segment:X4}:{target.Offset:X4}; include its body when linking");
                }

                Console.WriteLine($"  {call.Source.File}:{call.Source.Line}: {binding.OriginalIdentity}: {binding.Kind} {binding.Api?.Identity.CanonicalName} service={binding.Api?.ServiceId} ({binding.Diagnostic})");
            }
        }

        return 0;
    }

    if (command == "dump-ir")
    {
        var normalized = NormalizeIr(result.Module);
        foreach (var d in normalized.Diagnostics)
        {
            Console.Error.WriteLine($"{d.Code}: {d.Source?.File}:{d.Source?.Line}: {d.Message} [{d.OriginalText}]");
        }

        if (normalized.Diagnostics.Count > 0)
        {
            return 1;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var json = JsonSerializer.Serialize(normalized.Module, jsonOptions);
        if (output is null)
        {
            Console.WriteLine(json);
        }
        else
        {
            File.WriteAllText(output, json);
        }

        return 0;
    }

    if (acceptance && command is "translate" or "test")
    {
        var normalized = NormalizeIr(result.Module);
        foreach (var diagnostic in normalized.Diagnostics)
        {
            Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Source?.File}:{diagnostic.Source?.Line}: {diagnostic.Message}");
        }

        if (normalized.Diagnostics.Count != 0)
        {
            return 1;
        }

        IrFunction Target(string name) => normalized.Module.Functions.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"Acceptance input must contain {name}; use tests/Fixtures/Compiler/Acceptance.asm.");

        var program = new CompilerAcceptanceProgram(Target("TurnLeft"), Target("FindMonster"), Target("GrowArray"));
        string? graphicsInput = Option(args, "--graphics-input");
        bool complete = args.Contains("--callbacks");
        if (complete && graphicsInput is null)
        {
            throw new ArgumentException("Standalone callbacks require --graphics-input and --callback-fixtures.");
        }

        if (graphicsInput is not null)
        {
            if (!File.Exists(graphicsInput))
            {
                throw new InvalidDataException("--graphics-input must name the UI assembly source.");
            }

            var graphics = parser.ParseSelected(graphicsInput, new[] { "IsPointVisible", "InvertViewportTile" });
            foreach (var diagnostic in graphics.Diagnostics)
            {
                Console.Error.WriteLine($"{diagnostic.Source.File}:{diagnostic.Source.Line}: {diagnostic.Message}");
            }

            if (graphics.Diagnostics.Count != 0)
            {
                return 1;
            }

            byte[] image;
            if (complete)
            {
                string? fixturePath = Option(args, "--callback-fixtures");
                if (fixturePath is null || !File.Exists(fixturePath))
                {
                    throw new InvalidDataException("--callback-fixtures must name the synthetic callback assembly.");
                }

                string callbackPath = Option(args, "--callback-input") ?? graphicsInput;
                if (!File.Exists(callbackPath))
                {
                    throw new InvalidDataException("--callback-input must name the original UI assembly.");
                }

                var fixtures = parser.ParseSelected(fixturePath, new[] { "ENUMCALLBACK", "STOPCALLBACK", "VALUEWNDPROC" });
                var original = parser.ParseSelected(callbackPath, new[] { "SAMPLEWNDPROC" });
                foreach (var diagnostic in fixtures.Diagnostics.Concat(original.Diagnostics))
                {
                    Console.Error.WriteLine($"{diagnostic.Source.File}:{diagnostic.Source.Line}: {diagnostic.Message}");
                }

                if (fixtures.Diagnostics.Count + original.Diagnostics.Count != 0)
                {
                    return 1;
                }

                image = new WindowsCallbackAcceptanceProgram(normalized.Module, graphics.Module, fixtures.Module, original.Module).WriteExecutable();
            }
            else
            {
                image = new WindowsCallbackGraphicsProgram(normalized.Module, graphics.Module).WriteExecutable();
            }

            string label = complete
                ? "Windows API/graphics/callback"
                : "Windows GDI graphics";
            if (command == "translate")
            {
                if (output is null || !args.Contains("--pe"))
                {
                    throw new ArgumentException("Graphics acceptance translation requires --output and --pe.");
                }

                File.WriteAllBytes(output, image);
                Console.WriteLine($"Wrote standalone {label} acceptance executable to {output}; exit status 0 means all selected cases passed.");
                return 0;
            }

            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine($"SKIPPED Windows-only {label} execution on this platform; generate --pe and run it on Windows.");
                return 0;
            }

            string temporary = Path.Combine(Path.GetTempPath(), $"win16-graphics-{Guid.NewGuid():N}.exe");
            try
            {
                File.WriteAllBytes(temporary, image);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(temporary) { UseShellExecute = false })!;
                if (!process.WaitForExit(30000))
                {
                    process.Kill(entireProcessTree: true);
                    throw new InvalidDataException("Graphics acceptance timed out.");
                }

                Console.WriteLine($"{label} acceptance exit status: {process.ExitCode}");
                return process.ExitCode == 0
                    ? 0
                    : 1;
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        if (command == "test")
        {
            using var native = new NativeWin16Body(program.LinkNative());
            var state = new MachineState();
            ushort status = native.Invoke(ref state, new SegmentMemory(), new Win16CallDispatcher());
            Console.WriteLine(status == 0
                ? "All native compiler acceptance cases passed."
                : $"Native acceptance case {status} failed.");
            return status == 0
                ? 0
                : 1;
        }

        if (output is null || !args.Contains("--pe"))
        {
            Console.Error.WriteLine("Error: acceptance translation requires --output <file> --pe.");
            return 2;
        }

        File.WriteAllBytes(output, program.WriteExecutable());
        Console.WriteLine($"Wrote standalone AMD64 PE32+ acceptance executable to {output}; exit status 0 means every case passed.");
        return 0;
    }

    if (command == "translate")
    {
        var function = result.Module.Functions.FirstOrDefault(f => f.Name.Equals(Option(args, "--function") ?? "TurnLeft", StringComparison.OrdinalIgnoreCase));
        if (function is null)
        {
            Console.Error.WriteLine("Error: requested function was not found.");
            return 1;
        }

        bool turnLeft = function.Name.Equals("TurnLeft", StringComparison.OrdinalIgnoreCase);
        var checkedIr = NormalizeIr(result.Module with
        {
            Functions = new[] { function }
        });
        foreach (var d in checkedIr.Diagnostics)
        {
            Console.Error.WriteLine($"{d.Code}: {d.Source?.File}:{d.Source?.Line}: {d.Message} [{d.OriginalText}]");
        }

        if (checkedIr.Diagnostics.Count > 0)
        {
            return 1;
        }

        var loweredFunction = checkedIr.Module.Functions.Single(f => f.Name.Equals(function.Name, StringComparison.OrdinalIgnoreCase));
        bool peOutput = args.Contains("--pe");
        if (peOutput && !turnLeft)
        {
            Console.Error.WriteLine("Error: use --acceptance --pe with the combined targets to package segmented runtime helpers and acceptance fixtures.");
            return 3;
        }

        var abi = peOutput || OperatingSystem.IsWindows() || !turnLeft
            ? X64Abi.Windows
            : X64Abi.SystemV;
        var artifact = turnLeft
            ? new TurnLeftCodeGenerator().Generate(loweredFunction, abi)
            : function.Name == "SAMPLEWNDPROC"
                ? new Win16BodyCodeGenerator().Generate(CounterCallbackProfile.Configure(loweredFunction), symbols: CounterCallbackProfile.Symbols, targets: CounterCallbackProfile.Targets)
                : args.Contains("--callbacks")
                    ? new Win16BodyCodeGenerator().Generate(loweredFunction)
                    : function.Name == "FindMonster"
                        ? FindMonsterCodeGenerator.GenerateArtifact(loweredFunction, abi)
                        : function.Name == "GrowArray"
                            ? GrowArrayCodeGenerator.GenerateArtifact(loweredFunction, abi)
                            : GraphicsTargetProfile.Supports(function.Name)
                                ? new Win16BodyCodeGenerator().Generate(GraphicsTargetProfile.Configure(loweredFunction), symbols: GraphicsTargetProfile.Symbols, module: result.Module, targets: GraphicsTargetProfile.Targets)
                                : throw new InvalidDataException($"No translation target binding exists for '{function.Name}'. Supported roots: TurnLeft, FindMonster, GrowArray, IsPointVisible, InvertViewportTile, SAMPLEWNDPROC.");
        byte[] bytes = peOutput
            ? new Pe32PlusWriter().WriteExecutable(artifact.Bytes)
            : artifact.Bytes;
        if (output is null)
        {
            Console.Error.WriteLine("Error: --output is required for translation.");
            return 2;
        }

        File.WriteAllBytes(output, bytes);
        if (!turnLeft)
        {
            var manifestOptions = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            manifestOptions.Converters.Add(new JsonStringEnumConverter());
            File.WriteAllText(output + ".json", JsonSerializer.Serialize(artifact, manifestOptions));
            Console.WriteLine($"Runtime helper relocations and ABI recorded in {output}.json.");
        }

        Console.WriteLine($"Wrote {bytes.Length} bytes ({(args.Contains("--pe")
            ? "AMD64 PE32+ container"
            : "native x64 function code")}) to {output}.");
        return 0;
    }

    if (command == "test")
    {
        var function = result.Module.Functions.FirstOrDefault(f => f.Name.Equals("TurnLeft", StringComparison.OrdinalIgnoreCase));
        if (function is null)
        {
            Console.Error.WriteLine("Error: TurnLeft function was not found in the input.");
            return 1;
        }

        var checkedIr = NormalizeIr(result.Module with
        {
            Functions = new[] { function }
        });
        foreach (var d in checkedIr.Diagnostics)
        {
            Console.Error.WriteLine($"{d.Code}: {d.Source?.File}:{d.Source?.Line}: {d.Message} [{d.OriginalText}]");
        }

        if (checkedIr.Diagnostics.Count > 0)
        {
            return 1;
        }

        var loweredFunction = checkedIr.Module.Functions.Single(f => f.Name.Equals(function.Name, StringComparison.OrdinalIgnoreCase));
        var generator = new TurnLeftCodeGenerator();
        var abi = OperatingSystem.IsWindows()
            ? X64Abi.Windows
            : X64Abi.SystemV;
        var code = generator.Generate(loweredFunction, abi);
        foreach (short x in new short[]
        {
            short.MinValue,
            -2,
            -1,
            0,
            1,
            2,
            short.MaxValue
        }

        )
        {
            foreach (short y in new short[]
            {
                short.MinValue,
                -2,
                -1,
                0,
                1,
                2,
                short.MaxValue
            }

            )
            {
                var actual = generator.Execute(code, x, y);
                var expected = TurnLeftRules.TryTurnLeft(x, y, out short xOut, out short yOut)
                    ? (xOut, yOut)
                    : (unchecked((short)0x5555), unchecked((short)0x5555));
                if (actual != expected)
                {
                    Console.Error.WriteLine($"Differential mismatch for ({x},{y}): got {actual}, expected {expected}.");
                    return 1;
                }
            }
        }

        Console.WriteLine("TurnLeft differential check passed for 49 boundary and direction coordinate pairs.");
        return 0;
    }

    Console.Error.WriteLine($"Unknown command '{command}'.");
    return 2;
}
catch (Exception error) when (error is UnsupportedInstructionException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
{
    Console.Error.WriteLine($"Translation failed: {error.Message}");
    return 3;
}

static string? Option(string[] values, string name)
{
    int i = Array.IndexOf(values, name);
    return i >= 0 && i + 1 < values.Length
        ? values[i + 1]
        : null;
}

static (Translator.IR.IrModule Module, IReadOnlyList<Translator.Core.TranslationDiagnostic> Diagnostics) NormalizeIr(Translator.IR.IrModule module)
{
    var passes = new TranslationPipeline(new RegisterNormalizationPass(), new OperandWidthNormalizationPass(), new ArithmeticWidthNormalizationPass(), new FlagSemanticsNormalizationPass(), new ControlFlowNormalizationPass(), new NativeCompatibilityValidationPass());
    return passes.Run(module);
}
