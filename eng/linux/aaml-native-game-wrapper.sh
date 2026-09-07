#!/usr/bin/env bash
set -euo pipefail

if [[ $# -eq 0 ]]; then
  echo "Steam supplied an empty native game command." >&2
  exit 64
fi

[[ ${SteamAppId:-${SteamGameId:-}} == 268500 ]] || { echo "Steam did not identify XCOM 2 app 268500." >&2; exit 65; }
game_root=$(realpath "$PWD")
vanilla_libraries=$game_root/lib/x86_64
wotc_libraries=$game_root/XCOM2WotC/lib
[[ -d "$vanilla_libraries" ]] || { echo "Native Vanilla library directory is missing: $vanilla_libraries" >&2; exit 66; }
[[ -d "$wotc_libraries" ]] || { echo "Native WotC library directory is missing: $wotc_libraries" >&2; exit 67; }
export LD_LIBRARY_PATH="$wotc_libraries:$vanilla_libraries${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$@"
