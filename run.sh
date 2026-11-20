#!/usr/bin/env bash
# Build the mod and launch the local game copy (../base_game/) with it loaded.
# Extra arguments are passed to the game, e.g. ./run.sh --openWorld "test"
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

dotnet build "$ROOT/CookUi/CookUi.csproj"

cd "$ROOT/../base_game"
exec dotnet Vintagestory.dll --tracelog \
  --addModPath "$ROOT/CookUi/bin/Debug/Mods" \
  --addOrigin "$ROOT/CookUi/assets" \
  "$@"
