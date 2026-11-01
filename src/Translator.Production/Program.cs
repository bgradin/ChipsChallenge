using System.Text.Json;
using Translator.ChipsChallenge;
using Translator.Linker;
using Translator.Delivery;

if (args.Length > 0 && args[0] is "translate" or "--help" or "-h")
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancellation.Cancel();
    };
    return await TranslationCommand.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
}

// The historical Translator.Cli remains the developer/checkpoint command surface.
try
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i += 2)
    {
        if (args[i] is not ("--input" or "--output" or "--manifest" or "--package" or "--utility-input") || i + 1 >= args.Length || !values.TryAdd(args[i], args[i + 1]))
        {
            throw new ArgumentException("Usage: Translator.Production --input <CHIPS.EXE> [--output <CHIPS64.EXE>] [--manifest <diagnostics.json>] [--package development --utility-input <WEP4UTIL.DLL>]");
        }
    }

    if (!values.TryGetValue("--input", out var source))
    {
        throw new ArgumentException("--input is required.");
    }

    string input = Path.GetFullPath(source);
    string output = Path.GetFullPath(values.GetValueOrDefault("--output") ?? Path.Combine(Path.GetDirectoryName(input)!, "CHIPS64.EXE"));
    string manifest = Path.GetFullPath(values.GetValueOrDefault("--manifest") ?? output + ".json");
    var comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    if (input.Equals(output, comparison) || input.Equals(manifest, comparison) || output.Equals(manifest, comparison))
    {
        throw new ArgumentException("Input, executable output and diagnostic manifest must have distinct paths.");
    }

    byte[] bytes = File.ReadAllBytes(input);
    SupportedChipsSource.Validate(bytes);
    bool package = values.TryGetValue("--package", out var mode);
    if (package && mode is not ("development" or "application-development"))
    {
        throw new ArgumentException("Use --package development for the resource smoke or --package application-development for original startup.");
    }

    if (!package && values.ContainsKey("--utility-input"))
    {
        throw new ArgumentException("--utility-input requires --package development.");
    }

    byte[]? utility = package
        ? File.ReadAllBytes(values.GetValueOrDefault("--utility-input") ?? Path.Combine(Path.GetDirectoryName(input)!, "WEP4UTIL.DLL"))
        : null;
    if (package && (File.Exists(output) || File.Exists(manifest)))
    {
        throw new IOException("Output or manifest already exists; choose a new output path.");
    }

    var result = NeProductionCompiler.Compile(bytes, new(output, utility, package, mode == "application-development"));
    if (result.Executable is not null)
    {
        if (!Pe32PlusWriter.IsAmd64Pe32Plus(result.Executable))
        {
            throw new InvalidDataException("Invalid production PE output.");
        }

        // CreateNew preserves existing outputs even if another process publishes during compilation.
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(result.Executable);
    }

    File.WriteAllText(manifest, JsonSerializer.Serialize(result.Manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    foreach (var diagnostic in result.Manifest.Diagnostics)
    {
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
    }

    // Diagnostic-only invocations retain the historical fail-closed boundary.
    return result.Executable is null
        ? 3
        : 0;
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
