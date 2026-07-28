#!/usr/bin/env bash
#
# land.sh — the one-command "squash a finished branch into main" workflow for
# MassiveSLICER, paired with save.sh.
#
# What it does, in order:
#   1. Refuses to run on main itself (landing merges a BRANCH into main).
#   2. Refuses to run with uncommitted changes (commit or stash first).
#   3. Makes sure your branch is caught up with its own remote (so you're not
#      landing a stale copy over a teammate's push to the same branch).
#   4. Switches to main and pulls it, so main is genuinely current.
#   5. Squash-merges your branch into main, with a commit message recording
#      how many commits are landing and what main's new build number becomes
#      — e.g. "build-numbering (b3 -> main 502)".
#   6. Pushes main to GitHub.
#   7. Tags your branch's own tip commit as landed/<branch> (annotated, with
#      a message recording the same numbers) and pushes that tag. This is
#      what the build-numbering feature's Delta counter reads to correctly
#      reset to zero right after a landing — without it, Delta would keep
#      counting already-landed commits forever, since squashing breaks the
#      normal ancestry link git relies on.
#   8. Switches you back to your original branch and tells you it's safe to
#      delete it whenever you like (does NOT delete it automatically — the
#      landed tag keeps the full commit history alive forever regardless).
#
# Usage:
#   ./land.sh                      # summary auto-derived from the branch name
#   ./land.sh "custom summary"     # use your own summary text instead
#
set -euo pipefail

cd "$(dirname "$0")"

BRANCH="$(git rev-parse --abbrev-ref HEAD)"

if [ "$BRANCH" = "main" ]; then
  echo "!! You're on main already. Landing merges a BRANCH into main — switch to your feature branch first."
  exit 1
fi

if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "!! You have uncommitted changes. Commit or stash them first (e.g. ./save.sh), then run land.sh again."
  exit 1
fi

echo "==> Landing branch: $BRANCH"

echo "==> Making sure '$BRANCH' is caught up with its own remote first..."
git pull --no-edit origin "$BRANCH"

echo "==> Fetching latest main..."
git fetch origin main

echo "==> Switching to main and syncing it..."
git checkout main
git pull --no-edit origin main

# Reference point for "how many commits are landing" — the branch's real
# fork point (or last main-sync point) vs. its own tip.
MERGE_BASE="$(git merge-base main "$BRANCH")"
DELTA_COUNT="$(git rev-list --count --first-parent "$MERGE_BASE".."$BRANCH")"

if [ "$DELTA_COUNT" -eq 0 ]; then
  echo "!! Nothing to land — '$BRANCH' has no commits beyond main."
  git checkout "$BRANCH"
  exit 1
fi

# Main's commit count right now, before the squash commit is added — the
# squash always adds exactly one commit, so the new count is just +1.
OLD_MAIN_COUNT="$(git rev-list --count main)"
NEW_MAIN_COUNT=$((OLD_MAIN_COUNT + 1))

# Derive a short label from the branch name (strip a leading feature/, fix/, etc.)
SHORT_LABEL="$(echo "$BRANCH" | sed -E 's#^(feature|fix|bugfix)/##')"
if [ "$#" -ge 1 ]; then
  SUMMARY="$*"
else
  SUMMARY="$SHORT_LABEL"
fi
FINAL_MSG="$SUMMARY (b$DELTA_COUNT -> main $NEW_MAIN_COUNT)"

echo "==> Squash-merging '$BRANCH' into main ($DELTA_COUNT commit(s))..."
git merge --squash "$BRANCH"

if git diff --cached --quiet; then
  echo "!! Nothing to land — '$BRANCH' has no changes not already on main."
  git checkout "$BRANCH"
  exit 1
fi

git commit -m "$FINAL_MSG"

echo "==> Pushing main..."
git push origin main

echo "==> Tagging '$BRANCH' at its own tip as landed..."
BRANCH_TIP="$(git rev-parse "$BRANCH")"
git tag -a "landed/$BRANCH" -m "Landed as main $NEW_MAIN_COUNT ($FINAL_MSG)" "$BRANCH_TIP"
git push origin "refs/tags/landed/$BRANCH"

echo "==> Switching back to '$BRANCH'..."
git checkout "$BRANCH"

echo ""
echo "==> Done. '$BRANCH' is landed on main as build $NEW_MAIN_COUNT."
echo "==> Safe to delete '$BRANCH' whenever you like (not done automatically):"
echo "      git branch -d $BRANCH && git push origin --delete $BRANCH"
