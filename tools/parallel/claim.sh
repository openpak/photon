#!/usr/bin/env bash
# claim.sh — Register this Claude Code session as owning an area of the codebase.
#
# Part of the BlackIce.Server parallel-session protocol (see CLAUDE_PARALLEL.md).
# Multiple concurrent sessions coordinate file ownership through a single tracked
# coordinator file, PARALLEL_WORK.md, at the repo root.
#
# Usage:   tools/parallel/claim.sh <area> <files_or_dirs> [description]
# Example: tools/parallel/claim.sh authority "server/BlackIce.Server.LoadBalancing/Authority/*" "outcome rules"
#
# Env:
#   CLAUDE_SESSION_ID  — overrides the session identifier (default: host-PID).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$PROJECT_ROOT"

WORK_FILE="PARALLEL_WORK.md"
SESSION_ID="${CLAUDE_SESSION_ID:-$(hostname)-$$}"
TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

AREA="${1:-}"
FILES="${2:-}"
DESCRIPTION="${3:-No description provided}"

if [[ -z "$AREA" || -z "$FILES" ]]; then
    echo "Usage: $0 <area> <files_or_dirs> [description]"
    echo "Example: $0 authority 'server/BlackIce.Server.LoadBalancing/Authority/*' 'outcome rules'"
    exit 1
fi

# Branch model: sessions live on claude/* or feat/* branches. If we are already on
# such a branch, claim against it; otherwise derive claude/<area>.
CURRENT_BRANCH="$(git rev-parse --abbrev-ref HEAD)"
if [[ "$CURRENT_BRANCH" == claude/* || "$CURRENT_BRANCH" == feat/* ]]; then
    BRANCH="$CURRENT_BRANCH"
else
    BRANCH="claude/${AREA}"
fi

# Pull latest before claiming so the coordinator reflects merged work.
echo "→ Syncing with origin/master..."
git fetch origin master
git rebase origin/master 2>/dev/null || echo "  (rebase skipped — resolve manually if behind)"

# Warn if the target files are already claimed by an ACTIVE (🟢) session.
# Completed (✅) claims have released their files, so they don't count. The
# Files value is compared exactly (the '*' in a glob is not a regex here).
if [[ -f "$WORK_FILE" ]]; then
    CONFLICT_BLOCK="$(awk -v f="$FILES" '
        function flush() { if (show && blk != "") print blk; show = 0 }
        /^### / { flush(); active = ($0 ~ /🟢/); blk = $0; next }
        blk != "" { blk = blk "\n" $0 }
        active && /\*\*Files\*\*:/ {
            v = $0; sub(/^[^`]*`/, "", v); sub(/`.*/, "", v)
            if (v == f) show = 1
        }
        END { flush() }
    ' "$WORK_FILE")"
    if [[ -n "$CONFLICT_BLOCK" ]]; then
        echo "⚠️  WARNING: '$FILES' is already claimed by an active session:"
        echo "$CONFLICT_BLOCK"
        read -rp "Continue anyway? [y/N] " confirm
        [[ "$confirm" =~ ^[Yy]$ ]] || exit 1
    fi
fi

# Bootstrap the coordinator file on first use.
if [[ ! -f "$WORK_FILE" ]]; then
    cat > "$WORK_FILE" <<'EOF'
# Parallel Work Coordinator

Auto-managed by tools/parallel/claim.sh and release.sh — do not edit by hand.

## Active Sessions
EOF
fi

# Append the claim entry.
cat >> "$WORK_FILE" <<EOF

### 🟢 ${AREA}
- **Session**: \`${SESSION_ID}\`
- **Branch**: \`${BRANCH}\`
- **Files**: \`${FILES}\`
- **Description**: ${DESCRIPTION}
- **Claimed**: ${TIMESTAMP}
- **Status**: IN PROGRESS
EOF

# Ensure we are on the session branch.
if [[ "$BRANCH" != "$CURRENT_BRANCH" ]]; then
    if git show-ref --quiet "refs/heads/${BRANCH}"; then
        echo "→ Checking out existing branch '${BRANCH}'..."
        git checkout "${BRANCH}"
    else
        echo "→ Creating branch '${BRANCH}'..."
        git checkout -b "${BRANCH}"
    fi
fi

git add "$WORK_FILE"
git commit -m "chore: claim area '${AREA}' [session ${SESSION_ID}]" || true

echo ""
echo "✅ Claimed: ${AREA}"
echo "   Branch:  ${BRANCH}"
echo "   Files:   ${FILES}"
echo "   Session: ${SESSION_ID}"
echo ""
echo "When done: tools/parallel/release.sh ${AREA}"
