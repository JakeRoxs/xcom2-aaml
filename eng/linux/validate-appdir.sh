#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <appdir>" >&2
  exit 2
fi

appdir=$1
app_id=io.github.jakeroxs.xcom2_aaml
required_files=(
  AppRun
  "$app_id.desktop"
  "$app_id.png"
  usr/bin/AAML
  usr/lib/aaml/AAML
  usr/lib/aaml/libsteam_api.so
  usr/lib/aaml/tools/proton-wrapper/AAML.ProtonWrapper
  usr/lib/aaml/eng/linux/aaml-proton-launch-option.sh
  usr/lib/aaml/licenses/AAML-GPL-3.0.txt
  usr/lib/aaml/licenses/Steamworks.NET-LICENSE.txt
  "usr/share/applications/$app_id.desktop"
  "usr/share/metainfo/$app_id.appdata.xml"
  "usr/share/icons/hicolor/256x256/apps/$app_id.png"
)

for relative_path in "${required_files[@]}"; do
  [[ -e "$appdir/$relative_path" || -L "$appdir/$relative_path" ]] || {
    echo "AppDir is missing $relative_path" >&2
    exit 1
  }
done

for relative_path in AppRun usr/lib/aaml/AAML usr/lib/aaml/tools/proton-wrapper/AAML.ProtonWrapper usr/lib/aaml/eng/linux/aaml-proton-launch-option.sh; do
  [[ -x "$appdir/$relative_path" ]] || { echo "$relative_path is not executable" >&2; exit 1; }
done

[[ $(readlink "$appdir/usr/bin/AAML") == ../lib/aaml/AAML ]] || {
  echo "AppDir launcher symlink has an unexpected target." >&2
  exit 1
}

grep -qx 'Exec=AAML' "$appdir/$app_id.desktop"
grep -qx "Icon=$app_id" "$appdir/$app_id.desktop"
grep -q "<id>$app_id</id>" "$appdir/usr/share/metainfo/$app_id.appdata.xml"
