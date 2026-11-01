#!/usr/bin/env python3
"""Production whole-task KERNEL ABI contracts; fourteen controlled causal mutations."""
import pathlib
import subprocess
import tempfile
import sys

sys.dont_write_bytecode = True
from native_source import replace_c
from rebuild import ROOT, api_bindings_header

with tempfile.TemporaryDirectory(prefix="chips-ne-imports-") as directory:
    tmp = pathlib.Path(directory)
    (tmp / "api_bindings.h").write_text(api_bindings_header())
    for name in [
        "acceptance.c",
        "runtime_common.h",
        "ne_import_contract.c",
        "task_kernel.h",
        "import_contracts.h",
        "task_files.h",
        "task_resources.h",
        "task_profiles.h",
        "task_gui.h",
    ]:
        (tmp / name).write_text((ROOT / name).read_text())

    def run(name):
        executable = tmp / name
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
                str(tmp / "ne_import_contract.c"),
                "-o",
                str(executable),
            ],
            check=True,
        )
        return subprocess.run([str(executable)]).returncode

    code = run("production")
    if code:
        raise RuntimeError(f"Native import contract failed: {code}")
    mutations = [
        (
            "wrong-pascal-width",
            "import_contracts.h",
            "{15,6,2,2,{2,0},{2,4}}",
            "{15,6,2,2,{2,0},{2,2}}",
            {2, 3},
        ),
        (
            "far-result-truncated",
            "task_kernel.h",
            "if(wideResult){s->dx=(u16)(result>>16);}",
            "if(0){s->dx=(u16)(result>>16);}",
            {3},
        ),
        (
            "locked-free-rejected",
            "task_kernel.h",
            "if(block){block->locks=0;}",
            "if(0){block->locks=0;}",
            {7},
        ),
        ("source-destination-swapped", "task_kernel.h", "ordinal==88?1:0", "0", {14}),
        (
            "unknown-service-succeeds",
            "task_kernel.h",
            "else{r->error=14;return 0;}",
            "else{return 0;}",
            {19},
        ),
        (
            "file-handle-leaks",
            "task_files.h",
            "files->closeFile(files->context,record->native);r->release",
            "0;r->release",
            {33},
        ),
        (
            "resource-count-lost",
            "task_resources.h",
            "if(entry->uses){entry->uses--;}",
            "entry->uses=0;",
            {7},
        ),
        (
            "profile-default-unsigned",
            "task_profiles.h",
            "(short)args[2]",
            "(int)args[2]",
            {36},
        ),
        (
            "gui-signed-coordinate-lost",
            "task_gui.h",
            "args[i]=(short)value",
            "args[i]=value",
            {61},
        ),
        (
            "gui-dword-result-truncated",
            "task_gui.h",
            "if(p->result==2){s->dx",
            "if(0){s->dx",
            {62},
        ),
        (
            "gui-native-handle-truncated",
            "task_gui.h",
            "h->native=native;",
            "h->native=(u16)native;",
            {61},
        ),
        (
            "gui-free-on-failure",
            "task_gui.h",
            "p->release>=0&&native",
            "p->release>=0",
            {66},
        ),
        ("gui-borrowed-delete", "task_gui.h", "((type&32)&&!h->owned)", "(0)", {68}),
        ("gui-muldiv-overflow", "task_gui.h", "value>=-32768&&value<=32767", "1", {70}),
    ]
    for name, file, before, after, expected in mutations:
        original = (ROOT / file).read_text()
        (tmp / file).write_text(replace_c(original, before, after))
        code = run(name)
        (tmp / file).write_text(original)
        if code not in expected:
            raise RuntimeError(f"Mutant escaped controlled oracle: {name}: {code}")
print(
    "Native import contracts passed: physical Pascal arguments, near/far results, locked free, local heap growth/moving resize, original-pointer strings, segment locking, environment ownership, explicit unsupported rejection, NE resource counts, file-provider ownership, profile marshalling; fourteen causal mutants detected."
)
