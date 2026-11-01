#!/usr/bin/env python3
"""Execute the actual packed MCI bridge under ASan; reject its old stack overrun."""
import os
import pathlib
import subprocess
import sys
import tempfile

sys.dont_write_bytecode = True
from native_source import replace_c
from rebuild import ROOT, api_bindings_header

with tempfile.TemporaryDirectory(prefix="chips-ne-modules-") as directory:
    work = pathlib.Path(directory)
    (work / "api_bindings.h").write_text(api_bindings_header())
    # Copy the include closure so the single mutated production header is used.
    for source in list(ROOT.glob("*.h")) + [
        ROOT / "acceptance.c",
        ROOT / "ne_module_contract.c",
    ]:
        (work / source.name).write_bytes(source.read_bytes())

    def run(name):
        binary = work / name
        subprocess.run(
            [
                "gcc",
                "-O1",
                "-g",
                "-no-pie",
                "-fsanitize=address",
                "-fno-omit-frame-pointer",
                "-ffunction-sections",
                "-fdata-sections",
                "-Wl,--gc-sections",
                "-I",
                str(work),
                str(work / "ne_module_contract.c"),
                "-o",
                str(binary),
            ],
            check=True,
            timeout=180,
        )
        return subprocess.run(
            [str(binary)],
            capture_output=True,
            text=True,
            env={**os.environ, "ASAN_OPTIONS": "detect_leaks=0:halt_on_error=1"},
            timeout=30,
        )

    original = run("production")
    if original.returncode:
        raise RuntimeError("Packed MCI contract failed: " + original.stderr)
    header = work / "task_modules.h"
    text = header.read_text()
    before = "u8 native[36];zero(native,sizeof(native));"
    header.write_text(replace_c(text, before, "u8 native[36];zero(native,40);"))
    mutant = run("stack-overrun")
    if not mutant.returncode or "stack-buffer-overflow" not in mutant.stderr:
        raise RuntimeError(
            "Old MCI stack overwrite escaped the ASan oracle: " + mutant.stderr
        )
print(
    "Native packed MCI bridge passed under ASan; original 36-byte-buffer/40-byte-clear stack-overrun mutation detected."
)
