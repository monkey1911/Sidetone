#!/bin/bash
# Waits for the "Build (Linux)" CI run of the current commit, then prints the job results, build
# errors and failed self-tests. The x64 log is kept in /tmp/ci-x64.log (tools/ci-screenshots.sh
# reads it). Needs the GitHub CLI (gh), signed in. Run from anywhere in the repository.
#   ERRS=40 tools/ci-status.sh     # show more error lines
set -u
cd "$(git rev-parse --show-toplevel)"
R=$(gh repo view --json nameWithOwner --jq .nameWithOwner)
sha=$(git rev-parse HEAD)
id=""
for i in $(seq 1 30); do
  id=$(gh run list -R "$R" --workflow "Build (Linux)" --limit 5 --json databaseId,headSha --jq ".[] | select(.headSha==\"$sha\") | .databaseId" | head -1)
  [ -n "$id" ] && break; sleep 5
done
[ -z "$id" ] && { echo "no CI run found for ${sha:0:7} (pushed?)"; exit 1; }
echo "run $id (${sha:0:7})"
gh run watch "$id" -R "$R" --exit-status --interval 15 2>&1 | grep -E "^(X|✓) " | sort -u | tail -4
for j in $(gh api "repos/$R/actions/runs/$id/jobs" --jq '.jobs[].id'); do
  gh api "repos/$R/check-runs/$j/annotations" --jq '.[] | "\(.annotation_level): \(.path):\(.start_line) \(.message)"' 2>&1 | grep -v "Node.js\|ubuntu-latest" | sort -u | head -"${ERRS:-25}"
done
j=$(gh api "repos/$R/actions/runs/$id/jobs" --jq '.jobs[]|select(.name|contains("x64"))|.id')
gh api "repos/$R/actions/jobs/$j/logs" > /tmp/ci-x64.log 2>&1
clean() { sed -E 's/^\xEF\xBB\xBF//; s/^[0-9T:.-]+Z //'; }
grep -E "FAIL|ALL TESTS|TEST\(S\) FAILED|error CS|error NETSDK|error MSB|error NU|error AVLN|Unhandled exception" /tmp/ci-x64.log | clean | sort -u | head -"${ERRS:-25}"
grep -c "PASS:" /tmp/ci-x64.log | sed 's/^/self-test PASS lines: /'
