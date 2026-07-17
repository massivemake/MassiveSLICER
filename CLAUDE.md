# MassiveSLICER — assistant instructions

Full project context lives in **`memory.md`** (repo root) — read it at the
start of any substantive session. Product definition and backlog live in
**`ROADMAP.md`**. This file only carries the rules that must load automatically.

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

- `master` = integration branch (Thom merges everything there).
- Mac-side work lands on `main`; sync = merge `origin/master` into `main`.
- If a fetch reports **forced update**, history was rewritten upstream —
  verify content (`git diff HEAD origin/<branch>`) before adopting; never
  blind-merge rewritten history.
