#!/usr/bin/env python3
"""AMD64 GCC-host execution of the production native control resolver and mutations."""
import pathlib
import subprocess
import tempfile
import sys

sys.dont_write_bytecode = True
from native_source import replace_c
from rebuild import ROOT, api_bindings_header, validate_embedded_storage

with tempfile.TemporaryDirectory(prefix="chips-ne-control-") as directory:
    temporary = pathlib.Path(directory)
    (temporary / "api_bindings.h").write_text(api_bindings_header())
    forbidden = temporary / "forbidden.c"
    forbidden.write_text(
        "int unembedded_mutable = 1; int read_value(void) { return unembedded_mutable; }\n"
    )
    subprocess.run(
        ["gcc", "-c", str(forbidden), "-o", str(temporary / "forbidden.o")], check=True
    )
    try:
        validate_embedded_storage(temporary / "forbidden.o")
    except RuntimeError as error:
        if "Unembedded mutable native storage" not in str(error):
            raise
    else:
        raise RuntimeError("Native rebuild failed to reject unembedded mutable storage")

    def run(source, executable):
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
                str(source),
                "-o",
                str(executable),
            ],
            check=True,
        )
        return subprocess.run([str(executable)]).returncode

    if run(ROOT / "ne_control_contract.c", temporary / "contract"):
        raise RuntimeError("Production native control-flow contract failed")
    (temporary / "ne_control_contract.c").write_text(
        (ROOT / "ne_control_contract.c").read_text()
    )
    (temporary / "acceptance.c").write_text((ROOT / "acceptance.c").read_text())
    (temporary / "runtime_common.h").write_text((ROOT / "runtime_common.h").read_text())
    production = (ROOT / "program_control.h").read_text()
    mutations = [
        (
            "wrong-frame-width",
            replace_c(
                production,
                "if (farTransfer) { push(r,s,s->cs); }",
                "if (!farTransfer) { push(r,s,s->cs); }",
            ),
        ),
        (
            "rounded-address",
            replace_c(
                production,
                "if (candidate == address)",
                "if (candidate == (address & 0xFFFFFFF0))",
            ),
        ),
        ("lost-cleanup", replace_c(production, "s->sp+bytes+value", "s->sp+bytes")),
        (
            "pretend-import-success",
            replace_c(
                production,
                "r->error=b->kind==2?14:8; return;",
                "c->returned=1; return;",
            ),
        ),
        (
            "task-lifetime-quota",
            replace_c(production, "p->maximumTransfers!=0xffffffffu && ", ""),
        ),
        (
            "startup-fabricated-frame",
            replace_c(production, "if (pushFrame) {", "if (1) {"),
        ),
    ]
    for name, mutated in mutations:
        if mutated == production:
            raise RuntimeError("Mutation did not apply: " + name)
        (temporary / "program_control.h").write_text(mutated)
        code = run(temporary / "ne_control_contract.c", temporary / name)
        if code not in range(1, 11):
            raise RuntimeError("Mutation escaped its controlled oracle: " + name)
print(
    "Native control-flow contracts passed: calls/returns, runtime target changes, interior/data rejection, import failure/cleanup, nested callback, bounded loops, table validation; six mutations detected; task lifetime crosses counter wrap without losing fixture budgets; loader startup preserves original stack; unembedded-storage guard verified."
)
