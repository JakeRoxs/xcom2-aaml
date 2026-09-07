#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <linux-publish-directory>" >&2
  exit 2
fi

publish_dir=$(realpath "$1")
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
app_id=io.github.jakeroxs.xcom2_aaml

[[ -x "$publish_dir/AAML" ]] || { echo "Missing executable launcher: $publish_dir/AAML" >&2; exit 1; }
[[ -x "$publish_dir/tools/proton-wrapper/AAML.ProtonWrapper" ]] || { echo "Missing executable Proton wrapper." >&2; exit 1; }

install -d "$publish_dir/licenses" \
  "$publish_dir/eng/linux" \
  "$publish_dir/share/applications" \
  "$publish_dir/share/metainfo" \
  "$publish_dir/share/icons/hicolor/256x256/apps"
install -m 0644 "$repo_root/LICENSE" "$publish_dir/licenses/AAML-GPL-3.0.txt"
install -m 0644 "$repo_root/src/ThirdParty/Steamworks.NET/LICENSE.txt" "$publish_dir/licenses/Steamworks.NET-LICENSE.txt"
install -m 0755 "$repo_root/eng/linux/aaml-proton-launch-option.sh" "$publish_dir/eng/linux/aaml-proton-launch-option.sh"
install -m 0644 "$repo_root/eng/linux/$app_id.desktop" "$publish_dir/share/applications/$app_id.desktop"
install -m 0644 "$repo_root/eng/linux/$app_id.metainfo.xml" "$publish_dir/share/metainfo/$app_id.metainfo.xml"
install -m 0644 "$repo_root/assets/branding/generated/png/aaml-256.png" "$publish_dir/share/icons/hicolor/256x256/apps/$app_id.png"
