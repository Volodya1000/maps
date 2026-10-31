#!/usr/bin/env bash
# Install the exact full-size Android APK that CI will publish.
# Keep this as one process: android-emulator-runner runs each inline script
# line in a separate /bin/sh -c invocation, losing shell-local variables.
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 APK_PATH APPLICATION_ID" >&2
  exit 2
fi

apk="$1"
application_id="$2"
log_file="${RUNNER_TEMP:-/tmp}/belarus-install.log"

if [[ ! -s "$apk" ]]; then
  echo "Missing or empty distributable APK: $apk" >&2
  exit 1
fi

echo "Installing the exact full-size APK published to users: $apk"
stat -c '%n: %s bytes' "$apk"
echo "Emulator free storage before installation:"
adb shell df -h /data || true

if ! adb install -r "$apk" > "$log_file" 2>&1; then
  cat "$log_file"
  echo "Package manager diagnostics:"
  adb logcat -d -s PackageManager PackageInstaller installd | tail -n 150 || true
  echo "Emulator storage after failed installation:"
  adb shell df -h /data || true
  exit 1
fi

cat "$log_file"
echo "Verifying installed package $application_id"
adb shell pm path "$application_id" | grep -F 'package:'
