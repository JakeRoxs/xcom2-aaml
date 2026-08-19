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
set +e
recursive_output=$(OUTPUT_FILE="$WORK/recursive.txt" SteamAppId=268500 XDG_RUNTIME_DIR="$WORK/runtime" AAML_STEAM_WRAPPER_ACTIVE=1 \
  dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" "$WORK/capture.sh" 2>&1)
recursive_code=$?
set -e
test "$recursive_code" -eq 78
grep -q 'steam.launch.recursive_invocation' <<< "$recursive_output"
test ! -e "$WORK/recursive.txt"

echo "Testing deterministic argument and startup failures..."
set +e
unsupported_output=$(SteamAppId=1 XDG_RUNTIME_DIR="$WORK/runtime" dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" "$WORK/capture.sh" 2>&1)
unsupported_code=$?
runtime_output=$(SteamAppId=268500 env -u XDG_RUNTIME_DIR -u AAML_RUNTIME_DIR dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" "$WORK/capture.sh" 2>&1)
runtime_code=$?
exec_output=$(SteamAppId=268500 XDG_RUNTIME_DIR="$WORK/runtime" dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" "$WORK/missing-command" 2>&1)
exec_code=$?
probe_output=$(dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" --steam-probe --workshop-id=invalid 2>&1)
probe_code=$?
set -e
test "$unsupported_code" -eq 78
grep -q 'steam.launch.app_id_missing' <<< "$unsupported_output"
test "$runtime_code" -eq 75
grep -q 'steam.launch.runtime_unavailable' <<< "$runtime_output"
test "$exec_code" -eq 126
grep -q 'steam.launch.exec_failed' <<< "$exec_output"
test "$probe_code" -eq 64
grep -q '"stage": "arguments"' <<< "$probe_output"

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

echo "Testing cancellation and child process-tree cleanup..."
cat > "$WORK/sleeper.sh" <<'SLEEPER'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$$" > "$CHILD_PID_FILE"
while true; do sleep 1; done
SLEEPER
chmod +x "$WORK/sleeper.sh"
CHILD_PID_FILE="$WORK/child.pid" SteamAppId=268500 XDG_RUNTIME_DIR="$WORK/runtime" \
  dotnet "$WRAPPER_DIR/AAML.ProtonWrapper.dll" "$WORK/sleeper.sh" 2> "$WORK/cancel.json" &
wrapper_pid=$!
for _ in {1..50}; do
  test -s "$WORK/child.pid" && break
  sleep 0.1
done
test -s "$WORK/child.pid"
child_pid=$(< "$WORK/child.pid")
kill -TERM "$wrapper_pid"
set +e
wait "$wrapper_pid"
cancel_code=$?
set -e
test "$cancel_code" -eq 143
grep -q 'steam.launch.cancelled' "$WORK/cancel.json"
if kill -0 "$child_pid" 2>/dev/null; then
  echo "Cancelled wrapper left child process $child_pid running." >&2
  exit 1
fi

echo "AAML Proton wrapper integration tests passed."
