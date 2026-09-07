#!/usr/bin/env sh
set -eu

app_id=io.github.jakeroxs.xcom2_aaml
printf 'For native Steam, set XCOM 2 launch options to:\n'
printf 'flatpak run --command=AAML.ProtonWrapper %s %%command%%\n\n' "$app_id"
printf 'For Flatpak Steam, set XCOM 2 launch options to:\n'
printf 'flatpak-spawn --host flatpak run --command=AAML.ProtonWrapper %s %%command%%\n' "$app_id"
