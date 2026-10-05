#!/usr/bin/env bash
# install_ratings_plugin.sh — restart Jellyfin to load the freshly installed Ratings DLL,
# but only once nobody is watching anything.
#
# Restarting Jellyfin kills in-flight playback, so this waits for GET /Sessions to show no
# NowPlayingItem on two consecutive checks before restarting. Then it proves the new build
# actually loaded by calling the new /Ratings/MostCommented endpoint (404 = old DLL).
#
#   install_ratings_plugin.sh [--verify-only]
set -uo pipefail

TOKEN="$(cat /tmp/jf.token 2>/dev/null)"
JF="http://127.0.0.1:8096"
LOG="$HOME/.local/state/ratings-install.log"
mkdir -p "$(dirname "$LOG")"
say() { printf '%s %s\n' "$(date '+%F %T')" "$*" >> "$LOG"; }

busy_count() {   # -> number of sessions actively playing something
  curl -s -m 10 -H "Authorization: MediaBrowser Token=\"$TOKEN\"" "$JF/Sessions" 2>/dev/null \
    | python3 -c 'import json,sys
try: d=json.load(sys.stdin)
except Exception: print(-1); raise SystemExit
print(sum(1 for s in d if s.get("NowPlayingItem")))' 2>/dev/null
}

verify() {
  local code
  code=$(curl -s -o /tmp/mc.json -w '%{http_code}' -m 15 \
    -H "Authorization: MediaBrowser Token=\"$TOKEN\"" "$JF/Ratings/MostCommented?limit=5&minReviews=3")
  say "verify /Ratings/MostCommented -> HTTP $code"
  if [ "$code" = "200" ]; then
    say "loaded. body: $(head -c 300 /tmp/mc.json)"
    return 0
  fi
  return 1
}

if [ "${1:-}" = "--verify-only" ]; then verify; exit $?; fi

say "waiting for Jellyfin to be idle before restarting to load the new Ratings DLL"
idle=0
for i in $(seq 1 720); do          # up to ~6 h at 30 s
  n=$(busy_count)
  if [ "$n" = "0" ]; then
    idle=$((idle+1))
    say "idle check $idle (0 playing)"
    if [ "$idle" -ge 2 ]; then break; fi
  else
    [ "$idle" -gt 0 ] && say "someone started watching again (busy=$n) - resetting idle counter"
    idle=0
  fi
  sleep 30
done

if [ "$idle" -lt 2 ]; then say "gave up waiting after ~6h; DLL installed but NOT loaded"; exit 1; fi

say "restarting jellyfin"
sudo systemctl restart jellyfin
for i in $(seq 1 30); do
  sleep 5
  if curl -s -o /dev/null -m 5 "$JF/System/Info/Public" 2>/dev/null; then break; fi
done
say "jellyfin back up"
sleep 5
if verify; then say "DONE - most-commented section is live"; else say "WARNING - endpoint missing after restart (check the plugin loaded)"; fi
