#!/usr/bin/env bash
#
# Builds HvrChinese.dll — the Chinese localization plugin for "Holo vs Robo".
#
#   ./build.sh --game-path /path/to/your/game
#
# <game> is an installed copy of the game: the folder that holds
# "Holo vs Robo_Data", the _Data folder itself, or its Managed folder. Everything
# the build needs is in this folder or downloaded from NuGet — no 7z archive, no
# decompiled game, no Python packages.
#
# Requirements: .NET SDK, curl, python3 (standard library only) and network access
# for the NuGet packages (BepInEx 6 + HarmonyX).
#
# Output: src/bin/Release/netstandard2.1/{HvrChinese.dll,NotoSansSC-subset.otf}
#         -> copy both into <game>/BepInEx/plugins/HvrChinese/
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# The real work lives in tools/build.sh; this wrapper only forwards the arguments
# so the folder can be invoked from anywhere.
exec "$HERE/tools/build.sh" "$@"
