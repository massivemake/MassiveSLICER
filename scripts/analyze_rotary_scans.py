#!/usr/bin/env python3
"""
Solve the *true* rotary-bed (E1) model from a set of registered scans.

The app exports (console command `diag-scans`) a folder with:
  - manifest.json : { rotaryCenter:[x,y,z], rotationSign:+/-1, scans:[{file,e1,points}] }
  - NN_*.xyz      : one scan each, WORLD-space points (mm), captured at that scan's E1.

Each scan is the real bed surface seen at rotation E1. If the app's rotary model were perfect,
"un-rotating" every scan by its E1 about the true axis/centre would stack them into ONE consistent
surface. We solve the model that best achieves that, then compare to what the app assumes
(axis = world +Z, gain = 1 deg/deg, phase = 0, centre = manifest rotaryCenter).

Robustness for real data:
  * TOP-SURFACE FILTER — keeps only the holed table top (a tight Z band around the dominant plane,
    inside a max radius). Floor/robot points that got rotated into garbage are dropped.
  * SMALL-CORRECTION BOUNDS — the true fix is < ~1 inch, so tilt/gain/phase/centre are bounded
    (penalty barrier); the optimiser can't run off to a far, wrong alias.

Params solved (sign held fixed):
  tiltx,tilty  axis tilt from +Z (deg)   dcx,dcy,dcz  centre offset (mm)
  gain         scene-deg per E1-deg      phase        constant offset (deg) at E1=0

Usage: python scripts/analyze_rotary_scans.py <diag_dir> [--per-scan 3000] [--sample 6000]
"""
import sys, os, json, argparse
import numpy as np
from scipy.spatial import cKDTree
from scipy.optimize import minimize

D2R = np.pi / 180.0

# Bounds on the correction (a real mis-cal is small — the user reports < ~1 inch).
LIM = dict(tilt=3.0, ctr=25.0, gain=0.02, phase=3.0)   # deg, mm, frac, deg

def rot_axis_angle(axis, ang):
    a = axis / (np.linalg.norm(axis) + 1e-12)
    c, s = np.cos(ang), np.sin(ang); x, y, z = a; C = 1 - c
    return np.array([
        [c+x*x*C, x*y*C-z*s, x*z*C+y*s],
        [y*x*C+z*s, c+y*y*C, y*z*C-x*s],
        [z*x*C-y*s, z*y*C+x*s, c+z*z*C]])

def axis_from_tilt(tx, ty):
    n = np.array([0.0, 0.0, 1.0])
    n = rot_axis_angle([1, 0, 0], tx * D2R) @ n
    n = rot_axis_angle([0, 1, 0], ty * D2R) @ n
    return n / np.linalg.norm(n)

def load(diag_dir, z_below=30.0, z_above=8.0, r_max=950.0):
    man = json.load(open(os.path.join(diag_dir, "manifest.json")))
    center0 = np.array(man["rotaryCenter"], float)
    sign = float(man["rotationSign"])
    raw = []
    for s in man["scans"]:
        p = np.loadtxt(os.path.join(diag_dir, s["file"]))
        if p.ndim == 2 and len(p) > 50:
            raw.append((float(s["e1"]), p.astype(np.float64)))
    # dominant top-plane Z from all points pooled (fine histogram peak)
    allz = np.concatenate([p[:, 2] for _, p in raw])
    h, e = np.histogram(allz, bins=400)
    ztop = 0.5 * (e[h.argmax()] + e[h.argmax() + 1])
    kept = []
    raw_n = tot_n = 0
    for e1, p in raw:
        r = np.hypot(p[:, 0] - center0[0], p[:, 1] - center0[1])
        m = (p[:, 2] > ztop - z_below) & (p[:, 2] < ztop + z_above) & (r < r_max)
        tot_n += len(p); raw_n += int(m.sum())
        if m.sum() > 200:
            kept.append((e1, p[m]))
    print(f"Top-surface filter: kept {raw_n}/{tot_n} points (z in [{ztop-z_below:.0f},{ztop+z_above:.0f}], r<{r_max:.0f}); "
          f"{len(kept)}/{len(raw)} scans usable. Top plane z≈{ztop:.1f}")
    return center0, sign, kept, ztop

def unrotate_all(params, center0, sign, scans):
    tiltx, tilty, dcx, dcy, dcz, gain, phase = params
    axis = axis_from_tilt(tiltx, tilty)
    C = center0 + np.array([dcx, dcy, dcz])
    out, lab = [], []
    for i, (e1, pts) in enumerate(scans):
        ang = sign * (gain * e1 + phase) * D2R
        R = rot_axis_angle(axis, -ang)
        out.append((pts - C) @ R.T + C)
        lab.append(np.full(len(pts), i))
    return np.vstack(out), np.concatenate(lab)

def bounds_penalty(p):
    tiltx, tilty, dcx, dcy, dcz, gain, phase = p
    pen = 0.0
    for v, lim in ((tiltx, LIM['tilt']), (tilty, LIM['tilt']),
                   (dcx, LIM['ctr']), (dcy, LIM['ctr']), (dcz, LIM['ctr']),
                   (phase, LIM['phase'])):
        pen += max(0.0, abs(v) - lim) ** 2
    pen += (max(0.0, abs(gain - 1) - LIM['gain']) ** 2) * 1e4
    return pen * 50.0

def consistency_rms(params, center0, sign, scans, sample_idx, regularize=True):
    P, L = unrotate_all(params, center0, sign, scans)
    tree = cKDTree(P)
    sp, sl = P[sample_idx], L[sample_idx]
    d, idx = tree.query(sp, k=8)            # skip same-scan neighbours (self-overlap reads ~0)
    best = np.full(len(sp), np.nan)
    for j in range(len(sp)):
        for kk in range(8):
            if L[idx[j, kk]] != sl[j]:
                best[j] = d[j, kk]; break
    best = best[np.isfinite(best)]
    best.sort()
    best = best[: int(len(best) * 0.8) + 1]  # trim worst 20% (occlusion / non-overlap edges)
    rms = float(np.sqrt(np.mean(best ** 2)))
    if not regularize:
        return rms
    return rms + bounds_penalty(params) + 5e-4 * params[6] * params[6]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("diag_dir")
    ap.add_argument("--per-scan", type=int, default=3000)
    ap.add_argument("--sample", type=int, default=6000)
    a = ap.parse_args()

    center0, sign, scans, ztop = load(a.diag_dir)
    if len(scans) < 3:
        print("Need >=3 usable scans."); return
    rng = np.random.default_rng(0)
    scans = [(e1, p[rng.choice(len(p), min(a.per_scan, len(p)), replace=False)]) for e1, p in scans]
    e1s = np.array([e1 for e1, _ in scans])
    print(f"{len(scans)} scans, E1 [{e1s.min():.0f},{e1s.max():.0f}]°, centre0 "
          f"({center0[0]:.1f},{center0[1]:.1f},{center0[2]:.1f}), sign {sign:+.0f}")

    P0, _ = unrotate_all([0, 0, 0, 0, 0, 1, 0], center0, sign, scans)
    sample_idx = rng.choice(len(P0), min(a.sample, len(P0)), replace=False)

    x0 = np.array([0, 0, 0, 0, 0, 1.0, 0])
    base = consistency_rms(x0, center0, sign, scans, sample_idx, regularize=False)
    print(f"\nCurrent app model (axis=+Z, gain=1, phase=0): cross-scan RMS = {base:.3f} mm")

    res = minimize(consistency_rms, x0, args=(center0, sign, scans, sample_idx),
                   method="Powell", options={"xtol": 1e-4, "ftol": 1e-4, "maxiter": 6000})
    tiltx, tilty, dcx, dcy, dcz, gain, phase = res.x
    axis = axis_from_tilt(tiltx, tilty)
    tilt = np.degrees(np.arccos(np.clip(axis[2], -1, 1)))
    opt = consistency_rms(res.x, center0, sign, scans, sample_idx, regularize=False)

    print(f"\nOptimised model: cross-scan RMS = {opt:.3f} mm  (was {base:.3f} mm)\n")
    print(f"  axis tilt        : {tilt:.3f}°  (tiltX {tiltx:+.3f}, tiltY {tilty:+.3f}; axis {axis.round(4)})")
    print(f"  centre offset    : ({dcx:+.2f}, {dcy:+.2f}, {dcz:+.2f}) mm   |XY|={np.hypot(dcx,dcy):.2f}")
    print(f"  gain (deg/E1deg) : {gain:.5f}   ({100*(gain-1):+.3f}% vs 1.0)")
    print(f"  phase offset     : {phase:+.3f}°  at E1=0")

    print("\nDiagnosis:")
    if tilt > 0.15:
        print(f"  • Axis tilted {tilt:.2f}° — the viewport spins about pure +Z; with the top surface ~550 mm")
        print(f"    above the pivot this wobbles the bed (worst near ±180°). FIX: spin about the calibrated axis.")
    if abs(gain - 1) > 0.002:
        print(f"  • Scene rotates {100*(gain-1):+.2f}%/E1° vs reality (grows with E1). FIX: apply MeasuredDegPerE1.")
    if abs(phase) > 0.15:
        print(f"  • Constant {phase:+.2f}° phase at E1=0 (home/mounting reference).")
    if np.hypot(dcx, dcy) > 2:
        print(f"  • Rotation centre off {np.hypot(dcx,dcy):.1f} mm in XY — recalibrate bed centre.")
    if abs(dcz) > 3:
        print(f"  • Centre Z off {dcz:+.1f} mm (only matters because the axis is tilted).")
    for nm, v, lim in (("tilt", tilt, LIM['tilt']), ("|XY ctr|", np.hypot(dcx, dcy), LIM['ctr']),
                       ("phase", abs(phase), LIM['phase'])):
        if v > lim * 0.95:
            print(f"  ! {nm} hit its bound ({lim}); result may be clipped — widen LIM or check the data.")

if __name__ == "__main__":
    main()
