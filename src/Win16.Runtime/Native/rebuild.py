#!/usr/bin/env python3
"""Reproduce the freestanding Win64 helper/acceptance artifact (GCC + GNU binutils).
The application and tests consume the embedded result and do not require a C toolchain.
"""
import argparse
import sys

sys.dont_write_bytecode = True
from generate_service_bindings import render, render_gui_plans
from windows_unwind import windows_unwind
from native_source import format_c
import base64
import json
import pathlib
import re
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent
FIXTURES = ROOT.parents[2] / "tests" / "Translator.TestSupport" / "RuntimeFixtures"


def api_bindings_header() -> str:
    apis = json.loads((ROOT.parent / "Win16Apis.json").read_text())
    header = "/* Generated from Win16Apis.json by rebuild.py. */\n"
    for api in apis:
        name = api["Name"]
        header += f"#define SERVICE_{name} {api['ServiceId']}\n"
        for arg in api["Arguments"]:
            header += f"#define ARG_{name}_{arg['Name']} {arg['StackWord']}\n"
    header += "typedef struct { u16 id, offset, cleanup; } ApiBinding;\nstatic const ApiBinding api_bindings[] = {\n"
    for api in apis:
        cleanup = (
            sum(
                4 if a["Type"] in ("Dword", "SignedDword", "FarPointer") else 2
                for a in api["Arguments"]
            )
            if api["CalleeCleanup"]
            else 0
        )
        header += f"    {{{api['ServiceId']}, {api['EntryOffset']}, {cleanup}}},\n"
    header += "};\n"
    return format_c(header)


def validate_embedded_storage(elf: pathlib.Path) -> None:
    sections = subprocess.check_output(["readelf", "-SW", str(elf)], text=True)
    for section, size in re.findall(
        r"\[\s*\d+\]\s+(\.(?:bss|data)(?:\.\S+)?)\s+\S+\s+[0-9a-fA-F]+\s+[0-9a-fA-F]+\s+([0-9a-fA-F]+)",
        sections,
    ):
        if int(size, 16):
            raise RuntimeError(
                f"Unembedded mutable native storage: {section} ({int(size,16)} bytes); use Runtime-owned storage"
            )


def rebuild_variant(variant: str) -> None:
    if (ROOT / "task_import_dispatch.h").read_text() != render():
        raise RuntimeError("Stale service dispatch; run generate_service_bindings.py")
    if (ROOT / "task_gui_plans.h").read_text() != render_gui_plans():
        raise RuntimeError("Stale GUI plans; run generate_service_bindings.py")
    whole_task = variant == "whole-task"
    keyboard = variant == "input"
    scene = variant in ("scene", "input")
    paint = variant in ("paint", "scene", "input")
    window = variant in ("window", "paint", "scene", "input")
    application = variant in ("application", "window", "paint", "scene", "input")
    ne = variant in (
        "whole-task",
        "ne",
        "application",
        "window",
        "paint",
        "scene",
        "input",
    )
    graphics = variant not in (
        "whole-task",
        "baseline",
        "ne",
        "application",
        "window",
        "paint",
        "scene",
        "input",
    )
    complete = variant == "complete"
    with tempfile.TemporaryDirectory() as directory:
        tmp = pathlib.Path(directory)
        (tmp / "api_bindings.h").write_text(api_bindings_header())
        if window:
            window_header = "/* Generated native-only window contracts. */\n"
            for api in (
                json.loads((FIXTURES / "Win16WindowApis.json").read_text())
                + (
                    json.loads((FIXTURES / "Win16PaintApis.json").read_text())
                    if paint
                    else []
                )
                + (
                    json.loads((FIXTURES / "Win16SceneApis.json").read_text())
                    if scene
                    else []
                )
                + (
                    json.loads((FIXTURES / "Win16InputApis.json").read_text())
                    if keyboard
                    else []
                )
            ):
                window_header += f"#define WINDOW_{api['Name']} {api['EntryOffset']}\n"
                for arg in api["Arguments"]:
                    window_header += (
                        f"#define WARG_{api['Name']}_{arg['Name']} {arg['StackWord']}\n"
                    )
            (tmp / "window_api_bindings.h").write_text(window_header)
        subprocess.run(
            [
                "gcc",
                "-c",
                "-O2",
                "-fpie",
                "-fvisibility=hidden",
                "-ffreestanding",
                "-fno-builtin",
                "-fno-stack-protector",
                "-fstack-clash-protection",
                *(
                    [
                        "-fasynchronous-unwind-tables",
                        "-fno-shrink-wrap",
                        "-fno-shrink-wrap-separate",
                        "-fomit-frame-pointer",
                        "-maccumulate-outgoing-args",
                    ]
                    if whole_task
                    else ["-fno-asynchronous-unwind-tables", "-fno-unwind-tables"]
                ),
                "-fno-jump-tables",
                "-fno-tree-loop-distribute-patterns",
                "-mno-red-zone",
                "-mgeneral-regs-only",
                "-I",
                str(tmp),
                *(["-DGRAPHICS_ACCEPTANCE"] if graphics else []),
                *(["-DNE_RUNTIME"] if ne else []),
                *(["-DNE_WHOLE_TASK"] if whole_task else []),
                *(["-DNE_APPLICATION"] if application else []),
                *(["-DNE_WINDOW"] if window else []),
                *(["-DNE_PAINT"] if paint else []),
                *(["-DNE_SCENE"] if scene else []),
                *(["-DNE_INPUT"] if keyboard else []),
                *(["-DCALLBACK_ACCEPTANCE"] if complete else []),
                str(ROOT / ("runtime.c" if whole_task else "acceptance.c")),
                "-o",
                str(tmp / "runtime.o"),
            ],
            check=True,
        )
        # Absolute/GOT address loads are not valid after copying the helper blob to a new base.
        # Require explicit hidden declarations for translated code pointers so GCC emits RIP-relative LEA.
        relocation_text = subprocess.check_output(
            ["readelf", "-rW", str(tmp / "runtime.o")], text=True
        )
        kinds = set(re.findall(r"\bR_X86_64_[A-Z0-9_]+\b", relocation_text))
        unsupported = kinds - {"R_X86_64_PC32", "R_X86_64_PLT32"}
        if unsupported:
            raise RuntimeError(
                f"Non-relative native helper relocations: {sorted(unsupported)}"
            )
        targets = (
            []
            if ne
            else (
                ["FindMonster", "GrowArray", "TurnLeft"]
                + (["IsPointVisible", "InvertViewportTile"] if graphics else [])
                + (
                    ["ENUMCALLBACK", "STOPCALLBACK", "VALUEWNDPROC", "SAMPLEWNDPROC"]
                    if complete
                    else []
                )
            )
        )
        imports = [
            item
            for item in json.loads((ROOT / "WindowsImports.json").read_text())
            if graphics and (item["Scope"] == "graphics" or complete)
        ]
        if ne:
            imports = [
                {"Module": "KERNEL32.dll", "Name": name, "Symbol": "native_" + name}
                for name in (
                    ("HeapCreate", "HeapAlloc", "HeapFree", "HeapDestroy")
                    + (
                        (
                            "OpenFile",
                            "_lclose",
                            "_lread",
                            "_llseek",
                            "GetPrivateProfileIntA",
                            "GetPrivateProfileStringA",
                            "WritePrivateProfileStringA",
                            "GetWindowsDirectoryA",
                            "GetModuleFileNameA",
                            "GetShortPathNameA",
                        )
                        if whole_task
                        else ()
                    )
                )
            ]
        if whole_task:
            imports.append(
                {
                    "Module": "USER32.dll",
                    "Name": "CreateDialogIndirectParamW",
                    "Symbol": "package_CreateDialogIndirectParamW",
                }
            )
            imports += [
                {"Module": "KERNEL32.dll", "Name": name, "Symbol": "package_" + name}
                for name in (
                    "GetEnvironmentVariableA",
                    "CreateFileA",
                    "WriteFile",
                    "SetCurrentDirectoryA",
                    "SetFilePointerEx",
                )
            ]
        if whole_task:
            for plan in json.loads((ROOT.parent / "Win16GuiPlans.json").read_text()):
                library = (
                    "KERNEL32"
                    if plan["Module"] == "GDI" and plan["Ordinal"] == 128
                    else "GDI32" if plan["Module"] == "GDI" else "USER32"
                )
                imports.append(
                    {
                        "Module": library + ".dll",
                        "Name": plan["NativeName"],
                        "Symbol": "gui_native_" + plan["NativeName"],
                    }
                )
        if whole_task:
            imports += json.loads((ROOT / "UiNativeFunctions.json").read_text())
        if application:
            imports += [
                {"Module": module, "Name": name, "Symbol": "native_" + name}
                for module, names in (
                    (
                        "KERNEL32.dll",
                        (
                            "GetModuleHandleW",
                            "GetCurrentThreadId",
                            "GetCommandLineW",
                            "OutputDebugStringW",
                            "GetStartupInfoW",
                        ),
                    ),
                    (
                        "USER32.dll",
                        (
                            "PeekMessageW",
                            "GetMessageW",
                            "SetTimer",
                            "KillTimer",
                            "PostQuitMessage",
                            "MessageBoxW",
                        ),
                    ),
                )
                for name in names
            ]
        if window:
            imports += [
                {"Module": "USER32.dll", "Name": name, "Symbol": "native_" + name}
                for name in (
                    "RegisterClassW",
                    "UnregisterClassW",
                    "CreateWindowExW",
                    "DestroyWindow",
                    "ShowWindow",
                    "UpdateWindow",
                    "DefWindowProcW",
                    "SetWindowLongPtrW",
                    "GetWindowLongPtrW",
                    "TranslateMessage",
                    "DispatchMessageW",
                    "InvalidateRect",
                    "GetClientRect",
                    "PostMessageW",
                    "SendMessageW",
                )
            ]
        if paint:
            imports += [
                {"Module": module, "Name": name, "Symbol": "native_" + name}
                for module, names in (
                    (
                        "KERNEL32.dll",
                        (
                            "CreateFileW",
                            "WriteFile",
                            "ReadFile",
                            "GetFileSizeEx",
                            "CloseHandle",
                        ),
                    ),
                    (
                        "bcrypt.dll",
                        (
                            "BCryptOpenAlgorithmProvider",
                            "BCryptHash",
                            "BCryptCloseAlgorithmProvider",
                        ),
                    ),
                    ("USER32.dll", ("BeginPaint", "EndPaint")),
                    (
                        "GDI32.dll",
                        (
                            "SetDIBitsToDevice",
                            "CreateCompatibleDC",
                            "DeleteDC",
                            "CreateDIBSection",
                            "SelectObject",
                            "DeleteObject",
                            "IntersectClipRect",
                            "SelectClipRgn",
                            "GdiFlush",
                        ),
                    ),
                )
                for name in names
            ]
        if scene:
            imports += [
                {"Module": "GDI32.dll", "Name": name, "Symbol": "native_" + name}
                for name in ("BitBlt", "PatBlt", "StretchDIBits", "GetStockObject")
            ]
            imports += [
                {"Module": "USER32.dll", "Name": name, "Symbol": "native_" + name}
                for name in (
                    "AdjustWindowRect",
                    "GetParent",
                    "ScrollWindow",
                    "ValidateRect",
                )
            ]
        if keyboard:
            imports += [
                {"Module": "USER32.dll", "Name": name, "Symbol": "native_" + name}
                for name in (
                    "GetDC",
                    "ReleaseDC",
                    "SetFocus",
                    "GetFocus",
                    "DrawTextA",
                    "LoadCursorW",
                    "SetCursor",
                    "SetCapture",
                    "ReleaseCapture",
                    "SetWindowTextW",
                    "EnableMenuItem",
                    "CreateMenu",
                    "AppendMenuW",
                    "SetMenu",
                    "DestroyMenu",
                    "GetWindowRect",
                    "SetWindowPos",
                )
            ]
        if keyboard:
            imports += [
                {"Module": "GDI32.dll", "Name": name, "Symbol": "native_" + name}
                for name in (
                    "CreateFontIndirectA",
                    "GetDeviceCaps",
                    "SetTextColor",
                    "SetBkColor",
                    "SetBkMode",
                    "GetTextMetricsA",
                )
            ]
            imports += [
                {"Module": "KERNEL32.dll", "Name": "MulDiv", "Symbol": "native_MulDiv"}
            ]
        bindings = [("translated_" + name, name) for name in targets] + [
            (item["Symbol"], item["Symbol"]) for item in imports
        ]
        assembly = ".text\n" + "".join(
            f".global {symbol}\n{symbol}:\n.byte 0xe9\n.global fixup_{name}\nfixup_{name}: .long 0\n"
            for symbol, name in bindings
        )
        (tmp / "targets.s").write_text(assembly)
        subprocess.run(
            ["gcc", "-c", str(tmp / "targets.s"), "-o", str(tmp / "targets.o")],
            check=True,
        )
        (tmp / "layout.ld").write_text(
            "SECTIONS { . = 0; .text : { *(.text*) *(.rodata*) } .eh_frame : { *(.eh_frame*) } /DISCARD/ : { *(.note*) *(.comment*) } }"
        )
        subprocess.run(
            [
                "ld",
                "-T",
                str(tmp / "layout.ld"),
                str(tmp / "runtime.o"),
                str(tmp / "targets.o"),
                "-o",
                str(tmp / "runtime.elf"),
            ],
            check=True,
        )
        # Exported blobs embed .text/.rodata only. Mutable globals would point
        # outside the copied allocation and could corrupt adjacent translated code.
        validate_embedded_storage(tmp / "runtime.elf")
        subprocess.run(
            [
                "objcopy",
                "-O",
                "binary",
                "--only-section=.text",
                str(tmp / "runtime.elf"),
                str(tmp / "runtime.bin"),
            ],
            check=True,
        )
        symbols = {
            parts[2]: int(parts[0], 16)
            for line in subprocess.check_output(
                ["nm", "--defined-only", str(tmp / "runtime.elf")], text=True
            ).splitlines()
            if len(parts := line.split()) == 3
        }
        encoded = base64.b64encode((tmp / "runtime.bin").read_bytes()).decode()
        exports = [
            "acceptance",
            "__win16_read_word",
            "__win16_write_word",
            "__win16_call_service",
            "__win16_read_byte",
            "__win16_read_dword",
            "__win16_write_byte",
            "__win16_write_dword",
            "__win16_string",
            "__win16_divide",
            "__win16_interrupt",
            "__win16_flow_transfer",
            "__win16_program_invoke",
            "__win16_call_target",
            "__win16_tail_target",
            "__win16_dispatch_fault",
            "__win16_callback_invoke",
        ]
        if whole_task:
            exports += sorted(
                name
                for name in symbols
                if name.startswith("__win16_") and name not in exports
            )
        if ne:
            exports = (
                []
                if whole_task
                else ["ne_application_entry" if application else "ne_entry"]
            ) + [
                name
                for name in exports
                if name.startswith("__win16_") and name != "__win16_callback_invoke"
            ]
        class_name = (
            "NativeWholeTaskRuntime"
            if whole_task
            else (
                "NativeInputRuntime"
                if keyboard
                else (
                    "NativeSceneRuntime"
                    if scene
                    else (
                        "NativePaintRuntime"
                        if paint
                        else (
                            "NativeWindowRuntime"
                            if window
                            else (
                                "NativeApplicationRuntime"
                                if application
                                else (
                                    "NativeNeRuntime"
                                    if ne
                                    else (
                                        "NativeWindowsCallbackRuntime"
                                        if complete
                                        else (
                                            "NativeGraphicsRuntime"
                                            if graphics
                                            else "NativeAcceptanceRuntime"
                                        )
                                    )
                                )
                            )
                        )
                    )
                )
            )
        )
        if graphics:
            exports.append("graphics_acceptance")
        if complete:
            exports.extend(["callback_acceptance", "windows_callback_acceptance"])
        source = "runtime.c" if whole_task else "acceptance.c"
        output = f"// Generated by Native/rebuild.py from {source}; do not edit the bytes.\nnamespace Win16_Runtime;\n\npublic static class {class_name}\n{{\n"
        output += (
            "    public static byte[] Bytes => System.Convert.FromBase64String(\n"
            + "\n".join(
                '        "'
                + encoded[i : i + 120]
                + '"'
                + (" +" if i + 120 < len(encoded) else ");")
                for i in range(0, len(encoded), 120)
            )
            + "\n"
        )
        output += "    public static System.Collections.Generic.IReadOnlyDictionary<string, int> Exports { get; } = new System.Collections.Generic.Dictionary<string, int>\n    {\n"
        output += (
            "".join(f'        ["{name}"] = {symbols[name]},\n' for name in exports)
            + "    };\n"
        )
        output += "    public static System.Collections.Generic.IReadOnlyDictionary<string, int> TargetFixups { get; } = new System.Collections.Generic.Dictionary<string, int>\n    {\n"
        output += (
            "".join(
                f'        ["{name}"] = {symbols["fixup_"+name]},\n'
                for _, name in bindings
            )
            + "    };\n"
        )
        output += "    public static System.Collections.Generic.IReadOnlyList<(string Module, string Name, string Symbol)> Imports { get; } = new (string, string, string)[]\n    {\n"
        output += (
            "".join(
                f'        ("{item["Module"]}", "{item["Name"]}", "{item["Symbol"]}"),\n'
                for item in imports
            )
            + "    };\n}\n"
        )
        if whole_task:
            ranges = windows_unwind(tmp / "runtime.elf")
            output = (
                output[:-2]
                + "    public static System.Collections.Generic.IReadOnlyList<(int Start, int End, byte[] Info)> Unwind { get; } = new (int, int, byte[])[]\n    {\n"
            )
            for start, end, info in ranges:
                output += f'        ({start}, {end}, System.Convert.FromHexString("{info.hex()}")),\n'
            output += "    };\n}\n"
        destination = ROOT.parent if whole_task else FIXTURES
        (destination / (class_name + ".g.cs")).write_text(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--production",
        action="store_true",
        help="Build only the authoritative fixture-free whole-task payload",
    )
    options = parser.parse_args()
    variants = (
        ("whole-task",)
        if options.production
        else (
            "baseline",
            "graphics",
            "complete",
            "ne",
            "application",
            "window",
            "paint",
            "scene",
            "input",
            "whole-task",
        )
    )
    for variant in variants:
        rebuild_variant(variant)
