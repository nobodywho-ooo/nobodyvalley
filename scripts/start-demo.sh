#!/usr/bin/env bash
# Start a co-op session with one Stardew Valley instance per agent, tiled in a grid of windows,
# and (optionally) the NobodyWho agents.
#
#   scripts/start-demo.sh             # start the game instances
#   scripts/start-demo.sh --agents    # ...and then run the agents in this terminal
#   scripts/start-demo.sh --build     # rebuild + deploy the mod first
#   scripts/start-demo.sh --stop      # kill all game instances
#
# Configure with environment variables (defaults match our office save):
#   NV_SAVE        save folder to host                     (default: Nowhere_454507774)
#   NV_FARMHANDS   space-separated farmhand names          (default: "Albert Jonathan Marek")
#   NV_DISPLAY     display to tile the windows on; see `swift scripts/screen-bounds.swift list` (default: 0)
#   NV_DASHBOARD_DISPLAY  display for the agent dashboard window (default: a column on NV_DISPLAY)
#   NV_DASHBOARD_WIDTH    width of that column, in points (default: 360)
#   NV_GAME_DIR    folder containing StardewModdingAPI     (default: Steam install)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME_DIR="${NV_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS}"
SAVE="${NV_SAVE:-Nowhere_454507774}"
read -r -a FARMHANDS <<< "${NV_FARMHANDS:-Albert Jonathan Marek}"
LOG_DIR="$ROOT/logs"

BUILD=false
AGENTS=false
for arg in "$@"; do
    case "$arg" in
        --stop)
            # SIGKILL: on SIGTERM the game aborts in its native teardown (Steam networking, OpenAL),
            # and macOS's ReportCrashService then spends minutes at 100% CPU on each crash report.
            # (Nothing is lost: the game only saves overnight.)
            pkill -KILL -x StardewModdingAPI || true
            echo "Stopped."
            exit 0 ;;
        --build) BUILD=true ;;
        --agents) AGENTS=true ;;
        *) echo "unknown option $arg" >&2; exit 2 ;;
    esac
done

if [ ! -x "$GAME_DIR/StardewModdingAPI" ]; then
    echo "SMAPI not found in $GAME_DIR (set NV_GAME_DIR)." >&2
    exit 1
fi

if [ "$BUILD" = true ]; then
    echo "Building and deploying the mod..."
    export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
    dotnet build "$ROOT/mod/NobodyValley" -c Release -p:GamePath="$GAME_DIR" | tail -2
fi
if [ ! -f "$GAME_DIR/Mods/NobodyValley/NobodyValley.dll" ]; then
    echo "The NobodyValley mod isn't installed; run with --build." >&2
    exit 1
fi
if pgrep -x StardewModdingAPI > /dev/null; then
    echo "Game instances are already running; stop them first with: $0 --stop" >&2
    exit 1
fi

# Grid of game windows on the chosen display, leaving a column on the right for the dashboard
# (unless the dashboard goes on its own display).
read -r SX SY SW SH <<< "$(swift "$ROOT/scripts/screen-bounds.swift" "${NV_DISPLAY:-0}")"
DASH_W="${NV_DASHBOARD_WIDTH:-360}"
if [ -n "${NV_DASHBOARD_DISPLAY:-}" ] && [ "${NV_DASHBOARD_DISPLAY}" != "${NV_DISPLAY:-0}" ]; then
    read -r DX DY DW DH <<< "$(swift "$ROOT/scripts/screen-bounds.swift" "$NV_DASHBOARD_DISPLAY")"
    DASHBOARD_WINDOW="$DX,$DY,$DASH_W,$DH"
else
    SW=$((SW - DASH_W))
    DASHBOARD_WINDOW="$((SX + SW)),$SY,$DASH_W,$SH"
fi
COUNT=$((1 + ${#FARMHANDS[@]}))
COLS=1
while [ $((COLS * COLS)) -lt "$COUNT" ]; do COLS=$((COLS + 1)); done
ROWS=$(((COUNT + COLS - 1) / COLS))
TITLE_BAR=28
CELL_W=$((SW / COLS))
CELL_H=$((SH / ROWS))
window_for() { # <index> -> x,y,w,h (content size; the title bar sits above it)
    local i="$1"
    echo "$((SX + (i % COLS) * CELL_W)),$((SY + (i / COLS) * CELL_H + TITLE_BAR)),$CELL_W,$((CELL_H - TITLE_BAR))"
}

mkdir -p "$LOG_DIR"
launch() { # <log name> <env assignments...>
    local name="$1"; shift
    # Run SMAPI directly (not the StardewValley wrapper script, which opens a Terminal window).
    # stdin must block rather than hit EOF: SMAPI's console thread loops on Console.ReadLine() and
    # spins at 100% CPU on /dev/null. A FIFO opened read-write never reaches EOF.
    local stdin="$LOG_DIR/.$name.stdin"
    rm -f "$stdin" && mkfifo "$stdin"
    # exec, so no shell lingers to wait for (and report on) the game process.
    (cd "$GAME_DIR" && exec env "$@" ./StardewModdingAPI <> "$stdin" > "$LOG_DIR/$name.log" 2>&1 &)
    echo "Started $name (log: logs/$name.log)"
}

wait_in_game() { # <log name>: wait until that instance has loaded the world
    for _ in $(seq 1 60); do
        if grep -q "In game as" "$LOG_DIR/$1.log" 2>/dev/null; then
            grep "In game as" "$LOG_DIR/$1.log" | sed 's/\x1b\[[0-9;]*m//g'
            return 0
        fi
        sleep 2
    done
    echo "$1 didn't get into the game within 2 minutes; see logs/$1.log" >&2
    return 1
}

launch host NOBODYVALLEY_HOST="$SAVE" NOBODYVALLEY_WINDOW="$(window_for 0)"
wait_in_game host
# Join one at a time: the host is slow to answer while it's sending the world to another farmhand.
i=1
for f in "${FARMHANDS[@]}"; do
    launch "$f" NOBODYVALLEY_JOIN="$f" NOBODYVALLEY_WINDOW="$(window_for $i)"
    if ! wait_in_game "$f"; then
        # Occasionally a client gets stuck waiting for the farmhand list; a fresh process fixes it.
        echo "Restarting $f's instance..."
        for pid in $(pgrep -x StardewModdingAPI || true); do
            if ps eww "$pid" | grep -q "NOBODYVALLEY_JOIN=$f"; then kill -KILL "$pid"; fi
        done
        sleep 2
        launch "$f" NOBODYVALLEY_JOIN="$f" NOBODYVALLEY_WINDOW="$(window_for $i)"
        wait_in_game "$f" || true
    fi
    i=$((i + 1))
done
echo "Everyone's in."

if [ "$AGENTS" = true ]; then
    cd "$ROOT/agents"
    exec uv run nw-valley --dashboard-window "$DASHBOARD_WINDOW"
fi
