#!/bin/bash
# Builds Sidetone's AppImage from a published folder (the same files as the release tarball).
#   packaging/appimage/build-appimage.sh PUBLISHED_DIR OUTPUT.AppImage [TOOLS_DIR]
# Builds for the machine it runs on (x86_64 or aarch64), as CI does on each architecture's runner.
# appimagetool and the AppImage runtime are pinned releases, checked against their SHA-256 sums.
set -euo pipefail

published="$(readlink -f "$1")"
output="$(readlink -m "$2")"
tools="$(readlink -m "${3:-${TMPDIR:-/tmp}/sidetone-appimage-tools}")"
here="$(dirname "$(readlink -f "$0")")"
packaging="$(dirname "$here")"

arch="$(uname -m)"
case "$arch" in
    x86_64)
        tool_sha=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
        runtime_sha=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;;
    aarch64)
        tool_sha=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158
        runtime_sha=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;;
    *) echo "build-appimage: no AppImage for $arch" >&2; exit 1 ;;
esac
tool_url="https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-$arch.AppImage"
runtime_url="https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-$arch"

fetch() {   # url, file, sha256
    if [ ! -f "$2" ] || ! echo "$3  $2" | sha256sum -c --status; then
        curl -fsSL -o "$2" "$1"
    fi
    echo "$3  $2" | sha256sum -c --status || { echo "build-appimage: checksum mismatch for $2" >&2; exit 1; }
}
mkdir -p "$tools"
fetch "$tool_url" "$tools/appimagetool-$arch.AppImage" "$tool_sha"
fetch "$runtime_url" "$tools/runtime-$arch" "$runtime_sha"
chmod +x "$tools/appimagetool-$arch.AppImage"

for f in sidetone sidetone-cli libvocoder.so; do
    [ -f "$published/$f" ] || { echo "build-appimage: $published has no $f" >&2; exit 1; }
done

appdir="$(mktemp -d)/Sidetone.AppDir"
trap 'rm -rf "$(dirname "$appdir")"' EXIT
mkdir -p "$appdir/usr/lib/sidetone" "$appdir/usr/share/applications" \
         "$appdir/usr/share/icons/hicolor/256x256/apps" "$appdir/usr/share/icons/hicolor/scalable/apps"
cp -a "$published/." "$appdir/usr/lib/sidetone/"
rm -f "$appdir/usr/lib/sidetone/libcoreclrtraceptprovider.so"     # .NET's tracing hook: needs lttng, unused
rm -f "$appdir/usr/lib/sidetone/dvmcli" "$appdir/usr/lib/sidetone/dvmgui"   # the old names: a link named sidetone-cli does it here
install -m 755 "$here/AppRun" "$appdir/AppRun"
install -m 644 "$packaging/sidetone.desktop" "$appdir/sidetone.desktop"
install -m 644 "$packaging/sidetone.desktop" "$appdir/usr/share/applications/sidetone.desktop"
install -m 644 "$packaging/icons/sidetone-256.png" "$appdir/sidetone.png"
install -m 644 "$packaging/icons/sidetone-256.png" "$appdir/usr/share/icons/hicolor/256x256/apps/sidetone.png"
install -m 644 "$packaging/icons/sidetone.svg" "$appdir/usr/share/icons/hicolor/scalable/apps/sidetone.svg"
ln -s sidetone.png "$appdir/.DirIcon"

mkdir -p "$(dirname "$output")"
rm -f "$output"
ARCH="$arch" APPIMAGE_EXTRACT_AND_RUN=1 "$tools/appimagetool-$arch.AppImage" \
    --runtime-file "$tools/runtime-$arch" --no-appstream "$appdir" "$output" >/dev/null
chmod +x "$output"
echo "built $output"
