#!/usr/bin/env python3
"""Fetch extracted Drosophila sensorimotor circuits from neuPrint into Circuits/.

Pulls small named circuits (hundreds to ~2k neurons each) from the male CNS
connectome instead of the full 176k graph, per the FlyRarria extracted-circuits
decision. Output JSON (flyraria-circuit-v1) is consumed by Brain/CircuitLoader.cs
and tools/bench.py.

Circuits and their seed types:
  escape: LC4/LPLC2/LPLC1 -> DNp01/DNp02/DNp11 -> PSI -> TTMn
  steer:  DNa01/DNa02 + visual chase (LC10a/LC11) + walking DNs (DNp09/DNg100/MDN)
  feed:   sugar GRNs (LB3b/LB3c/PhG1a-c/LgLG3/LgLG4) -> MN9
          + bitter GRNs (LB1a-d) + food odor ORNs (DM1/VA2)
  groom:  JO-C/E (wind/deflection), JO-FV, BM_InOm bristles
          -> aDN1 (DNg62)/DNge078/DNg12 (+ head/leg/abdomen DNs)
  song:   pC1 -> pIP10/pMP2 + social odor (ORN_VA1v) + heat (TRN_VP2)
  (P1 is not a male-cns:v1.0 type name; pC1 covers the P1 cluster there.)

Every type the C# encoders/decoder reference is seeded somewhere, otherwise its
population resolves empty and that sense silently does nothing.

Method:
  1. Seed neurons: type == seed or type starts with seed + "_" (the same rule as
     PopulationIndex and bench.pop). Anonymous Cypher against
     https://neuprint.janelia.org/api/custom/custom, dataset male-cns:v1.0.
  2. Bridge expansion: add neurons that receive >= --min-weight synapses from a
     seed AND send >= --min-weight to a seed, i.e. one-hop interneurons on
     seed -> x -> seed paths. Circuits in BRIDGE_HOPS also get two-hop paths
     (seed -> x -> y -> seed). A plain one-hop downstream expansion with these
     seeds is ~14k neurons, too heavy for the dense bench and the inline
     game-tick LIF loop; bridges give ~7.8k merged.
  3. Fetch edges within the union at weight >= --min-weight, with consensusNt /
     predictedNt, somaSide and instance for every neuron.

Output neuron fields: body, type, nt (raw transmitter string; the sign mapping
lives in Brain/Connectome.cs and bench.SIGN_NEG), side, and x = side sign
(L -1, R +1, else 0). male-cns has no signed soma X, and PopulationIndex / bench
split "L"/"R" populations on the sign of x. side is somaSide, or the _L/_R suffix
of instance for neurons without one (sensory neurons like JO and bristles have
their somata outside the CNS, so somaSide is null for them).
pos = [x, y, z] in male-cns voxels for the neuroscope overlay: somaLocation, or
the mean of up to POS_SYNAPSES of the neuron's synapses when it has no soma in
the volume (sensory neurons). tools/scope_shape.py projects the same coordinates.

Usage:
  pip install requests numpy
  python tools/extract_circuits.py --out Circuits/
  python tools/extract_circuits.py --circuit escape --min-weight 5
  python tools/extract_circuits.py --list-only   # print what would be fetched
  python tools/extract_circuits.py --positions-only   # add pos to existing files
"""
import argparse
import json
import sys
import time
from pathlib import Path

try:
    import requests
except ImportError:
    requests = None

NEUPRINT = "https://neuprint.janelia.org/api/custom/custom"
DATASET = "male-cns:v1.0"

JO_WIND = ["JO-CA1", "JO-CA2", "JO-CL", "JO-CM",
           "JO-ED1", "JO-ED2", "JO-EV1", "JO-EV2", "JO-EV3", "JO-EV4", "JO-EV5", "JO-EV6"]

CIRCUITS = {
    "escape": ["LC4", "LPLC2", "LPLC1", "DNp01", "DNp02", "DNp11", "PSI", "TTMn"],
    "steer": ["DNa01", "DNa02", "LC10a", "LC11", "DNp09", "DNg100", "MDN"],
    "feed": ["LB3b", "LB3c", "PhG1a", "PhG1b", "PhG1c", "LgLG3", "LgLG4",
             "LB1a", "LB1b", "LB1c", "LB1d", "GNG232", "DNg67", "MN9", "GNG087",
             "ORN_DM1", "ORN_VA2"],
    "groom": ["DNg62", "DNge078", "DNg12", "DNg07", "DNg08", "DNg11", "DNp29",
              "JO-FV", "BM_InOm"] + JO_WIND,
    "song": ["pC1", "pIP10", "pMP2", "ORN_VA1v", "TRN_VP2"],
}

# Circuits whose sensory -> motor path needs two interneurons. feed: with one hop,
# 3 of the 4 sugar GRN -> x -> MN9 neurons are inhibitory (GABA/histamine), MN9
# stays silent and bench `sugar` fails; with two hops all bench experiments pass.
BRIDGE_HOPS = {"feed": 2}

CHUNK = 2000  # bodyIds per inlined IN-list
POS_SYNAPSES = 300  # synapses averaged for a neuron without a soma location


def type_match(seeds, var: str = "n") -> str:
    """Exact type or `type_` subtype: the same rule as PopulationIndex and bench.pop.
    (A bare STARTS WITH would let LC4 pull in LC40/LC41/LC43/LC44/LC46b.)"""
    return "any(x IN %s WHERE %s.type = x OR %s.type STARTS WITH x + '_')" % (
        json.dumps(seeds), var, var)


def neuron_side(soma_side, instance) -> str:
    """somaSide, else the _L/_R suffix of instance, else "" (midline/unknown)."""
    if soma_side in ("L", "R", "M"):
        return soma_side
    if instance and instance[-2:] in ("_L", "_R"):
        return instance[-1]
    return ""


def side_sign(side: str) -> int:
    """PopulationIndex/bench split L/R on the sign of x: L -> -1, R -> +1, else 0."""
    return {"L": -1, "R": 1}.get(side, 0)


def cypher(query: str):
    # neuPrint's custom endpoint rejects $parameters (ParameterMissing) and picks
    # the dataset from the request (n.dataset is null), so values are inlined.
    if requests is None:
        raise RuntimeError("pip install requests first")
    for attempt in range(4):
        r = requests.post(NEUPRINT, params={"dataset": DATASET},
                          json={"cypher": query, "dataset": DATASET}, timeout=600)
        if r.status_code < 500 or attempt == 3:
            break
        time.sleep(5 * (attempt + 1))  # neuPrint's gateway returns transient 502s
    try:
        body = r.json()
    except ValueError:
        body = {"error": r.text[:300]}
    if r.status_code != 200 or "error" in body:
        raise RuntimeError(f"neuPrint HTTP {r.status_code}: {body.get('error')}")
    return body["data"]


def chunks(ids):
    ids = sorted(ids)
    for i in range(0, len(ids), CHUNK):
        yield ids[i:i + CHUNK]


def partners(bodies, minw: int, direction: str) -> set:
    """One hop from `bodies`: "down" = their targets, "up" = their inputs."""
    arrow = "(a :Neuron)-[e :ConnectsTo]->(b :Neuron)" if direction == "down" \
        else "(b :Neuron)-[e :ConnectsTo]->(a :Neuron)"
    out = set()
    for part in chunks(bodies):
        out |= {r[0] for r in cypher(
            f"MATCH {arrow} WHERE a.bodyId IN {json.dumps(part)} AND e.weight >= {minw} "
            f"RETURN DISTINCT b.bodyId")}
    return out


def fetch_positions(bodies) -> dict:
    """bodyId -> [x, y, z] voxels: somaLocation, else a synapse centroid."""
    pos = {}
    for part in chunks(bodies):
        for body, soma in cypher(
                f"MATCH (n :Neuron) WHERE n.bodyId IN {json.dumps(part)} "
                f"RETURN n.bodyId, n.somaLocation"):
            if soma:
                pos[body] = [round(c) for c in soma["coordinates"]]
    rest = sorted(set(bodies) - set(pos))
    for i in range(0, len(rest), 150):  # the per-neuron subquery is slow in big batches
        for body, x, y, z, k in cypher(
                f"UNWIND {json.dumps(rest[i:i + 150])} AS b MATCH (n :Neuron {{bodyId: b}}) "
                f"CALL {{ WITH n MATCH (n)-[:Contains]->(:SynapseSet)-[:Contains]->(s :Synapse) "
                f"WITH s LIMIT {POS_SYNAPSES} RETURN avg(s.location.x) AS x, avg(s.location.y) AS y, "
                f"avg(s.location.z) AS z, count(s) AS k }} RETURN n.bodyId, x, y, z, k"):
            if k:
                pos[body] = [round(x), round(y), round(z)]
    return pos


def add_positions(neurons) -> None:
    """Set pos on every neuron record neuPrint can place (in place)."""
    pos = fetch_positions([n["body"] for n in neurons])
    for n in neurons:
        if n["body"] in pos:
            n["pos"] = pos[n["body"]]


def fetch_circuit(name: str, seeds, min_weight: int) -> dict:
    minw = int(min_weight)
    seed_ids = {r[0] for r in cypher(f"MATCH (n :Neuron) WHERE {type_match(seeds)} RETURN n.bodyId")}
    down, up = partners(seed_ids, minw, "down"), partners(seed_ids, minw, "up")
    grown = seed_ids | (down & up)
    if BRIDGE_HOPS.get(name, 1) >= 2:
        # seed -> x -> y -> seed: x receives from a seed, y sends to a seed, x -> y.
        ys = json.dumps(sorted(up - seed_ids))
        for part in chunks(down - seed_ids):
            for a, b in cypher(
                    f"MATCH (a :Neuron)-[e :ConnectsTo]->(b :Neuron) "
                    f"WHERE a.bodyId IN {json.dumps(part)} AND b.bodyId IN {ys} "
                    f"AND e.weight >= {minw} RETURN a.bodyId, b.bodyId"):
                grown |= {a, b}
    neurons = []
    for part in chunks(grown):
        for body, typ, cnt, pred, soma_side, instance in cypher(
                f"MATCH (n :Neuron) WHERE n.bodyId IN {json.dumps(part)} "
                f"RETURN n.bodyId, n.type, n.consensusNt, n.predictedNt, n.somaSide, n.instance"):
            side = neuron_side(soma_side, instance)
            neurons.append({"body": body, "type": typ or "?", "nt": cnt or pred or "",
                            "x": side_sign(side), "side": side})
    add_positions(neurons)
    grown_list = json.dumps(sorted(grown))
    edges = []
    for part in chunks(grown):
        edges += [[a, b, int(w)] for a, b, w in cypher(
            f"MATCH (a :Neuron)-[e :ConnectsTo]->(b :Neuron) "
            f"WHERE a.bodyId IN {json.dumps(part)} AND b.bodyId IN {grown_list} "
            f"AND e.weight >= {minw} RETURN a.bodyId, b.bodyId, e.weight")]
    return {
        "dataset": DATASET, "circuit": name, "min_weight": min_weight,
        "seed_types": seeds, "format": "flyraria-circuit-v1",
        "neurons": neurons, "edges": edges,
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="Circuits")
    ap.add_argument("--circuit", choices=sorted(CIRCUITS), default=None)
    ap.add_argument("--min-weight", type=int, default=5)
    ap.add_argument("--list-only", action="store_true")
    ap.add_argument("--positions-only", action="store_true",
                    help="add pos to the existing circuit files without refetching the wiring")
    args = ap.parse_args()

    targets = [args.circuit] if args.circuit else sorted(CIRCUITS)
    if args.positions_only:
        for name in targets:
            path = Path(args.out) / f"{name}.json"
            manifest = json.loads(path.read_text())
            add_positions(manifest["neurons"])
            path.write_text(json.dumps(manifest) + "\n")
            print(f"{name}: positions for {sum(1 for n in manifest['neurons'] if 'pos' in n)}"
                  f"/{len(manifest['neurons'])} neurons", flush=True)
        return 0
    if args.list_only:
        for name in targets:
            seeds = CIRCUITS[name]
            print(f"{name}: {len(seeds)} seed types, {BRIDGE_HOPS.get(name, 1)}-hop bridge: {', '.join(seeds)}")
        return 0

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    for name in targets:
        manifest = fetch_circuit(name, CIRCUITS[name], args.min_weight)
        (out / f"{name}.json").write_text(json.dumps(manifest) + "\n")
        print(f"{name}: {len(manifest['neurons'])} neurons, {len(manifest['edges'])} edges", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
