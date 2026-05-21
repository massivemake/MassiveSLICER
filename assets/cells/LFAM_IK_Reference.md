# LFAM Robot IK Reference

> MassiveSLICE — KUKA KR 210 R3100 Ultra (KR QUANTEC series)  
> Generated: 2026-05-14

---

## OPW Kinematic Parameters

These are the parameters for the OPW (Offset Parallel Wrist) analytical IK model.

| Parameter | Value (mm) | Description |
|-----------|-----------|-------------|
| `c1` | 550 | Vertical offset from base to shoulder joint (A2 pivot height) |
| `c2` | 1350 | Upper arm length (A2 → A3 pivot) |
| `c3` | 1820 | Forearm length (A3 → wrist centre, A4/A5 intersection) |
| `c4` | 215 | Wrist length (wrist centre → flange face, along A6 axis) |
| `a1` | 730 | Horizontal offset from base centre to A2 pivot (shoulder reach) |
| `a2` | −115 | Vertical offset from A2 pivot to A3 pivot (signed, usually negative) |
| `b` | 0 | Lateral offset between A1 axis and A2 axis (zero for symmetric robots) |

---

## DH Parameters (Modified Denavit-Hartenberg)

| Joint | α (deg) | a (mm) | d (mm) | Notes |
|-------|---------|--------|--------|-------|
| A1 | 0 | 0 | c1 = 550 | Base to shoulder |
| A2 | −90 | a1 = 730 | 0 | Shoulder offset |
| A3 | 0 | c2 = 1350 | 0 | Upper arm |
| A4 | 90 | a2 = −115 | c3 = 1820 | Elbow to wrist |
| A5 | −90 | 0 | 0 | Wrist pitch |
| A6 | 90 | 0 | c4 = 215 | Wrist roll / flange |

---

## Angle Conventions (KRL ↔ Math)

KUKA KRL zero posture ≠ DH zero posture. Apply these offsets **before** feeding angles into IK, and **after** getting solutions back.

| Joint | KRL → Math Offset (deg) | Sign | Notes |
|-------|------------------------|------|-------|
| A1 | 0 | +1 | No offset |
| A2 | +90 | −1 | KRL 0° = arm horizontal; DH 0° = arm up |
| A3 | −90 | +1 | KRL 0° = arm straight; offset compensates |
| A4 | 0 | −1 | Axis inversion |
| A5 | 0 | −1 | Axis inversion |
| A6 | 0 | −1 | Axis inversion |

**Formula:**  
`math_angle = sign × (krl_angle + offset)`  
`krl_angle  = sign × math_angle − offset`

---

## Joint Limits (Software)

| Joint | Min (deg) | Max (deg) | Range (deg) |
|-------|-----------|-----------|-------------|
| A1 | −60 | +60 | 120 |
| A2 | −120 | +70 | 190 |
| A3 | −120 | +168 | 288 |
| A4 | −350 | +350 | 700 |
| A5 | −125 | +125 | 250 |
| A6 | −350 | +350 | 700 |

> Hardware limits may be tighter. Always verify on the physical controller.

---

## Joint Axes (Three.js / Bone-Local)

All axis signs are **−1** (inverted) relative to the bone-local Y-axis in the Three.js scene.

| Joint | Bone Name | Rotation Axis | Sign |
|-------|-----------|--------------|------|
| A1 | `joint_a1` | Y | −1 |
| A2 | `joint_a2` | Y | −1 |
| A3 | `joint_a3` | Y | −1 |
| A4 | `joint_a4` | Y | −1 |
| A5 | `joint_a5` | Y | −1 |
| A6 | `joint_a6` | Y | −1 |

---

## Reach Envelope

| Metric | Value |
|--------|-------|
| Maximum reach | 3904 mm (c1 not included, wrist centre) |
| Minimum reach (singularity exclusion) | ~180 mm |
| Overreach warning threshold | 94% of max (~3670 mm) |

> Computed as: `max_reach = sqrt((a1 + c2)² + c3²)` approximately

---

## Home / Default Joint Angles (KRL degrees)

These are the stored default start positions for each cell in MassiveSLICE.

| Cell | A1 | A2 | A3 | A4 | A5 | A6 |
|------|----|----|----|----|----|----|
| LFAM 1 | 0 | −90 | 90 | 0 | 15 | 0 |
| LFAM 2 | 0 | −90 | 90 | 0 | 15 | 0 |
| LFAM 3 | 0 | −90 | 90 | 0 | 15 | 0 |

---

## BASE_DATA[1] — World-to-Robot-Base Transform

Measured from the world origin (build plate centre, Z=0) to the robot A1 axis.

| Component | Value (mm / deg) |
|-----------|-----------------|
| X | 1433.829 |
| Y | −1377.359 |
| Z | −870.000 |
| A (Rz) | 0.000 |
| B (Ry) | 0.000 |
| C (Rx) | 0.000 |

> **Note:** LFAM 1 (rail) and LFAM 3 (rotary base) may have different or dynamic BASE_DATA — confirm from `$config.dat` on each controller.

---

## Tool TCP — Extruder Nozzle Tip

### HV Extruder (LFAM 2 — TOOL_DATA[2])

| Component | Value (mm / deg) |
|-----------|-----------------|
| X | 694.170 |
| Y | −156.390 |
| Z | 311.950 |
| A | 0 |
| B | 90 |
| C | 0 |

### HF Extruder (LFAM 1, LFAM 3 — TOOL_DATA[1])

> Exact TCP values TBD — read from `$config.dat` on LFAM 1 / LFAM 3 controllers.

---

## Machine Cell Specifications

| Parameter | LFAM 1 | LFAM 2 | LFAM 3 |
|-----------|--------|--------|--------|
| **Robot IP** | 192.168.1.10 | 192.168.1.20 | 192.168.1.30 |
| **TOOL_NO** | 1 (HF) | 2 (HV) | 1 (HF) |
| **BASE_NO** | 1 | 1 | 1 |
| **Special axis** | Rail (E1) | None | Rotary base (E6) |
| **Bed size (mm)** | 1200 × 800 | 800 × 600 | ∅ 800 rotary |
| **Booster height** | Variable (rail) | Fixed | Fixed |
| **$ADVANCE** | 5 | 3 | 5 |
| **$APO.CVEL** | 50 | — | 50 |
| **Travel speed (m/s)** | 0.100 | null (no change) | 0.200 |
| **Travel lift (mm)** | 5 | 3 | 3 |

---

## RPM / Extrusion Rate Factors

RPM is set per-move based on feed rate:  
`RPM = feedRate_mm_per_s × rpmFactor`

| Machine | rpmFactor | Example @ 60 mm/s |
|---------|-----------|------------------|
| LFAM 1 (HF) | 0.9641 | 57.85 RPM |
| LFAM 2 (HV) | 0.9641 | 57.85 RPM |
| LFAM 3 (HF) | 1.3241 | 79.45 RPM |

---

## KRL Export Key Settings (MassiveSLICE krlExporter.js)

```javascript
// LFAM 1
{ toolNo:1, baseNo:1, hasMat:true, advance:5, hasApoCvel:true, apoCvel:50,
  travelSpeed:0.100, velResetInsideBlock:true, travelLiftMM:5, approachHeightMM:150,
  rpmFactor:0.9641, footerTemps:'standby', hasRail:true, hasRotaryBase:false }

// LFAM 2
{ toolNo:2, baseNo:1, hasMat:false, advance:3, hasApoCvel:false,
  travelSpeed:null, velResetInsideBlock:false, travelLiftMM:3, approachHeightMM:150,
  rpmFactor:0.9641, footerTemps:'zero', hasRail:false, hasRotaryBase:false }

// LFAM 3
{ toolNo:1, baseNo:1, hasMat:true, advance:5, hasApoCvel:true, apoCvel:50,
  travelSpeed:0.200, firstApproachUseTravelSpeed:true, velResetInsideBlock:false,
  travelLiftMM:3, approachHeightMM:150,
  rpmFactor:1.3241, footerTemps:'standby', hasRail:false, hasRotaryBase:true }
```

---

*End of reference document.*
