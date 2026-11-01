"""Shared formatting and whitespace-independent native mutation operations."""

from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parent
_TOKEN = re.compile(
    r"(?P<comment>//[^\n]*|/\*[\s\S]*?\*/)|"
    r'(?:u8|u|U|L)?(?:"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\')|'
    r"[A-Za-z_]\w*|0[xX][0-9A-Fa-f]+[uUlL]*|\d+[uUlL]*|"
    r"<<=|>>=|->|==|!=|<=|>=|&&|\|\||<<|>>|\+\+|--|\+=|-=|\*=|/=|\S"
)


def replace_c(source: str, before: str, after: str) -> str:
    """Apply one required causal mutation without depending on source whitespace."""
    expected = [token[0] for token in _TOKEN.finditer(before) if not token["comment"]]
    if not expected:
        raise ValueError("A native mutation must contain C tokens")
    tokens = [token for token in _TOKEN.finditer(source) if not token["comment"]]
    for start in range(len(tokens) - len(expected) + 1):
        candidate = tokens[start : start + len(expected)]
        if [token[0] for token in candidate] == expected:
            return (
                source[: candidate[0].start()] + after + source[candidate[-1].end() :]
            )
    raise ValueError("Native mutation did not apply: " + before)


def format_c(source: str) -> str:
    """Keep metadata-generated headers consistent with the repository C style."""
    return subprocess.run(
        [
            "clang-format",
            "--style={BasedOnStyle: InheritParentConfig, ColumnLimit: 100}",
            "--assume-filename=" + str(ROOT / "generated.h"),
        ],
        input=source,
        text=True,
        stdout=subprocess.PIPE,
        check=True,
    ).stdout
