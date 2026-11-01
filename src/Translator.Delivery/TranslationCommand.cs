namespace Translator.Delivery;

public static class TranslationCommand
{
    public const string Usage = "ChipShift64 translate --input <CHIPS.EXE> [--output <CHIPS64.EXE>]";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token = default)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                await output.WriteLineAsync(Usage);
                return 0;
            }

            if (args.Length == 0 || args[0] != "translate")
            {
                throw new ArgumentException(Usage);
            }

            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (args[i] is not ("--input" or "--output") || i + 1 >= args.Length || !options.TryAdd(args[i], args[i + 1]))
                {
                    throw new ArgumentException(Usage);
                }
            }

            if (!options.TryGetValue("--input", out string? input))
            {
                throw new ArgumentException(Usage);
            }

            var progress = new TextProgress(output);
            var result = await new TranslationService().TranslateAsync(new(input, options.GetValueOrDefault("--output")), progress, token);
            await output.WriteLineAsync($"Created {result.Output}");
            await output.WriteLineAsync("Launch the executable directly.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Translation cancelled. Existing files were preserved.");
            return 130;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            await error.WriteLineAsync(e.Message);
            return 2;
        }
    }

    private sealed class TextProgress(TextWriter writer) : IProgress<TranslationProgress>
    {
        public void Report(TranslationProgress value) => writer.WriteLine(value.Message);
    }
}
