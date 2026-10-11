"""Reading and writing the translation sources.

The files in translations/src/ are JSON, but JSON has no comment syntax and a
translation file is exactly the kind of thing that wants a note next to it ("this
is the line the minigame is named after", "the game's own ja is wrong here"). So
a '#' starts a comment that runs to the end of the line, anywhere on the line:

    {
      # the minigame is named after this line
      "en": "Airborne Assault",
      "ja": "親方！空からロボットが！",   # trailing comments work too
      "zh": "老大！天上下机器人了！"
    }

A '#' inside a string value is left alone — the scanner tracks whether it is
inside a string, so "color #ff0000" survives.

For a note that belongs to one entry use its "note" field rather than a comment:
tools/collect_translation_sources.py rewrites these files, and it carries "note"
across while comments are lost.
"""

from __future__ import annotations

import json
from pathlib import Path


def strip_comments(text: str) -> str:
    """Remove '#' comments, leaving string contents untouched."""
    out = []
    i = 0
    length = len(text)
    in_string = False

    while i < length:
        ch = text[i]
        if in_string:
            if ch == "\\" and i + 1 < length:
                out.append(text[i:i + 2])
                i += 2
                continue
            if ch == '"':
                in_string = False
            out.append(ch)
            i += 1
            continue
        if ch == '"':
            in_string = True
            out.append(ch)
            i += 1
            continue
        if ch == "#":
            end = text.find("\n", i)
            if end == -1:
                break
            i = end  # keep the newline itself
            continue
        out.append(ch)
        i += 1

    return "".join(out)


def loads(text: str):
    return json.loads(strip_comments(text))


def load(path: Path):
    if not path.exists():
        return []
    return loads(path.read_text(encoding="utf-8"))


def dumps(data) -> str:
    return json.dumps(data, ensure_ascii=False, indent=1) + "\n"


def save(path: Path, data) -> None:
    path.write_text(dumps(data), encoding="utf-8")
