#!/usr/bin/env sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
wrapper="$root/tools/proton-wrapper/AAML.ProtonWrapper"
if [ ! -x "$wrapper" ]; then
  printf 'Wrapper is missing or not executable: %s\n' "$wrapper" >&2
  exit 1
fi
printf 'Set Steam launch options for XCOM 2 to:\n"%s" %%command%%\n' "$wrapper"
