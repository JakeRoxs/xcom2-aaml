#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <linux-publish-directory> <appdir>" >&2
  exit 2
fi

publish_dir=$(realpath "$1")
appdir=$2
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
app_id=io.github.jakeroxs.xcom2_aaml

[[ -x "$publish_dir/AAML" ]] || { echo "Missing executable launcher: $publish_dir/AAML" >&2; exit 1; }
[[ -x "$publish_dir/tools/proton-wrapper/AAML.ProtonWrapper" ]] || { echo "Missing executable Proton wrapper." >&2; exit 1; }
[[ -f "$publish_dir/licenses/AAML-GPL-3.0.txt" ]] || { echo "Missing AAML license." >&2; exit 1; }
[[ -f "$publish_dir/licenses/Steamworks.NET-LICENSE.txt" ]] || { echo "Missing Steamworks.NET license." >&2; exit 1; }
[[ -x "$publish_dir/eng/linux/aaml-proton-launch-option.sh" ]] || { echo "Missing executable Proton setup script." >&2; exit 1; }
[[ -f "$publish_dir/share/applications/$app_id.desktop" ]] || { echo "Missing desktop entry." >&2; exit 1; }
[[ -f "$publish_dir/share/metainfo/$app_id.metainfo.xml" ]] || { echo "Missing AppStream metadata." >&2; exit 1; }
[[ -f "$publish_dir/share/icons/hicolor/256x256/apps/$app_id.png" ]] || { echo "Missing application icon." >&2; exit 1; }

rm -rf "$appdir"
install -d "$appdir/usr/lib/aaml" \
  "$appdir/usr/bin" \
  "$appdir/usr/share/applications" \
  "$appdir/usr/share/metainfo" \
  "$appdir/usr/share/icons/hicolor/256x256/apps"
cp -a "$publish_dir/." "$appdir/usr/lib/aaml/"

ln -s ../lib/aaml/AAML "$appdir/usr/bin/AAML"
install -m 0644 "$publish_dir/share/applications/$app_id.desktop" "$appdir/$app_id.desktop"
install -m 0644 "$publish_dir/share/applications/$app_id.desktop" "$appdir/usr/share/applications/$app_id.desktop"
install -m 0644 "$publish_dir/share/metainfo/$app_id.metainfo.xml" "$appdir/usr/share/metainfo/$app_id.appdata.xml"
install -m 0644 "$publish_dir/share/icons/hicolor/256x256/apps/$app_id.png" "$appdir/$app_id.png"
install -m 0644 "$publish_dir/share/icons/hicolor/256x256/apps/$app_id.png" "$appdir/usr/share/icons/hicolor/256x256/apps/$app_id.png"

cat > "$appdir/AppRun" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
appdir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
exec "$appdir/usr/lib/aaml/AAML" "$@"
EOF
chmod 0755 "$appdir/AppRun" \
  "$appdir/usr/lib/aaml/AAML" \
  "$appdir/usr/lib/aaml/tools/proton-wrapper/AAML.ProtonWrapper" \
  "$appdir/usr/lib/aaml/eng/linux/aaml-proton-launch-option.sh"

bash "$repo_root/eng/linux/validate-appdir.sh" "$appdir"
