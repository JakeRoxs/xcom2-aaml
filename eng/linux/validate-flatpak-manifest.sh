#!/usr/bin/env bash
set -euo pipefail

manifest=${1:-eng/linux/flatpak/io.github.jakeroxs.xcom2_aaml.yml}
[[ -f "$manifest" ]] || { echo "Flatpak manifest not found: $manifest" >&2; exit 1; }

grep -qx "app-id: io.github.jakeroxs.xcom2_aaml" "$manifest"
grep -qx "runtime-version: '25.08'" "$manifest"
grep -qx "command: AAML" "$manifest"
grep -qx '  - --socket=fallback-x11' "$manifest"
grep -qx '  - --filesystem=xdg-data/Steam' "$manifest"
grep -qx '  - --filesystem=~/.var/app/com.valvesoftware.Steam/.local/share/Steam' "$manifest"
grep -qx '  - --talk-name=org.freedesktop.Flatpak' "$manifest"
grep -qx '        path: payload' "$manifest"
grep -qx '        path: aaml-proton-launch-option.sh' "$manifest"

if grep -Eq -- '--filesystem=(host|home)(:|$)' "$manifest"; then
  echo "Flatpak manifest grants an unbounded host or home filesystem permission." >&2
  exit 1
fi

if grep -qx '  - --socket=x11' "$manifest"; then
  echo "Flatpak manifest must not grant unconditional X11 access when native Wayland is available." >&2
  exit 1
fi

if grep -Eq 'https?://' "$manifest"; then
  echo "Flatpak manifest must not download sources during its build phase." >&2
  exit 1
fi
