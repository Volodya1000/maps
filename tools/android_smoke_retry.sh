#!/usr/bin/env bash
set -euo pipefail

for attempt in 1 2; do
  adb kill-server || true
  adb start-server || true
  if python3 tools/android_smoke.py \
      --apk artifacts/CableRoutePlanner-CI-Minsk.apk \
      --package "$ANDROID_APPLICATION_ID" \
      --output artifacts/android-smoke \
      --settle-seconds 90 \
      --wait-map-ready; then
    exit 0
  fi

  if [[ "$attempt" -eq 1 ]]; then
    echo "Android smoke attempt failed; restarting adb before retry." >&2
    sleep 5
  fi
done

exit 1
