#!/bin/bash
# Extracts the GUI screenshots CI renders (printed as base64 in the x64 job log) into a folder.
# Run tools/ci-status.sh first; it saves the log to /tmp/ci-x64.log.
#   tools/ci-screenshots.sh [FOLDER]      # default: ./ci-shots
set -u
out="${1:-ci-shots}"
mkdir -p "$out"
clean() { sed -E 's/^\xEF\xBB\xBF//; s/^[0-9T:.-]+Z //' /tmp/ci-x64.log; }   # log lines carry timestamps, and sometimes a byte-order mark
for name in $(clean | sed -n 's/^-----BEGIN PNG \(.*\)-----$/\1/p'); do
  clean | awk -v t="$name" '$0=="-----BEGIN PNG " t "-----"{f=1;next} $0=="-----END PNG " t "-----"{f=0} f' | base64 -d > "$out/gui-$name.png" 2>/dev/null
  printf '%-28s %s\n' "gui-$name.png" "$(file -b "$out/gui-$name.png" | cut -c1-50)"
done
