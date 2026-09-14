#!/usr/bin/env python3
"""Headless validation for extracted circuits. Mirror of Brain/LifNetwork.cs.

Runs the validation protocol from docs (same targets as
blendi-remade/fly-brain-minecraft docs/VALIDATION.md, scaled to circuits):

  silent  0 spikes with no drive
  sugar   sugar GRNs @120Hz -> MN9 30-90Hz, Kenyon cells ~0 (no KCs in circuits)
  bitter  bitter + sugar -> MN9 suppressed to 0-10Hz
  loom    LC4+LPLC2 @150Hz -> DNp01 burst, decoder enters ESCAPE
  groom   JO-FV/JO-CM/BM_InOm @150Hz -> aDN1 (DNg62) high

The Python model must match Brain/LifNetwork.cs exactly: current-based LIF,
tau_m 20ms, tau_s 5ms, rest/reset -52mV, threshold -45mV, refractory 2.2ms,
delay 1.8ms, 0.275mV/synapse * gain, Dale's-law signs, Poisson sensory drive,
exact linear per-step integration. Any intentional divergence must be ported
to the C# side too.

Usage:
  pip install numpy scipy
  python tools/bench.py --circuits Circuits/
  python tools/bench.py --circuits Circuits/ --experiment loom --gain 0.65
"""
import argparse
import json
import sys
from pathlib import Path

import numpy as np
from scipy import sparse

SIGN_NEG = {"gaba", "glutamate", "glut", "histamine", "hist"}


class CircuitNet:
    def __init__(self, circuits_dir: Path, gain=0.65, dt_ms=0.5, seed=7):
        nlist, elist = [], []
        idx = {}
        seen = set()  # circuits share neurons and edges; count each edge once (as CircuitLoader.cs)
        for f in sorted(circuits_dir.glob("*.json")):
            m = json.loads(f.read_text())
            if m["format"] == "flyraria-scope-shape-v1":  # neuroscope outline, not a circuit
                continue
            assert m["format"] == "flyraria-circuit-v1", f
            for n in m["neurons"]:
                if n["body"] in idx:
                    continue
                idx[n["body"]] = len(nlist)
                nlist.append(n)
            for pre, post, w in m["edges"]:
                if (pre, post) in seen:
                    continue
                seen.add((pre, post))
                elist.append((idx[pre], idx[post], w))
        self.n = len(nlist)
        self.types = [n["type"] for n in nlist]
        self.sx = np.array([n.get("x", 0) for n in nlist], float)
        sign = np.array([1 if (n.get("nt", "") or "").strip().lower() not in SIGN_NEG else -1
                         for n in nlist], float)
        w = np.array([c for _, _, c in elist], float)
        pre = np.array([p for p, _, _ in elist], dtype=np.int64)
        post = np.array([q for _, q, _ in elist], dtype=np.int64)
        # Sparse CSC (post x pre): a dense n x n matrix would need ~250 GB at
        # full-brain scale (176k neurons); this stores only the real edges.
        self.W = sparse.csc_matrix((w * sign[pre] * 0.275 * gain, (post, pre)),
                                   shape=(self.n, self.n))
        self.dt = dt_ms
        self.rng = np.random.default_rng(seed)
        self.tau_m, self.tau_s = 20.0, 5.0
        self.rest, self.thr = -52.0, -45.0
        self.refr_steps = int(round(2.2 / dt_ms))
        self.delay_steps = max(1, int(round(1.8 / dt_ms)))
        self.decay_v = float(np.exp(-dt_ms / self.tau_m))
        self.decay_g = float(np.exp(-dt_ms / self.tau_s))
        self.reset()

    def reset(self):
        self.v = np.full(self.n, self.rest)
        self.g = np.zeros(self.n)
        self.refr = np.zeros(self.n, int)
        self.queue = [np.zeros(self.n) for _ in range(self.delay_steps)]
        self.qi = 0
        self.hz = np.zeros(self.n)

    def pop(self, prefix, side=None):
        out = []
        for i, t in enumerate(self.types):
            if t != prefix and not t.startswith(prefix + "_"):
                continue
            if side == "L" and self.sx[i] > 0:
                continue
            if side == "R" and self.sx[i] < 0:
                continue
            out.append(i)
        return out

    def drive(self, prefix, hz, side=None):
        for i in self.pop(prefix, side):
            self.hz[i] = hz

    def step(self, ms):
        steps = int(round(ms / self.dt))
        spikes = np.zeros(self.n, int)
        p = self.hz * (self.dt / 1000.0)
        for _ in range(steps):
            self.g += self.queue[self.qi]
            self.queue[self.qi] = 0.0
            self.qi = (self.qi + 1) % self.delay_steps
            poisson = self.rng.random(self.n) < p
            fired = np.zeros(self.n, bool)
            fired[poisson] = True
            active = self.refr <= 0
            self.v[active] = self.rest + (self.v[active] - self.rest) * self.decay_v \
                + self.g[active] * (1.0 - self.decay_v)
            self.g *= self.decay_g
            fired |= (self.v >= self.thr) & active & (self.hz == 0) | \
                     (self.v >= self.thr) & active & poisson
            # Spontaneous threshold crossings only; Poisson cells fire from drive.
            fired = ((self.v >= self.thr) & active) | poisson
            self.v[fired] = self.rest
            self.g[fired] = 0.0
            self.refr[fired] = self.refr_steps
            self.refr[active] -= 0
            self.refr[self.refr > 0] -= 1
            if fired.any():
                # Column slice touches only the spiking neurons' outgoing edges.
                self.queue[self.qi] += self.W[:, np.flatnonzero(fired)].sum(axis=1).A1
            spikes += fired
        secs = ms / 1000.0
        return spikes / secs if secs else spikes

    def rate(self, prefix, side=None):
        idx = self.pop(prefix, side)
        if not idx:
            return float("nan")
        return float(self.last[idx].mean())

    def run(self, ms, warmup_ms=500):
        self.step(warmup_ms)
        self.last = self.step(ms)
        return self.last


def experiment(net: CircuitNet, name: str):
    net.reset()
    if name == "silent":
        net.run(1000)
        total = net.last.sum()
        return total == 0, f"total spikes={total:.0f} (want 0)"
    if name == "sugar":
        for t, hz in [("LB3b", 120), ("LB3c", 120), ("PhG1a", 100), ("LgLG3", 80)]:
            net.drive(t, hz)
        net.run(2000)
        mn9 = net.rate("MN9")
        ok = 30 <= mn9 <= 90
        return ok, f"MN9={mn9:.0f}Hz (want 30-90)"
    if name == "bitter":
        for t, hz in [("LB3b", 120), ("LB3c", 120), ("LB1a", 120), ("LB1b", 120)]:
            net.drive(t, hz)
        net.run(2000)
        mn9 = net.rate("MN9")
        ok = mn9 <= 10
        return ok, f"MN9={mn9:.0f}Hz (want 0-10)"
    if name == "loom":
        for side in ("L", "R"):
            net.drive("LC4", 150, side)
            net.drive("LPLC2", 150, side)
        net.run(1000)
        gf = net.rate("DNp01")
        ok = gf > 100 and not np.isnan(gf)
        return ok, f"DNp01={gf:.0f}Hz (want burst >100)"
    if name == "groom":
        net.drive("DNg62", 0)  # driven via JO proxies below
        for t in ("JO-FV", "JO-CM", "BM_InOm"):
            for i in net.pop(t):
                net.hz[i] = 150
        net.run(2000)
        a = net.rate("DNg62")
        ok = a > 40 and not np.isnan(a)
        return ok, f"aDN1(DNg62)={a:.0f}Hz (want >40)"
    raise ValueError(name)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--circuits", default="Circuits")
    ap.add_argument("--experiment", default="all",
                    choices=["all", "silent", "sugar", "bitter", "loom", "groom"])
    ap.add_argument("--gain", type=float, default=0.65)
    args = ap.parse_args()

    net = CircuitNet(Path(args.circuits), gain=args.gain)
    print(f"loaded {net.n} neurons, gain={args.gain}")
    names = ["silent", "sugar", "bitter", "loom", "groom"] \
        if args.experiment == "all" else [args.experiment]
    failed = 0
    for name in names:
        try:
            ok, detail = experiment(net, name)
        except Exception as e:
            ok, detail = False, f"error: {e}"
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
        failed += not ok
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
