#!/usr/bin/env bash
# scripts/run-semgrep.sh
# Scans only the files changed in this PR (not the whole repo's existing
# backlog) and writes semgrep.json. Requires changed-files.txt (see
# compute-diff.sh) and a working docker daemon.
set -euo pipefail

mapfile -t EXISTING_FILES < <(
  while IFS= read -r f; do
    [ -f "$f" ] && printf '%s\n' "$f"
  done < changed-files.txt
)

if [ "${#EXISTING_FILES[@]}" -eq 0 ]; then
  echo '{"results": []}' > semgrep.json
else
  rm -f semgrep.json
  set +e
  docker run --rm -v "$PWD:/src" -w /src semgrep/semgrep \
    semgrep scan --config=p/security-audit --config=p/secrets \
    --config=p/owasp-top-ten --json --output=semgrep.json \
    --error "${EXISTING_FILES[@]}"
  SEMGREP_EXIT=$?
  set -e
  python3 scripts/validate-semgrep-run.py \
    --exit-code "$SEMGREP_EXIT" \
    --output semgrep.json
fi
