#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work_dir=$(mktemp -d)
trap 'rm -rf "$work_dir"' EXIT
publish_dir=$work_dir/publish
appdir=$work_dir/AAML.AppDir

mkdir -p "$publish_dir/tools/proton-wrapper"
  touch "$publish_dir/AAML" "$publish_dir/libsteam_api.so" \
  "$publish_dir/tools/proton-wrapper/AAML.ProtonWrapper"
chmod 0755 "$publish_dir/AAML" "$publish_dir/tools/proton-wrapper/AAML.ProtonWrapper"

bash "$repo_root/eng/linux/stage-linux-package.sh" "$publish_dir"
bash "$repo_root/eng/linux/build-appdir.sh" "$publish_dir" "$appdir"
bash "$repo_root/eng/linux/validate-appdir.sh" "$appdir"

if bash "$repo_root/eng/linux/build-appdir.sh" "$work_dir/missing" "$work_dir/invalid" 2>/dev/null; then
  echo "AppDir assembly accepted an invalid publish stage." >&2
  exit 1
fi
