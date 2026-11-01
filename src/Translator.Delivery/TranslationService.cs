using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Translator.ChipsChallenge;
using Translator.Linker;
using Win16_Runtime;

namespace Translator.Delivery;

public sealed record TranslationRequest(string Input, string? Output = null);
public sealed record TranslationProgress(string Stage, string Message);
public sealed record TranslationResult(string Output, string Sha256, IReadOnlyList<string> CopiedAssets);
/// <summary>Shared user-facing workflow. No file is replaced, and no game state is supplied.</summary>
public sealed class TranslationService
{
    // Original external filenames, not replacement content or game rules.
    public static IReadOnlyList<string> RequiredAssets { get; } = Array.AsReadOnly(new[] { "CHIPS.DAT", "BLIP2.WAV", "BUMMER.WAV", "CLICK3.WAV", "DITTY1.WAV", "DOOR.WAV", "OOF3.WAV", "POP2.WAV", "STRIKE.WAV", "TELEPORT.WAV", "WATER2.WAV", "CHIP01.MID", "CHIP02.MID" });

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, IProgress<TranslationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() => Translate(request, progress, cancellationToken), cancellationToken);

    private static TranslationResult Translate(TranslationRequest request, IProgress<TranslationProgress>? progress, CancellationToken token)
    {
        void Report(string stage, string message)
        {
            progress?.Report(new(stage, message));
            token.ThrowIfCancellationRequested();
        }

        Report("Validating", "Checking the original executable and required companion files…");
        string input = Path.GetFullPath(request.Input);
        string sourceDirectory = Path.GetDirectoryName(input)!;
        string output = Path.GetFullPath(request.Output ?? Path.Combine(sourceDirectory, "CHIPS64.EXE"));
        string outputDirectory = Path.GetDirectoryName(output)!;
        if (!Path.GetExtension(output).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose an output filename ending in .exe.");
        }

        if (File.Exists(output) || Directory.Exists(output))
        {
            throw new IOException($"Output already exists: {output}. Choose another filename or move it yourself before retrying. Nothing was replaced.");
        }

        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException($"Output folder does not exist: {outputDirectory}. Choose an existing writable folder.");
        }

        byte[] original = ReadRequired(input);
        SupportedChipsSource.Validate(original);
        byte[] utility = ReadRequired(Path.Combine(sourceDirectory, "WEP4UTIL.DLL"));
        Win16ExportContracts.VerifyWepImage(utility);
        var assets = RequiredAssets.Select(name => (Name: name, Bytes: ReadRequired(Path.Combine(sourceDirectory, name)))).ToArray();
        if (assets.Any(a => a.Bytes.Length == 0))
        {
            throw new InvalidDataException("A required game asset is empty. Restore the original companion files and retry.");
        }

        // DAT and saves are user data. Require presence/readability, not the pristine DAT hash.
        var help = HelpFileConversion.Plan(original, utility, input, output);
        var outputAssets = assets.Concat(help.Select(a => (Name: a.RelativePath, a.Bytes)));
        var copies = new List<(string Destination, byte[] Bytes)>();
        foreach (var asset in outputAssets)
        {
            string destination = Path.Combine(outputDirectory, asset.Name);
            for (string? parent = Path.GetDirectoryName(destination); parent is not null && !parent.Equals(outputDirectory, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            {
                if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new NotSupportedException("Output help folders must not traverse symbolic links: " + asset.Name);
                }
            }

            if (File.Exists(destination))
            {
                if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(asset.Bytes))
                {
                    throw new IOException($"Companion file differs in the output folder: {destination}. Choose another folder; existing files and saves will not be replaced.");
                }
            }
            else if (Directory.Exists(destination))
            {
                throw new IOException($"A folder occupies the required companion filename: {destination}.");
            }
            else
            {
                copies.Add((destination, asset.Bytes));
            }
        }

        if (outputAssets.Any(asset => Path.GetFileName(output).Equals(asset.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The output must not use a companion filename.");
        }

        long companionBytes = copies.Sum(a => (long)a.Bytes.Length);
        CheckSpace(outputDirectory, companionBytes + 64L * 1024 * 1024);
        var staged = new List<(string Temporary, string Destination)>();
        var published = new List<string>();
        var createdDirectories = new List<string>();
        try
        {
            // Probe real directory write permissions before expensive translation. This file is
            // also the eventual staging file; CreateNew never truncates a pre-existing file.
            string temporary = Path.Combine(outputDirectory, ".chips64-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            staged.Add((temporary, output));
            Report("Translating", "Translating the original application. Cancellation will discard the result…");
            var compilation = NeProductionCompiler.Compile(original, new(output, utility, true, true));
            token.ThrowIfCancellationRequested();
            byte[] executable = compilation.Executable ?? throw new InvalidDataException("Translation did not produce an executable.");
            Report("ValidatingOutput", "Checking the native executable before publishing…");
            ValidateExecutable(executable);
            string hash = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
            if (hash != compilation.Manifest.Package?.ExecutableSha256 || compilation.Manifest.Package.Resources.Count == 0)
            {
                throw new InvalidDataException("Generated executable identity did not match the translation result.");
            }

            CheckSpace(outputDirectory, companionBytes + executable.LongLength + 1024 * 1024);
            WriteStaged(temporary, executable);
            foreach (var copy in copies)
            {
                token.ThrowIfCancellationRequested();
                string assetTemporary = Path.Combine(outputDirectory, ".chips64-" + Guid.NewGuid().ToString("N") + ".tmp");
                // Register only after exclusive creation, so cleanup never deletes somebody else's file.
                using (new FileStream(assetTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                staged.Add((assetTemporary, copy.Destination));
                WriteStaged(assetTemporary, copy.Bytes);
            }

            Report("Publishing", "Publishing the validated executable and companion files…");
            // Assets first, executable last. A race creating any destination fails without replacement.
            foreach (var item in staged.Skip(1).Concat(staged.Take(1)))
            {
                token.ThrowIfCancellationRequested();
                string parent = Path.GetDirectoryName(item.Destination)!;
                var missing = new Stack<string>();
                for (string? directory = parent; directory is not null && !Directory.Exists(directory); directory = Path.GetDirectoryName(directory))
                {
                    missing.Push(directory);
                }

                while (missing.TryPop(out string? directory))
                {
                    Directory.CreateDirectory(directory);
                    createdDirectories.Add(directory);
                }

                File.Move(item.Temporary, item.Destination, overwrite: false);
                published.Add(item.Destination);
            }

            // Publication is complete: cancellation arriving now must not report a cancelled result.
            return new(output, hash, copies.Select(a => a.Destination).ToArray());
        }
        catch
        {
            foreach (string path in published.AsEnumerable().Reverse())
            {
                File.Delete(path);
            }

            foreach (string directory in createdDirectories.AsEnumerable().Reverse())
            {
                try
                {
                    Directory.Delete(directory, recursive: false);
                }
                catch (IOException)
                { /* Another writer owns content now. */
                }
            }

            throw;
        }
        finally
        {
            foreach (var item in staged)
            {
                if (File.Exists(item.Temporary))
                {
                    File.Delete(item.Temporary);
                }
            }
        }
    }

    private static byte[] ReadRequired(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required input is missing: {path}. Restore it beside the original CHIPS.EXE and retry.", path);
        }

        return File.ReadAllBytes(path);
    }

    private static void WriteStaged(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static void CheckSpace(string directory, long required)
    {
        long available = new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;
        if (available < required)
        {
            throw new IOException($"Not enough free space in {directory}. Free at least {required / (1024 * 1024) + 1} MiB and retry.");
        }
    }

    public static void ValidateExecutable(byte[] executable)
    {
        using var pe = new PEReader(new MemoryStream(executable, writable: false));
        var h = pe.PEHeaders;
        var image = h.PEHeader ?? throw new InvalidDataException("Output has no PE header.");
        if (h.CoffHeader.Machine != Machine.Amd64 || image.Magic != PEMagic.PE32Plus || image.Subsystem != Subsystem.WindowsGui || image.CorHeaderTableDirectory.Size != 0 || image.ImportTableDirectory.Size == 0 || image.ExceptionTableDirectory.Size == 0 || image.BaseRelocationTableDirectory.Size == 0)
        {
            throw new InvalidDataException("Output is not the expected CLR-free native AMD64 GUI executable.");
        }

        foreach (var section in h.SectionHeaders)
        {
            if (section.PointerToRawData < 0 || section.SizeOfRawData < 0 || (long)section.PointerToRawData + section.SizeOfRawData > executable.LongLength || (section.SectionCharacteristics & (SectionCharacteristics.MemWrite | SectionCharacteristics.MemExecute)) == (SectionCharacteristics.MemWrite | SectionCharacteristics.MemExecute))
            {
                throw new InvalidDataException("Output contains invalid or writable executable sections.");
            }
        }

        if (!h.SectionHeaders.Any(s => image.AddressOfEntryPoint >= s.VirtualAddress && image.AddressOfEntryPoint < (long)s.VirtualAddress + s.VirtualSize && (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0))
        {
            throw new InvalidDataException("Output entry point is outside executable code.");
        }
    }
}
