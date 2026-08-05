#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
WRAPPER_DIR="${1:-$SCRIPT_DIR}"
WORK="$(mktemp -d --tmpdir aaml-proton-wrapper.XXXXXX)"
trap 'rm -rf -- "$WORK"' EXIT

command -v dotnet >/dev/null 2>&1 || { echo "dotnet 10 is required." >&2; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "python3 is required." >&2; exit 1; }
test -f "$WRAPPER_DIR/AAML.ProtonWrapper.dll" || { echo "AAML.ProtonWrapper.dll was not found in $WRAPPER_DIR." >&2; exit 1; }

mkdir -p "$WORK/runtime"
cat > "$WORK/capture.sh" <<'CAPTURE'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$@" > "$OUTPUT_FILE"
printf 'GUARD=%s\n' "${AAML_STEAM_WRAPPER_ACTIVE:-}" >> "$OUTPUT_FILE"
exit "${FAKE_EXIT:-0}"
CAPTURE
chmod +x "$WORK/capture.sh"

echo "Testing no-request pass-through and literal token preservation..."
OUTPUT_FILE="$WORK/pass-through.txt" SteamAppId=268500 XDG_RUNTIME_DIR="$WORK/runtime" \
  dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" "$WORK/capture.sh" "Value With Spaces" '&;|$() Ω'
grep -Fx 'Value With Spaces' "$WORK/pass-through.txt" >/dev/null
grep -Fx '&;|$() Ω' "$WORK/pass-through.txt" >/dev/null
grep -Fx 'GUARD=1' "$WORK/pass-through.txt" >/dev/null

echo "Testing recursion rejection..."
if OUTPUT_FILE="$WORK/recursive.txt" SteamAppId=268500 XDG_RUNTIME_DIR="$WORK/runtime" AAML_STEAM_WRAPPER_ACTIVE=1 \
  dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" "$WORK/capture.sh"; then
  echo "Recursive invocation unexpectedly succeeded." >&2
  exit 1
fi
test ! -e "$WORK/recursive.txt"

echo "Testing one-shot request claim, configuration, and exact transformed vector..."
GAME="$WORK/library/steamapps/common/Game With Spaces Ω"
TARGET="$GAME/Binaries/Win64/XCom2.exe"
WORKSHOP="$WORK/library/steamapps/workshop/content/268500"
EXTERNAL="$WORK/External Mods Ω"
mkdir -p "$(dirname -- "$TARGET")" "$WORKSHOP" "$EXTERNAL" \
  "$WORK/library/steamapps/compatdata/268500/pfx/drive_c/users/steamuser" \
  "$WORK/runtime/aaml/steam-launch"
touch "$TARGET"
chmod 700 "$WORK/runtime/aaml/steam-launch"
python3 - "$WORK/runtime/aaml/steam-launch/request-268500.json" "$GAME" "$TARGET" "$WORKSHOP" "$EXTERNAL" <<'PY'
import datetime, json, sys, uuid
now = datetime.datetime.now(datetime.timezone.utc)
request = {
    "protocolVersion": 2,
    "requestId": str(uuid.uuid4()),
    "appId": {"value": 268500},
    "variant": 0,
    "gameInstallPath": sys.argv[2],
    "targetExecutablePath": sys.argv[3],
    "activePackageIds": ["AllRegionLinks"],
    "modRootLocations": [sys.argv[4], sys.argv[5]],
    "additionalArguments": ["-Name=Mixed Case", "&;|$() Ω"],
    "createdAtUtc": now.isoformat(),
    "expiresAtUtc": (now + datetime.timedelta(seconds=30)).isoformat()
}
with open(sys.argv[1], "w", encoding="utf-8") as stream:
    json.dump(request, stream)
PY
chmod 600 "$WORK/runtime/aaml/steam-launch/request-268500.json"
OUTPUT_FILE="$WORK/transformed.txt" SteamAppId=268500 XDG_RUNTIME_DIR="$WORK/runtime" \
  dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" "$WORK/capture.sh" "/old/Binaries/Win64/XCom2.exe" "Steam Original"
mapfile -t transformed < "$WORK/transformed.txt"
test "${transformed[0]}" = "$TARGET"
test "${transformed[1]}" = "Steam Original"
test "${transformed[2]}" = "-Name=Mixed Case"
test "${transformed[3]}" = '&;|$() Ω'
test "${transformed[4]}" = "GUARD=1"
CONFIG="$WORK/library/steamapps/compatdata/268500/pfx/drive_c/users/steamuser/Documents/My Games/XCOM2/XComGame/Config"
grep -q '^ActiveMods=AllRegionLinks$' "$CONFIG/XComModOptions.ini"
grep -Fq 'ModRootDirs=S:\workshop\content\268500' "$CONFIG/XComEngine.ini"
grep -Fq 'ModRootDirs=Z:' "$CONFIG/XComEngine.ini"
test ! -e "$WORK/runtime/aaml/steam-launch/request-268500.json"

echo "Testing child exit-code propagation..."
set +e
OUTPUT_FILE="$WORK/exit.txt" FAKE_EXIT=37 SteamAppId=268500 XDG_RUNTIME_DIR="$WORK/runtime" \
  dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" "$WORK/capture.sh"
exit_code=$?
set -e
test "$exit_code" -eq 37

echo "AAML Proton wrapper integration tests passed."
