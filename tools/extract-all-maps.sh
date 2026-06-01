#!/usr/bin/env bash
# extract-all-maps.sh — batch-extract every Unity scene (level0..levelN) from a Black Ice install into
# BNAV .navmesh artifacts under maps/. Offline, clean-room: the tool code is committed, the .navmesh
# outputs are game-derived and gitignored. Re-runnable; skips scenes that yield no walkable geometry
# (menus/tutorials) and reports vertex/triangle counts and XZ/Y bounds so you can see which level covers
# which world region.
#
# Usage:
#   tools/extract-all-maps.sh [GAME_DATA_DIR] [OUT_DIR] [MAX_LEVEL]
#
#   GAME_DATA_DIR  the game's "Black Ice_Data" folder (default: the common Steam path).
#   OUT_DIR        where to write levelN.navmesh (default: maps/ at the repo root).
#   MAX_LEVEL      highest level index to try (default: 24).
#
# Examples:
#   tools/extract-all-maps.sh
#   tools/extract-all-maps.sh "/d/Games/Black Ice/Black Ice_Data" maps 24
set -u

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DATA="${1:-/c/Program Files (x86)/Steam/steamapps/common/Black Ice/Black Ice_Data}"
OUT_DIR="${2:-$REPO_ROOT/maps}"
MAX_LEVEL="${3:-24}"

if [ ! -d "$GAME_DATA" ]; then
  echo "game data dir not found: $GAME_DATA" >&2
  echo "pass it as the first argument (the 'Black Ice_Data' folder)." >&2
  exit 2
fi

mkdir -p "$OUT_DIR"

# Build the extractor once (Release) so each level doesn't pay a rebuild.
echo "building MapExtractor (Release)..."
dotnet build "$REPO_ROOT/tools/MapExtractor" -c Release --nologo -v q || { echo "build failed" >&2; exit 1; }
DLL="$REPO_ROOT/tools/MapExtractor/bin/Release/net8.0/MapExtractor.dll"

printf '%-10s %-9s %-9s %s\n' "LEVEL" "VERTS" "TRIS" "RESULT"
extracted=0; empty=0; missing=0
for i in $(seq 0 "$MAX_LEVEL"); do
  scene="$GAME_DATA/level$i"
  out="$OUT_DIR/level$i.navmesh"
  if [ ! -f "$scene" ]; then missing=$((missing+1)); continue; fi

  log="$(dotnet "$DLL" "$scene" "$out" --map-name "level$i" 2>&1)"
  if [ -f "$out" ]; then
    # The tool prints a summary line containing the vert/tri counts; surface them if present.
    v="$(printf '%s' "$log" | grep -oiE '[0-9]+ vert' | grep -oE '[0-9]+' | head -1)"
    t="$(printf '%s' "$log" | grep -oiE '[0-9]+ tri'  | grep -oE '[0-9]+' | head -1)"
    printf '%-10s %-9s %-9s %s\n' "level$i" "${v:-?}" "${t:-?}" "ok -> $out"
    extracted=$((extracted+1))
  else
    # No artifact written → no baked navmesh in that scene (menu/empty). Not an error.
    printf '%-10s %-9s %-9s %s\n' "level$i" "-" "-" "no walkable geometry (skipped)"
    empty=$((empty+1))
  fi
done

echo
echo "done: $extracted extracted, $empty empty/skipped, $missing missing. artifacts in $OUT_DIR"
