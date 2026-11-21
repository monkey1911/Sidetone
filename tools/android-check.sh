#!/usr/bin/env bash
# Runs Sidetone's on-device self-test on a running emulator (or a phone on adb), then takes screenshots
# of the demo at phone and tablet sizes. CI runs it on an Android 11 emulator.
#   tools/android-check.sh Sidetone-TAG-android.apk OUTDIR
set -uo pipefail

apk="$1"
out="$2"
pkg=org.sidetone.app
activity="$pkg/$pkg.MainActivity"
mkdir -p "$out"

adb wait-for-device
# -g grants the runtime permissions (the microphone, notifications) so the test can open the microphone
adb install -r -g "$apk" || { echo "::error::the APK didn't install"; exit 1; }
adb logcat -c

echo "== self-test"
adb shell am start -W -n "$activity" --ez selftest true
result=""
for _ in $(seq 1 150); do
  result=$(adb logcat -d -s sidetone:I | grep -oE "ALL ANDROID CHECKS PASSED.*|[0-9]+ ANDROID CHECK\(S\) FAILED" | tail -1)
  [ -n "$result" ] && break
  sleep 1
done
adb logcat -d -s sidetone:I | sed -E 's/^.*sidetone *: //' | tee "$out/selftest.txt"
adb exec-out screencap -p > "$out/android-phone-selftest.png"

if ! grep -q "ALL ANDROID CHECKS PASSED" "$out/selftest.txt"; then
  echo "::error::the Android self-test didn't pass (${result:-no result: did the app start?})"
  echo "== what Android logged about the app"
  adb logcat -d | grep -iE "AndroidRuntime|FATAL|sidetone|mono|dotnet|monodroid" | tail -150
  exit 1
fi

# screenshots of the demo: a phone (the emulator as it is), then a tablet's size and density
echo "== screenshots"
adb shell am force-stop "$pkg"
adb shell am start -W -n "$activity" --ez demo true --es theme dark
sleep 6
adb exec-out screencap -p > "$out/android-phone.png"
adb shell wm size 1600x2560
adb shell wm density 320
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1
sleep 8
adb exec-out screencap -p > "$out/android-tablet.png"
adb shell wm size reset
adb shell wm density reset
adb shell settings put system user_rotation 0
for f in "$out"/android-*.png; do
  name=$(basename "$f" .png)
  echo "-----BEGIN PNG $name-----"
  base64 -w 120 "$f"
  echo "-----END PNG $name-----"
done

adb logcat -d | grep -iE "AndroidRuntime|FATAL EXCEPTION" | tail -40 || true
echo "Android check: $result"
