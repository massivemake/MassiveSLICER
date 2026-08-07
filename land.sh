#!/usr/bin/env bash
#
# land.sh — the one-command "squash a finished branch into main" workflow for
# MassiveSLICER, paired with save.sh.
#
# Works from ANY folder of this repo — your dev checkout or a dedicated main
# worktree — and figures out the rest itself. It never guesses which branch you
# meant: if that isn't obvious it stops and tells you exactly how to say it.
#
# What it does, in order:
#   1. Works out which branch is being landed (see "Choosing the branch" below).
#   2. Refuses if that branch, or main, has uncommitted changes.
#   3. Makes sure the branch is caught up with its own remote (so you're not
#      landing a stale copy over a teammate's push to the same branch).
#   4. Brings main fully up to date.
#   5. Squash-merges the branch into main, with a commit message recording how
#      many commits are landing and what main's new build number becomes
#      — e.g. "build-numbering (b3 -> main 502)".
#   6. Verifies main's content now matches the branch exactly, then pushes main.
#   7. Tags the branch's own tip as landed/<branch> (annotated, recording the
#      same numbers) and pushes that tag. This is what the build-numbering
#      feature's Delta counter reads to correctly reset to zero right after a
#      landing — without it, Delta would keep counting already-landed commits
#      forever, since squashing breaks the normal ancestry link git relies on.
#   8. Leaves every folder on the branch it started on, and tells you the
#      branch is safe to delete whenever you like (does NOT delete it — the
#      landed tag keeps the full commit history alive regardless).
#
# Choosing the branch:
#   - Run it from a folder that's on a feature branch → that's the branch.
#   - Run it from a folder that's on main → it will NOT pick one for you. It
#     lists the candidates and asks you to name one with --branch. This is the
#     case that used to be dangerous: two folders sharing one repo, and the
#     script acting on whichever one it happened to live in.
#
# Main in a separate worktree:
#   If main is checked out in another folder (git worktree), the main-side work
#   happens over there via `git -C` and nothing here is ever checked out. The
#   old version did `git checkout main` in whatever folder it ran from, which
#   git refuses with "main is already checked out" — that's what this fixes.
#   A plain single-folder clone still works exactly as before.
#
# Usage:
#   ./land.sh                          # branch = the one this folder is on
#   ./land.sh "custom summary"         # ...with your own summary text
#   ./land.sh --branch fix/foo         # name the branch explicitly
#   ./land.sh --branch fix/foo "text"
#   ./land.sh --dry-run                # show exactly what it WOULD do, change nothing
#
set -euo pipefail

cd "$(dirname "$0")"

MAIN="main"

# ---------------------------------------------------------------- arguments --
BRANCH=""
SUMMARY=""
DRY_RUN=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --branch)   BRANCH="${2:-}"; shift 2 ;;
    --branch=*) BRANCH="${1#*=}"; shift ;;
    --dry-run)  DRY_RUN=1; shift ;;
    -h|--help)  sed -n '2,48p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)          SUMMARY="${SUMMARY:+$SUMMARY }$1"; shift ;;
  esac
done

run() {
  if [ "$DRY_RUN" -eq 1 ]; then
    echo "   [dry-run] $*"
  else
    "$@"
  fi
}

# ------------------------------------------------- map worktrees to branches --
# `git worktree list --porcelain` emits, per worktree, a "worktree <path>" line
# and (unless detached) a "branch refs/heads/<name>" line, blocks blank-separated.
WT_PATHS=()
WT_BRANCHES=()
while IFS= read -r line; do
  case "$line" in
    worktree\ *) WT_PATHS+=("${line#worktree }"); WT_BRANCHES+=("") ;;
    branch\ refs/heads/*) WT_BRANCHES[${#WT_BRANCHES[@]}-1]="${line#branch refs/heads/}" ;;
  esac
done < <(git worktree list --porcelain)

MAIN_WT=""
for i in "${!WT_PATHS[@]}"; do
  [ "${WT_BRANCHES[$i]}" = "$MAIN" ] && MAIN_WT="${WT_PATHS[$i]}"
done

# Folder we were invoked from, and the branch it's on.
HERE="$(git rev-parse --show-toplevel)"
HERE_BRANCH="$(git rev-parse --abbrev-ref HEAD)"

# ------------------------------------------------------- choose the branch ---
if [ -z "$BRANCH" ]; then
  if [ "$HERE_BRANCH" != "$MAIN" ] && [ "$HERE_BRANCH" != "HEAD" ]; then
    BRANCH="$HERE_BRANCH"
  else
    # We're sitting on main (or detached). Do NOT guess — name the candidates.
    CANDIDATES=()
    for i in "${!WT_PATHS[@]}"; do
      b="${WT_BRANCHES[$i]}"
      [ -n "$b" ] && [ "$b" != "$MAIN" ] && CANDIDATES+=("$b")
    done

    echo "!! This folder is on '$HERE_BRANCH', so there's no feature branch here to land."
    echo "!!   $HERE"
    echo ""
    if [ "${#CANDIDATES[@]}" -eq 0 ]; then
      echo "!! No other folder of this repo is on a feature branch either, so there is"
      echo "!! nothing to land. Checked:"
      for i in "${!WT_PATHS[@]}"; do
        echo "!!   ${WT_PATHS[$i]}  [${WT_BRANCHES[$i]:-detached}]"
      done
    else
      echo "!! Which branch do you want to land? Candidates:"
      for i in "${!WT_PATHS[@]}"; do
        b="${WT_BRANCHES[$i]}"
        [ -n "$b" ] && [ "$b" != "$MAIN" ] && echo "!!   $b   (checked out in ${WT_PATHS[$i]})"
      done
      echo ""
      echo "!! Say it explicitly, e.g.:"
      echo "!!   ./land.sh --branch ${CANDIDATES[0]}"
    fi
    exit 1
  fi
fi

if ! git show-ref --verify --quiet "refs/heads/$BRANCH"; then
  echo "!! No such branch: '$BRANCH'"
  exit 1
fi
if [ "$BRANCH" = "$MAIN" ]; then
  echo "!! '$MAIN' is not a branch you land — landing merges a feature branch INTO $MAIN."
  exit 1
fi

# Which folder holds the branch (so we pull it in the right place)?
BRANCH_WT=""
for i in "${!WT_PATHS[@]}"; do
  [ "${WT_BRANCHES[$i]}" = "$BRANCH" ] && BRANCH_WT="${WT_PATHS[$i]}"
done
[ -z "$BRANCH_WT" ] && BRANCH_WT="$HERE"

echo "==> Landing branch: $BRANCH"
echo "==>   branch folder: $BRANCH_WT"
echo "==>   main folder:   ${MAIN_WT:-<not checked out anywhere; will switch this folder>}"
[ "$DRY_RUN" -eq 1 ] && echo "==>   DRY RUN — nothing will be changed or pushed"

# ------------------------------------------------------- cleanliness checks --
dirty() { ! git -C "$1" diff --quiet || ! git -C "$1" diff --cached --quiet; }

if dirty "$BRANCH_WT"; then
  echo "!! '$BRANCH' has uncommitted changes in $BRANCH_WT"
  echo "!! Commit or stash them first (e.g. ./save.sh), then run land.sh again."
  exit 1
fi
if [ -n "$MAIN_WT" ] && dirty "$MAIN_WT"; then
  echo "!! The main folder has uncommitted changes: $MAIN_WT"
  echo "!! Landing commits to main there, so clear those first."
  exit 1
fi

# ---------------------------------------------------------- sync the branch --
echo "==> Making sure '$BRANCH' is caught up with its own remote first..."
if git ls-remote --exit-code --heads origin "$BRANCH" >/dev/null 2>&1; then
  run git -C "$BRANCH_WT" pull --no-edit origin "$BRANCH"
else
  echo "   ('$BRANCH' isn't on GitHub yet — nothing to catch up with.)"
fi

echo "==> Fetching latest main..."
run git fetch origin "$MAIN"

# ------------------------------------------------------------- sync main -----
# If main lives in its own folder, work there and never check anything out here.
# Otherwise fall back to the classic single-folder checkout dance.
RESTORE_BRANCH=""
if [ -n "$MAIN_WT" ]; then
  MAIN_GIT=(git -C "$MAIN_WT")
  echo "==> Syncing main in its own folder ($MAIN_WT)..."
else
  MAIN_GIT=(git)
  RESTORE_BRANCH="$HERE_BRANCH"
  echo "==> Switching this folder to main and syncing it..."
  run git checkout "$MAIN"
fi
run "${MAIN_GIT[@]}" pull --no-edit origin "$MAIN"

restore() {
  if [ -n "$RESTORE_BRANCH" ]; then
    echo "==> Switching back to '$RESTORE_BRANCH'..."
    run git checkout "$RESTORE_BRANCH"
  fi
}

# ------------------------------------------------------ counts for the msg ---
# Reference point for "how many commits are landing". Squashing breaks ancestry,
# so after a branch has landed once, merge-base still points at the old fork and
# would re-count commits that already shipped. The landed/<branch> tag is the
# real record of where this branch last landed — prefer it when it exists.
LANDED_TAG="landed/$BRANCH"
RELANDING=0
if git rev-parse -q --verify "refs/tags/$LANDED_TAG" >/dev/null &&
   git merge-base --is-ancestor "$LANDED_TAG" "$BRANCH" 2>/dev/null; then
  MERGE_BASE="$(git rev-parse "$LANDED_TAG")"
  RELANDING=1
  echo "==> '$BRANCH' landed before; counting from tag $LANDED_TAG."
else
  MERGE_BASE="$("${MAIN_GIT[@]}" merge-base "$MAIN" "$BRANCH")"
fi
DELTA_COUNT="$("${MAIN_GIT[@]}" rev-list --count --first-parent "$MERGE_BASE".."$BRANCH")"

if [ "$DELTA_COUNT" -eq 0 ]; then
  echo "!! Nothing to land — '$BRANCH' has no commits beyond main."
  restore
  exit 1
fi

OLD_MAIN_COUNT="$("${MAIN_GIT[@]}" rev-list --count "$MAIN")"
NEW_MAIN_COUNT=$((OLD_MAIN_COUNT + 1))

SHORT_LABEL="$(echo "$BRANCH" | sed -E 's#^(feature|fix|bugfix)/##')"
[ -z "$SUMMARY" ] && SUMMARY="$SHORT_LABEL"
FINAL_MSG="$SUMMARY (b$DELTA_COUNT -> main $NEW_MAIN_COUNT)"

echo "==> Squash-merging '$BRANCH' into main ($DELTA_COUNT commit(s))..."
echo "==>   message: $FINAL_MSG"

if [ "$DRY_RUN" -eq 1 ]; then
  echo "   [dry-run] git merge --squash $BRANCH   (in ${MAIN_WT:-$HERE})"
  echo "   [dry-run] git commit -m \"$FINAL_MSG\""
  echo "   [dry-run] verify: git diff $BRANCH $MAIN --stat  must be empty"
  echo "   [dry-run] git push origin $MAIN"
  if [ "$RELANDING" -eq 1 ]; then
    echo "   [dry-run] git tag -f -a $LANDED_TAG ... $(git rev-parse "$BRANCH")   (moving existing tag)"
    echo "   [dry-run] git push --force origin refs/tags/$LANDED_TAG"
  else
    echo "   [dry-run] git tag -a $LANDED_TAG -m \"Landed as main $NEW_MAIN_COUNT ($FINAL_MSG)\" $(git rev-parse "$BRANCH")"
    echo "   [dry-run] git push origin refs/tags/$LANDED_TAG"
  fi
  restore
  echo ""
  echo "==> Dry run complete. Nothing was changed."
  exit 0
fi

"${MAIN_GIT[@]}" merge --squash "$BRANCH"

if "${MAIN_GIT[@]}" diff --cached --quiet; then
  echo "!! Nothing to land — '$BRANCH' has no changes not already on main."
  "${MAIN_GIT[@]}" merge --abort 2>/dev/null || "${MAIN_GIT[@]}" reset --hard >/dev/null
  restore
  exit 1
fi

"${MAIN_GIT[@]}" commit -m "$FINAL_MSG"

# Last gate before anything leaves the machine: main must now be byte-identical
# to the branch. If it isn't, the squash dropped or added something.
echo "==> Verifying main matches '$BRANCH' exactly..."
if ! "${MAIN_GIT[@]}" diff --quiet "$BRANCH" "$MAIN"; then
  echo "!! STOPPED — main does not match '$BRANCH' after the squash:"
  "${MAIN_GIT[@]}" diff "$BRANCH" "$MAIN" --stat
  echo "!! Nothing has been pushed. Inspect the difference above before continuing."
  echo "!! To undo the local squash commit:  git -C ${MAIN_WT:-$HERE} reset --hard origin/$MAIN"
  restore
  exit 1
fi
echo "   clean — main and '$BRANCH' are identical."

echo "==> Pushing main..."
"${MAIN_GIT[@]}" push origin "$MAIN"

echo "==> Tagging '$BRANCH' at its own tip as landed..."
BRANCH_TIP="$(git rev-parse "$BRANCH")"
if [ "$RELANDING" -eq 1 ]; then
  # Second (or later) landing of the same branch. The Delta counter wants the
  # tag to say where this branch landed MOST recently, so move it forward.
  echo "   (moving existing $LANDED_TAG forward to $BRANCH_TIP)"
  git tag -f -a "$LANDED_TAG" -m "Landed as main $NEW_MAIN_COUNT ($FINAL_MSG)" "$BRANCH_TIP" >/dev/null
  git push --force origin "refs/tags/$LANDED_TAG"
else
  git tag -a "$LANDED_TAG" -m "Landed as main $NEW_MAIN_COUNT ($FINAL_MSG)" "$BRANCH_TIP"
  git push origin "refs/tags/$LANDED_TAG"
fi

restore

echo ""
echo "==> Done. '$BRANCH' is landed on main as build $NEW_MAIN_COUNT."
echo "==> Safe to delete '$BRANCH' whenever you like (not done automatically):"
echo "      git branch -d $BRANCH && git push origin --delete $BRANCH"
