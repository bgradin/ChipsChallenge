"""Convert the bounded GCC CFA prolog vocabulary to Windows AMD64 UNWIND_INFO.

The authoritative payload is compiled without shrink wrapping, frame pointers,
SIMD or dynamic outgoing stack allocation. Reject any unrepresented CFA rule.
Reference: https://learn.microsoft.com/en-us/cpp/build/exception-handling-x64
"""

import re
import struct
import subprocess

# DWARF and Windows register numbers differ for the original eight registers.
REG = {3: 3, 6: 5, 4: 6, 5: 7, 12: 12, 13: 13, 14: 14, 15: 15}


def windows_unwind(elf):
    frames = subprocess.check_output(["readelf", "-wf", str(elf)], text=True)
    result = []
    for block in re.split(r"\n(?=[0-9a-f]+ )", frames):
        match = re.search(r"FDE .* pc=([0-9a-f]+)\.\.([0-9a-f]+)", block)
        if not match:
            continue
        start, end = (int(n, 16) for n in match.groups())
        at, cfa, operations, pending, epilog = 0, 8, [], {}, False

        def flush():
            nonlocal cfa
            if not pending:
                return
            new = pending.get("cfa", cfa)
            saved = pending.get("saved", [])
            if new < cfa:
                raise RuntimeError("Unexpected CFA decrease in prolog")
            delta = new - cfa
            if delta == 8 and len(saved) == 1 and saved[0][1] == -new:
                operations.append((at, bytes([at, REG[saved[0][0]] << 4])))
                saved = []
            elif delta:
                if delta % 8 or delta < 0:
                    raise RuntimeError("Unaligned stack allocation")
                if delta <= 128:
                    op = bytes([at, ((delta // 8 - 1) << 4) | 2])
                elif delta <= 524280:
                    op = bytes([at, 1]) + struct.pack("<H", delta // 8)
                else:
                    op = bytes([at, 0x11]) + struct.pack("<I", delta)
                operations.append((at, op))
            for reg, offset in saved:
                # CFA-relative saves need the final frame size, filled below.
                operations.append((at, (reg, offset)))
            cfa = new
            pending.clear()

        for line in block.splitlines()[1:]:
            advance = re.search(r"DW_CFA_advance_loc\d*:.* to ([0-9a-f]+)", line)
            if advance:
                flush()
                at = int(advance[1], 16) - start
                continue
            if "DW_CFA_remember_state" in line or "DW_CFA_restore" in line:
                flush()
                epilog = True
                break
            offset = re.search(r"DW_CFA_def_cfa_offset: (\d+)", line)
            if offset:
                new = int(offset[1])
                if new < cfa:
                    flush()
                    epilog = True
                    break
                pending["cfa"] = new
                continue
            saved = re.search(r"DW_CFA_offset: r(\d+) .* at cfa(-?\d+)", line)
            if saved:
                reg, value = map(int, saved.groups())
                if reg not in REG:
                    raise RuntimeError(f"Unsupported saved register {reg}")
                pending.setdefault("saved", []).append((reg, value))
                continue
            if "DW_CFA_" in line and "DW_CFA_nop" not in line:
                raise RuntimeError(f"Unrepresented prolog CFA: {line}")
        if not epilog:
            flush()
        encoded = []
        for pc, op in reversed(operations):
            if pc > 255:
                raise RuntimeError(f"Unwind prolog exceeds 255 bytes at {start:x}+{pc}")
            if isinstance(op, tuple):
                reg, offset = op
                slot = cfa + offset
                if slot < 0 or slot % 8 or slot > 524280:
                    raise RuntimeError("Unsupported register save offset")
                op = bytes([pc, (REG[reg] << 4) | 4]) + struct.pack("<H", slot // 8)
            encoded.append(op)
        codes = b"".join(encoded)
        if len(codes) // 2 > 255:
            raise RuntimeError("Too many unwind codes")
        info = (
            bytes([1, max((pc for pc, _ in operations), default=0), len(codes) // 2, 0])
            + codes
        )
        info += b"\0" * ((-len(info)) % 4)
        result.append((start, end, info))
    if not result:
        raise RuntimeError("No payload CFA function ranges")
    return result
