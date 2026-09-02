#!/usr/bin/env bash
# Build Release + copy the zip into a test profile's Mods folder.
# Usage: tools/deploy.sh [Mods folder]   (default: $CAMINUS_MODS_DIR or ~/.config/VSLInstallations/test-caminus/Mods)
set -euo pipefail
cd "$(dirname "$0")/.."
export VINTAGE_STORY="${VINTAGE_STORY_1227:-$HOME/.config/VSLGameVersions/1.22.7}"
MODS="${1:-${CAMINUS_MODS_DIR:-$HOME/.config/VSLInstallations/test-caminus/Mods}}"
dotnet build -c Release
mkdir -p "$MODS"
rm -f "$MODS"/caminus_*.zip
cp dist/caminus_*.zip "$MODS/"
echo "Deployed: $(ls "$MODS"/caminus_*.zip)"
