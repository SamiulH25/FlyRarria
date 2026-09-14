#!/usr/bin/env python3
"""Build the whole male-cns connectome file, Circuits/male-cns.connectome.gz.

extract_circuits.py pulls small named circuits; this is all of it: every neuPrint
:Neuron in male-cns:v1.0 (176,422) and every connection between two of them with
at least --min-weight synapses (6,287,789 connections at 5).

Sources:
  wiring   Janelia's flat-connectome export (connectome-weights-...-minconf-0.5.feather,
           1.05 GB, gs://flyem-male-cns/v1.0), streamed in batches so the 152M rows
           never sit in memory at once.
  neurons  neuPrint, for exactly the :Neuron set and the fields extract_circuits.py
           uses: type, consensusNt else predictedNt, somaSide else instance suffix,
           somaLocation else a synapse centroid (the same fetch_positions query).
Downloads and neuPrint pulls are cached in .cache/male-cns-v1.0/ (gitignored);
delete that folder to refetch.

Nothing is written unless every check passes: connection and synapse totals equal
neuPrint's own count, no pair appears twice, and every neuron (type, nt, side) and
connection (weight) in the Circuits/*.json files matches.

Format flyraria-connectome-v1: gzip of, little-endian,
  magic       b"FLYCONN1"
  dataset     str                  str = u16 byte length + UTF-8
  minWeight   i32
  N, E, S     i32 x 3              neurons, connections, strings
  strings     str x S              string table, index 0 is ""
  body        i32 x N              bodyId, ascending (male-cns ids fit in i32)
  type        i32 x N              string index; "?" when untyped
  nt          i32 x N              string index; consensusNt else predictedNt, "" unknown
  superclass  i32 x N              string index
  side        u8 x N               'L', 'R', 'M' or 0 (unknown)
  pos         f32 x 3N             x, y, z in male-cns voxels; NaN when neuPrint can't place it
  rowLen      varint x N           outgoing connections per neuron
  targets     varint x E           neuron indices, ascending per row, each minus the previous
                                   target in its row (the first is absolute)
  weights     varint x E           synapse counts, same order
Brain/ConnectomeFile.cs reads it.

Usage:
  pip install pyarrow numpy requests
  python tools/extract_connectome.py
  python tools/extract_connectome.py --min-weight 1 --out /tmp/male-cns-all.connectome.gz
"""
import argparse
import gzip
import json
import struct
import sys
import time
from pathlib import Path

import numpy as np
import pyarrow as pa
import pyarrow.compute as pc
import pyarrow.ipc as ipc
import requests

from extract_circuits import DATASET, cypher, fetch_positions, neuron_side

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / ".cache" / "male-cns-v1.0"
EXPORT_URL = ("https://storage.googleapis.com/flyem-male-cns/v1.0/connectome-data/flat-connectome/"
              "connectome-weights-male-cns-v1.0-minconf-0.5.feather")
MAGIC = b"FLYCONN1"
NEURON_FIELDS = ("n.bodyId, n.type, n.instance, n.somaSide, n.rootSide, n.consensusNt, "
                 "n.predictedNt, n.somaLocation, n.superclass, n.status, n.pre, n.post")


def download(url: str, path: Path) -> None:
    size = int(requests.head(url, timeout=60).headers["Content-Length"])
    if path.exists() and path.stat().st_size == size:
        return
    print(f"downloading {url} ({size / 1e6:.0f} MB)", flush=True)
    part = path.with_suffix(".part")
    with requests.get(url, stream=True, timeout=600) as r:
        r.raise_for_status()
        with open(part, "wb") as f:
            for chunk in r.iter_content(1 << 20):
                f.write(chunk)
    if part.stat().st_size != size:
        raise SystemExit(f"download of {url} is truncated")
    part.replace(path)


def fetch_neurons() -> list:
    """Every :Neuron as [bodyId, type, instance, somaSide, rootSide, consensusNt,
    predictedNt, somaLocation, superclass, status, pre, post], ascending bodyId."""
    path = CACHE / "neuprint-neurons.json"
    if path.exists():
        return json.loads(path.read_text())
    rows, last = [], -1
    while True:
        page = cypher(f"MATCH (n :Neuron) WHERE n.bodyId > {last} WITH n ORDER BY n.bodyId "
                      f"LIMIT 40000 RETURN {NEURON_FIELDS}")
        if not page:
            break
        rows += page
        last = page[-1][0]
    path.write_text(json.dumps(rows))
    return rows


def fetch_centroids(bodies) -> dict:
    """bodyId -> synapse centroid for neurons without a soma location."""
    path = CACHE / "neuprint-synapse-centroids.json"
    if path.exists():
        return {int(k): v for k, v in json.loads(path.read_text()).items()}
    pos = fetch_positions(bodies)
    path.write_text(json.dumps(pos))
    return pos


def load_connections(bodies: np.ndarray, min_weight: int):
    """(pre, post, weight) arrays for connections between two of `bodies`."""
    ids = pa.array(bodies, pa.int64())
    reader = ipc.open_file(pa.memory_map(str(CACHE / EXPORT_URL.rsplit("/", 1)[1])))
    kept = []
    for b in range(reader.num_record_batches):
        rb = reader.get_batch(b).select(["body_pre", "body_post", "weight"])
        rb = rb.filter(pc.greater_equal(rb["weight"], min_weight))
        rb = rb.filter(pc.and_(pc.is_in(rb["body_pre"], value_set=ids),
                               pc.is_in(rb["body_post"], value_set=ids)))
        kept.append(rb)
    t = pa.Table.from_batches(kept)
    return (t["body_pre"].to_numpy().astype(np.int64), t["body_post"].to_numpy().astype(np.int64),
            t["weight"].to_numpy().astype(np.int64))


def varints(values: np.ndarray) -> bytes:
    """Unsigned LEB128, vectorised."""
    v = values.astype(np.uint64)
    length = np.ones(len(v), np.int64)
    for bits in (7, 14, 21, 28):
        length += v >= (1 << bits)
    out = np.empty(int(length.sum()), np.uint8)
    start = np.concatenate(([0], np.cumsum(length)[:-1]))
    for k in range(5):
        sel = length > k
        more = (length[sel] > k + 1).astype(np.uint64) << np.uint64(7)
        out[start[sel] + k] = ((v[sel] & np.uint64(0x7F)) | more).astype(np.uint8)
        v >>= np.uint64(7)
    return out.tobytes()


def check_circuits(index, types, nts, sides, positions, row_start, post_i, weight) -> list:
    """Mismatches between the connectome and every Circuits/*.json circuit file."""
    problems, pos_differ, n_neurons, n_edges = [], 0, 0, 0
    for f in sorted((ROOT / "Circuits").glob("*.json")):
        m = json.loads(f.read_text())
        if m.get("format") != "flyraria-circuit-v1":
            continue
        for n in m["neurons"]:
            n_neurons += 1
            i = index.get(n["body"])
            if i is None:
                problems.append(f"{f.name}: neuron {n['body']} is not a :Neuron")
                continue
            for field, want, got in (("type", n["type"], types[i]), ("nt", n["nt"], nts[i]),
                                     ("side", n["side"], sides[i])):
                if want != got:
                    problems.append(f"{f.name}: neuron {n['body']} {field} {want!r} != {got!r}")
            if "pos" in n and n["pos"] != positions[i]:
                pos_differ += 1
        for a, b, c in m["edges"]:
            n_edges += 1
            i, j = index.get(a), index.get(b)
            if i is None or j is None:
                problems.append(f"{f.name}: edge {a}->{b} has an unknown end")
                continue
            lo, hi = row_start[i], row_start[i + 1]
            k = lo + int(np.searchsorted(post_i[lo:hi], j))
            if k >= hi or post_i[k] != j or weight[k] != c:
                problems.append(f"{f.name}: edge {a}->{b} weight {c} not in the connectome")
    print(f"circuit files: {n_neurons} neurons and {n_edges} connections checked; "
          f"{pos_differ} positions differ (synapse centroids sample arbitrary synapses)", flush=True)
    return problems


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--min-weight", type=int, default=5)
    ap.add_argument("--out", default=str(ROOT / "Circuits" / "male-cns.connectome.gz"))
    args = ap.parse_args()
    t0 = time.time()
    CACHE.mkdir(parents=True, exist_ok=True)

    download(EXPORT_URL, CACHE / EXPORT_URL.rsplit("/", 1)[1])
    rows = fetch_neurons()
    body = np.array([r[0] for r in rows], np.int64)
    if not (np.all(np.diff(body) > 0) and body.max() < 2 ** 31):
        raise SystemExit("neuPrint bodyIds are not ascending i32s")
    centroids = fetch_centroids([r[0] for r in rows if not r[7]])
    print(f"{len(rows)} neurons ({time.time() - t0:.0f}s)", flush=True)

    pre, post, weight = load_connections(body, args.min_weight)
    pre_i, post_i = np.searchsorted(body, pre), np.searchsorted(body, post)
    order = np.lexsort((post_i, pre_i))
    pre_i, post_i, weight = pre_i[order], post_i[order], weight[order]
    row_start = np.searchsorted(pre_i, np.arange(len(body) + 1))
    print(f"{len(weight)} connections, {int(weight.sum())} synapses ({time.time() - t0:.0f}s)", flush=True)

    problems = []
    same_row = pre_i[1:] == pre_i[:-1]
    dupes = int(np.count_nonzero(same_row & (post_i[1:] == post_i[:-1])))
    if dupes:
        problems.append(f"{dupes} connections listed twice")
    if weight.max() > 65535:
        problems.append("a weight doesn't fit Connectome.SynapseCounts (u16)")
    (np_edges, np_synapses), = cypher(
        f"MATCH (a :Neuron)-[w :ConnectsTo]->(b :Neuron) WHERE w.weight >= {args.min_weight} "
        f"RETURN count(w), sum(w.weight)")
    if (np_edges, np_synapses) != (len(weight), int(weight.sum())):
        problems.append(f"neuPrint counts {np_edges} connections / {np_synapses} synapses")
    print(f"neuPrint agrees: {np_edges} connections, {np_synapses} synapses", flush=True)

    types = [r[1] or "?" for r in rows]
    nts = [r[5] or r[6] or "" for r in rows]
    sides = [neuron_side(r[3], r[2]) for r in rows]
    positions = [[round(c) for c in r[7]["coordinates"]] if r[7] else centroids.get(r[0]) for r in rows]
    index = {b: i for i, b in enumerate(body.tolist())}
    problems += check_circuits(index, types, nts, sides, positions, row_start, post_i, weight)
    if problems:
        print("\n".join(problems[:20]), file=sys.stderr)
        raise SystemExit(f"{len(problems)} problems, nothing written")

    strings, table = {"": 0}, [""]

    def sid(s: str) -> int:
        if s not in strings:
            strings[s] = len(table)
            table.append(s)
        return strings[s]

    def pack_str(s: str) -> bytes:
        b = s.encode()
        return struct.pack("<H", len(b)) + b

    type_i = np.array([sid(s) for s in types], "<i4")
    nt_i = np.array([sid(s) for s in nts], "<i4")
    super_i = np.array([sid(r[8] or "") for r in rows], "<i4")
    side_b = np.array([ord(s) if s else 0 for s in sides], np.uint8)
    pos = np.array([p if p else [np.nan] * 3 for p in positions], "<f4").reshape(-1)
    delta = np.diff(post_i, prepend=0)
    row_first = np.concatenate(([True], ~same_row))
    delta[row_first] = post_i[row_first]

    parts = [MAGIC, pack_str(DATASET), struct.pack("<iiii", args.min_weight, len(body), len(weight), len(table))]
    parts += [pack_str(s) for s in table]
    parts += [body.astype("<i4").tobytes(), type_i.tobytes(), nt_i.tobytes(), super_i.tobytes(),
              side_b.tobytes(), pos.tobytes(),
              varints(np.diff(row_start)), varints(delta), varints(weight)]
    raw = b"".join(parts)
    Path(args.out).write_bytes(gzip.compress(raw, compresslevel=9, mtime=0))
    unplaced = sum(1 for p in positions if not p)
    print(f"wrote {args.out}: {Path(args.out).stat().st_size / 1e6:.1f} MB ({len(raw) / 1e6:.1f} MB raw), "
          f"{len(table)} strings, {unplaced} neurons without a position ({time.time() - t0:.0f}s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
