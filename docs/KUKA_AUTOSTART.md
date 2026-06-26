# KUKA boot auto-start + variable-driven motion (CELL.SRC integration)

Goal: power on the machine → the app drives all motion over the network, with **no separate
program to start** and **no `.src` edits/reboots to change motion**.

## How it works (this controller)
LFAM 3's `R1\cell.src` is **already a boot-running dispatcher loop**: it auto-starts RSI and polls
GUI-set globals (`bRunScanPick`, `bRunScanDeposit`) to run programs — set a variable over
KUKAVARPROXY, the loop runs the work. We use the **same pattern** for motion: the motion handler is
**integrated into that loop**, gated on the `MS_*` globals. So motion runs whenever `cell.src` runs
— exactly when your scanner triggers do, with no extra start.

The app writes the target + params, then **bumps `MS_SEQ`**; the loop runs the command and writes
`MS_SEQ` back to `MS_ACK`; the host waits for `MS_ACK == MS_SEQ`.

## Variable protocol (`MS_*`, declared in `$config.dat`)
```
DECL INT    MS_SEQ=0    ; host increments to issue a new command
DECL INT    MS_ACK=0    ; loop echoes the finished seq (host waits MS_ACK==MS_SEQ)
DECL INT    MS_CMD=0    ; 1=PTP pose, 2=LIN pose, 3=PTP axes, 4=home
DECL INT    MS_VEL=20   ; speed % (1..100)
DECL INT    MS_TOOL=6   ; TOOL_DATA index
DECL INT    MS_BASE=1   ; BASE_DATA index (<=0 => $NULLFRAME)
DECL INT    MS_STAT=0   ; 0=idle 1=busy 2=done 3=bad-cmd
DECL BOOL   MS_BUSY=FALSE
DECL E6POS  MS_POSE     ; Cartesian target (X..C; S/T taken from current pose)
DECL E6AXIS MS_AXIS     ; joint target (A1..A6, E1)
```

## What's installed on LFAM 3 (192.168.0.153)
- `$config.dat` — `MS_*` globals added in USER GLOBALS (backup: `$config.massbak.dat`).
- `cell.src` — the `MS_SEQ` motion branch added to the dispatcher loop (backup: `cell.massbak.src`).
- Repo reference of the integrated file: `assets/krl/cell.src.lfam3.reference`.

**One reboot is required** after the `$config.dat` change so the new globals register. After that:
no reboots, no file reloads, no program selection to change motion.

## Boot auto-start — the honest boundary
Motion now runs wherever `cell.src` runs. Whether `cell.src` itself comes up with **zero pendant
interaction** depends on hardware/safety, not software:
- **Operating-mode keyswitch (T1/T2/AUT/EXT):** physical. Leave it where your scanner workflow
  already runs (it polls in both AUT and EXT). Software can't change the key.
- **Drives-on + safety circuit:** must be satisfied for AUTO. In EXT an external start brings
  `cell.src` up automatically; in AUT it's started once after boot.
- **Bottom line:** the motion server adds **no** start step of its own — it inherits `cell.src`'s
  boot behavior. If your `bRunScanPick` works after a plain boot today, so does motion.

## Driving / testing it (app console — robot synced, T1 first)
```
move-pose <x> <y> <z> [a b c] [vel%]   PTP the tool to a Cartesian pose (mm, deg)
move-lin  <x> <y> <z> [a b c] [vel%]   LIN to a Cartesian pose
move-home [vel%]                       go to HOME (XHOME)
```
(`move-pose x y z v%` with 4 numbers treats the 4th as velocity.) In code:
`RobotPanelViewModel.SendPoseAsync / SendAxesAsync / GoHomeAsync`.

## Applying to LFAM 1 / LFAM 2
When those controllers are online: add the same `MS_*` block to their `$config.dat`, and add the
identical `MS_SEQ` branch into their `cell.src` loop (each controller's `cell.src` is its own file —
don't overwrite; insert the branch). Back up both first. Then one reboot.

## Safety
The loop executes whatever target the app sends — speed is clamped (1–100%) and the current
status/turn is kept, but **targets are not collision-checked**. Verify first moves in **T1**.
