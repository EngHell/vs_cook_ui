#!/usr/bin/env bash
# Build the mod and launch the local game copy (../base_game/) with it loaded.
# Extra arguments are passed to the game, e.g. ./run.sh --openWorld "test"
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

dotnet build "$ROOT/CookUi/CookUi.csproj"

cd "$ROOT/../base_game"
# Other mods to test against (e.g. A Culinary Artillery) go in ../base_game/ExtraMods;
# base_game/Mods is reserved for the game's own mods.
MOD_PATHS=("$ROOT/CookUi/bin/Debug/Mods")
[ -d ExtraMods ] && MOD_PATHS+=("$PWD/ExtraMods")

exec dotnet Vintagestory.dll --tracelog \
  --addModPath "${MOD_PATHS[@]}" \
  --addOrigin "$ROOT/CookUi/assets" \
  "$@"
