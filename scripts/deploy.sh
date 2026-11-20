#!/usr/bin/env bash
# Deploy NobodyValley to another Mac (by default the one configured in scripts/local.env) and run the demo there.
#
#   scripts/deploy.sh            # build the mod here; copy the mod and the agents over
#   scripts/deploy.sh --start    # ...and (re)start the demo there (games + agents), in the background
#   scripts/deploy.sh --start --stop-after 60   # ...and stop it again after 60 minutes
#   scripts/deploy.sh --stop     # stop the demo there
#   scripts/deploy.sh --status   # what's running there, and the latest agent output
#   scripts/deploy.sh --fetch    # copy its run logs (logs/runs/*) into logs/remote-runs/ here
#
# Configure with environment variables, or in `scripts/local.env` (gitignored; see
# `scripts/local.env.example`; environment variables win over the file):
#   NV_REMOTE      user@host to deploy to
#   NV_REMOTE_DIR  project folder there, relative to its home (default: nobodyvalley)
#
# The remote needs Steam, Stardew Valley + SMAPI (same versions as here) and uv, but not the .NET SDK:
# the mod is built here. Saves sync through Steam Cloud (same Steam account); don't run the demo on
# both machines at once, or the saves conflict.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Optional local settings (save name, farmhand names, ...): scripts/local.env (gitignored).
# Export them: the remote demo.sh and the agents rely on the same values as environment vars.
set -a
[ -f "$ROOT/scripts/local.env" ] && source "$ROOT/scripts/local.env"
set +a
REMOTE="${NV_REMOTE:?set NV_REMOTE (or scripts/local.env; see scripts/local.env.example)}"
REMOTE_DIR="${NV_REMOTE_DIR:-nobodyvalley}"
GAME_DIR="${NV_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS}"
# Relative to the remote home; backslash-escaped for the remote shell (rsync passes it through one).
REMOTE_MODS='Library/Application\ Support/Steam/steamapps/common/Stardew\ Valley/Contents/MacOS/Mods'

mkdir -p "$HOME/.ssh"
remote() { ssh "$REMOTE" "cd $REMOTE_DIR 2>/dev/null || true; $*"; }
sync() { rsync -az "$@"; }

case "${1:-}" in
    --stop)
        remote 'scripts/demo.sh stop'
        exit 0 ;;
    --status)
        remote 'pgrep -lf "StardewModdingAPI|bin/nobodyvalley" | cut -c1-80; echo; echo "Logs: $(readlink logs/latest)"; tail -n 25 logs/latest/agents.log 2>/dev/null | cut -c1-200'
        exit 0 ;;
    --fetch)
        mkdir -p "$ROOT/logs/remote-runs"
        sync "$REMOTE:$REMOTE_DIR/logs/runs/" "$ROOT/logs/remote-runs/"
        echo "Copied to logs/remote-runs/:"; ls "$ROOT/logs/remote-runs"
        exit 0 ;;
    ""|--start) ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
esac
STOP_AFTER=""
if [ "${2:-}" = "--stop-after" ]; then
    STOP_AFTER="${3:?--stop-after needs a number of minutes}"
fi

echo "Building the mod..."
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
dotnet build "$ROOT/mod/NobodyValley" -c Release -p:GamePath="$GAME_DIR" | tail -2

echo "Copying to $REMOTE:$REMOTE_DIR ..."
remote "mkdir -p ~/$REMOTE_DIR/logs $REMOTE_MODS/NobodyValley"
# The agents and scripts (the remote keeps its own venv, logs and model cache).
sync --delete --exclude .venv --exclude __pycache__ --exclude '*.log' \
    "$ROOT/agents" "$ROOT/scripts" "$ROOT/README.md" "$REMOTE:$REMOTE_DIR/"
# The built mod, as installed here (but the remote keeps its own config.json).
sync --delete --exclude config.json "$GAME_DIR/Mods/NobodyValley/" "$REMOTE:$REMOTE_MODS/NobodyValley/"
remote 'cd agents && uv sync --quiet && echo "Agents installed."'

if [ "${1:-}" = "--start" ]; then
    if pgrep -x StardewModdingAPI > /dev/null; then
        echo "Note: the demo is also running here; with the same Steam account, stop one of them." >&2
    fi
    echo "Starting the demo there (about a minute; check with: $0 --status)..."
    # nohup + no terminal: it keeps running after we disconnect.
    remote 'pkill -f "nv-stop-after" || true; scripts/demo.sh stop > /dev/null; nohup scripts/demo.sh start > logs/demo.out 2>&1 < /dev/null &'
    if [ -n "$STOP_AFTER" ]; then
        # A timer there (named, so a new --start cancels it); --stop keeps the save with the run's logs.
        remote "nohup bash -c ': nv-stop-after; sleep $((STOP_AFTER * 60)); scripts/demo.sh stop' > logs/stop-after.out 2>&1 < /dev/null &"
        echo "It stops itself in $STOP_AFTER minutes."
    fi
fi
