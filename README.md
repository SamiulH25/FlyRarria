# FlyRarria

A living Terraria companion driven by real fruit-fly brain circuits. Cosmetic pet, emergent personality, bond levels, evolving look.

## Status

v0.1.0 scaffold: pet follows/teleports, world sampling -> `SensoryFrame` -> LIF circuits -> `MotorDecoder` steering, bond persistence, `/fly` readout. Circuit data not yet fetched — the pet runs in documented `[reflex]` fallback until `Circuits/*.json` land. No fake brain activity is ever displayed.

## Layout

- `Brain/` — engine-independent LIF core (Shiu et al. 2024 params): `Connectome`, `LifNetwork`, `SensoryFrame`, `SensoryEncoders`, `MotorDecoder`, `PopulationIndex`
- `Circuits/` — extracted circuit JSON (via `tools/extract_circuits.py`, neuPrint male-cns:v1.0)
- `Content/Pets/` — `MoteProjectile` / `MoteBuff` / `MoteItem`
- `Content/Bond/` — bond levels + hunger persistence
- `Content/Debug/` — `/fly stats|senses`
- `tools/` — circuit extractor

## Build

Requires tModLoader (Terraria 1.4.5) + .NET 8 SDK (`mise use dotnet@8`). Copy/clone into the tModLoader `Mods/` sources or build via the in-game workshop pipeline. `dotnet build` alone cannot resolve `tModLoader.targets` — that import only exists under a tModLoader install.

## Circuits

```
pip install requests numpy
python tools/extract_circuits.py --out Circuits/
```

## Sources


- Berg et al., male CNS connectome v1.0 (Janelia/Google/Cambridge)
- Shiu et al. 2024, Drosophila computational brain model (LIF params)
- blendi-remade/fly-brain-minecraft (architecture reference: sensors/encoders/decoder split, validation protocol)
## Duo runbook (everything below needs a run — code is done)

1. `python tools/extract_circuits.py --circuit escape` — first live neuPrint
   query; compare neuron counts vs flyproject.io pen tables (escape ~429).
   Then the rest: `python tools/extract_circuits.py --out Circuits/`
2. `python tools/bench.py --circuits Circuits/` — all five experiments must
   PASS. If MN9 is silent, lower gain; if grooming runs away, raise it.
   Keep `tools/bench.py` and `Brain/LifNetwork.cs` in lockstep.
3. Build the mod in tModLoader — `Circuits/*.json` embed automatically.
   In-game: `/fly stats` should read `[brain]` instead of `[reflex]`.
4. Playtest: offer food (sugar->FEED), sprint at it (loom->ESCAPE), rain
   (groom), night at bond 4 (head perch). Tune `MotorDecoder.Thresholds`.
