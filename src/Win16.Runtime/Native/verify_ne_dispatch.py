#!/usr/bin/env python3
"""Run native service contracts through production import dispatch, not direct adapters."""
import json
import re
import subprocess
import tempfile
import sys

sys.dont_write_bytecode = True
from native_source import replace_c
from rebuild import ROOT, api_bindings_header
from generate_service_bindings import render

if (ROOT / "task_import_dispatch.h").read_text() != render():
    raise RuntimeError("Stale production service dispatch")
rows = json.loads((ROOT.parent / "Win16TaskServiceBindings.json").read_text())
modules = {"KERNEL": 1, "USER": 2, "GDI": 3, "WEP4UTIL": 4}
routes = {}
for row in rows:
    routes.setdefault(row["NativeExport"], []).append(row)
with tempfile.TemporaryDirectory(prefix="chips-ne-dispatch-") as directory:
    tmp = ROOT.__class__(directory)
    (tmp / "api_bindings.h").write_text(api_bindings_header())
    (tmp / "acceptance.c").write_text((ROOT / "acceptance.c").read_text())
    (tmp / "runtime_common.h").write_text((ROOT / "runtime_common.h").read_text())
    replaced = 0
    for source in ROOT.glob("*_contract.h"):
        text = source.read_text()
        for export, bindings in routes.items():
            kind = bindings[0]["Invocation"]
            if kind == "State":
                pattern = re.escape(export) + r"\(\s*&r,\s*&s\s*\)"
                text, count = re.subn(
                    pattern, "__win16_import_dispatch(&r,&s,2,471)", text
                )
            elif kind == "ModuleOrdinal":
                text, count = re.subn(
                    re.escape(export) + r"\(\s*&r,\s*&s,",
                    "__win16_import_dispatch(&r,&s,",
                    text,
                )
            else:
                module_ids = {modules[b["Identity"].split(".")[0]] for b in bindings}
                if len(module_ids) != 1:
                    raise RuntimeError("Ordinal-only helper spans multiple modules")
                module = next(iter(module_ids))
                text, count = re.subn(
                    re.escape(export) + r"\(\s*&r,\s*&s,",
                    f"__win16_import_dispatch(&r,&s,{module},",
                    text,
                )
            replaced += count
        (tmp / source.name).write_text(text)
    if replaced < 50:
        raise RuntimeError("Service contracts did not route through dispatcher")
    # All original conversion/ownership/callback assertions now exercise dispatch.
    source = (
        (ROOT / "ne_import_contract.c")
        .read_text()
        .replace("int main(void)", "static int routed_contracts(void)")
    )
    source += """

int main(void)
{
    int result = routed_contracts();
    if (result)
    {
        return result;
    }
    Runtime r;
    State s;
    zero(&r, sizeof(r));
    zero(&s, sizeof(s));
    s.ax = 0x1234;
    s.dx = 0x5678;
    s.sp = 0x9abc;
    const u16 rejected[][2] = {{0, 3}, {5, 3}, {1, 178}, {2, 178 + 1}, {4, 3}};
    for (u32 i = 0; i < sizeof(rejected) / sizeof(rejected[0]); i++)
    {
        r.error = 0;
        if (__win16_import_dispatch(&r, &s, rejected[i][0], rejected[i][1]) || r.error != 14 ||
            s.ax != 0x1234 || s.dx != 0x5678 || s.sp != 0x9abc)
        {
            return 120;
        }
    }
    r.error = 9;
    if (__win16_import_dispatch(&r, &s, 1, 3) || r.error != 9 || s.ax != 0x1234)
    {
        return 121;
    }
    return 0;
}
"""
    (tmp / "dispatch_contract.c").write_text(source)
    production = render()

    def run(name, header):
        (tmp / "task_import_dispatch.h").write_text(header)
        executable = tmp / name
        subprocess.run(
            [
                "gcc",
                "-O2",
                "-DNE_SERVICE_DISPATCH",
                "-ffunction-sections",
                "-fdata-sections",
                "-Wl,--gc-sections",
                "-I",
                str(tmp),
                "-I",
                str(ROOT),
                str(tmp / "dispatch_contract.c"),
                "-o",
                str(executable),
            ],
            check=True,
        )
        return subprocess.run([str(executable)]).returncode

    if code := run("production", production):
        raise RuntimeError(f"Production dispatch contract failed: {code}")
    mutations = [
        ("missing-route", "case 0x00010003u:", "case 0x00010004u:"),
        (
            "module-confusion",
            "case 0x00030001u: return __win16_gui_scalar(r,s,module,ordinal);",
            "case 0x00030001u: return __win16_gui_scalar(r,s,2,ordinal);",
        ),
        ("constant-succeeds", "default:r->error=14;return 0;", "default:return 0;"),
        ("lost-error", "if(r->error){return 0;}", "r->error=0;"),
        (
            "wrong-cleanup-route",
            "case 0x0002002au: return __win16_gui_scalar(r,s,module,ordinal);",
            "case 0x0002002au: return __win16_gui_scalar(r,s,module,43);",
        ),
    ]
    for name, before, after in mutations:
        if run(name, replace_c(production, before, after)) == 0:
            raise RuntimeError("Dispatch mutant escaped: " + name)
print(
    f"Native production import dispatch passed: {replaced} routed contract call sites; constants/unknown modules rejected; five causal mutations detected. Windows backends remain unverified."
)
