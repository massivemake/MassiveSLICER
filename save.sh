#!/usr/bin/env bash
#
# save.sh — the one-command "don't lose code" workflow for MassiveSLICER.
#
# What it does, in order:
#   1. Stash any uncommitted work so the pull can't be blocked.
#   2. Pull the latest from GitHub (merge) so you're never working on a stale copy.
#   3. Restore your work, commit it, and push it back to GitHub.
#
# Why: code goes missing when someone works on an out-of-date copy and then
# overwrites a teammate's push. Pulling BEFORE pushing — every time — prevents
# that. If GitHub ever rejects a push, the answer is always "pull first, resolve,
# then push", never "force". This script bakes that habit into one command.
#
# Usage:
#   ./save.sh                       # commits with an auto timestamped message
#   ./save.sh "fixed KRL export"    # commits with your message
#
set -euo pipefail

cd "$(dirname "$0")"

# --- figure out the commit message -------------------------------------------
if [ "$#" -ge 1 ]; then
  MSG="$*"
else
  MSG="wip: save progress ($(date '+%Y-%m-%d %H:%M'))"
fi

BRANCH="$(git rev-parse --abbrev-ref HEAD)"
echo "==> Branch: $BRANCH"

# --- 1. stash local work so the pull can't be blocked ------------------------
STASHED=0
if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "==> Stashing local changes so pull can't be blocked..."
  git stash push -u -m "save.sh auto-stash" >/dev/null
  STASHED=1
fi

# --- 2. pull the latest (integrate teammates' work) --------------------------
echo "==> Pulling latest from GitHub (origin/$BRANCH)..."
if ! git pull --no-edit origin "$BRANCH"; then
  echo ""
  echo "!! Pull hit a problem (likely a merge conflict)."
  if [ "$STASHED" -eq 1 ]; then
    echo "!! Your changes are safe in the stash. Recover them with:"
    echo "     git stash pop"
  fi
  echo "!! Resolve the conflict, then run ./save.sh again. Do NOT force-push."
  exit 1
fi

# --- 3. restore your work -----------------------------------------------------
if [ "$STASHED" -eq 1 ]; then
  echo "==> Restoring your stashed changes..."
  if ! git stash pop; then
    echo ""
    echo "!! Your changes conflict with what you just pulled."
    echo "!! Resolve the conflicts shown above, then commit + push manually:"
    echo "     git add -A && git commit -m \"$MSG\" && git push origin $BRANCH"
    exit 1
  fi
fi

# --- commit (only if there's something to commit) ----------------------------
git add -A
if git diff --cached --quiet; then
  echo "==> Nothing new to commit."
else
  echo "==> Committing: $MSG"
  git commit -m "$MSG" >/dev/null
fi

# --- push (covers both fresh commits and any local commits not yet pushed) ---
echo "==> Pushing to GitHub..."
git push origin "$BRANCH"

echo ""
echo "==> Done. '$BRANCH' is backed up on GitHub."
