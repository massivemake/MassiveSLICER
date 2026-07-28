# MassiveSLICER — assistant instructions

Product definition and backlog live in **`ROADMAP.md`**. Past work lives in
**`memory.md`**. This file only carries the rules that must load automatically.

## Work efficiently — this repo is large

Four files are 2,600–14,900 lines. Reading `ViewportView.axaml.cs` whole costs ~150k tokens,
so do it **on purpose, not by reflex**. This is a cost default, not a capability limit — the
rules below are about not paying 150k tokens to change one line. Deep reading is explicitly
sanctioned when the work calls for it (see the next section).

1. **Read `docs/CODE-MAP.md` first.** It maps every subsystem to a file, with section
   anchors inside the big files, and it is the cheapest way to find where you are going.
2. **Default to targeted reads.** `grep -n` to locate, then read with `offset`/`limit`.
3. **`memory.md` is recent history only.** Older entries are in `docs/memory-archive.md`;
   go there when digging into a specific past decision.
4. **Trace a setting** with the chain in CODE-MAP rather than searching blind.

## When to read broadly (do this without hesitation)

**The test is whether you can name what you are looking for.** If you can name it, grep for
it. If you are looking for what you don't yet know is there, read broadly — a targeted read
cannot find a bug you can't name, and pattern-matching across a whole subsystem is how the
non-obvious problems surface.

Read whole files / whole subsystems for:

- **Auditing a class of problem** — thread safety, disposal, error handling, silent failure
  paths. These live in the gaps between named functions.
- **A bug you cannot localize.** If the symptom doesn't map to a function name, stop grepping.
- **Refactors and file splits**, performance sweeps, and reviewing an unfamiliar subsystem
  before a design change.
- **Anything where being wrong is expensive** — export/KRL correctness, robot motion, and
  anything that reaches real hardware. Verify by reading, not by assuming.

Two ways to make broad reading cheap, so cost never becomes a reason to skip it:

- **Delegate it.** Hand the whole-file pass to a subagent and keep the conclusions, not the
  file contents — the 150k tokens land in a context you throw away.
- **Read it once per session**, in sequential chunks, with the question written down first.
  Re-reading the same file repeatedly is the actual waste, not the first read.

If a targeted read leaves you inferring rather than confirming, widen the read. Guessing to
save tokens is how a wrong fix ships — that costs far more than the read did.

## Escalating to a deep dive — surface it, price it, let the developer choose

Default to fast. But when one of these fires, **say so and offer the deep dive with a cost
estimate** instead of either guessing or silently spending 178k tokens:

**Triggers — name the one that fired:**

1. **Two targeted attempts have missed.** Stop trying a third grep.
2. **The symptom has no name.** Intermittent, timing-, ordering-, lifecycle-, or
   disposal-related. These live between functions and grep cannot see them.
3. **You are about to write "probably" / "likely" / "should"** about behavior that reaches
   the robot, KRL output, or a customer print.
4. **The report contradicts your model** — the user says something is broken that the code
   you've read says can't break. Your model is wrong; find out where.
5. **A cross-cutting change** — rename, refactor, file split, or auditing a whole class of
   problem (thread safety, error handling, silent failure).
6. **The fix would be a guess.** You cannot point at the line that causes the behavior.

**How to ask** — one short paragraph, no ceremony:

> This needs a deeper look: *<what you'd examine and the trigger that fired>*. That's about
> *<N>*k tokens and *<M>* minutes. It would tell us *<the specific thing you'd learn>*.
> Faster alternative: *<narrower option, or "ship the likely fix and verify on the machine">*.
> Want me to?

**Don't ask when it's cheap.** Under ~25k tokens (see the table), just read it — asking is
friction. Ask above that, and for anything that would run many files or many rounds.

**Real read costs in this repo** (measured 2026-07-27; ≈ bytes ÷ 3.7):

| Target | Tokens | Note |
|---|---|---|
| `SliceSettings.cs` | ~7k | just read it |
| `PlanarSlicer.cs` | ~18k | just read it |
| `SceneRenderer.cs` | ~31k | ask if combined with others |
| `MainWindowViewModel.cs` | ~46k | ask |
| `LightningPlanner.cs` | ~55k | ask |
| `RightPanelView.axaml` | ~76k | ask |
| `ViewportViewModel.cs` | ~86k | ask |
| **`ViewportView.axaml.cs`** | **~178k** | ask — nearly a whole context window |
| all of `Core/Slicing/` | ~223k | ask; better delegated to a subagent |
| all of `App/ViewModels/` | ~261k | ask; better delegated to a subagent |

Tokens are the objective number; convert to credits for whatever plan you're on. A cheaper
shape for the big ones: hand the pass to a subagent and keep only the findings.

**When the stakes are high, recommend it — don't present it neutrally.** For KRL/export
correctness, robot motion, or anything that could scrap a print, the honest answer is "we
should read this properly," and say so.

## Verifying a change (these are hard rules, not cost defaults)

The reading guidance above is about money. The rules here are about being **wrong** — each has
already cost a real cycle or shipped a stale build to a tester. Follow them.

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
