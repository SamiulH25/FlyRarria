#!/usr/bin/env python3
"""Fetch extracted Drosophila sensorimotor circuits from neuPrint into Circuits/.

Mirrors tools/fetch_neuprint.py from blendi-remade/fly-brain-minecraft, but pulls
small named circuits (hundreds of neurons) instead of the full 176k CNS, per the
FlyRarria extracted-circuits decision. Output JSON is the contract consumed by a
future Brain/CircuitLoader.cs (not yet written).

Circuits (cf. flyproject.io pens, BANC v888 / male-cns:v1.0):
  escape: LC4/LPLC2/LPLC1 -> DNp01/DNp02/DNp11 -> PSI -> TTMn
  steer:  DNa01/DNa02 -> leg/wing motor pools
  feed:   sugar GRNs (LB3b/LB3c/PhG1a-c/LgLG3) -> G2N-1/Fudog -> MN9
          + bitter GRNs (LB1a-d) -> Scapula
  groom:  JO-F/bristles -> aDN1/aDN2/DNg12
  song:   pC1/P1 -> pIP10/pMP2

Usage:
  pip install requests numpy
  python tools/extract_circuits.py --out Circuits/
  python tools/extract_circuits.py --circuit escape --min-weight 5

Anonymous neuPrint access; see PROVENANCE.md in the Minecraft mod for the exact
Cypher queries this is modeled on.
"""
import argparse
import json
import sys
from pathlib import Path

NEUPRINT = "https://neuprint.janelia.org/api/custom/custom"
DATASET = "male-cns:v1.0"

CIRCUITS = {
    "escape": ["LC4", "LPLC2", "LPLC1", "DNp01", "DNp02", "DNp11", "PSI", "TTMn"],
    "steer": ["DNa01", "DNa02"],
    "feed": ["LB3b", "LB3c", "PhG1a", "LgLG3", "LB1a", "GNG232", "DNg67", "MN9", "GNG087"],
    "groom": ["DNg62", "DNge078", "DNg12", "DNg11", "DNp29"],
    "song": ["pC1", "pIP10", "pMP2"],
}

NT_SIGN = {
    "GABA": -1, "Glutamate": -1, "Glut": -1, "Histamine": -1, "Hist": -1,
}


def sign_for(nt: str) -> int:
    return NT_SIGN.get((nt or "").strip(), 1)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="Circuits")
    ap.add_argument("--circuit", choices=sorted(CIRCUITS), default=None)
    ap.add_argument("--min-weight", type=int, default=5)
    args = ap.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    targets = [args.circuit] if args.circuit else sorted(CIRCUITS)

    print(f"dataset={DATASET} min_weight={args.min_weight}")
    print("NOTE: wiring query implementation against neuPrint Cypher is TODO;")
    print("this scaffold writes the circuit manifests so Brain/ can be built against the format.")
    for name in targets:
        manifest = {
            "dataset": DATASET,
            "circuit": name,
            "min_weight": args.min_weight,
            "seed_types": CIRCUITS[name],
            "format": "flyraria-circuit-v1",
            "neurons": [],
            "edges": [],
        }
        (out / f"{name}.json").write_text(json.dumps(manifest, indent=1) + "\n")
        print(f"wrote {out / f'{name}.json'} (manifest only — run full fetch to fill neurons/edges)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
