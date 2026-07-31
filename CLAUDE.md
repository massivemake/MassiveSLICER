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

### "Build me a copy of <branch>" — do exactly this

A teammate asking for a build of someone else's branch is routine. Steps, in order:

1. **Protect their work first.** If the tree is dirty or their own branch has unpushed
   commits, say so and let them commit/stash before you switch — never discard it silently.
2. `git fetch origin` — the branch usually doesn't exist locally yet.
3. `git checkout <branch>` (exact name; `git branch -r` if unsure — misspellings are common
   when a name is retyped from chat).
4. **Force a rebuild:** `dotnet build src/MassiveSlicer.App/MassiveSlicer.App.csproj --no-incremental`.
   Switching branches changes source files but leaves binaries from the previous branch, and a
   plain incremental build can report "0 Errors" while doing nothing.
5. **Prove the branch's code is in the binary** before handing it over — don't trust the build
   log. Pick a string the branch introduced and check it as UTF-16 (see the command above).
6. Launch, and tell them which branch they're on plus how to get back (`git checkout main`).

Also tell them if the branch is **not yet print-verified** — check the warning banner at the
top of `memory.md`. Unproven export/motion changes must not reach a real print unannounced.

Build identity is auto-generated: build number = git commit count
(`build N · date · sha` in the status bar). Never hand-edit build numbers.

## Git conventions

- `main` = the ONLY shared branch (GitHub default; `master` was deleted 2026-07 —
  the old master/main split is gone).
- Sync = `git pull` on `main` (or merge `origin/main` into your feature branch).
- If a fetch reports **forced update**, history was rewritten upstream —
  verify content (`git diff HEAD origin/<branch>`) before adopting; never
  blind-merge rewritten history.

## Keeping 5 developers out of each other's way (prompt the developer)

Four or five of us work on this repo at once. Don't silently start editing — run these checks
and **tell the developer what you found and what you recommend**. They decide; you just make
sure the decision is deliberate.

**1. Before the first edit of a session — check sync.**

```bash
git fetch -q && git log --oneline HEAD..origin/main | head
```

If anything comes back, say so before editing:

> You're *N* commits behind `origin/main` (*newest: "…"*). Recommend pulling first so we're not
> building on stale code. Want me to pull?

Escalate the wording if the incoming commits touch the same files as the request — that's a
merge conflict you can see coming. Pull first, then work.

**2. Before a big change — recommend a branch.**

Treat it as **big** if any of these are true, and say so up front:

- Touches the shared high-traffic files (`ViewportView.axaml.cs`, `ViewportViewModel.cs`,
  `MainWindowViewModel.cs`, `RightPanelView.axaml`) in more than a localized way
- Changes **slicer output, KRL/export, or robot motion** — anything that reaches hardware
- Changes the `.mass` schema or a shared settings model
- Is multi-file or won't finish in this session
- Is unproven on real hardware (see the Cut Modifier warning at the top of `memory.md`)

> This is a big one — it changes *<what>*. Recommend a `feature/<name>` branch so `main` stays
> shippable for the others until it's tested on the machine. Want me to branch?

Small and safe — single-file bug fix, a label or copy fix, docs, a contained UI tweak — just
commit to `main` and push. Branching those adds ceremony and merge cost for no benefit.

**3. Before moving to a different feature — land or park the current one.**

If a feature branch has work on it and the developer changes subject, stop and say so:

> `feature/<name>` still has *N* unmerged commits. Before starting something new, either merge
> it to `main` (if it's tested) or park it deliberately. Starting a second feature on the same
> branch tangles both and makes either one hard to ship or revert.

**4. Keep branches fresh, and push at every stopping point.**

- Merge `main` into any live feature branch **daily**. Branches rot fast here: as of
  2026-07-27 `feature/presets` was **80 commits behind** and `feature/cut-modifier` was 18
  behind while already fully merged (i.e. dead and should be deleted).
- **Push at every natural stopping point.** Unpushed work is invisible to the other four and
  lives on one laptop; this repo already grew a `save.sh` because code got lost.
- When a branch is merged, say so and offer to delete it — stale branches are read by everyone
  as work in progress.
