#!/bin/bash
# Writes a finished PKGBUILD (to stdout) from PKGBUILD.in, for one release.
#   packaging/arch/make-pkgbuild.sh VERSION TAG X64_TARBALL ARM64_TARBALL
# VERSION is like 0.27.0, TAG the release tag (v0.27.0); the tarballs are that release's two
# sidetone-TAG-linux-ARCH.tar.gz files, for their checksums.
set -euo pipefail
here="$(dirname "$(readlink -f "$0")")"
version="$1" tag="$2"
case "$version" in *-*|"") echo "make-pkgbuild: '$version' isn't a pacman version (no '-')" >&2; exit 1 ;; esac
sha() { sha256sum "$1" | cut -d' ' -f1; }
sed -e "s/@VERSION@/$version/" -e "s/@TAG@/$tag/g" \
    -e "s/@SHA_X64@/$(sha "$3")/" -e "s/@SHA_ARM64@/$(sha "$4")/" \
    -e "s/@SHA_INSTALL@/$(sha "$here/sidetone.install")/" \
    "$here/PKGBUILD.in"
