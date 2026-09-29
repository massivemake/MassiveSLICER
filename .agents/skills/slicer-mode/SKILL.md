---
name: slicer-mode
description: "Route non-trivial MassiveSLICER work through a playbook."
version: 0.1.0
author: Thom Boessel, MassiveAI
license: MIT
platforms: [macos, windows]
---

# Slicer-mode

Sticky engineering mode for **this repo only**. Not pstack, not MassiveSWARM, not overnight merge.

Reads the ask, copies **one** playbook into the todo list, then runs existing MassiveAI skills. Proof of the running app is `verify-massiveslicer`.

## When to Use

- New mill op, KRL/export, IK/overlay, named home, multi-file chrome
- Bug that already survived one guess
- User says “slicer-mode” / “do this rigorously”

Don't use for: one-line copy, Lab ERP, Drive RSI, WhatsApp/HR, or installing `mdsmithaustin/pstack`.

Opt out when the user says so. Stay out of the way on trivia.

## Procedure

1. **Match one playbook** (only these): `bug` · `mill` · `krl` · `chrome` · `feature`. Completion: name it and `todo` the playbook steps verbatim.
2. **Architect gate** — if the change crosses a function/file boundary (`ViewportView.axaml.cs`, mill IK, KRL exporter, cell JSON): read `references/architect.md` and settle shape **before** code. Completion: written shape (types, owner file, what you will not touch).
3. **Blast-radius** — if it “looks small”: `references/blast-radius.md`. Completion: one fact from running code that it is safe, or a list of coupled systems you will re-verify.
4. **Implement** with existing skills only:
   - bug → `systematic-debugging` then `test-driven-development` (Core math). UI/IK still needs the running app.
   - throwaway → `spike` (delete after)
   - plan-only → `plan`
   - cleanup → `simplify-code` when the user asks
5. **Prove** with `verify-massiveslicer` (doctor + one mapped feature). `dotnet test` vs `docs/KNOWN-TEST-FAILURES.md` is extra, not the user path.
6. **Shop test before mill/IK/KRL commit.** `save.sh` only after they confirm. Never stash / `reset --hard` / force-push. Never overnight-land a stack.

## Playbooks

| File | For |
|---|---|
| `playbooks/bug.md` | Repro, root cause, fix, live proof |
| `playbooks/mill.md` | MILL OPERATION/TOOLPATHING / T12 |
| `playbooks/krl.md` | SRC header, travel, PointLoader, Drive Send |
| `playbooks/chrome.md` | Theme, ComboBox, dialog corners, density |
| `playbooks/feature.md` | New behavior from a named data shape |

## Principles (inline — not 23 skills)

- Prove it in the **app**, not only tests.
- Test behavior, not implementation details.
- Subtract before you add. Do not rewrite Mill Start / TOOL_DATA to hide an overlay bug.
- Sequence the check that can invalidate the rest first (HeatedBed mill, PointLoader 61/57, noexec apphost).
- Build the lever once (console + bridge), don't click a one-off.

## Pitfalls

- Do not spawn `/swarm` or name MassiveSWARM here.
- Do not iron-law TDD every lime-pixel tweak; shop rejects commit-before-they-see-it.
- Do not strip KRL comments with a “no-comments” pass — `T1`/`HALT` in comments break PointLoader.
- `main` is the only shared branch. Big motion/export work: offer `feature/<name>` first.
