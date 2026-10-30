#!/bin/sh
# Lab: start a bot player that joins the host copy (bridge 37900) and circles next to its Batman.
#   sh lab/botfriend.sh <seconds> <radius> <speed> <log> [center x,y,z] [extra bot arguments...]
ROOT=$(cd "$(dirname "$0")/.." && pwd -W)
bridge() { python -I "$ROOT/lab/bridge.py" 37900 "$@"; }
# The bot runs its own copy of the link, so deploys can replace dist/ while it plays.
mkdir -p "$ROOT/lab/out/botlink" && cp "$ROOT/dist/BatCoop/link/"* "$ROOT/lab/out/botlink/" 2>/dev/null
bridge host direct >/dev/null
sleep 2
pos=${5:-$(bridge state | python -c "import json,sys; d=json.load(sys.stdin); print(','.join(str(round(v)) for v in d['pos']))")}
"$ROOT/tools/dotnet/dotnet.exe" "$ROOT/lab/Offline/bin/Release/net10.0/Offline.dll" bot --link-exe "$ROOT/lab/out/botlink/BatCoopLink.exe" \
  --join 127.0.0.1:37950 --center "$pos" --radius "${2:-300}" --speed "${3:-400}" --seconds "${1:-600}" --name BotFriend --link-port 37960 $6 $7 $8 $9 > "${4:-/dev/null}" 2>&1 &
echo "bot circling $pos"
