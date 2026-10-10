#!/usr/bin/env bash
#
# Collects the reference assemblies HvrChinese.dll is compiled against into ./lib.
#
#   tools/fetch-refs.sh --game-path /path/to/game
#
# <game> is an installed (or extracted) copy of the game. It may be the folder
# that contains "Holo vs Robo_Data", the _Data folder itself, or its Managed
# folder — all three are accepted.
#
# The 14 game assemblies are copied from there; BepInEx 6 and HarmonyX are
# downloaded from NuGet. ./lib is git-ignored.
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
LIB="$ROOT/lib/refs"

BEPINEX_VERSION="${BEPINEX_VERSION:-6.0.0-be.788}"
HARMONYX_VERSION="${HARMONYX_VERSION:-2.10.2}"
NUGET_ORG="https://api.nuget.org/v3-flatcontainer"
BEPINEX_FEED="https://nuget.bepinex.dev/v3"

# The game assemblies src/HvrChinese.csproj references.
GAME_ASSEMBLIES=(
    Assembly-CSharp.dll
    Unity.Localization.dll
    Unity.ResourceManager.dll
    Unity.Addressables.dll
    Unity.TextMeshPro.dll
    UnityEngine.dll
    UnityEngine.CoreModule.dll
    UnityEngine.AudioModule.dll
    UnityEngine.ImageConversionModule.dll
    UnityEngine.UI.dll
    UnityEngine.TextRenderingModule.dll
    UnityEngine.TextCoreFontEngineModule.dll
    UnityEngine.JSONSerializeModule.dll
    UnityEngine.InputLegacyModule.dll
)

usage() {
    cat >&2 <<'EOF'
usage: tools/fetch-refs.sh --game-path <game-folder>

  <game-folder>  an installed or extracted copy of the game: the folder holding
                 "Holo vs Robo_Data", the _Data folder itself, or its Managed
                 folder. Defaults to this repository's own game/ folder.
EOF
}

# Resolve a directory that holds Assembly-CSharp.dll.
resolve_managed() {
    local base="$1" cand
    for cand in \
        "$base" \
        "$base/Holo vs Robo_Data" \
        "$base/Holo vs Robo_Data/Managed" \
        "$base/Managed"
    do
        [ -f "$cand/Assembly-CSharp.dll" ] && { echo "$cand"; return 0; }
    done
    # Tolerate other install layouts (different parent folder name, …).
    local found
    found="$(find "$base" -maxdepth 3 -name Assembly-CSharp.dll -printf '%h\n' 2>/dev/null | head -1)"
    [ -n "$found" ] && { echo "$found"; return 0; }
    return 1
}

GAME_PATH=""
while [ $# -gt 0 ]; do
    case "$1" in
        --game-path) GAME_PATH="${2:-}"; shift 2 ;;
        --game-path=*) GAME_PATH="${1#*=}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage; exit 1 ;;
    esac
done

if [ -z "$GAME_PATH" ]; then
    GAME_PATH="$ROOT/game"
fi
if ! MANAGED="$(resolve_managed "$GAME_PATH")"; then
    echo "error: no Assembly-CSharp.dll under $GAME_PATH" >&2
    echo "       expected <game>/Holo vs Robo_Data/Managed" >&2
    exit 1
fi

fetch() { # url destination
    echo "  -> $(basename "$2")"
    curl -sSL --retry 3 --retry-delay 2 -f "$1" -o "$2"
}

extract_ns20() { # nupkg destination-folder
    python3 - "$1" "$2" <<'PY'
import sys, zipfile, os, shutil
pkg, dest = sys.argv[1], sys.argv[2]
os.makedirs(dest, exist_ok=True)
with zipfile.ZipFile(pkg) as z:
    for name in z.namelist():
        if name.startswith("lib/") and "/netstandard2.0/" in name and name.endswith(".dll"):
            with z.open(name) as src, open(os.path.join(dest, os.path.basename(name)), "wb") as dst:
                shutil.copyfileobj(src, dst)
PY
}

mkdir -p "$LIB"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "[1/2] game assemblies"
missing=()
for f in "${GAME_ASSEMBLIES[@]}"; do
    [ -f "$MANAGED/$f" ] || missing+=("$f")
done
if [ ${#missing[@]} -gt 0 ]; then
    echo "error: missing from $MANAGED: ${missing[*]}" >&2
    exit 1
fi
for f in "${GAME_ASSEMBLIES[@]}"; do
    cp -f "$MANAGED/$f" "$LIB/$f"
done
echo "  copied ${#GAME_ASSEMBLIES[@]} assemblies from $MANAGED"

echo "[2/2] BepInEx $BEPINEX_VERSION + HarmonyX $HARMONYX_VERSION (NuGet)"
fetch "$BEPINEX_FEED/package/bepinex.core/$BEPINEX_VERSION/bepinex.core.$BEPINEX_VERSION.nupkg" "$TMP/core.nupkg"
fetch "$BEPINEX_FEED/package/bepinex.unity.mono/$BEPINEX_VERSION/bepinex.unity.mono.$BEPINEX_VERSION.nupkg" "$TMP/mono.nupkg"
extract_ns20 "$TMP/core.nupkg" "$LIB"
extract_ns20 "$TMP/mono.nupkg" "$LIB"
fetch "$NUGET_ORG/HarmonyX/$HARMONYX_VERSION/harmonyx.$HARMONYX_VERSION.nupkg" "$TMP/harmonyx.nupkg"
extract_ns20 "$TMP/harmonyx.nupkg" "$LIB"

echo
echo "Done. $(ls -1 "$LIB" | wc -l) reference assemblies in $LIB"
