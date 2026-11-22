#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-only
#
# Builds the universal Sidetone.app from the two self-contained builds and zips it (macOS only):
#   make-app.sh VERSION ARM64_DIR X64_DIR OUT_DIR
# Each build folder holds sidetone, sidetone-cli and libvocoder.dylib (and the rest of dotnet publish's output).
# The .app gets the universal launcher (launcher.c), the icon, and an ad-hoc signature: free, and needed on
# Apple silicon; people let it in once with System Settings, Privacy & Security, Open Anyway (see README).
set -euo pipefail

VERSION="$1"; ARM="$2"; X64="$3"; OUT="$4"
HERE="$(cd "$(dirname "$0")" && pwd)"
ICON="$HERE/../icons/sidetone-1024.png"
APP="$OUT/Sidetone.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$ARM" "$APP/Contents/Resources/arm64"
cp -R "$X64" "$APP/Contents/Resources/x64"

# the launcher, for both CPUs in one file; the same file runs sidetone or sidetone-cli by its own name
clang -O2 -arch arm64 -arch x86_64 -mmacosx-version-min=14.0 -o "$APP/Contents/MacOS/sidetone" "$HERE/launcher.c"
cp "$APP/Contents/MacOS/sidetone" "$APP/Contents/MacOS/sidetone-cli"

sed "s/@VERSION@/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"

# the icon, every size macOS asks for, from the 1024 px one
ICONSET="$(mktemp -d)/sidetone.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
    sips -z $s $s "$ICON" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    sips -z $((s * 2)) $((s * 2)) "$ICON" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/sidetone.icns"

# ad-hoc signature: every program and library inside first, then the bundle
find "$APP/Contents" -type f \( -name "*.dylib" -o -perm -u+x \) -print0 | while IFS= read -r -d '' f; do
    # (not file | grep -q: with pipefail, grep quitting early breaks file's pipe and reads as "not Mach-O")
    case "$(file -b "$f")" in
        *Mach-O*) codesign --force --sign - "$f" ;;
    esac
done
codesign --force --sign - "$APP"
codesign --verify --deep --strict "$APP"

ditto -c -k --keepParent "$APP" "$OUT/Sidetone-$VERSION-macos.zip"
echo "made $OUT/Sidetone-$VERSION-macos.zip"
