#!/usr/bin/env python3
"""Execute production native memory semantics and require each causal mutant to fail."""
import pathlib
import subprocess
import tempfile
import sys

sys.dont_write_bytecode = True
from native_source import replace_c
from rebuild import ROOT, api_bindings_header

with tempfile.TemporaryDirectory(prefix="chips-ne-memory-") as directory:
    tmp = pathlib.Path(directory)
    (tmp / "api_bindings.h").write_text(api_bindings_header())
    for name in [
        "ne_memory_contract.c",
        "acceptance.c",
        "runtime_common.h",
        "memory.h",
        "local_memory.h",
        "program_control.h",
    ]:
        (tmp / name).write_text((ROOT / name).read_text())

    def run(name):
        target = tmp / name
        subprocess.run(
            [
                "gcc",
                "-O2",
                "-ffunction-sections",
                "-fdata-sections",
                "-Wl,--gc-sections",
                "-I",
                str(tmp),
                "-I",
                str(ROOT),
                str(tmp / "ne_memory_contract.c"),
                "-o",
                str(target),
            ],
            check=True,
        )
        return subprocess.run([str(target)]).returncode

    result = run("production")
    if result:
        raise RuntimeError(f"Native memory contract failed at {result}")
    mutations = [
        (
            "code-write-ignored",
            "memory.h",
            "if (s->backing->executable || (s->permissions & 4))",
            "if (0)",
            {3},
        ),
        (
            "detached-alias-resize",
            "memory.h",
            "if (v->backing == old)",
            "if (v == s)",
            {6},
        ),
        (
            "local-contents-lost",
            "local_memory.h",
            "s->bytes[n+i]=s->bytes[pointer+i]",
            "s->bytes[n+i]=0",
            {14},
        ),
        (
            "callback-cpu-not-restored",
            "local_memory.h",
            "*s=saved;return value;",
            "return value;",
            {16, 17},
        ),
        (
            "alias-address-canonicalized",
            "program_control.h",
            "s->cs=(u16)(c.nextAddress>>16)",
            "s->cs=b->segment",
            {18},
        ),
    ]
    for name, file, before, after, expected in mutations:
        original = (ROOT / file).read_text()
        (tmp / file).write_text(replace_c(original, before, after))
        result = run(name)
        (tmp / file).write_text(original)
        if result not in expected:
            raise RuntimeError(
                f"Mutation {name} escaped its controlled oracle: {result}"
            )
print(
    "Native memory contracts passed: aliases, code-write guard, resize/free lifetime, fixed/movable globals, local near pointers, nested DS callbacks, separate SS, complete CPU restoration, persistent storage, full allocator reclamation; five causal mutants detected."
)
