#!/usr/bin/env python3
"""Retain separate causal native-contract evidence for the native application release gate.

Run on the development host with Python 3, GCC and AddressSanitizer. This does not
replace actual Windows payload/application checks or an observable parity matrix.
"""
import argparse
import hashlib
import json
import pathlib
import platform
import os
import signal
import subprocess
import sys

sys.dont_write_bytecode = True

root = pathlib.Path(__file__).resolve().parent
fixtures = root.parents[2] / "tests" / "Translator.TestSupport" / "RuntimeFixtures"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--output", type=pathlib.Path, required=True)
parser.add_argument(
    "--resume",
    action="store_true",
    help="Reuse only fingerprint-matching completed runner receipts.",
)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=args.resume)
runners = [
    ("cpu", 4),
    ("control", 6),
    ("memory", 5),
    ("imports", 14),
    ("dispatch", 5),
    ("modules", 1),
]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def fingerprint():
    return dict(
        SourceSha256={
            p.name: digest(p)
            for p in sorted(root.iterdir())
            if p.suffix in (".h", ".c", ".py")
        },
        GeneratedPayloadSha256={
            p.name: digest(p)
            for p in sorted(
                list(root.parent.glob("Native*.g.cs"))
                + list(fixtures.glob("Native*.g.cs"))
            )
        },
        ContractInputSha256={
            p.name: digest(p)
            for p in sorted(
                list(root.parent.glob("*.json")) + list(fixtures.glob("*.json"))
            )
        },
    )


identity = fingerprint()
results = []
checkpoint = args.output / "acceptance.json"
if args.resume:
    previous = json.loads(checkpoint.read_text())
    if any(previous.get(key) != value for key, value in identity.items()):
        raise RuntimeError(
            "Cannot resume: native source, contract inputs or generated payloads changed."
        )
    for receipt in previous["Runners"]:
        log = args.output / (receipt["Name"] + ".log")
        if (
            receipt["ExitCode"] != 0
            or not log.exists()
            or digest(log) != receipt["LogSha256"]
        ):
            raise RuntimeError(
                "Cannot resume an unsuccessful or altered runner receipt."
            )
        results.append(receipt)


def save():
    report = dict(
        Format="NeNativeContracts/v1",
        Host=platform.platform(),
        AllPassed=len(results) == len(runners),
        CausalMutations=sum(r["CausalMutations"] for r in results),
        Runners=results,
        **identity,
    )
    temporary = args.output / "acceptance.tmp"
    temporary.write_text(json.dumps(report, indent=2) + "\n")
    temporary.replace(checkpoint)


save()
for name, mutations in runners:
    if any(r["Name"] == name for r in results):
        print(f"Native {name}: reused matching completed receipt", flush=True)
        continue
    command = [sys.executable, str(root / f"verify_ne_{name}.py")]
    # Bound a hung mutant/compiler and terminate its descendant processes too.
    with subprocess.Popen(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        start_new_session=True,
    ) as process:
        try:
            output, _ = process.communicate(timeout=900)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            output, _ = process.communicate()
            (args.output / f"{name}.log").write_text(output + "\nRunner timed out.\n")
            raise RuntimeError(
                f"Native {name} runner timed out; earlier receipts remain resumable."
            )
    log = args.output / f"{name}.log"
    log.write_text(output)
    if process.returncode:
        raise RuntimeError(
            f"Native {name} contract failed ({process.returncode}); inspect retained log."
        )
    if fingerprint() != identity:
        raise RuntimeError(
            "Native inputs changed during verification; do not accept this run."
        )
    results.append(
        dict(
            Name=name,
            Command=command,
            ExitCode=0,
            CausalMutations=mutations,
            LogSha256=digest(log),
        )
    )
    save()
    print(f"Native {name}: passed ({mutations} causal mutations)", flush=True)
