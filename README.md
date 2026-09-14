# FlyRarria

A living Terraria companion driven by real fruit-fly brain circuits. Cosmetic pet, emergent personality, bond levels, evolving look.

## Status

v0.1.0: pet follows/teleports, world sampling -> `SensoryFrame` -> LIF circuits -> `MotorDecoder` steering, bond persistence, `/fly` readout. Circuit data is extracted (`Circuits/*.json`, male-cns:v1.0: 7,805 neurons, 216,901 edges merged), so the pet runs in `[brain]` mode; without embedded circuits it falls back to documented `[reflex]` following. No fake brain activity is ever displayed.

Verified headlessly (bench + the C# brain driven with scripted frames): loom -> ESCAPE, sugar -> FEED (MN9 ~55Hz), bitter suppresses feeding, touch/rain -> GROOM, chase left/right -> FOLLOW with matching yaw, owner damage -> GROOM, small moving critters on the right -> a weak turn away (LC11 on the left reaches no DNa02). Hunger (bond `fed`, decays 1.5/min while the pet is out) scales sugar-neuron gain, so a full mote refuses food and a hungry one feeds — the circuit makes the call. Food *seeking* is not wired: food odor (ORN_DM1/VA2) reaches no motor neurons in the extracted circuits. STARTLE and MDN/DNp09 forward-backward movement are handled in `Steer` but don't fire with current circuits (any loom spikes DNp01 first). The engine only steps neurons that could spike (same spikes as stepping all of them, checked seed-for-seed against the old dense loop): ~1-2 ms per brain tick for these circuits, on a worker thread every 3rd frame. Known gap: SONG never fires — social odor/heat don't drive pC1 -> pIP10 in the extracted song circuit.

## Layout

- `Brain/` — engine-independent LIF core (Shiu et al. 2024 params): `Connectome`, `ConnectomeFile`, `LifNetwork`, `SensoryFrame`, `SensoryEncoders`, `MotorDecoder`, `PopulationIndex`
- `Circuits/` — extracted circuit JSON (via `tools/extract_circuits.py`, neuPrint male-cns:v1.0), and `male-cns.connectome.gz`: the whole male CNS, 176,422 neurons and 6,287,789 connections of 5+ synapses (via `tools/extract_connectome.py`; not loaded by the game yet)
- `Content/Pets/` — `MoteProjectile` / `MoteBuff` / `MoteItem`
- `Content/Bond/` — bond levels + hunger persistence
- `Content/Debug/` — `/fly stats|senses|scope`; the neuroscope overlay (`N` or `/fly scope`) draws the real brain and VNC silhouette (male-cns neuropil meshes, brain seen from behind) with every neuron where it sits in the fly (soma, or synapse centroid for sensory neurons), lights the ones spiking, and traces their strongest synapses (orange excitatory, blue inhibitory), with live input drives and decoder readout rates labelled
- `tools/` — circuit and whole-connectome extractors, neuroscope silhouette baker (`scope_shape.py`) + headless bench

## Build

Requires tModLoader (Terraria 1.4.5) + .NET 8 SDK (`mise use dotnet@8`). Copy/clone (or symlink) into the tModLoader `ModSources/` folder and build via Workshop -> Develop Mods. `dotnet build` alone cannot resolve `tModLoader.targets` — that import only exists under a tModLoader install.

## Circuits

```
python3 -m venv .venv && .venv/bin/pip install requests numpy scipy
.venv/bin/python tools/extract_circuits.py --out Circuits/   # anonymous neuPrint, ~30 s
.venv/bin/python tools/scope_shape.py                         # neuroscope brain outline, ~10 s
.venv/bin/python tools/bench.py --circuits Circuits/         # all five must PASS
```

Whole connectome (`Circuits/male-cns.connectome.gz`, 16.6 MB):

```
.venv/bin/pip install pyarrow
.venv/bin/python tools/extract_connectome.py   # 1 GB Janelia export + neuPrint, cached in .cache/; ~2 min cached, longer first time
```

It writes nothing unless its totals equal neuPrint's own count and every neuron and connection in the circuit JSON matches. The format is documented in the script's docstring.

Circuits are seed types plus bridge interneurons (seed -> x -> seed; `feed` uses two hops, since its one-hop sugar -> MN9 bridges are mostly inhibitory). See the docstring in `tools/extract_circuits.py` for seeds, the side encoding, and neuPrint API quirks.

## Sources

- Berg et al., male CNS connectome v1.0 (Janelia/Google/Cambridge)
- Shiu et al. 2024, Drosophila computational brain model (LIF params)
- blendi-remade/fly-brain-minecraft (architecture reference: sensors/encoders/decoder split, validation protocol)

## Duo runbook

1. Extract circuits — done (see Circuits above). Seed names follow male-cns:v1.0:
   `P1` isn't a type there (pC1 covers it); wind/touch/bristle drives use
   `JO-C*`/`JO-E*`, `JO-FV`, `BM_InOm` — best-guess equivalents, worth a check.
2. `tools/bench.py` — 5/5 PASS at gain 0.65. If MN9 is silent, check the feed
   bridges before touching gain; if grooming runs away, raise it.
   Keep `tools/bench.py` and `Brain/LifNetwork.cs` in lockstep (both report
   rates from every spike in the window).
3. Build the mod in tModLoader — `Circuits/*.json` embed automatically.
   In-game: `/fly stats` should read `[brain]` instead of `[reflex]`.
4. Playtest: offer food (sugar->FEED), sprint at it (loom->ESCAPE), rain
   (groom), night at bond 4 (head perch). Tune `MotorDecoder.Thresholds`
   (`FeedMn9Hz` is 35: sugar gives MN9 ~50-57Hz, touch leaks ~25Hz).
5. Open: make SONG reachable (song circuit seeds/drives).
