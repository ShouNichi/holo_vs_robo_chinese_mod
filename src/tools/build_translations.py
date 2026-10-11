#!/usr/bin/env python3
"""
Compile translations/src/*.json into the flat files the plugin embeds.

The plugin reads five flat files, each a list of {key, value} pairs — compact,
but with no idea of where a string appears or what the game actually says. The
sources in translations/src/ carry that (scene, context, the English and
Japanese, the Chinese) and are what a translator edits; this script turns them
back into the flat files:

    strings_chinese.json   key = the string-table path
    enums_chinese.json     enum member name -> Chinese
    dialogue_chinese.json  key = the English line
    almanac_chinese.json   almanac.<field>.<tower id | value>
    misc_chinese.json      achievement/shop/ui-text keys

The sources are JSON with one addition: a line whose first non-whitespace
character is '#' is a comment and is ignored, so notes can be dropped anywhere.
For a note that belongs to one entry use its "note" field, which survives a
re-collect; for anything about the file as a whole, a '#' line.

Run it after every edit; the build embeds whatever these files contain.

    build_translations.py [--check] [--translations DIR]

--check only reports whether the committed files are up to date, without
writing, so it can be used to catch a forgotten compile step.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import translation_json

SRC = {
    "strings": "strings_chinese.json",
    "enums": "enums_chinese.json",
    "dialogue": "dialogue_chinese.json",
    "almanac": "almanac_chinese.json",
    "misc": "misc_chinese.json",
}


def load(path: Path) -> list:
    data = translation_json.load(path)
    return data if isinstance(data, list) else []


def flat_strings(entries: list, key_of) -> list:
    """{key, value} pairs.

    A key can show up more than once in the sources — a line the game repeats,
    or a value several towers share — and as long as they agree that is fine: the
    plugin looks the key up once. Disagreement is a real mistake and stops the
    build.
    """
    out: dict = {}
    for entry in entries:
        key = key_of(entry)
        if key is None:
            continue
        value = entry.get("zh", "")
        if not value:
            continue  # untranslated on purpose: the plugin keeps the English
        if key in out and out[key] != value:
            raise SystemExit(f"conflicting translations for {key!r}: "
                             f"{out[key]!r} vs {value!r}")
        out[key] = value
    return [{"key": k, "value": v} for k, v in out.items()]


def report(name: str, entries: list, produced: list) -> None:
    untranslated = [e for e in entries if not e.get("zh", "")]
    no_english = [e for e in entries if not e.get("en", "")]
    no_japanese = [e for e in entries if not e.get("ja", "")]
    print(f"  {name:9s} {len(entries):4d} entries -> {len(produced):4d} translated"
          f"   ({len(untranslated)} untranslated,"
          f" {len(no_english)} without English, {len(no_japanese)} without Japanese)")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--translations", default="translations")
    parser.add_argument("--check", action="store_true",
                        help="only report whether the committed files are current")
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    src_dir = root / args.translations / "src"
    if not src_dir.is_dir():
        raise SystemExit(f"{src_dir} not found — run tools/collect_translation_sources.py first")

    strings = load(src_dir / "strings.json")
    enums = load(src_dir / "enums.json")
    dialogue = load(src_dir / "dialogue.json")
    almanac = load(src_dir / "almanac.json")
    misc = load(src_dir / "misc.json")

    outputs = {
        "strings_chinese.json": {
            "locale": "zh",
            "count": 0,
            "strings": flat_strings(strings, lambda e: e.get("key")),
        },
        "enums_chinese.json": {
            "locale": "zh",
            "count": 0,
            "names": flat_strings(
                enums,
                lambda e: None if not (e.get("enum") and e.get("name")) else
                (e["enum"], e["name"])),
        },
        "dialogue_chinese.json": {
            "locale": "zh",
            "count": 0,
            "strings": flat_strings(dialogue, lambda e: e.get("en")),
        },
        "almanac_chinese.json": {
            "locale": "zh",
            "count": 0,
            "strings": flat_strings(almanac, almanac_key),
        },
        "misc_chinese.json": {
            "locale": "zh",
            "count": 0,
            "strings": flat_strings(misc, lambda e: e.get("key")),
        },
    }

    # enums_chinese.json keeps the {enum, name, value} shape the plugin reads.
    shaped = []
    for entry in outputs["enums_chinese.json"]["names"]:
        enum_name, member = entry["key"]
        shaped.append({"enum": enum_name, "name": member, "value": entry["value"]})
    outputs["enums_chinese.json"]["names"] = shaped

    for filename, payload in outputs.items():
        key_field = "names" if "names" in payload else "strings"
        payload["count"] = len(payload[key_field])

    print("translations compiled from translations/src/:")
    report("strings", strings, outputs["strings_chinese.json"]["strings"])
    report("enums", enums, outputs["enums_chinese.json"]["names"])
    report("dialogue", dialogue, outputs["dialogue_chinese.json"]["strings"])
    report("almanac", almanac, outputs["almanac_chinese.json"]["strings"])
    report("misc", misc, outputs["misc_chinese.json"]["strings"])

    if args.check:
        stale = []
        for filename, payload in outputs.items():
            path = root / args.translations / filename
            if not path.exists():
                stale.append(filename)
                continue
            if translation_json.load(path) != payload:
                stale.append(filename)
        if stale:
            print(f"\n{len(stale)} file(s) are out of date: {', '.join(stale)}")
            print("run tools/build_translations.py to update them")
            return 1
        print("\nall translation files are up to date")
        return 0

    for filename, payload in outputs.items():
        path = root / args.translations / filename
        translation_json.save(path, payload)
    print(f"\nwrote {len(outputs)} files to {root / args.translations}")
    return 0


def almanac_key(entry: dict):
    """almanac.<field>.<tower id> for free text, almanac.<field>.<value> for the
    closed sets the game picks from."""
    field = entry.get("field")
    if not field:
        return None
    if entry.get("kind") == "value":
        return f"almanac.{field}.{entry.get('en', '')}"
    tower = entry.get("tower")
    if tower is None:
        return None
    return f"almanac.{field}.{tower}"


if __name__ == "__main__":
    sys.exit(main())
