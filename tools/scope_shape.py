#!/usr/bin/env python3
"""Bake the neuroscope's brain silhouette into Circuits/scope_shape.json.

The neuroscope overlay draws every neuron at its real position (the `pos` field
tools/extract_circuits.py writes). This script fetches the male-cns ROI meshes,
projects them the same way, and rasterizes them into row spans that the C# side
draws as rectangles, so the dots sit inside a true-to-shape brain.

Projection (a composite, since no single camera shows brain and VNC well):
  brain (z < z_neck): frontal view. Screen u from x, flipped so the fly's left
      hemisphere is on screen left (seen from behind); v from y, dorsal up.
  VNC (z >= z_neck): hangs below the brain as a dorsal view; v from z, scaled by
      vnc_scale so the panel stays screen-sized.
Brain/ScopeLayout.cs applies the projection stored in the output file, so the
formula lives in exactly two places: project() here and ScopeShape.Project there.

Regions (each a list of [row, col, length] spans, in pixels of width x height):
  central   CentralBrain neuropil
  optic     LA/ME/LO/LOP neuropils, both sides
  vnc       VNC neuropil plus the cervical connective joining it to the brain
  rind      cell-body cortex: the neuropil dilated by --rind px (somata live here)
  surface   outer contour of the whole CNS
  neuropil  boundaries between neuropils and against the rind

Usage:
  pip install requests numpy
  python tools/scope_shape.py --out Circuits/scope_shape.json
"""
import argparse
import glob
import json
import sys
from pathlib import Path

import numpy as np

try:
    import requests
except ImportError:
    requests = None

DATASET = "male-cns:v1.0"
MESH_URL = "https://neuprint.janelia.org/api/roimeshes/mesh/%s/%s"

CENTRAL, OPTIC, VNC, RIND = 1, 2, 3, 4
ROIS = [("CentralBrain", CENTRAL)] + [
    (f"{roi}({side})", OPTIC) for roi in ("LA", "ME", "LO", "LOP") for side in "LR"
] + [("VNC", VNC)]


def fetch_mesh(roi: str):
    if requests is None:
        raise RuntimeError("pip install requests first")
    r = requests.get(MESH_URL % (DATASET, requests.utils.quote(roi)), timeout=300)
    if r.status_code != 200 or not r.text.lstrip().startswith(("#", "v")):
        raise RuntimeError(f"mesh {roi}: HTTP {r.status_code} {r.text[:120]}")
    verts, faces = [], []
    for line in r.text.splitlines():
        if line.startswith("v "):
            verts.append([float(t) for t in line.split()[1:4]])
        elif line.startswith("f "):
            faces.append([int(t.split("/")[0]) - 1 for t in line.split()[1:4]])
    return np.array(verts), np.array(faces, dtype=np.int64)


def project(p, proj):
    """Voxel xyz (n, 3) -> pixel (u, v). Mirrored by ScopeShape.Project in C#."""
    u = proj["pad"] + (proj["x_max"] - p[:, 0]) * proj["scale"]
    v_brain = proj["pad"] + (p[:, 1] - proj["y_min"]) * proj["scale"]
    v_vnc = proj["vnc_top"] + (p[:, 2] - proj["vnc_z_min"]) * proj["scale"] * proj["vnc_scale"]
    return u, np.where(p[:, 2] < proj["z_neck"], v_brain, v_vnc)


def rasterize(label, val, u, v, faces):
    """Fill every projected triangle; a closed surface's projection is its silhouette."""
    h, w = label.shape
    tu, tv = u[faces], v[faces]
    lo_u, hi_u = np.floor(tu.min(1)).astype(int), np.floor(tu.max(1)).astype(int)
    lo_v, hi_v = np.floor(tv.min(1)).astype(int), np.floor(tv.max(1)).astype(int)
    small = (hi_u - lo_u <= 1) & (hi_v - lo_v <= 1)
    # Sub-pixel triangles (most of them): mark the pixels under their corners.
    cu = np.clip(np.floor(tu[small]).astype(int), 0, w - 1).ravel()
    cv = np.clip(np.floor(tv[small]).astype(int), 0, h - 1).ravel()
    label[cv, cu] = np.maximum(label[cv, cu], val)
    for k in np.flatnonzero(~small):
        c0, c1 = max(lo_u[k], 0), min(hi_u[k], w - 1)
        r0, r1 = max(lo_v[k], 0), min(hi_v[k], h - 1)
        if c0 > c1 or r0 > r1:
            continue
        gu, gv = np.meshgrid(np.arange(c0, c1 + 1) + 0.5, np.arange(r0, r1 + 1) + 0.5)
        (au, bu, cu3), (av, bv, cv3) = tu[k], tv[k]
        e0 = (bu - au) * (gv - av) - (bv - av) * (gu - au)
        e1 = (cu3 - bu) * (gv - bv) - (cv3 - bv) * (gu - bu)
        e2 = (au - cu3) * (gv - cv3) - (av - cv3) * (gu - cu3)
        inside = ((e0 >= 0) & (e1 >= 0) & (e2 >= 0)) | ((e0 <= 0) & (e1 <= 0) & (e2 <= 0))
        block = label[r0:r1 + 1, c0:c1 + 1]
        block[inside] = np.maximum(block[inside], val)


def neighbours_differ(label):
    """Per pixel: does any 4-neighbour (or the image edge) carry a different label?"""
    pad = np.pad(label, 1, constant_values=0)
    core = pad[1:-1, 1:-1]
    return ((pad[:-2, 1:-1] != core) | (pad[2:, 1:-1] != core)
            | (pad[1:-1, :-2] != core) | (pad[1:-1, 2:] != core))


def touches_empty(label):
    pad = np.pad(label, 1, constant_values=0)
    return ((pad[:-2, 1:-1] == 0) | (pad[2:, 1:-1] == 0)
            | (pad[1:-1, :-2] == 0) | (pad[1:-1, 2:] == 0))


def dilate(mask, steps):
    for _ in range(steps):
        pad = np.pad(mask, 1)
        mask = mask | pad[:-2, 1:-1] | pad[2:, 1:-1] | pad[1:-1, :-2] | pad[1:-1, 2:]
    return mask


def close_holes(label, val):
    """Sub-pixel splatting can leave 1px pinholes; fill pixels boxed in by val."""
    m = label == val
    pad = np.pad(m, 1)
    boxed = pad[:-2, 1:-1] & pad[2:, 1:-1] & pad[1:-1, :-2] & pad[1:-1, 2:]
    label[boxed & (label == 0)] = val


def spans(mask):
    out = []
    for r in range(mask.shape[0]):
        row = np.concatenate(([0], mask[r].astype(np.int8), [0]))
        d = np.diff(row)
        for start, end in zip(np.flatnonzero(d == 1), np.flatnonzero(d == -1)):
            out.append([r, int(start), int(end - start)])
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="Circuits/scope_shape.json")
    ap.add_argument("--width", type=int, default=300, help="pixels; the overlay draws at this size")
    ap.add_argument("--pad", type=int, default=6)
    ap.add_argument("--rind", type=int, default=3, help="cortex thickness in px around the neuropil")
    ap.add_argument("--vnc-scale", type=float, default=0.6)
    ap.add_argument("--neck", type=int, default=8, help="gap between brain and VNC in px")
    ap.add_argument("--neck-width", type=int, default=12)
    args = ap.parse_args()

    meshes = [(roi, val, *fetch_mesh(roi)) for roi, val in ROIS]
    brain = np.vstack([v for _, val, v, _ in meshes if val != VNC])
    vnc = next(v for _, val, v, _ in meshes if val == VNC)
    central = next(v for _, val, v, _ in meshes if val == CENTRAL)

    x_min, x_max = min(brain[:, 0].min(), vnc[:, 0].min()), max(brain[:, 0].max(), vnc[:, 0].max())
    y_min, y_max = brain[:, 1].min(), brain[:, 1].max()
    pad = args.pad + args.rind
    scale = (args.width - 2 * pad) / (x_max - x_min)
    brain_bottom = pad + (y_max - y_min) * scale
    proj = {
        "x_max": float(x_max), "y_min": float(y_min), "scale": float(scale), "pad": float(pad),
        "z_neck": float((central[:, 2].max() + vnc[:, 2].min()) / 2),
        "vnc_z_min": float(vnc[:, 2].min()), "vnc_top": float(brain_bottom + args.neck),
        "vnc_scale": args.vnc_scale,
    }
    height = int(np.ceil(proj["vnc_top"] + (vnc[:, 2].max() - vnc[:, 2].min()) * scale * args.vnc_scale + pad))
    width = args.width

    label = np.zeros((height, width), dtype=np.int8)
    for roi, val, verts, faces in meshes:
        u, v = project(verts, proj)
        rasterize(label, val, u, v, faces)
        close_holes(label, val)

    # Cervical connective: a strip from the bottom of the central brain down to the VNC.
    central_rows = np.flatnonzero((label == CENTRAL).any(1))
    vnc_rows = np.flatnonzero((label == VNC).any(1))
    top_vnc = label[vnc_rows[0]:vnc_rows[0] + 4] == VNC
    mid = int(round(np.flatnonzero(top_vnc.any(0)).mean()))
    c0 = mid - args.neck_width // 2
    neck = label[central_rows[-1] - 4:vnc_rows[0] + 4, c0:c0 + args.neck_width]
    neck[neck == 0] = VNC

    neuropil = label > 0
    label[dilate(neuropil, args.rind) & ~neuropil] = RIND

    surface = (label > 0) & touches_empty(label)
    inner = (label > 0) & (label != RIND) & neighbours_differ(label) & ~surface

    regions = {
        "rind": spans((label == RIND) & ~surface),
        "central": spans((label == CENTRAL) & ~inner & ~surface),
        "optic": spans((label == OPTIC) & ~inner & ~surface),
        "vnc": spans((label == VNC) & ~inner & ~surface),
        "neuropil": spans(inner),
        "surface": spans(surface),
    }

    # Report how well the extracted neurons fit, if the circuits carry positions.
    pos = {}
    for f in glob.glob(str(Path(args.out).parent / "*.json")):
        doc = json.loads(Path(f).read_text())
        if doc.get("format") == "flyraria-circuit-v1":
            pos.update({n["body"]: n["pos"] for n in doc["neurons"] if n.get("pos")})
    if pos:
        u, v = project(np.array(list(pos.values())), proj)
        ui = np.clip(u.astype(int), 0, width - 1)
        vi = np.clip(v.astype(int), 0, height - 1)
        print(f"{np.mean(label[vi, ui] > 0):.1%} of {len(pos)} neurons land inside the shape")

    out = {
        "format": "flyraria-scope-shape-v1", "dataset": DATASET, "rois": [r for r, _ in ROIS],
        "width": width, "height": height, "projection": proj, "regions": regions,
    }
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text(json.dumps(out, separators=(",", ":")) + "\n")
    print(f"{args.out}: {width}x{height}px, " + ", ".join(f"{k} {len(s)} spans" for k, s in regions.items()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
