#!/usr/bin/env python3
"""
Solve the *true* rotary-bed (E1) model from a set of registered scans.

The app exports (via the console command `diag-scans`) a folder containing:
  - manifest.json : { rotaryCenter:[x,y,z], rotationSign: +/-1, scans:[{file,e1,points}] }
  - NN_*.xyz      : one scan each, WORLD-space points (mm), captured at the scan's E1.

Each scan is the real bed surface seen at rotation E1. If the app's rotary model were perfect,
"un-rotating" every scan by its E1 about the true axis/centre would stack them into ONE consistent
surface. We optimise the model parameters that best achieve that, then compare to what the app
currently assumes (axis = world +Z, gain = 1 deg/deg, phase = 0, centre = manifest rotaryCenter).

Parameters solved (sign held fixed from the manifest):
  tiltx, tilty : small rotations (deg) tilting the rotary axis away from world +Z
  dcx,dcy,dcz  : centre offset (mm) from the manifest rotaryCenter
  gain         : scene-degrees per E1-degree (1.0 = ideal direct drive)
  phase        : constant angular offset (deg) at E1 = 0

Usage:
  python scripts/analyze_rotary_scans.py <diag_dir> [--per-scan 3000] [--sample 6000]
"""
import sys, os, json, argparse
import numpy as np
from scipy.spatial import cKDTree
from scipy.optimize import minimize

D2R = np.pi / 180.0

def rot_axis_angle(axis, ang):
    """Rodrigues rotation matrix for a unit axis and angle (rad)."""
    a = axis / (np.linalg.norm(axis) + 1e-12)
    c, s = np.cos(ang), np.sin(ang)
    x, y, z = a
    C = 1 - c
    return np.array([
        [c + x*x*C,   x*y*C - z*s, x*z*C + y*s],
        [y*x*C + z*s, c + y*y*C,   y*z*C - x*s],
        [z*x*C - y*s, z*y*C + x*s, c + z*z*C],
    ])

def axis_from_tilt(tiltx_deg, tilty_deg):
    """World +Z tilted by small rotations about X then Y (deg) -> unit axis."""
    n = np.array([0.0, 0.0, 1.0])
    n = rot_axis_angle([1, 0, 0], tiltx_deg * D2R) @ n
    n = rot_axis_angle([0, 1, 0], tilty_deg * D2R) @ n
    return n / np.linalg.norm(n)

def load(diag_dir):
    man = json.load(open(os.path.join(diag_dir, "manifest.json")))
    center0 = np.array(man["rotaryCenter"], float)
    sign = float(man["rotationSign"])
    scans = []
    for s in man["scans"]:
        pts = np.loadtxt(os.path.join(diag_dir, s["file"]))
        if pts.ndim == 1:
            continue
        scans.append((float(s["e1"]), pts.astype(np.float64)))
    return center0, sign, scans

def unrotate_all(params, center0, sign, scans, labels_out=None):
    """Bring every scan back to the E1=0 frame with the candidate model; return stacked pts+labels."""
    tiltx, tilty, dcx, dcy, dcz, gain, phase = params
    axis = axis_from_tilt(tiltx, tilty)
    C = center0 + np.array([dcx, dcy, dcz])
    out, labels = [], []
    for i, (e1, pts) in enumerate(scans):
        ang = sign * (gain * e1 + phase) * D2R     # the rotation the model applied at capture
        R = rot_axis_angle(axis, -ang)             # undo it
        q = (pts - C) @ R.T + C
        out.append(q)
        labels.append(np.full(len(q), i))
    P = np.vstack(out)
    L = np.concatenate(labels)
    if labels_out is not None:
        labels_out.append(L)
    return P, L

def consistency_rms(params, center0, sign, scans, sample_idx, regularize=True):
    """Mean cross-scan nearest-neighbour distance of a fixed sample (lower = scans agree)."""
    P, L = unrotate_all(params, center0, sign, scans)
    tree = cKDTree(P)
    sp = P[sample_idx]
    sl = L[sample_idx]
    # k=6 so we can skip neighbours from the same scan (self-overlap would read 0).
    d, idx = tree.query(sp, k=6)
    best = np.full(len(sp), np.nan)
    for j in range(len(sp)):
        for kk in range(6):
            if L[idx[j, kk]] != sl[j]:
                best[j] = d[j, kk]
                break
    best = best[np.isfinite(best)]
    # robust: trim the worst 10% (occlusion / non-overlap edges) so the fit isn't dragged by them
    best.sort()
    best = best[: int(len(best) * 0.9) + 1]
    rms = float(np.sqrt(np.mean(best ** 2)))
    if not regularize:
        return rms
    # A real correction is small; a symmetric bed can otherwise alias to a +90°/180° phase that
    # also "aligns". Mild regularisation breaks the tie toward the physical (small-phase) solution
    # without distorting genuine small offsets (10°→0.05mm, 90°→4mm).
    phase = params[6]
    return rms + 5e-4 * phase * phase

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("diag_dir")
    ap.add_argument("--per-scan", type=int, default=3000, help="max points kept per scan for the fit")
    ap.add_argument("--sample", type=int, default=6000, help="scoring sample size")
    args = ap.parse_args()

    center0, sign, scans = load(args.diag_dir)
    if len(scans) < 2:
        print("Need >=2 scans with distinct E1."); return
    # decimate per scan for speed
    rng = np.random.default_rng(0)
    scans = [(e1, p[rng.choice(len(p), min(args.per_scan, len(p)), replace=False)]) for e1, p in scans]
    e1s = np.array([e1 for e1, _ in scans])
    print(f"Loaded {len(scans)} scans, E1 range [{e1s.min():.1f}, {e1s.max():.1f}]°, "
          f"centre0 ({center0[0]:.1f}, {center0[1]:.1f}, {center0[2]:.1f}), sign {sign:+.0f}")

    # fixed scoring sample (same indices every eval so the objective is smooth)
    Ptmp, _ = unrotate_all([0,0,0,0,0,1,0], center0, sign, scans)
    sample_idx = rng.choice(len(Ptmp), min(args.sample, len(Ptmp)), replace=False)

    x0 = np.array([0, 0, 0, 0, 0, 1.0, 0])      # current app model
    base = consistency_rms(x0, center0, sign, scans, sample_idx)
    print(f"\nCurrent app model (axis=+Z, gain=1, phase=0): cross-scan RMS = {base:.3f} mm")

    res = minimize(consistency_rms, x0, args=(center0, sign, scans, sample_idx),
                   method="Powell", options={"xtol": 1e-4, "ftol": 1e-4, "maxiter": 4000})
    tiltx, tilty, dcx, dcy, dcz, gain, phase = res.x
    axis = axis_from_tilt(tiltx, tilty)
    tilt_total = np.degrees(np.arccos(np.clip(axis[2], -1, 1)))
    opt_rms = consistency_rms(res.x, center0, sign, scans, sample_idx, regularize=False)

    print(f"\nOptimised model: cross-scan RMS = {opt_rms:.3f} mm  (was {base:.3f} mm)\n")
    print(f"  axis tilt        : {tilt_total:.3f}°  (tiltX {tiltx:+.3f}, tiltY {tilty:+.3f}; axis {axis.round(4)})")
    print(f"  centre offset    : ({dcx:+.2f}, {dcy:+.2f}, {dcz:+.2f}) mm  from the app's rotary centre")
    print(f"  gain (deg/E1deg) : {gain:.5f}   (error {100*(gain-1):+.3f}% vs ideal 1.0)")
    print(f"  phase offset     : {phase:+.3f}°  at E1 = 0")

    print("\nDiagnosis:")
    if tilt_total > 0.2:
        print(f"  • Axis is tilted {tilt_total:.2f}° from world-vertical — the viewport spins the bed about")
        print(f"    pure +Z, so scans wobble (worst at large E1). FIX: rotate the pivot about this axis.")
    if abs(gain - 1) > 0.002:
        print(f"  • Scene rotates {100*(gain-1):+.2f}% per E1° vs reality — error grows with E1.")
        print(f"    FIX: apply MeasuredDegPerE1 (gain) to the pivot rotation, not a flat 1:1.")
    if abs(phase) > 0.2:
        print(f"  • Constant {phase:+.2f}° phase offset at E1=0 (mounting/home reference).")
    if np.hypot(dcx, dcy) > 2:
        print(f"  • Rotation centre off by {np.hypot(dcx,dcy):.1f} mm in XY — recalibrate bed centre.")
    if tilt_total <= 0.2 and abs(gain-1) <= 0.002 and abs(phase) <= 0.2 and np.hypot(dcx,dcy) <= 2:
        print("  • Model already consistent — residual is scan noise, not a calibration error.")

if __name__ == "__main__":
    main()
