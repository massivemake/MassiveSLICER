# KUKA boot auto-start + variable-driven motion (MASSIVE_SERVER)

Goal: power on the machine → the robot is ready and the app drives all motion over the network,
with **no pendant/HMI steps per session** and **no `.src` edits or reboots to change motion**.

This is done with a small permanent KRL **motion command server** (`MASSIVE_SERVER.src`) that the
app drives by writing variables over KUKAVARPROXY (port 7000) — the same channel already used for
sync, the `$FLAG` handshake, and `BASE_DATA` writes. After it's running, the bed scan, calibration
sweeps, jogging, etc. are all host-side command sequences.

## Pieces
| File | Role |
|---|---|
| `assets/krl/MASSIVE_SERVER.src` | The dispatcher loop. Reads `MS_*` globals, executes moves, acks. |
| `$config.dat` (controller) | Declares the `MS_*` globals (below). One-time edit + reboot. |
| `assets/krl/CELL.SRC.template` | Optional: makes the server run at boot (Automatic External). |
| App: `RobotSyncService` / `RobotPanelViewModel` | `SendPoseAsync`, `SendAxesAsync`, `GoHomeAsync`, … |
| Console: `srv-deploy`, `move-pose`, `move-lin`, `move-home`, `srv-stop` | Drive/test it. |

## Variable protocol (`MS_*`)
Host writes the target + params, then **bumps `MS_SEQ`**; the server runs the command and writes
`MS_SEQ` back to `MS_ACK`. The host waits for `MS_ACK == MS_SEQ`.

```
DECL GLOBAL INT    MS_SEQ  = 0     ; host increments to issue a new command
DECL GLOBAL INT    MS_ACK  = 0     ; server echoes the finished seq
DECL GLOBAL INT    MS_CMD  = 0     ; 1=PTP pose, 2=LIN pose, 3=PTP axes, 4=home, 99=stop
DECL GLOBAL INT    MS_VEL  = 20    ; speed % (1..100)
DECL GLOBAL INT    MS_TOOL = 6     ; TOOL_DATA index
DECL GLOBAL INT    MS_BASE = 1     ; BASE_DATA index (<=0 => $NULLFRAME)
DECL GLOBAL INT    MS_STAT = 0     ; 0=idle 1=busy 2=done 3=bad-cmd
DECL GLOBAL BOOL   MS_BUSY = FALSE
DECL GLOBAL E6POS  MS_POSE         ; Cartesian target (X..C; S/T taken from current pose)
DECL GLOBAL E6AXIS MS_AXIS         ; joint target (A1..A6, E1)
```

## One-time commissioning
1. **Declare the globals.** Add the `MS_*` block above to the controller's `$config.dat`
   (`…\KRC\R1\System\$config.dat`, in a USER GLOBALS section). **Restart the controller once** so
   the globals are registered (this is the only required reboot).
2. **Deploy the server.** In the app console: `srv-deploy` (copies `MASSIVE_SERVER.src` to
   `\\<ip>\krc\ROBOTER\KRC\R1\Program`). Because it's a new program, **restart the KUKA** so the
   Navigator sees it.
3. **Run it.** Either:
   - **Manual:** on the pendant, select `MASSIVE_SERVER`, set AUT, drives on, Start. It now loops.
   - **Boot auto-start (no pendant per session):** configure **Automatic External** and set
     `CELL.SRC` to call `MASSIVE_SERVER` (see `CELL.SRC.template`). With the mode left in **AUT EXT**
     and the safety circuit satisfied, the server runs on every boot.
4. **From the app:** Sync → the app calls `InitCommandServerAsync` and can issue moves.

## Daily use (once commissioned)
- Power on. (Auto-External: server already running. Manual: select+start once.)
- In the app: **Sync** the robot, then drive motion — console `move-pose`/`move-lin`/`move-home`,
  or any feature that calls `SendPoseAsync`/`SendAxesAsync`. No file edits, no reboots.

## Console commands
```
srv-deploy                         copy MASSIVE_SERVER.src to the controller
move-pose <x> <y> <z> [a b c] [v%] PTP the tool to a Cartesian pose (mm, deg)
move-lin  <x> <y> <z> [a b c] [v%] LIN to a Cartesian pose
move-home [v%]                     go to HOME
srv-stop                           stop the server loop (CMD 99)
```
(`move-pose x y z v%` with 4 numbers treats the 4th as velocity.)

## Safety — the hard boundary (hardware, not software)
- **Operating-mode keyswitch (T1/T2/AUT/EXT):** physical on the pendant. Boot-and-go needs it left
  in **AUT EXT**. Software cannot change it.
- **Safety circuit** (operator-safety gate, E-stop, external enable): must be satisfied for drives-on
  in AUTO. The app can *request* drives-on via Auto-External but cannot override a tripped input.
- The server executes whatever Cartesian/joint target the app sends. It clamps **speed** (1–100%)
  and keeps the current status/turn, but it does **not** validate that a target is collision-free —
  the host must send safe targets, and **first moves should be checked in T1**.
