#!/usr/bin/env bash
# Start a co-op session with one Stardew Valley instance per agent, tiled in a grid of windows,
# plus the NobodyWho agents.
#
#   scripts/start-demo.sh             # the default: stop what's running, rebuild + deploy the mod,
#                                     #   and start the game instances and the agents
#   scripts/start-demo.sh start       # start the game instances and the agents (no rebuild)
#   scripts/start-demo.sh build       # rebuild + deploy the mod only
#   scripts/start-demo.sh stop        # kill the game instances (and the agents)
#
# Configure with environment variables, or in `scripts/local.env` (gitignored; see
# `scripts/local.env.example`; environment variables win over the file):
#   NV_SAVE        save folder to host
#   NV_FARMHANDS   space-separated farmhand names
#   NV_DISPLAY     display to tile the windows on; see `swift scripts/screen-bounds.swift list` (default: 0)
#   NV_DASHBOARD_DISPLAY  display for the agent dashboard window (default: a column on NV_DISPLAY)
#   NV_DASHBOARD_WIDTH    width of that column, in points (default: 360)
#   NV_GAME_DIR    folder containing StardewModdingAPI     (default: Steam install)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Optional local settings (save name, farmhand names, ...): scripts/local.env (gitignored).
[ -f "$ROOT/scripts/local.env" ] && source "$ROOT/scripts/local.env"
GAME_DIR="${NV_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS}"
SAVE="${NV_SAVE:?set NV_SAVE (or scripts/local.env; see scripts/local.env.example)}"
read -r -a FARMHANDS <<< "${NV_FARMHANDS:?set NV_FARMHANDS (or scripts/local.env; see scripts/local.env.example)}"
LOG_DIR="$ROOT/logs"

STOP=false
BUILD=false
AGENTS=false
for arg in "$@"; do
    case "$arg" in
        stop) STOP=true ;;
        build) BUILD=true ;;
        start) AGENTS=true ;;
        *) echo "unknown command $arg" >&2; exit 2 ;;
    esac
done

# SIGKILL: on SIGTERM the game aborts in its native teardown (Steam networking, OpenAL), and
# macOS's ReportCrashService then spends minutes at 100% CPU on each crash report.
# (Nothing is lost: the game only saves overnight.)
stop_all() {
    pkill -KILL -x StardewModdingAPI || true
    pkill -TERM -f 'bin/nobodyvalley' || true # the agents
}

if [ "$STOP" = true ]; then
    stop_all
    # Keep the game state with the latest run's logs (the save as of the last night).
    if [ -d "$LOG_DIR/latest" ] && [ -d "$HOME/.config/StardewValley/Saves/$SAVE" ]; then
        cp -R "$HOME/.config/StardewValley/Saves/$SAVE" "$LOG_DIR/latest/save-at-stop"
        date "+Stopped at %Y-%m-%d %H:%M:%S" >> "$LOG_DIR/latest/start-demo.out"
    fi
    echo "Stopped."
    exit 0
fi

# What to do: the default (no arguments) is the full cycle (stop, rebuild, start).
MODE=start
if [ "$BUILD" = true ] && [ "$AGENTS" = true ]; then
    MODE=restart
elif [ "$BUILD" = true ]; then
    MODE=build
elif [ "$AGENTS" = true ]; then
    MODE=start
else
    MODE=restart
fi

if [ "$MODE" = restart ]; then
    echo "Stopping what's running..."
    stop_all
fi

if [ ! -x "$GAME_DIR/StardewModdingAPI" ]; then
    echo "SMAPI not found in $GAME_DIR (set NV_GAME_DIR)." >&2
    exit 1
fi

if [ "$MODE" = build ] || [ "$MODE" = restart ]; then
    echo "Building and deploying the mod..."
    export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
    dotnet build "$ROOT/mod/NobodyValley" -c Release -p:GamePath="$GAME_DIR" | tail -2
fi
if [ "$MODE" = build ]; then
    echo "Built."
    exit 0
fi
if [ ! -f "$GAME_DIR/Mods/NobodyValley/NobodyValley.dll" ]; then
    echo "The NobodyValley mod isn't installed; run 'scripts/start-demo.sh build' (or just './scripts/start-demo.sh', which builds first)." >&2
    exit 1
fi
if pgrep -x StardewModdingAPI > /dev/null; then
    echo "Game instances are already running; run '$0 stop' first (or just '$0' to rebuild and restart)." >&2
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

# Everything from this run goes into its own folder (logs/latest points at the newest one):
# the games' output (timestamped), this script's output, and the agents' log and LLM prompts.
RUN_DIR="$LOG_DIR/runs/$(date +%Y-%m-%d_%H%M%S)"
mkdir -p "$RUN_DIR"
ln -sfn "runs/$(basename "$RUN_DIR")" "$LOG_DIR/latest"
exec > >(tee -a "$RUN_DIR/start-demo.out") 2>&1
echo "Logs: $RUN_DIR"

launch() { # <log name> <env assignments...>
    local name="$1"; shift
    # Run SMAPI directly (not the StardewValley wrapper script, which opens a Terminal window).
    # stdin must block rather than hit EOF: SMAPI's console thread loops on Console.ReadLine() and
    # spins at 100% CPU on /dev/null. A FIFO opened read-write never reaches EOF.
    local stdin="$LOG_DIR/.$name.stdin"
    rm -f "$stdin" && mkfifo "$stdin"
    # Timestamp each line (the console output has none). The outer shell's stderr goes to /dev/null, so
    # it doesn't report "Killed: 9" when 'stop' kills the game.
    ( (cd "$GAME_DIR" && env "$@" ./StardewModdingAPI <> "$stdin" 2>&1 \
        | /usr/bin/perl -MPOSIX -pe 'BEGIN { $| = 1 } print strftime("%H:%M:%S ", localtime)' > "$RUN_DIR/$name.log") \
        2> /dev/null & )
    echo "Started $name (log: logs/latest/$name.log)"
}

wait_in_game() { # <log name>: wait until that instance has loaded the world
    for _ in $(seq 1 60); do
        if grep -q "In game as" "$RUN_DIR/$1.log" 2>/dev/null; then
            grep "In game as" "$RUN_DIR/$1.log" | sed 's/\x1b\[[0-9;]*m//g'
            return 0
        fi
        sleep 2
    done
    echo "$1 didn't get into the game within 2 minutes; see $RUN_DIR/$1.log" >&2
    return 1
}

wait_joined() { # <farmhand>: wait until it's in the game; fails if it gets stuck before joining
    local log="$RUN_DIR/$1.log" asked=""
    for _ in $(seq 1 120); do
        if grep -q "In game as" "$log" 2>/dev/null; then
            grep "In game as" "$log" | sed 's/\x1b\[[0-9;]*m//g'
            return 0
        fi
        # The farmhand list normally arrives within seconds of asking for it; occasionally it never
        # does, and only a fresh process helps. (Loading the world after "Joining as" can take longer.)
        if [ -z "$asked" ] && grep -q "Auto-joining" "$log" 2>/dev/null; then asked=$SECONDS; fi
        if [ -n "$asked" ] && ! grep -q "Joining as" "$log" 2>/dev/null && [ $((SECONDS - asked)) -ge 20 ]; then
            echo "$1 got stuck waiting for the host's farmhand list."
            return 1
        fi
        sleep 1
    done
    echo "$1 didn't get into the game within 2 minutes; see $RUN_DIR/$1.log" >&2
    return 1
}

launch host NOBODYVALLEY_HOST="$SAVE" NOBODYVALLEY_WINDOW="$(window_for 0)"
wait_in_game host
# Join one at a time: the host is slow to answer while it's sending the world to another farmhand.
i=1
for f in "${FARMHANDS[@]}"; do
    for attempt in 1 2 3; do
        launch "$f" NOBODYVALLEY_JOIN="$f" NOBODYVALLEY_WINDOW="$(window_for $i)"
        wait_joined "$f" && break
        echo "Restarting $f's instance..."
        for pid in $(pgrep -x StardewModdingAPI || true); do
            if ps eww "$pid" | grep -q "NOBODYVALLEY_JOIN=$f"; then kill -KILL "$pid"; fi
        done
        sleep 2
    done
    i=$((i + 1))
done
echo "Everyone's in."

cd "$ROOT/agents"
exec uv run nobodyvalley --dashboard-window "$DASHBOARD_WINDOW" --log "$RUN_DIR/agents.log" --prompt-log "$RUN_DIR/prompts.log"
