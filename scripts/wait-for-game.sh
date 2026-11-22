#!/usr/bin/env bash
# Wait until the NobodyValley mod answers "hello" with world_ready=true — i.e. the game has
# booted and a save is loaded. Polls the mod itself (fast, no log-grepping), so it works across
# restarts and is never fooled by a stale log line.
# Usage: scripts/wait-for-game.sh [port] [timeout-seconds]
PORT="${1:-8790}"
TIMEOUT="${2:-120}"
END=$(( $(date +%s) + TIMEOUT ))

while [ "$(date +%s)" -lt "$END" ]; do
    OUT=$(python3 -c "
import socket
s = socket.create_connection(('127.0.0.1', $PORT), timeout=1)
s.sendall(b'{\"cmd\":\"hello\"}\n')
s.settimeout(2)
data = b''
while True:
    chunk = s.recv(4096)
    data += chunk
    if not chunk or b'\n' in chunk:
        break
s.close()
print(data.decode().strip())
" 2>/dev/null | tr -d ' ')
    if [ -n "$OUT" ] && printf '%s' "$OUT" | grep -q '"world_ready": *true'; then
        FARMER=$(printf '%s' "$OUT" | sed -n 's/.*"farmer": *"\([^"]*\)".*/\1/p')
        echo "In game as ${FARMER:-?} (world_ready)."
        exit 0
    fi
    sleep 1
done
echo "Timed out waiting for the game on port $PORT (${TIMEOUT}s)." >&2
exit 1