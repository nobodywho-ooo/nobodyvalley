#!/usr/bin/env bash
# Deploy NobodyValley to another Mac (by default the office Mac Mini) and run the demo there.
#
#   scripts/deploy.sh            # build the mod here; copy the mod and the agents over
#   scripts/deploy.sh --start    # ...and (re)start the demo there (games + agents), in the background
#   scripts/deploy.sh --stop     # stop the demo there
#   scripts/deploy.sh --status   # what's running there, and the latest agent output
#   scripts/deploy.sh --ssh CMD  # run a command there (in the project folder)
#
# Configure with environment variables:
#   NV_REMOTE      user@host to deploy to   (default: nobodywho@nobodywhos-mac-mini.local)
#   NV_REMOTE_DIR  project folder there, relative to its home (default: nw-valley)
#
# The remote needs Steam, Stardew Valley + SMAPI (same versions as here) and uv, but not the .NET SDK:
# the mod is built here. Saves sync through Steam Cloud (same Steam account); don't run the demo on
# both machines at once, or the saves conflict.
#
# All ssh/rsync calls share one connection (ControlMaster), so an ssh agent that asks for approval per
# connection only asks once every 30 minutes.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REMOTE="${NV_REMOTE:-nobodywho@nobodywhos-mac-mini.local}"
REMOTE_DIR="${NV_REMOTE_DIR:-nw-valley}"
GAME_DIR="${NV_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS}"
# Relative to the remote home; backslash-escaped for the remote shell (rsync passes it through one).
REMOTE_MODS='Library/Application\ Support/Steam/steamapps/common/Stardew\ Valley/Contents/MacOS/Mods'

mkdir -p "$HOME/.ssh"
SSH_OPTS=(-o ConnectTimeout=10 -o ControlMaster=auto -o "ControlPath=$HOME/.ssh/nv-deploy-%C" -o ControlPersist=30m)
remote() { ssh "${SSH_OPTS[@]}" "$REMOTE" "cd $REMOTE_DIR 2>/dev/null || true; $*"; }
sync() { rsync -az -e "ssh ${SSH_OPTS[*]}" "$@"; }

case "${1:-}" in
    --stop)
        remote 'scripts/start-demo.sh --stop'
        exit 0 ;;
    --status)
        remote 'pgrep -lf "StardewModdingAPI|bin/nw-valley" | cut -c1-80; echo; tail -n 25 agents/agents.log 2>/dev/null | cut -c1-200'
        exit 0 ;;
    --ssh)
        shift
        remote "$@"
        exit 0 ;;
    ""|--start) ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
esac

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
    remote 'scripts/start-demo.sh --stop > /dev/null; nohup scripts/start-demo.sh --agents > logs/start-demo.out 2>&1 < /dev/null &'
fi
