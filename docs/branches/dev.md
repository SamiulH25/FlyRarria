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
- 2026-09-15 — Bearings + SEEK (uncommitted): Escape flees the sampled
  threat bearing (`TryFindThreat`) instead of the owner; food smell split
  L/R with strongest-smell position tracked; new SEEK mode (hungry-weighted
  ORN mean >= 25 Hz, steers to the smell, contact hands to FEED). bench
  `seek` passes on circuits + CNS. Proved the GF path is hair-trigger
  (1 Hz loom drive already bursts DNp01), so night dims tracking, not escape.
- 2026-09-15 — Andrew gap hunt (probed, not just read): wind alone reads
  ~24-35 on the groom DNs (dose: 67Hz->12, 110Hz->24, 150Hz->53) vs rain/
  damage 110+, so threshold 40->30 and the dead JO-E drive line is gone;
  storms usually groom, never on threshold 40. Fallback wind is
  noise-bistable (32 once, 0 on repeats), so bench reseeds per experiment.
  Feed/Groom/Song now move differently (nibble bob / shimmy / orbit); scare
  has its own bond cooldown; `/fly senses` reports live frame values. Still
  open: SONG trigger (VA1v does not reach pIP10; top afferent aIPg7's drivers
  unknown), Sleep stays game-side, damage grooms instead of alarming,
  fallback lacks LC10a/pIP10/ORN_VA1v/TRN_VP2 (dead on fallback).
- 2026-09-15 — World interactions: counted sweet meals consume the dropped
  item (owner-client, synced, revalidated); idle minds fetch hearts/stars/
  coins to the owner; Song orbits the nearest company; escape smoke + groom
  water dust. No interaction stubs existed anywhere — these are all new.
- 2026-09-15 — Bond loop closed: fetch pays +3 XP; L2 unlocks fetch (320px
  at L3), L1 keeps 1.5x distance; level-ups announce in gold text; HUD
  shows trust names. Earn via meals/scares/fetches; each level changes
  behavior, not just the L4 perch.
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
