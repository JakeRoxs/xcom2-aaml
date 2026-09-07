#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <linux-publish-directory> <work-directory> <output.flatpak>" >&2
  exit 2
fi

publish_dir=$(realpath "$1")
work_dir=$(realpath -m "$2")
output=$(realpath -m "$3")
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
manifest_dir=$repo_root/eng/linux/flatpak
payload=$manifest_dir/payload

command -v flatpak-builder >/dev/null || { echo "flatpak-builder is required." >&2; exit 1; }
command -v flatpak >/dev/null || { echo "flatpak is required." >&2; exit 1; }
bash "$repo_root/eng/linux/validate-flatpak-manifest.sh" "$manifest_dir/io.github.jakeroxs.xcom2_aaml.yml"
[[ -x "$publish_dir/AAML" ]] || { echo "Linux package stage is invalid." >&2; exit 1; }
[[ -f "$publish_dir/share/metainfo/io.github.jakeroxs.xcom2_aaml.metainfo.xml" ]] || { echo "Linux package metadata is missing." >&2; exit 1; }

rm -rf "$payload" "$work_dir"
mkdir -p "$payload" "$work_dir"
cp -a "$publish_dir/." "$payload/"
trap 'rm -rf "$payload"' EXIT

flatpak-builder --force-clean --disable-download --default-branch=stable --repo="$work_dir/repo" \
  "$work_dir/build" "$manifest_dir/io.github.jakeroxs.xcom2_aaml.yml"
mkdir -p "$(dirname "$output")"
flatpak build-bundle "$work_dir/repo" "$output" io.github.jakeroxs.xcom2_aaml stable
