# `dev` (current work)

Cut from `main` at `3d04223`. Inherits all `fly-brain.md` known issues.

## Log

- 2026-09-14 — PR #1 reviewed (engine / behaviour / tools slices, all
  approve-with-nits, no blockers) and merged. Mod installed locally:
  real-copy source in tModLoader `ModSources/`, headless
  `-build <absolute path>` (bare name silently builds an empty stub),
  53 MB `.tmod` enabled, server smoke test clean.
- 2026-09-14 — Quick wins: `VisionGain` gates all visual drives by ambient
  light (fixes dead `LightLevel` channel); fallback edge dedupe wired up
  (`seenEdges`); held-FOLLOW no longer steers on
  DNa02 EMA residue; Startle fires on LC11 (small things freeze to watch).
  bench gains `night` (dark tracking weaker but alive) and `startle`
  (LC11 high, DNp01 quiet) experiments.
- 2026-09-14 — Documented intended behaviour of every `MoteMode`
  (Idle / Follow / Escape / Startle / Feed / Groom / Song / Sleep).
- 2026-09-14 — Input-roadmap discussion: `LightLevel` sampled but never
  encoded (dead channel); food smell and threat need bearings, not just
  scalars; readout is 10 populations with hand thresholds, so each new
  channel must move a motor readout (bench-check first).

## Review nits queued (from PR #1 review)

- Poisson drive bypasses refractory (`LifNetwork.cs:505`, pre-existing).
- `ConnectomeFile` corrupt-input paths throw `IndexOutOfRange` instead
  of `InvalidData` (fails closed to `[reflex]` regardless).
- `SignForTransmitter(null)` throws; scope projection lives in two
  places; bench covers a subset of driven seeds; README vs bench
  docstring disagree on whole-CNS bench cost.

## Open (from PR / README)

- Make SONG reachable; antennal-lobe latch modelling; food seeking;
  sense-rename verification; in-client playtest.
