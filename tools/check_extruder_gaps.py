#!/usr/bin/env python3
"""
Audit a KUKA .src for extruder silence — the failure behind Scene 08 (2026-07-29).

The Caracol extruder drops to idle if the robot stops talking to it, then reports
not-ready and the program halts at WAIT FOR $IN[6]. Caracol's own Eidos output never
lets a gap exceed ~35s; MassiveSLICER now caps it at 30s (ExtruderKeepAliveEnabled).

Usage:
    python3 tools/check_extruder_gaps.py FILE.src [FILE2.src ...]

Reports estimated print time, how many commands the extruder hears, and the longest
gaps. Times come from distance / $VEL.CP and ignore acceleration and $APO blending,
so real gaps are slightly LONGER than reported — treat the numbers as a lower bound.

Rule of thumb: anything over ~35s is a risk; over a minute has killed prints.
"""
import re, sys, math

MOVE = re.compile(r'^\s*(LIN|LIN_REL|PTP)\s*\{')
XYZ  = re.compile(r'X\s*(-?[\d.]+),\s*Y\s*(-?[\d.]+),\s*Z\s*(-?[\d.]+)')
VEL  = re.compile(r'^\s*\$VEL\.CP\s*=\s*([\d.]+)')
# Anything the extruder hears: screw speed, digital handshakes, temps.
CMD  = re.compile(r'^\s*(RPM\s*=|\$ANOUT\[|\$OUT\[\s*[789]\s*\]\s*=|T[123]\s*=)')
WAIT = re.compile(r'^\s*WAIT\s+SEC\s+([\d.]+)', re.I)

def analyse(path):
    t = 0.0; vel = 0.2; prev = None
    events = []           # (time, line, text)
    ncmd = 0
    for i, raw in enumerate(open(path, errors='replace'), 1):
        line = raw.rstrip('\n')
        m = VEL.match(line)
        if m:
            v = float(m.group(1))
            if v > 0: vel = v
            continue
        w = WAIT.match(line)
        if w:
            t += float(w.group(1)); continue
        if CMD.match(line):
            ncmd += 1
            events.append((t, i, line.strip()))
            continue
        if MOVE.match(line):
            p = XYZ.search(line)
            if not p: continue
            cur = tuple(float(x) for x in p.groups())
            if prev is not None:
                d = math.dist(prev, cur)          # mm
                t += (d / 1000.0) / vel           # vel is m/s
            prev = cur
    return t, ncmd, events

for path in sys.argv[1:]:
    total, ncmd, ev = analyse(path)
    name = path.split('/')[-1]
    print(f"\n=== {name} ===")
    print(f"  est. print time : {total/3600:.2f} h   ({total/60:.0f} min)")
    print(f"  extruder commands: {ncmd}")
    if len(ev) < 2:
        print("  (too few events for gap analysis)"); continue
    gaps = [(ev[k+1][0]-ev[k][0], ev[k][0], ev[k][1]) for k in range(len(ev)-1)]
    tail = total - ev[-1][0]
    gaps.append((tail, ev[-1][0], ev[-1][1]))
    gaps.sort(reverse=True)
    print(f"  last command at : {ev[-1][0]/60:.1f} min   → then {tail/60:.1f} min of silence to end")
    print(f"  LONGEST gaps:")
    for g, at, ln in gaps[:4]:
        print(f"     {g:8.1f} s  starting at {at/60:7.2f} min (line {ln})")
