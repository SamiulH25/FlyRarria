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
