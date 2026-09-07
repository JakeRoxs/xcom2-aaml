#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <appdir> <output.AppImage>" >&2
  exit 2
fi

appdir=$(realpath "$1")
output=$2
tool_commit=8c8c91f762b412a19f4e8d2c4b35afb98f2d7c81
tool_sha256=a6d71e2b6cd66f8e8d16c37ad164658985e0cf5fcaa950c90a482890cb9d13e0
tool_url=https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
tool_dir=${RUNNER_TEMP:-${TMPDIR:-/tmp}}/aaml-appimagetool-$tool_commit
tool=$tool_dir/appimagetool-x86_64.AppImage
runtime_commit=75849dce7cc37e4319b633df1f116ca895c71a12
runtime_sha256=1cc49bcf1e2ccd593c379adb17c9f85a36d619088296504de95b1d06215aebbf
runtime_url=https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64
runtime=$tool_dir/runtime-x86_64-$runtime_commit

mkdir -p "$tool_dir"
if [[ ! -f "$tool" ]]; then
  curl --fail --location --proto '=https' --tlsv1.2 --output "$tool" "$tool_url"
fi
echo "$tool_sha256  $tool" | sha256sum --check --status || {
  rm -f "$tool"
  echo "appimagetool checksum verification failed for pinned commit $tool_commit." >&2
  exit 1
}
chmod 0755 "$tool"
if [[ ! -f "$runtime" ]]; then
  curl --fail --location --proto '=https' --tlsv1.2 --output "$runtime" "$runtime_url"
fi
echo "$runtime_sha256  $runtime" | sha256sum --check --status || {
  rm -f "$runtime"
  echo "AppImage runtime checksum verification failed for pinned commit $runtime_commit." >&2
  exit 1
}

bash "$(dirname "${BASH_SOURCE[0]}")/validate-appdir.sh" "$appdir"
mkdir -p "$(dirname "$output")"
rm -f "$output"
ARCH=x86_64 SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-0} "$tool" --appimage-extract-and-run --runtime-file "$runtime" "$appdir" "$output"
(cd "$(dirname "$output")" && sha256sum "$(basename "$output")") > "$output.sha256"
