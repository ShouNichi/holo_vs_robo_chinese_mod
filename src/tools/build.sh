#!/usr/bin/env bash
#
# Builds HvrChinese.dll from the translation sources.
#
#   tools/build.sh --game-path /path/to/game
#
# Three steps, in order:
#
#   1. collect the reference assemblies the plugin links against (14 DLLs from
#      the game's Managed folder, plus BepInEx and HarmonyX from NuGet) into
#      lib/refs;
#   2. compile translations/src/*.json into the flat translations/*_chinese.json
#      the plugin embeds — this runs every time, so an edited source is never
#      shipped as stale JSON;
#   3. build the plugin.
#
# --game-path points at an installed or extracted copy of the game: the folder
# holding "Holo vs Robo_Data", the _Data folder itself, or its Managed folder.
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$(dirname "$ROOT")/.dotnet-home}"
if ! command -v dotnet >/dev/null 2>&1; then
    for candidate in /usr/share/dotnet /usr/lib/dotnet /usr/local/share/dotnet; do
        [ -x "$candidate/dotnet" ] && export PATH="$candidate:$PATH" && break
    done
fi

# The translation preprocessor only needs the standard library, so a plain
# python3 is enough; a project venv is used when one exists.
PY="$ROOT/.venv/bin/python"
if [ ! -x "$PY" ]; then
    PY="python3"
fi

GAME_PATH=""
while [ $# -gt 0 ]; do
    case "$1" in
        --game-path) GAME_PATH="${2:-}"; shift 2 ;;
        --game-path=*) GAME_PATH="${1#*=}"; shift ;;
        -h|--help)
            sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "unknown argument: $1 (use --game-path=DIR)" >&2; exit 1 ;;
    esac
done

# ------------------------------------------------------------ 1/3 references
echo "== 1/3 reference assemblies =="
if [ -n "$GAME_PATH" ]; then
    "$HERE/fetch-refs.sh" --game-path "$GAME_PATH"
elif [ -d "$ROOT/game" ]; then
    echo "   game/ already extracted, using it"
    "$HERE/fetch-refs.sh" --game-path "$ROOT/game"
elif [ -f "$ROOT/lib/refs/Assembly-CSharp.dll" ]; then
    echo "   lib/refs already populated, skipping"
else
    echo "error: nothing to build against." >&2
    echo "       pass --game-path /path/to/your/game." >&2
    exit 1
fi

# ----------------------------------------------------------- 2/3 translations
echo "== 2/3 translations =="
"$PY" "$HERE/build_translations.py" --translations "$ROOT/translations"

# ------------------------------------------------------------ 3/3 the plugin
echo "== 3/3 build =="
dotnet build "$ROOT/src/HvrChinese.csproj" -c Release -v q --nologo

OUT="$ROOT/src/bin/Release/netstandard2.1"
echo
echo "Done. Ship these two files:"
echo "  $OUT/HvrChinese.dll"
echo "  $OUT/NotoSansSC-subset.otf"
