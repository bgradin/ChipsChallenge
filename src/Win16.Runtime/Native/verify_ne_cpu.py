#!/usr/bin/env python3
"""Compile and run production freestanding CMPS helpers against host CPU flags.
Requires an AMD64 GCC host; produces no repository or original-image changes.
"""
import pathlib
import subprocess
import tempfile
import sys

sys.dont_write_bytecode = True
from native_source import replace_c
from rebuild import ROOT, api_bindings_header

with tempfile.TemporaryDirectory(prefix="chips-ne-cpu-") as directory:
    temporary = pathlib.Path(directory)
    (temporary / "api_bindings.h").write_text(api_bindings_header())
    executable = temporary / "ne-cpu"
    subprocess.run(
        [
            "gcc",
            "-O2",
            "-ffunction-sections",
            "-fdata-sections",
            "-Wl,--gc-sections",
            "-I",
            str(temporary),
            "-I",
            str(ROOT),
            str(ROOT / "ne_cpu_contract.c"),
            "-o",
            str(executable),
        ],
        check=True,
    )
    subprocess.run([str(executable)], check=True)
    # Causal mutations must fail the same independent hardware/contract oracle.
    (temporary / "ne_cpu_contract.c").write_text(
        (ROOT / "ne_cpu_contract.c").read_text()
    )
    (temporary / "acceptance.c").write_text((ROOT / "acceptance.c").read_text())
    (temporary / "runtime_common.h").write_text((ROOT / "runtime_common.h").read_text())
    production = (ROOT / "instructions.h").read_text()
    mutations = [
        ("reversed-comparison", replace_c(production, "(a - b) &", "(b - a) &"), 2),
        ("wrong-word-width", replace_c(production, " || op == 7", ""), 1),
        (
            "inverted-repeat-condition",
            replace_c(production, "(prefix == 3) == !!", "(prefix == 3) != !!"),
            4,
        ),
        (
            "pretend-interrupt-service-success",
            replace_c(
                production,
                "if(vector!=0x21||!r->lifecycle){r->error=13;return 0;}",
                "if(vector!=0x21||!r->lifecycle){r->error=0;return 0;}",
            ),
            7,
        ),
    ]
    for name, mutated, expected in mutations:
        if mutated == production:
            raise RuntimeError("Mutation did not apply: " + name)
        (temporary / "instructions.h").write_text(mutated)
        mutant = temporary / name
        subprocess.run(
            [
                "gcc",
                "-O2",
                "-ffunction-sections",
                "-fdata-sections",
                "-Wl,--gc-sections",
                "-I",
                str(temporary),
                "-I",
                str(ROOT),
                str(temporary / "ne_cpu_contract.c"),
                "-o",
                str(mutant),
            ],
            check=True,
        )
        if subprocess.run([str(mutant)]).returncode != expected:
            raise RuntimeError(
                "Mutation was not detected by its expected oracle: " + name
            )
print(
    "Freestanding CPU contracts passed: 32 hardware flag comparisons, 8 repeat/direction cases, zero-count/fault isolation, explicit interrupt-service rejection; four causal mutations detected."
)
