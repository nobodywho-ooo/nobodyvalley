#!/usr/bin/env bash
# Start (or restart) a single-player test instance on a copy of a save, for trying out the mod
# without touching the real co-op session. Usage: scripts/test-instance.sh [source save folder]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME_DIR="${NV_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS}"
SAVES="$HOME/.config/StardewValley/Saves"
SRC="${1:-Nowhere_454507043}"
ID="${SRC##*_}"
TEST="NVTest_$ID"

for pid in $(pgrep -x StardewModdingAPI || true); do
    if ps eww "$pid" | grep -q "NOBODYVALLEY_LOAD=$TEST"; then kill "$pid"; fi
done
sleep 1

# Fresh copy of the save each time (folder and file name must match).
rm -rf "${SAVES:?}/$TEST"
cp -R "$SAVES/$SRC" "$SAVES/$TEST"
mv "$SAVES/$TEST/$SRC" "$SAVES/$TEST/$TEST"
rm -f "$SAVES/$TEST/${SRC}_old" "$SAVES/$TEST/SaveGameInfo_old"

mkdir -p "$ROOT/logs"
# stdin is a FIFO opened read-write, so it blocks instead of hitting EOF (SMAPI's console thread
# spins at 100% CPU on /dev/null; see start-demo.sh).
rm -f "$ROOT/logs/.test.stdin" && mkfifo "$ROOT/logs/.test.stdin"
(cd "$GAME_DIR" && NOBODYVALLEY_LOAD="$TEST" NOBODYVALLEY_PORT="${NV_PORT:-8790}" NOBODYVALLEY_WINDOW="${NV_WINDOW:-60,60,1280,800}" ./StardewModdingAPI <> "$ROOT/logs/.test.stdin" > "$ROOT/logs/test.log" 2>&1 &)
echo "Started test instance on $TEST (log: logs/test.log). Waiting for it to load..."
for _ in $(seq 1 30); do
    if grep -q "In game as" "$ROOT/logs/test.log" 2>/dev/null; then
        grep "In game as" "$ROOT/logs/test.log" | sed 's/\x1b\[[0-9;]*m//g'
        exit 0
    fi
    sleep 2
done
echo "Timed out; see logs/test.log" >&2
exit 1
