#!/usr/bin/env bash
#
# jsave.sh — Jeff's "back up my work to GitHub" one-command workflow.
#
# Same job as save.sh (commit everything on your branch and push it), but safe
# in a two-folder setup. save.sh acts on whichever folder its own file sits in,
# so running the copy that lives in the main worktree commits and pushes
# straight to main — silently, with no warning. This one won't.
#
# Kept as a separate file on purpose: save.sh belongs to the shared
# massivemake account and other people rely on it as-is. Fixing it there is a
# conversation to have with the team, not a thing to do unilaterally.
#
# What it does, in order:
#   1. Works out which branch you mean (see "Choosing the branch" below).
#   2. Refuses outright to commit or push main. Backing up work means backing
#      up YOUR branch; putting things on main is what land.sh is for.
#   3. Stashes anything uncommitted so the pull can't be blocked.
#   4. Pulls that branch's own remote (never main), so you don't push over a
#      teammate's work on the same branch.
#   5. Restores the stash, commits everything, pushes.
#   6. Creates the branch on GitHub if it isn't there yet.
#
# Choosing the branch:
#   - Run it from a folder that's on a feature branch → that's the branch.
#   - Run it from a folder that's on main → it will NOT pick one for you. It
#     lists the candidates and asks you to name one with --branch.
#
# Usage:
#   ./jsave.sh                          # message auto-generated
#   ./jsave.sh "what I did"             # your own commit message
#   ./jsave.sh --branch fix/foo "text"  # name the branch explicitly
#   ./jsave.sh --dry-run                # show what it WOULD do, change nothing
#
set -euo pipefail

cd "$(dirname "$0")"

MAIN="main"

# ---------------------------------------------------------------- arguments --
BRANCH=""
MSG=""
DRY_RUN=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --branch)   BRANCH="${2:-}"; shift 2 ;;
    --branch=*) BRANCH="${1#*=}"; shift ;;
    --dry-run)  DRY_RUN=1; shift ;;
    -h|--help)  sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)          MSG="${MSG:+$MSG }$1"; shift ;;
  esac
done

[ -z "$MSG" ] && MSG="wip: save progress ($(date '+%Y-%m-%d %H:%M'))"

run() {
  if [ "$DRY_RUN" -eq 1 ]; then
    echo "   [dry-run] $*"
  else
    "$@"
  fi
}

# ------------------------------------------------- map worktrees to branches --
WT_PATHS=()
WT_BRANCHES=()
while IFS= read -r line; do
  case "$line" in
    worktree\ *) WT_PATHS+=("${line#worktree }"); WT_BRANCHES+=("") ;;
    branch\ refs/heads/*) WT_BRANCHES[${#WT_BRANCHES[@]}-1]="${line#branch refs/heads/}" ;;
  esac
done < <(git worktree list --porcelain)

HERE="$(git rev-parse --show-toplevel)"
HERE_BRANCH="$(git rev-parse --abbrev-ref HEAD)"

# ------------------------------------------------------- choose the branch ---
if [ -z "$BRANCH" ]; then
  if [ "$HERE_BRANCH" != "$MAIN" ] && [ "$HERE_BRANCH" != "HEAD" ]; then
    BRANCH="$HERE_BRANCH"
  else
    CANDIDATES=()
    for i in "${!WT_PATHS[@]}"; do
      b="${WT_BRANCHES[$i]}"
      [ -n "$b" ] && [ "$b" != "$MAIN" ] && CANDIDATES+=("$b")
    done

    echo "!! This folder is on '$HERE_BRANCH':"
    echo "!!   $HERE"
    echo "!! jsave.sh backs up a FEATURE BRANCH. It will not commit or push main."
    echo ""
    if [ "${#CANDIDATES[@]}" -eq 0 ]; then
      echo "!! No folder of this repo is on a feature branch, so there's nothing to back up:"
      for i in "${!WT_PATHS[@]}"; do
        echo "!!   ${WT_PATHS[$i]}  [${WT_BRANCHES[$i]:-detached}]"
      done
    else
      echo "!! Which branch do you want to back up? Candidates:"
      for i in "${!WT_PATHS[@]}"; do
        b="${WT_BRANCHES[$i]}"
        [ -n "$b" ] && [ "$b" != "$MAIN" ] && echo "!!   $b   (checked out in ${WT_PATHS[$i]})"
      done
      echo ""
      echo "!! Say it explicitly, e.g.:"
      echo "!!   ./jsave.sh --branch ${CANDIDATES[0]}"
    fi
    exit 1
  fi
fi

if [ "$BRANCH" = "$MAIN" ]; then
  echo "!! Refusing to commit or push '$MAIN'."
  echo "!! jsave.sh backs up your own branch. To put work ON main, use land.sh."
  exit 1
fi
if ! git show-ref --verify --quiet "refs/heads/$BRANCH"; then
  echo "!! No such branch: '$BRANCH'"
  exit 1
fi

# Which folder holds that branch? That's where the work actually lives.
BRANCH_WT=""
for i in "${!WT_PATHS[@]}"; do
  [ "${WT_BRANCHES[$i]}" = "$BRANCH" ] && BRANCH_WT="${WT_PATHS[$i]}"
done
if [ -z "$BRANCH_WT" ]; then
  echo "!! '$BRANCH' exists but isn't checked out in any folder, so there are no"
  echo "!! working files to back up. Check it out first."
  exit 1
fi

G=(git -C "$BRANCH_WT")

echo "==> Backing up branch: $BRANCH"
echo "==>   folder: $BRANCH_WT"
[ "$DRY_RUN" -eq 1 ] && echo "==>   DRY RUN — nothing will be changed or pushed"

# ------------------------------------------------------------ stash + pull ---
STASHED=0
if ! "${G[@]}" diff --quiet || ! "${G[@]}" diff --cached --quiet ||
   [ -n "$("${G[@]}" ls-files --others --exclude-standard)" ]; then
  echo "==> Stashing local changes so the pull can't be blocked..."
  run "${G[@]}" stash push -u -m "jsave.sh auto-stash"
  STASHED=1
fi

if git ls-remote --exit-code --heads origin "$BRANCH" >/dev/null 2>&1; then
  echo "==> Pulling latest from GitHub (origin/$BRANCH)..."
  if ! run "${G[@]}" pull --no-edit origin "$BRANCH"; then
    echo ""
    echo "!! Pull hit a problem (likely a merge conflict)."
    if [ "$STASHED" -eq 1 ]; then
      echo "!! Your changes are safe in the stash. Recover them with:"
      echo "!!   git -C $BRANCH_WT stash pop"
    fi
    echo "!! Resolve the conflict, then run jsave.sh again. Do NOT force-push."
    exit 1
  fi
else
  echo "==> '$BRANCH' isn't on GitHub yet — it'll be created by the push."
fi

if [ "$STASHED" -eq 1 ]; then
  echo "==> Restoring your stashed changes..."
  if ! run "${G[@]}" stash pop; then
    echo ""
    echo "!! Your changes conflict with what was just pulled."
    echo "!! Resolve the conflicts shown above, then commit + push by hand:"
    echo "!!   git -C $BRANCH_WT add -A"
    echo "!!   git -C $BRANCH_WT commit -m \"$MSG\""
    echo "!!   git -C $BRANCH_WT push origin $BRANCH"
    exit 1
  fi
fi

# --------------------------------------------------------- commit and push ---
run "${G[@]}" add -A
if [ "$DRY_RUN" -eq 0 ] && "${G[@]}" diff --cached --quiet; then
  echo "==> Nothing new to commit."
else
  echo "==> Committing: $MSG"
  run "${G[@]}" commit -m "$MSG"
fi

echo "==> Pushing to GitHub..."
run "${G[@]}" push -u origin "$BRANCH"

if [ "$DRY_RUN" -eq 1 ]; then
  echo ""
  echo "==> Dry run complete. Nothing was changed."
  exit 0
fi

echo ""
echo "==> Done. '$BRANCH' is backed up on GitHub."
