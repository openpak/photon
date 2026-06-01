#!/usr/bin/env bash
# release.sh — Mark an area complete, push the session branch, optionally merge.
#
# Part of the BlackIce.Server parallel-session protocol (see CLAUDE_PARALLEL.md).
#
# Usage:   tools/parallel/release.sh <area> [--merge]
# Example: tools/parallel/release.sh authority
#          tools/parallel/release.sh authority --merge
#
# --merge is the explicit opt-in required before touching master. Only use it when
# the full test suite is green on the branch and the work has no in-flight
# dependency on another session. Large features should land via PR/review instead.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$PROJECT_ROOT"

WORK_FILE="PARALLEL_WORK.md"
SESSION_ID="${CLAUDE_SESSION_ID:-$(hostname)-$$}"
TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

AREA="${1:-}"
MERGE_FLAG="${2:-}"

if [[ -z "$AREA" ]]; then
    echo "Usage: $0 <area> [--merge]"
    exit 1
fi

if [[ ! -f "$WORK_FILE" ]]; then
    echo "❌ $WORK_FILE not found. Nothing to release."
    exit 1
fi

# Derive the session branch the same way claim.sh did.
CURRENT_BRANCH="$(git rev-parse --abbrev-ref HEAD)"
if [[ "$CURRENT_BRANCH" == claude/* || "$CURRENT_BRANCH" == feat/* ]]; then
    BRANCH="$CURRENT_BRANCH"
else
    BRANCH="claude/${AREA}"
fi

# Flip the area's marker 🟢 → ✅ and stamp completion on its Status line.
awk -v area="$AREA" -v timestamp="$TIMESTAMP" '
    /^### 🟢 / && $3 == area { sub(/🟢/, "✅"); found = 1 }
    found && /- \*\*Status\*\*: IN PROGRESS/ {
        sub(/IN PROGRESS/, "COMPLETED @ " timestamp); found = 0
    }
    { print }
' "$WORK_FILE" > "${WORK_FILE}.tmp" && mv "${WORK_FILE}.tmp" "$WORK_FILE"

git add -A
git commit -m "feat(${AREA}): complete area [session ${SESSION_ID}]" \
    || echo "→ Nothing new to commit."

echo "→ Pushing ${BRANCH}..."
git push -u origin "${BRANCH}" --force-with-lease

echo ""
echo "✅ Released: ${AREA} (branch ${BRANCH} pushed)"

if [[ "$MERGE_FLAG" == "--merge" ]]; then
    echo "→ Merging ${BRANCH} into master (explicit --merge)..."
    git checkout master
    git pull origin master
    git merge "${BRANCH}" --no-ff \
        -m "merge(${AREA}): integrate session branch [${TIMESTAMP}]"
    git push origin master
    git checkout "${BRANCH}"
    echo "✅ Merged into master."
fi

echo ""
echo "Next: another session can now safely claim files in this area."
