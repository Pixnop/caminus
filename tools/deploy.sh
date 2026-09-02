#!/usr/bin/env bash
# Build Release + copie du zip dans le dossier Mods d'un profil de test.
# Usage : tools/deploy.sh [dossier Mods]   (défaut : $CAMINUS_MODS_DIR ou ~/.config/VSLInstallations/test-caminus/Mods)
set -euo pipefail
cd "$(dirname "$0")/.."
export VINTAGE_STORY="${VINTAGE_STORY_1227:-$HOME/.config/VSLGameVersions/1.22.7}"
MODS="${1:-${CAMINUS_MODS_DIR:-$HOME/.config/VSLInstallations/test-caminus/Mods}}"
dotnet build -c Release
mkdir -p "$MODS"
rm -f "$MODS"/caminus_*.zip
cp dist/caminus_*.zip "$MODS/"
echo "Déployé : $(ls "$MODS"/caminus_*.zip)"
