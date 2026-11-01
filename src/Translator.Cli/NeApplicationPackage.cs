using System.Security.Cryptography;
using System.Text.Json;
using Translator.Linker;

/// <summary>Deterministic distribution sidecars. The native PE consumes none of these files.</summary>
internal static class NeApplicationPackage
{
    public static void WriteSidecars(string output, NeApplicationManifest manifest)
    {
        if (manifest.Distribution is null)
        {
            throw new ArgumentException("Distribution metadata is required.");
        }

        bool gameplay = manifest.Startup.Gameplay;
        string name = Path.GetFileName(output);
        string readme = $"""
            Native original Chip's Challenge UI checkpoint
            ============================================
            Requires Windows 10 or newer, AMD64. The EXE does not require .NET,
            a translator installation, this README, the launcher or either JSON file.

            Launch directly:
              .\{name} --original "C:\Games\Chip's Challenge\CHIPS.EXE"
            Optional diagnostic file (must be a NEW writable path):
              .\{name} --original "C:\Games\Chip's Challenge\CHIPS.EXE" --diagnostics "C:\Temp\chips-new.log"
            Native bounded verification (no modal errors):
              .\{name} --lifecycle-test --original "C:\Games\Chip's Challenge\CHIPS.EXE"
            Optional PowerShell launcher:
              & '.\{name}.launch.ps1' -OriginalExe "C:\Games\Chip's Challenge\CHIPS.EXE" -Verify

            What is implemented
            -------------------
            Original board, info, inventory and counter painting, with native windows.
            Original MAINWNDPROC -> MoveChip arrow input changes the buffered direction.
            The FIRST arrow buffers input; subsequent arrows/release preserve the buffer.
            THE PLAYER DOES NOT MOVE. No game timer, level loading, menus, sound or
            gameplay is implemented. Full original startup remains an explicit bootstrap.
            The scene is an authored fixture, not a loaded original level.

            Assets and provenance
            ---------------------
            Supply your own verified original CHIPS.EXE. It is needed only for bitmap
            resources; its code is never executed or dynamically loaded. Translated
            original function bodies and initialized data are already linked into the PE.
            No original EXE or original bitmap payloads are included in this distribution.
            Source SHA-256: {manifest.ExecutableSha256}
            Native PE SHA-256: {manifest.PeSha256}
            Original translated bodies: {manifest.Translation.Functions.Count}
            See {name}.json for exact original addresses, contracts, fatal boundaries,
            fixture fields, resource identities, native imports and runtime limits.
            See {name}.package.json for hashes of distribution files.

            Diagnostics
            -----------
            Normal failures show a specific native error dialog and debugger message.
            --diagnostics writes a small ASCII status report after shutdown, including
            stage, exit code, runtime fault and cleanup result. Existing files are never
            overwritten. The optional launcher selects a fresh log path automatically.
            --lifecycle-test suppresses error dialogs and performs bounded native checks.

            Exit codes
            ----------
            {string.Join("\n", manifest.ExitCodes.OrderBy(p => p.Key).Select(p => $"{p.Key}: {p.Value}"))}
            """;
        if (gameplay)
        {
            readme = $"""
            Native original Chip's Challenge gameplay
            ========================================
            Windows 10+ AMD64. The native EXE requires no .NET, translator, PowerShell,
            JSON manifests, launcher or README at runtime.

            Launch (LESSON 1 is the default):
              .\{name} --original "C:\Games\Chip's Challenge\CHIPS.EXE"
            Select the original LESSON 2:
              .\{name} --original "C:\Games\Chip's Challenge\CHIPS.EXE" --level 2
            CHIPS.DAT is discovered beside the supplied CHIPS.EXE. Override its path:
              .\{name} --original "C:\Games\Chip's Challenge\CHIPS.EXE" --levels "D:\Assets\CHIPS.DAT" --level 1
            Native verification for either starting level (99 or 21 stages):
              .\{name} --original "C:\Games\Chip's Challenge\CHIPS.EXE" --level 1 --lifecycle-test
            Optional launcher:
              & '.\{name}.launch.ps1' -OriginalExe "C:\Games\Chip's Challenge\CHIPS.EXE" -Level 2 -Verify
            Optional status report: --diagnostics <NEW writable file>. Existing logs
            and original assets are never overwritten. No installed saves are changed.

            Play
            ----
            Use arrow keys to move. Collect chips and keys, open doors and the chip
            socket, then enter the exit. The original code computes completion and
            score; a native notification presents its text. Dismiss it, inspect the
            next original level's title/password placard, and press Space to play.
            On death, dismiss the original message and press Space after the restart
            placard. The Restart menu invokes the original restart routine.

            Verified subset and limits
            --------------------------
            Authentic LESSON 1 collection/door/socket/completion route and LESSON 2
            bug, water/fire/collision/timeout death and restart paths are certified.
            Both launch profiles use the same statically linked translated original
            engine. Unsupported dependencies fail explicitly with native diagnostics.
            Blocks, sliding, other monster families, later levels and the last-level
            animation remain uncertified. Music is disabled. Progress/scores are
            process-local and do not persist between runs. Full original WinMain is
            replaced by the documented native bootstrap; no gameplay rules are
            implemented by that bootstrap. Original completion dialog layout is
            temporarily replaced with a notification of original-computed results.

            External assets and provenance
            ------------------------------
            Supply your own hash-validated original CHIPS.EXE and CHIPS.DAT. Neither
            original file nor extracted bitmap payloads are included in this package.
            Asset code is never executed, loaded as a native module or interpreted.
            Source EXE SHA-256: {manifest.ExecutableSha256}
            Source DAT SHA-256: {NeLevelInitialization.DataHash}
            Native PE SHA-256: {manifest.PeSha256}
            Original translated bodies: {manifest.Translation.Functions.Count}
            See {name}.json for source addresses, contracts, initialization profiles,
            fatal boundaries, external-resource identities and ownership limits.
            See {name}.tiles.json for certified tile paths and limitations.
            See {name}.package.json for distribution file hashes.

            Native diagnostics
            ------------------
            Normal failures show a native dialog and debugger message. --diagnostics
            records status, stage, exit code, runtime error, game snapshot and cleanup
            result. --lifecycle-test suppresses error dialogs and verifies independent
            state, pixels and resource cleanup for the requested starting level.

            Exit codes
            ----------
            {string.Join("\n", manifest.ExitCodes.OrderBy(p => p.Key).Select(p => $"{p.Key}: {p.Value}"))}
            """;
        }

        string exeLiteral = name.Replace("'", "''", StringComparison.Ordinal);
        string launcher = """
            #Requires -Version 5.1
            <# Optional convenience launcher; the native EXE itself needs no PowerShell/.NET.
            Uses the supplied ORIGINAL file only for validated external assets. Creates a
            fresh diagnostic log by default and returns the native process exit status.
            -Verify runs bounded original input/painting checks; normal mode stays open.
            No files or security settings are changed beyond the requested new log. #>
            [CmdletBinding()]
            param(
                [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$OriginalExe,
                [string]$DiagnosticsPath,
                [switch]$Verify
            )
            $ErrorActionPreference = 'Stop'
            $original = (Resolve-Path -LiteralPath $OriginalExe).Path
            if (-not $DiagnosticsPath) {
                $DiagnosticsPath = Join-Path $PSScriptRoot ('diagnostics-' + [Guid]::NewGuid().ToString('N') + '.log')
            }
            $log = [IO.Path]::GetFullPath($DiagnosticsPath)
            if ($original.Contains('"') -or $log.Contains('"')) { throw 'Paths cannot contain double quotes.' }
            if (Test-Path -LiteralPath $log) { throw 'Choose a new diagnostic file; existing files are preserved.' }
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = Join-Path $PSScriptRoot '__EXECUTABLE__'
            $info.WorkingDirectory = $PSScriptRoot
            $info.UseShellExecute = $false
            $info.Arguments = '--original "' + $original + '" --diagnostics "' + $log + '"'
            if ($Verify) { $info.Arguments = '--lifecycle-test ' + $info.Arguments }
            $process = [Diagnostics.Process]::Start($info)
            try {
                if ($Verify) {
                    if (-not $process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit(); throw 'Native verification timed out.' }
                } else { $process.WaitForExit() }
                $code = $process.ExitCode
            } finally { $process.Dispose() }
            Write-Host ('Native diagnostic report: ' + $log)
            if ($code -ne 0) { Write-Host ('Native application failed with exit code ' + $code) }
            exit $code
            """.Replace("__EXECUTABLE__", exeLiteral, StringComparison.Ordinal);
        if (gameplay)
        {
            launcher = launcher.Replace("[string]$DiagnosticsPath,", "[string]$LevelData,\n    [ValidateSet(1,2)][int]$Level=1,\n    [string]$DiagnosticsPath,", StringComparison.Ordinal).Replace("if ($Verify) { $info.Arguments", """
                $info.Arguments += ' --level ' + $Level
                if ($LevelData) {
                    $data=(Resolve-Path -LiteralPath $LevelData).Path
                    if ($data.Contains('"')) { throw 'Paths cannot contain double quotes.' }
                    $info.Arguments += ' --levels "' + $data + '"'
                }
                if ($Verify) { $info.Arguments
                """, StringComparison.Ordinal).Replace("WaitForExit(15000)", "WaitForExit(30000)", StringComparison.Ordinal);
        }

        File.WriteAllText(output + ".README.txt", readme.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        File.WriteAllText(output + ".launch.ps1", launcher.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new System.Text.UTF8Encoding(true));
        string[] files = gameplay
            ? [output, output + ".json", output + ".tiles.json", output + ".README.txt", output + ".launch.ps1"]
            : [output, output + ".json", output + ".README.txt", output + ".launch.ps1"];
        var package = new
        {
            Format = gameplay
                ? "Native original gameplay distribution v1"
                : "Native original UI distribution v1",
            SourceSha256 = manifest.ExecutableSha256,
            Platform = "Windows 10+ AMD64",
            RuntimeRequiresClr = false,
            RuntimeRequiresSidecars = false,
            IncludesOriginalAssets = false,
            Behavior = gameplay
                ? "Original gameplay, death/restart and completion/progression; certified LESSON 1 and LESSON 2 paths"
                : "Original scene and buffered arrow input; player movement and gameplay unsupported",
            Files = files.Select(path => new { Name = Path.GetFileName(path), Size = new FileInfo(path).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() }).ToArray()
        };
        File.WriteAllText(output + ".package.json", JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
