#!/usr/bin/env python3
"""Headless validation for the brain. Mirror of Brain/LifNetwork.cs.

Runs the validation protocol from docs (same targets as
blendi-remade/fly-brain-minecraft docs/VALIDATION.md) on the extracted circuits
(--circuits, the default) or on the whole male CNS (--connectome):

  silent  0 spikes with no drive
  sugar   sugar GRNs @120Hz -> MN9 30-90Hz (Kenyon cell rate reported on the whole CNS)
  bitter  bitter + sugar -> MN9 suppressed to 0-10Hz
  loom    LC4+LPLC2 @150Hz -> DNp01 burst, decoder enters ESCAPE
  groom   JO-FV/JO-CM/BM_InOm @150Hz -> aDN1 (DNg62) high

The Python model is the C# one: current-based LIF, tau_m 20ms, tau_s 5ms,
rest/reset -52mV, threshold -45mV, refractory 2.2ms, delay 1.8ms,
0.275mV/synapse * gain, Dale's-law signs with dopamine/octopamine/serotonin at 0
and Kenyon cell -> Kenyon cell synapses inhibitory (Brain/Connectome.cs), Poisson
sensory drive, exact linear per-step integration. The whole CNS also runs
short-term depression on every neuron that isn't Poisson-driven
(LifNetwork.Params.WholeCns). Poisson draws come from numpy, so spikes agree with
the C# engine statistically, not spike for spike. Any intentional divergence must
be ported to the C# side too.

Usage:
  pip install numpy scipy
  python tools/bench.py --circuits Circuits/
  python tools/bench.py --circuits Circuits/ --experiment loom --gain 0.65
  python tools/bench.py --connectome Circuits/male-cns.connectome.gz   # a few minutes, ~2 GB RAM
"""
import argparse
import gzip
import json
import struct
import sys
from pathlib import Path

import numpy as np
from scipy import sparse

SIGN_NEG = {"gaba", "glutamate", "glut", "histamine", "hist"}
# Modulatory transmitters act through G-protein receptors, not fast synapses (Connectome.SignForTransmitter).
SIGN_ZERO = {"dopamine", "octopamine", "serotonin"}

# LifNetwork.Params: Shiu2024() for the circuits, WholeCns() for the whole CNS.
CIRCUIT_DEFAULTS = {"gain": 0.65, "std_u": 0.0, "std_tau": 300.0}
WHOLE_CNS_DEFAULTS = {"gain": 0.8, "std_u": 0.05, "std_tau": 300.0}
CATCH_UP_STEPS = 8192  # LifNetwork.CatchUpSteps: resources count as recovered after this many steps


def transmitter_sign(nt):
    nt = (nt or "").strip().lower()
    return -1 if nt in SIGN_NEG else 0 if nt in SIGN_ZERO else 1


def load_circuits(circuits_dir: Path):
    """Merged circuit JSON as (types, soma x, transmitters, pre, post, synapses)."""
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
    return ([n["type"] for n in nlist],
            np.array([n.get("x", 0) for n in nlist], float),
            [n.get("nt", "") for n in nlist],
            np.array([p for p, _, _ in elist], dtype=np.int64),
            np.array([q for _, q, _ in elist], dtype=np.int64),
            np.array([c for _, _, c in elist], float))


def read_varints(buf, pos, count):
    """count LEB128 varints starting at buf[pos], as int64, plus the position after them."""
    view = np.frombuffer(buf, np.uint8, count=min(len(buf) - pos, 5 * count), offset=pos)
    ends = np.flatnonzero(view < 0x80)[:count]
    starts = np.concatenate([[0], ends[:-1] + 1])
    lengths = ends - starts + 1
    values = np.zeros(count, np.int64)
    for shift in range(5):
        more = lengths > shift
        values[more] |= (view[starts[more] + shift].astype(np.int64) & 0x7F) << (7 * shift)
    return values, pos + int(ends[-1]) + 1


def load_connectome(path: Path):
    """tools/extract_connectome.py output (flyraria-connectome-v1) in the same shape as load_circuits."""
    buf = gzip.decompress(path.read_bytes())
    assert buf[:8] == b"FLYCONN1", path
    pos = 8

    def string():
        nonlocal pos
        (length,) = struct.unpack_from("<H", buf, pos)
        pos += 2 + length
        return buf[pos - length:pos].decode()

    def int32s(count):
        nonlocal pos
        pos += 4 * count
        return np.frombuffer(buf, "<i4", count, pos - 4 * count)

    string()  # dataset
    int32s(1)  # min weight
    n, e, s = (int(v) for v in int32s(3))
    strings = np.array([string() for _ in range(s)], dtype=object)
    int32s(n)  # body ids
    types = list(strings[int32s(n)])
    nts = list(strings[int32s(n)])
    int32s(n)  # superclass
    side = np.frombuffer(buf, np.uint8, n, pos)
    pos += n + 12 * n  # sides, then positions (unused)
    sx = np.where(side == ord("L"), -1.0, np.where(side == ord("R"), 1.0, 0.0))
    row_len, pos = read_varints(buf, pos, n)
    deltas, pos = read_varints(buf, pos, e)
    weights, pos = read_varints(buf, pos, e)
    assert pos == len(buf), "connectome file has trailing bytes"
    row_start = np.concatenate([[0], np.cumsum(row_len)[:-1]])
    pre = np.repeat(np.arange(n, dtype=np.int64), row_len)
    total = np.cumsum(deltas)
    post = total - np.concatenate([[0], total])[row_start][pre]  # targets are delta-coded per row
    return types, sx, nts, pre, post, weights.astype(float)


class CircuitNet:
    def __init__(self, graph, gain, std_u=0.0, std_tau=300.0, dt_ms=0.5, seed=7):
        types, sx, nts, pre, post, w = graph
        self.n = len(types)
        self.types = types
        self.sx = sx
        sign = np.array([transmitter_sign(nt) for nt in nts], float)
        # Kenyon cell -> Kenyon cell synapses inhibit (Connectome.InvertedEdges, Manoim et al. 2022).
        kc = np.array([t.startswith("KC") for t in types])
        edge_sign = np.where(kc[pre] & kc[post], -1.0, 1.0)
        # Sparse CSC (post x pre): a dense n x n matrix would need ~250 GB at
        # full-brain scale (176k neurons); this stores only the real edges.
        self.W = sparse.csc_matrix((w * sign[pre] * edge_sign * 0.275 * gain, (post, pre)),
                                   shape=(self.n, self.n))
        self.dt = dt_ms
        self.std_u = std_u
        self.std_tau = std_tau
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
        self.t = 0
        self.resource = np.ones(self.n)
        self.last_fire = np.zeros(self.n, np.int64)

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
            active = self.refr <= 0
            self.v[active] = self.rest + (self.v[active] - self.rest) * self.decay_v \
                + self.g[active] * (1.0 - self.decay_v)
            self.g *= self.decay_g
            # Threshold crossings, plus Poisson cells firing from drive.
            fired = ((self.v >= self.thr) & active) | poisson
            self.v[fired] = self.rest
            self.g[fired] = 0.0
            self.refr[fired] = self.refr_steps
            self.refr[self.refr > 0] -= 1
            idx = np.flatnonzero(fired)
            if idx.size:
                # Column slice touches only the spiking neurons' outgoing edges.
                self.queue[self.qi] += self.W[:, idx] @ self.efficacy(idx)
            spikes += fired
            self.t += 1
        secs = ms / 1000.0
        return spikes / secs if secs else spikes

    def efficacy(self, idx):
        """Short-term depression: the share of synaptic resources each spike transmits."""
        eff = np.ones(idx.size)
        if self.std_u <= 0:
            return eff
        own = self.hz[idx] <= 0  # Poisson-driven sensory neurons don't depress
        cells = idx[own]
        k = self.t - self.last_fire[cells]
        available = np.where(k >= CATCH_UP_STEPS, 1.0,
                             1.0 - (1.0 - self.resource[cells]) * np.exp(-k * self.dt / self.std_tau))
        self.resource[cells] = available * (1.0 - self.std_u)
        self.last_fire[cells] = self.t
        eff[own] = available
        return eff

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
        kcs = [i for i, t in enumerate(net.types) if t.startswith("KC")]
        kc = f", Kenyon cells {net.last[kcs].mean():.1f}Hz" if kcs else ""
        return ok, f"MN9={mn9:.0f}Hz (want 30-90){kc}"
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
    ap.add_argument("--connectome", help="whole-CNS file from tools/extract_connectome.py, instead of the circuits")
    ap.add_argument("--experiment", default="all",
                    choices=["all", "silent", "sugar", "bitter", "loom", "groom"])
    ap.add_argument("--gain", type=float, help="default 0.65 (circuits) or 0.8 (whole CNS)")
    ap.add_argument("--std-u", type=float, help="short-term depression per spike; default 0 (circuits) or 0.05 (whole CNS)")
    ap.add_argument("--std-tau", type=float, help="depression recovery in ms; default 300")
    args = ap.parse_args()

    if args.connectome:
        graph, defaults = load_connectome(Path(args.connectome)), WHOLE_CNS_DEFAULTS
    else:
        graph, defaults = load_circuits(Path(args.circuits)), CIRCUIT_DEFAULTS
    gain = defaults["gain"] if args.gain is None else args.gain
    std_u = defaults["std_u"] if args.std_u is None else args.std_u
    std_tau = defaults["std_tau"] if args.std_tau is None else args.std_tau
    net = CircuitNet(graph, gain=gain, std_u=std_u, std_tau=std_tau)
    print(f"loaded {net.n} neurons, gain={gain}, depression U={std_u} tau={std_tau}ms")
    names = ["silent", "sugar", "bitter", "loom", "groom"] \
        if args.experiment == "all" else [args.experiment]
    failed = 0
    for name in names:
        try:
            ok, detail = experiment(net, name)
        except Exception as e:
            ok, detail = False, f"error: {e}"
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}", flush=True)
        failed += not ok
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
