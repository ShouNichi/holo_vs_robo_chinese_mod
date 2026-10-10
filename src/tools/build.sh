#!/usr/bin/env bash
#
# Builds HvrChinese.dll.
#
#   tools/build.sh --game-path /path/to/game
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

PY="$ROOT/.venv/bin/python"
if [ ! -x "$PY" ]; then
    PY="python3"
fi

GAME_PATH=""
FULL=0
while [ $# -gt 0 ]; do
    case "$1" in
        --game-path) GAME_PATH="${2:-}"; shift 2 ;;
        --game-path=*) GAME_PATH="${1#*=}"; shift ;;
        -h|--help)
            sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "unknown argument: $1 (use --game-path=DIR)" >&2; exit 1 ;;
    esac
done

# ------------------------------------------------------------ 1/2 references
echo "== 1/2 reference assemblies =="
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

# ------------------------------------------------------------ 2/2 the plugin
echo "== 2/2 build =="
dotnet build "$ROOT/src/HvrChinese.csproj" -c Release -v q --nologo

OUT="$ROOT/src/bin/Release/netstandard2.1"
echo
echo "Done. Ship these two files:"
echo "  $OUT/HvrChinese.dll"
echo "  $OUT/NotoSansSC-subset.otf"


