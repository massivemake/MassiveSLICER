# MassiveSLICER — assistant instructions (same content as CLAUDE.md; keep in sync)

Product definition and backlog live in **`ROADMAP.md`**. Past work lives in
**`memory.md`**. This file only carries the rules that must load automatically.

## Work efficiently — this repo is large

Four files are 2,600–14,900 lines; reading `ViewportView.axaml.cs` whole costs ~150k tokens
and buys almost nothing. Rules:

1. **Read `docs/CODE-MAP.md` first.** It maps every subsystem to a file, with section
   anchors inside the big files. Grep the anchor, read a few hundred lines, edit.
2. **Never read a >1,000-line file end to end.** Use `grep -n` to locate, then read with
   `offset`/`limit`. Same for `memory.md` — skim the newest entries, don't ingest 1,200 lines.
3. **`memory.md` is recent history only.** Anything older than the current milestone is in
   `docs/memory-archive.md`; go there only when digging into a specific past decision.
4. **Trace a setting** with the chain in CODE-MAP rather than searching blind.

## Verifying a change (saves a whole wasted cycle)

- **The test suite has 15 known failures** — path/CWD-dependent and WIP tests, listed in
  `docs/KNOWN-TEST-FAILURES.md`. Compare against that list; do **not** re-derive a baseline
  by stashing, and do not treat them as your regression.
- **After any `git stash` test run, force a rebuild** before launching:
  `dotnet build src/MassiveSlicer.App/MassiveSlicer.App.csproj --no-incremental`.
  A stash run leaves binaries built from the stashed source, and a normal incremental build
  reports "0 Errors" while doing nothing (restored files have older mtimes). This has shipped
  a stale build to a tester before.
- **Confirm a change reached the binary** — `strings` gives false negatives on .NET
  (literals are UTF-16):
  `python3 -c "print('my literal'.encode('utf-16-le') in open('src/MassiveSlicer.App/bin/Debug/net8.0/MassiveSlicer.Core.dll','rb').read())"`

## Documentation duties (do these without being asked)

- **`memory.md` = the PAST.** After bug fixes, shipped features, important
  test results, or priority changes: add a dated entry at the top of
  "## Session changelog" (symptom → cause → fix → key files) and bump the
  "Last updated" line. Do this at natural stopping points.
- **`ROADMAP.md` = the FUTURE + product definition.** When you ship a
  backlog item: move it from Planned 🔲 to Built ✅ and remove/trim its
  backlog section. When new work is planned: add it to the backlog with a
  short "why". Keep "What does it do today?" truthful as features land.
- Commit documentation updates together with (or right after) the code they
  describe, and push.

## Build & run (macOS)

```bash
dotnet build src/MassiveSlicer.App/MassiveSlicer.App.csproj
open MassiveSlicer.app        # launcher; regenerate with tools/make_macos_app.sh
```

Build identity is auto-generated: build number = git commit count
(`build N · date · sha` in the status bar). Never hand-edit build numbers.

## Git conventions

- `main` = the ONLY shared branch (GitHub default; `master` was deleted 2026-07 —
  the old master/main split is gone).
- Risky or experimental work goes on a `feature/<name>` branch; merge `main` into
  it regularly, and merge it back to `main` only after real-machine testing.
- Sync = `git pull` on `main` (or merge `origin/main` into your feature branch).
- If a fetch reports **forced update**, history was rewritten upstream —
  verify content (`git diff HEAD origin/<branch>`) before adopting; never
  blind-merge rewritten history.
