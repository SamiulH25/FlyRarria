#!/usr/bin/env python3
"""Fetch extracted Drosophila sensorimotor circuits from neuPrint into Circuits/.

Pulls small named circuits (hundreds of neurons) from the male CNS connectome
instead of the full 176k graph, per the FlyRarria extracted-circuits decision.
Output JSON (flyraria-circuit-v1) is consumed by Brain/CircuitLoader.cs.

Circuits and their seed types:
  escape: LC4/LPLC2/LPLC1 -> DNp01/DNp02/DNp11 -> PSI -> TTMn
  steer:  DNa01/DNa02 (+ their motor targets, one hop)
  feed:   sugar GRNs (LB3b/LB3c/PhG1a-c/LgLG3) -> G2N-1/Fudog -> MN9
          + bitter GRNs (LB1a-d) -> Scapula
  groom:  JO-F/bristles -> aDN1/aDN2/DNg12 (+ head/leg/abdomen DNs)
  song:   pC1/P1 -> pIP10/pMP2

Method (modeled on blendi-remade/fly-brain-minecraft tools/fetch_neuprint.py):
  1. Query neurons whose `type` starts with a seed prefix (anonymous Cypher
     against https://neuprint.janelia.org/api/custom/custom, dataset male-cns:v1.0).
  2. Expand one hop to pre/post partners (captures interneurons like G2N-1,
     Scapula, PSI that seeds alone would miss).
  3. Fetch edges within the union at weight >= --min-weight, with consensusNt /
     predictedNt and soma positions for every neuron.

Transmitter->sign mapping lives in Brain/Connectome.cs (Dale's law); the JSON
stores the raw nt string so the mapping stays in one place.

Usage:
  pip install requests numpy
  python tools/extract_circuits.py --out Circuits/
  python tools/extract_circuits.py --circuit escape --min-weight 5
  python tools/extract_circuits.py --list-only   # print what would be fetched

UNTESTED-LIVE: query shapes follow the Minecraft mod's PROVENANCE.md recipe but
have not been run against neuPrint yet. First run with --circuit escape and
compare neuron counts against the flyproject.io pen tables (escape ~429).
"""
import argparse
import json
import sys
from pathlib import Path

try:
    import requests
except ImportError:
    requests = None

NEUPRINT = "https://neuprint.janelia.org/api/custom/custom"
DATASET = "male-cns:v1.0"

CIRCUITS = {
    "escape": ["LC4", "LPLC2", "LPLC1", "DNp01", "DNp02", "DNp11", "PSI", "TTMn"],
    "steer": ["DNa01", "DNa02"],
    "feed": ["LB3b", "LB3c", "PhG1a", "PhG1b", "PhG1c", "LgLG3", "LgLG4",
             "LB1a", "LB1b", "LB1c", "LB1d", "GNG232", "DNg67", "MN9", "GNG087"],
    "groom": ["DNg62", "DNge078", "DNg12", "DNg07", "DNg08", "DNg11", "DNp29"],
    "song": ["pC1", "P1", "pIP10", "pMP2"],
}

NEURON_QUERY = """MATCH (n :Neuron)
WHERE n.dataset = $dataset AND any(t IN $seeds WHERE n.type STARTS WITH t)
RETURN n.bodyId AS body, n.type AS type,
       n.consensusNt AS nt, n.predictedNt AS predNt,
       n.somaX AS x, n.somaY AS y"""

PARTNERS_QUERY = """MATCH (a :Neuron)-[e :ConnectsTo]->(b :Neuron)
WHERE n.dataset = $dataset AND a.bodyId IN $bodies AND e.weight >= $minw
RETURN DISTINCT b.bodyId AS body"""

EDGES_QUERY = """MATCH (a :Neuron)-[e :ConnectsTo]->(b :Neuron)
WHERE n.dataset = $dataset AND a.bodyId IN $bodies AND b.bodyId IN $bodies
  AND e.weight >= $minw
RETURN a.bodyId AS pre, b.bodyId AS post, e.weight AS w"""


def cypher(query: str, params: dict):
    if requests is None:
        raise RuntimeError("pip install requests first");
    r = requests.post(NEUPRINT, params={"dataset": DATASET},
                      json={"cypher": query, "dataset": DATASET, **params},
                      timeout=120)
    r.raise_for_status()
    return r.json()["data"]


def fetch_circuit(name: str, seeds, min_weight: int) -> dict:
    rows = cypher(NEURON_QUERY, {"seeds": seeds})
    bodies = {row[0] for row in rows}
    partner_rows = cypher(PARTNERS_QUERY, {"bodies": sorted(bodies), "minw": min_weight})
    grown = bodies | {row[0] for row in partner_rows}
    # Detail pass for grown bodies (type/nt/pos for partners).
    detail = cypher(
        "MATCH (n :Neuron) WHERE n.dataset = $dataset AND n.bodyId IN $bodies "
        "RETURN n.bodyId, n.type, n.consensusNt, n.predictedNt, n.somaX, n.somaY",
        {"bodies": sorted(grown)})
    neurons = [
        {"body": d[0], "type": d[1] or "?",
         "nt": d[2] or d[3] or "", "x": d[4] or 0, "y": d[5] or 0}
        for d in detail
    ]
    edge_rows = cypher(EDGES_QUERY, {"bodies": sorted(grown), "minw": min_weight})
    edges = [[e[0], e[1], int(e[2])] for e in edge_rows]
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
    args = ap.parse_args()

    targets = [args.circuit] if args.circuit else sorted(CIRCUITS)
    if args.list_only:
        for name in targets:
            seeds = CIRCUITS[name]
            print(f"{name}: {len(seeds)} seed prefixes: {', '.join(seeds)}");
        return 0

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    for name in targets:
        manifest = fetch_circuit(name, CIRCUITS[name], args.min_weight)
        (out / f"{name}.json").write_text(json.dumps(manifest) + "\n")
        print(f"{name}: {len(manifest['neurons'])} neurons, {len(manifest['edges'])} edges");
    return 0


if __name__ == "__main__":
    sys.exit(main())
