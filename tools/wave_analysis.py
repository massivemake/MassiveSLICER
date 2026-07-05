#!/usr/bin/env python3
"""Measure sine-wave phase coherence across layers in a KRL .src toolpath.

v2 — probes FIXED WORLD POINTS instead of the per-layer seam, and subtracts the
deliberate per-layer stagger, so the output shows only UNWANTED drift.

For each sampled layer and each probe point:
  1. Find the contour point nearest the probe (XY).
  2. Estimate the local base contour by smoothing (+/-15 mm arc window).
  3. Compute signed lateral deviation (the wave signal).
  4. Correlate a +/-2-wavelength window around the probe against sin/cos at the
     nominal wavelength -> local wave phase at that world location.
  5. Subtract the expected stagger (Z * stagger radians) -> residual phase.

If the wave is phase-locked, the residual at each probe stays constant across
layers.  Output is in mm of sideways wave shift (wavelength * phase/2pi).

Usage: wave_analysis.py file.src [wavelength_mm] [stagger_rad_per_mm]
"""
import re
import math
import sys
from collections import defaultdict

SRC      = sys.argv[1]
LAMBDA   = float(sys.argv[2]) if len(sys.argv) > 2 else 23.9
STAGGER  = float(sys.argv[3]) if len(sys.argv) > 3 else 0.2
SAMPLE   = 25   # analyze every Nth layer

pat = re.compile(r"LIN \{X ([-\d.]+), Y ([-\d.]+), Z ([-\d.]+)")
layers = defaultdict(list)
with open(SRC) as f:
    for line in f:
        m = pat.match(line.strip())
        if m:
            layers[float(m.group(3))].append((float(m.group(1)), float(m.group(2))))

zs = sorted(layers.keys())
print(f"file: {SRC.split('/')[-1]}")
print(f"layers: {len(zs)}   z: {zs[0]:.0f}..{zs[-1]:.0f}   lambda={LAMBDA}  stagger={STAGGER}")

def arcs_of(pts):
    a = [0.0]*len(pts)
    for i in range(1, len(pts)):
        a[i] = a[i-1] + math.hypot(pts[i][0]-pts[i-1][0], pts[i][1]-pts[i-1][1])
    return a

def smooth(pts, arc, half_w=15.0):
    n = len(pts)
    smx=[0.0]*n; smy=[0.0]*n
    lo=0; hi=0; sx=0.0; sy=0.0; c=0
    for i in range(n):
        while hi < n and arc[hi] <= arc[i]+half_w:
            sx+=pts[hi][0]; sy+=pts[hi][1]; c+=1; hi+=1
        while arc[lo] < arc[i]-half_w:
            sx-=pts[lo][0]; sy-=pts[lo][1]; c-=1; lo+=1
        smx[i]=sx/c; smy[i]=sy/c
    return smx, smy

def deviation(pts, smx, smy):
    n=len(pts); dev=[0.0]*n
    for i in range(1, n-1):
        tx=smx[i+1]-smx[i-1]; ty=smy[i+1]-smy[i-1]
        tl=math.hypot(tx,ty)
        if tl<1e-9: continue
        tx/=tl; ty/=tl
        dev[i] = tx*(pts[i][1]-smy[i]) - ty*(pts[i][0]-smx[i])
    return dev

def winding(pts):
    a2 = 0.0
    for i in range(len(pts)-1):
        a2 += pts[i][0]*pts[i+1][1] - pts[i+1][0]*pts[i][1]
    return 1.0 if a2 >= 0 else -1.0

def tangent_at(pts, i, orient):
    j0, j1 = max(0, i-1), min(len(pts)-1, i+1)
    tx, ty = pts[j1][0]-pts[j0][0], pts[j1][1]-pts[j0][1]
    tl = math.hypot(tx, ty)
    if tl < 1e-9: return (1.0, 0.0)
    return (orient*tx/tl, orient*ty/tl)

def phase_at(pts, arc, dev, probe, probe_dir, orient, lam):
    """Correlation phase near `probe`, matching only same-direction points so the
    probe can't jump between the two faces of a single-bead wall."""
    best=float('inf'); bi=-1
    for i,(x,y) in enumerate(pts):
        tx, ty = tangent_at(pts, i, orient)
        if tx*probe_dir[0] + ty*probe_dir[1] <= 0: continue
        d=(x-probe[0])**2+(y-probe[1])**2
        if d<best: best=d; bi=i
    if bi < 0: return None, -1
    s0=arc[bi]; w=2*lam
    re_=0.0; im_=0.0; cnt=0
    for i in range(len(pts)):
        ds=arc[i]-s0
        if -w<=ds<=w:
            ang=2*math.pi*ds/lam
            re_+=dev[i]*math.cos(ang); im_+=dev[i]*math.sin(ang); cnt+=1
    if cnt<8: return None, math.sqrt(best)
    return math.atan2(re_, im_), math.sqrt(best)   # phase of sin fit

# probes = fixed world points from layer 0 (position + direction): start + 1/4, 1/2, 3/4
p0   = layers[zs[0]]
a0   = arcs_of(p0)
L0   = a0[-1]
or0  = winding(p0)
def at_arc_idx(arc, s):
    for i in range(len(arc)):
        if arc[i]>=s: return i
    return len(arc)-1
probe_idx = [0, at_arc_idx(a0, L0*0.25), at_arc_idx(a0, L0*0.5), at_arc_idx(a0, L0*0.75)]
probes    = [(p0[i], tangent_at(p0, i, or0)) for i in probe_idx]
names     = ["anchor","quarter","half","threeQ"]

print(f"\n{'Z':>7} " + "".join(f"{n+'(mm)':>13}{'d':>6}" for n in names))
series = {n: [] for n in names}
for z in zs[::SAMPLE]:
    pts = layers[z]
    if len(pts) < 200: continue
    arc = arcs_of(pts)
    if arc[-1] < 500: continue
    orient = winding(pts)
    smx, smy = smooth(pts, arc)
    dev = deviation(pts, smx, smy)
    row = f"{z:7.0f} "
    for n, (pr, pdir) in zip(names, probes):
        ph, dist = phase_at(pts, arc, dev, pr, pdir, orient, LAMBDA)
        if ph is None:
            row += f"{'--':>13}{dist:6.0f}"; continue
        resid = (ph - z*STAGGER) % (2*math.pi)
        mm = resid/(2*math.pi)*LAMBDA
        series[n].append(mm)
        row += f"{mm:13.1f}{dist:6.0f}"
    print(row)

def circ_std_mm(vals, lam):
    if len(vals)<3: return None
    angs=[v/lam*2*math.pi for v in vals]
    c=sum(math.cos(a) for a in angs)/len(angs)
    s=sum(math.sin(a) for a in angs)/len(angs)
    R=math.hypot(c,s)
    if R<1e-9: return lam/2
    return math.sqrt(-2*math.log(R))/(2*math.pi)*lam

print("\nresidual spread (circular std, mm of wave shift — lower = better lock):")
for n in names:
    sd = circ_std_mm(series[n], LAMBDA)
    print(f"  {n:8s}: {sd:.2f} mm" if sd is not None else f"  {n:8s}: n/a")
