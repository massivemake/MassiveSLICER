#!/usr/bin/env bash
#
# save.sh — commit real work, pull, push.
# This Mac: bash save.sh   |   Shop PC: .\save.ps1
#
# No stash — on this SMB share stash fails with:
#   error: unable to create file save.sh: File exists
# Flow: stage → commit (if needed) → pull → push.
# Every git call uses  -c safe.directory=*
#
set -euo pipefail

say() { printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*"; }

G=(-c safe.directory='*')
g() { say "git $*"; git "${G[@]}" "$@"; }

cd "$(dirname "$0")"
say "=== MassiveSLICER save.sh ==="
say "folder: $(pwd)"
say "using: git -c safe.directory=*  (no stash — SMB-safe)"

add_safe_dir() {
  local p="${1%/}"
  [ -z "$p" ] && return 0
  if ! git "${G[@]}" config --global --get-all safe.directory 2>/dev/null | grep -Fxq "$p"; then
    git "${G[@]}" config --global --add safe.directory "$p" >/dev/null 2>&1 || true
    say "safe.directory (global) += $p"
  fi
}

add_safe_dir "$(pwd -P 2>/dev/null || pwd)"
add_safe_dir "/Volumes/MassiveFILES/Research/LFAM/MassiveSLICER"
add_safe_dir "//192.168.0.191/MassiveFILES/Research/LFAM/MassiveSLICER"
add_safe_dir "*"

g config core.filemode false || true
g config core.trustctime false || true
g config core.untrackedCache false || true

if [ -f .git/index.lock ]; then
  if pgrep -x git >/dev/null 2>&1; then
    say "STOP: .git/index.lock exists and git is running."
    exit 1
  fi
  say "removing stale .git/index.lock"
  rm -f .git/index.lock
fi

# Drop leftover auto-stashes from older scripts that failed mid-stash.
if git "${G[@]}" stash list 2>/dev/null | grep -qE 'save\.(ps1|sh) auto-stash'; then
  say "dropping leftover save auto-stash..."
  g stash clear || true
fi

g update-index --refresh >/dev/null 2>&1 || true

inside="$(git "${G[@]}" rev-parse --is-inside-work-tree 2>/dev/null || true)"
say "rev-parse --is-inside-work-tree => '$inside'"
if [ "$inside" != "true" ]; then
  say "STOP: Git does not see a repo here."
  exit 1
fi

if [ "$#" -ge 1 ]; then
  MSG="$*"
else
  MSG="wip: save progress ($(date '+%Y-%m-%d %H:%M'))"
fi

BRANCH="$(git "${G[@]}" rev-parse --abbrev-ref HEAD)"
BEFORE="$(git "${G[@]}" log -1 --oneline)"
say "branch: $BRANCH"
say "HEAD:   $BEFORE"
say "message: $MSG"

say "--- status before ---"
g status -sb
g diff --stat || true

say "staging real files..."
g add -A
g reset -q -- install.sh 2>/dev/null || true
SKIPPED=0
while IFS=$'\t' read -r add del path; do
  [ -z "${path:-}" ] && continue
  if [ "$add" = "0" ] && [ "$del" = "0" ]; then
    g reset -q HEAD -- "$path" || true
    SKIPPED=$((SKIPPED + 1))
  fi
done < <(git "${G[@]}" diff --cached --numstat)
if [ "$SKIPPED" -gt 0 ]; then
  say "unstaged $SKIPPED chmod-only file(s)"
fi

say "--- staged ---"
g diff --cached --stat || true

if git "${G[@]}" diff --cached --quiet; then
  say "nothing new to commit"
else
  say "committing: $MSG"
  g commit -m "$MSG"
  say "commit OK"
fi

say "pulling origin/$BRANCH ..."
if ! g pull --no-edit origin "$BRANCH"; then
  say "STOP: pull failed (likely merge conflict)."
  say "Fix, then: git -c safe.directory=* add -A && git -c safe.directory=* commit && git -c safe.directory=* push"
  exit 1
fi
say "pull OK"

say "pushing origin/$BRANCH ..."
g push origin "$BRANCH"

AFTER="$(git "${G[@]}" log -1 --oneline)"
say "--- status after ---"
g status -sb
say "HEAD now: $AFTER"
say "=== DONE. $BRANCH is on GitHub. ==="
