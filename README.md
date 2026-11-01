# ChipShift64

ChipShift64 translates the original Windows 16-bit Chip’s Challenge into a native
64-bit Windows executable. It compiles the original game instructions to AMD64,
preserves its embedded resources, and supplies compatibility services for the
Win16 APIs the game uses. The resulting `CHIPS64.EXE` runs directly on x64 Windows,
without a 16-bit Windows environment or an installed .NET runtime.

The project targets a verified edition of the original game. It is not a general
Win16 application converter. Translation preserves the original game’s logic;
“64” refers to the executable architecture, not higher-resolution artwork.

**This repository contains no original Chip’s Challenge source code, game binaries
or game assets**, including artwork, levels, music, sound effects and help content.
It contains the translator, compatibility code and authored test fixtures. Names,
file identifiers and interface metadata identify the external inputs it supports.
You must supply your own lawfully obtained game files.

## Requirements

- **To translate:** x64 Windows and the **.NET 10 Runtime**, plus the
  published `ChipShift64.exe`.
- **To play:** x64 Windows and the generated executable with its companion game
  assets and help files. The translator and .NET are not needed to play.
- **Original files:** your own copy of the supported game and its companion files,
  listed below. ChipShift64 does not supply the original game assets.

The supported `CHIPS.EXE` is **267,776 bytes**, with SHA-256:

```text
8e26acd67cf120bd5b512de4b4e78b80aca1579413cd04f3b2b68909a375866c
```

Other executable versions are rejected. Keep these original files in the same
folder before translating:

```text
CHIPS.EXE       WEP4UTIL.DLL    CHIPS.DAT       CHIPS.HLP
CHIP01.MID      CHIP02.MID
BLIP2.WAV       BUMMER.WAV      CLICK3.WAV      DITTY1.WAV
DOOR.WAV        OOF3.WAV        POP2.WAV        STRIKE.WAV
TELEPORT.WAV    WATER2.WAV
```

## Translate and play

1. Launch `ChipShift64.exe` and select your original `CHIPS.EXE`.
2. Review the output location. It defaults to `CHIPS64.EXE` beside the original;
   use **Change output** to choose another name or an existing writable folder.
3. Click **Translate** and wait for completion.
4. Launch `CHIPS64.EXE` directly.

Existing output files are never overwritten. Choose a new output name or move an
existing output before translating again. Original files and saves are preserved.
When you choose a different folder, required missing assets are copied there;
conflicting existing assets are refused. Saves and profiles are not copied, so a
new folder starts with its own game profile.

Keep the generated game and its companion assets together when moving it. Help
conversion creates matching `.HLP` and `.CHM` companions for the chosen output
name. The generated game no longer needs the original `CHIPS.EXE` or
`WEP4UTIL.DLL`.

For scripting, wait for the translator process to exit and check its exit code:

```powershell
$arguments = 'translate --input "C:\Games\CHIPS\CHIPS.EXE"'
$process = Start-Process .\ChipShift64.exe -ArgumentList $arguments -Wait -PassThru
$process.ExitCode
# Optional: append --output "C:\Games\CHIPS64\CHIPS64.EXE" to $arguments.
# Create the output folder first.
```

Exit codes are `0` for success, `2` for invalid input, usage or I/O errors, and
`130` for cancellation. Use `--help` to display command usage. Cancellation
removes incomplete output.

## Build from source

Install the **.NET 10 SDK** on Windows. From the repository root:

```powershell
dotnet build .\ChipShift64.sln
dotnet publish .\src\ChipShift64\ChipShift64.csproj -c Release -r win-x64 --self-contained false -o .\bin\delivery\ChipShift64
```

Launch and distribute `bin\delivery\ChipShift64\ChipShift64.exe`. Publishing bundles
the referenced project assemblies into the executable. Release builds embed debug
symbols in the assemblies, so no separate `.pdb` files are needed. This
framework-dependent executable requires the .NET 10 Desktop Runtime.
Ordinary `dotnet build` output retains separate DLLs for development and tests.

NuGet dependencies are restored automatically. The test projects use xUnit and
Microsoft.NET.Test.Sdk. Normal builds use checked-in native compatibility
payloads; they do not require a C compiler, Python or the original game.

## Test

To run the development test suite from the repository root:

```powershell
.\test.ps1
# Run a selected group instead:
.\test.ps1 -TestArguments @('--filter', 'FullyQualifiedName~NeDeliveryTests')
```

The runner restores dependencies and isolates temporary test executables in
`bin/test-temp`. Windows integration and acceptance checks require Windows; checks
using the original game require a locally supplied supported binary:

```powershell
$env:CHIPS_NE_INPUT = 'C:\path\to\CHIPS.EXE'
.\test.ps1
```

The suite includes compiler unit tests, authored assembly fixtures, synthetic NE
executables, and Windows integration tests. Original-game tests skip when
`CHIPS_NE_INPUT` is absent; delivery and gameplay tests also need the companion
files beside it. A run with skipped tests does not verify original-game behavior.

Optional native development checks require Python 3:

```powershell
python tests/Translator.TestSupport/Native/test_native_source.py
```

Rebuilding native payloads additionally requires GCC, GNU binutils and
clang-format 18. See the [native development guide](src/Win16.Runtime/Native/README.md)
for regeneration commands and deeper native verification.

## How it works

The translator reads the original NE (16-bit Windows) executable, discovers code
from its entry points, relocations and control flow, lowers instructions through
an intermediate representation, and links AMD64 code with a Win16 compatibility
payload into a PE32+ (64-bit Windows) executable. The translated program retains
the original game logic; the compatibility layer supplies memory, windowing,
graphics, audio and other Windows services.

Embedded resources are preserved or converted. The built-in help converter
supports HC30 / WinHelp 3.0 and produces HTML Help without an external help
compiler. Unsupported inputs or unresolved required dependencies stop delivery.
The project targets the verified executable above, not arbitrary Win16 programs
or every historical help format.

The `Translator.*` projects implement the compiler, packaging and developer tools;
`Win16.Runtime` supplies compatibility support, and `ChipShift64` provides the
Windows interface. Tests use authored fixtures and optional, locally supplied
original files. No original game is needed to build the translator.

## License and legal notice

The project code is licensed under the **GNU General Public License, version 2
only (GPL-2.0-only)**. See [LICENSE](LICENSE) for the full terms, including the
warranty disclaimer.

Chip’s Challenge and associated names and materials belong to their respective
rights holders. This is an independent project and is not affiliated with,
endorsed by or sponsored by those rights holders. The repository license grants
no rights to the original game or its assets.

Generated executables contain translated original game code and embedded
resources; copied assets and converted help also retain third-party content.
They are not automatically licensed under this repository’s GPL. You are
responsible for ensuring that your use, translation and any redistribution comply
with applicable law and the permissions for those materials.
