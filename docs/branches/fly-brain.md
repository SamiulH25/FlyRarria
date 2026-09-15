# `fly-brain` → PR #1 (merged as `3d04223`)

By Andrew. The mote's brain becomes the whole male-cns:v1.0 connectome
(176,422 neurons, 25.9M connections) on a multi-threaded spiking engine;
the 7,805-neuron circuit JSON stays as fallback.

## Commits (oldest-first)

- `e041474` — Wire up the fly brain: real circuits, working pet, fixed
  engine. tModLoader 2026.7 port, summon item, bridge-expansion extraction,
  hunger as brain input, owner-damage + small-object senses, startle and
  walk steering.
- `84b144b` — Hunger as a brain input, damage/small-object senses, startle
  and walk steering.
- `cf21ef0` — Neuroscope overlay on the real fly brain, HUD panel,
  follow/wind/NaN fixes. `N` / `/fly scope`, mode/bond/hunger HUD.
- `5b967af` — Event-driven LIF engine, brain steps on a worker thread.
  Sleeping neurons + exact catch-up, delay-block sharding (≤12 threads),
  whole-CNS tick ~7 ms avg / ~30 ms worst vs 50 ms budget.
- `bd9a5cd` — Whole male-cns connectome file, extractor and C# reader
  (`tools/extract_connectome.py`, `Circuits/male-cns.connectome.gz`
  51.8 MB, `Brain/ConnectomeFile.cs`, background load ~0.7 s / ~190 MB).
- `7308513` — Whole-CNS file keeps every connection.
- `2db8384` — Run the whole male CNS in game. Modulatory transmitters
  (DA/OA/5HT) → sign 0, KC→KC inhibitory (mAChR-B), depression U 0.05 /
  300 ms / gain 0.8.
- `85a77b9` — Hold behaviours with hysteresis and dwell, groom only in
  strong wind. Decoder: 0.6× release, 150 ms confirm, 0.5 s dwell
  (Escape instant); Feed threshold MN9 35 Hz; wind curve grooms at 0.7+.

## Verified at merge

- Headless tModLoader build: 0 errors, 0 warnings.
- Scripted whole-CNS frames: loom→ESCAPE, sugar→FEED (hungry) / refuse
  (full), bitter suppresses, touch/rain/damage/strong wind→GROOM,
  chase→FOLLOW with correct yaw. `tools/bench.py` 5/5 on circuits and
  whole CNS.
- Post-merge: headless `-build` with absolute source path → 53 MB
  `.tmod` with all content; dedicated-server smoke test loads FlyRarria
  v0.1.0 through Finalizing Content with no errors.

## Known issues carried to `dev`

- Antennal-lobe latch (excitatory lLN loop holds ~5k spikes/tick after
  smell; reads through it; needs modelling decision, not wiring).
- ~0.5 s song bout on chase right after food smell.
- Food seeking unwired (odour reaches no motor neurons); startle/forward
  drive dormant.
- Sense renames unverified in male-cns: JO wind→JO-C/JO-E, JO-F→JO-FV,
  bristle→BM_InOm, PhG1→PhG1a.
- Circuit-JSON fallback doesn't dedupe edges (216,901 vs 214,779 unique);
  `seenEdges` in `CircuitLoader.cs` is allocated but never used.
- Not playtested in-client after the final commit.
