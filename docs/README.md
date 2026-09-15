# FlyRarria docs

Branch-by-branch record of what was built, and who built it. Each merged
branch gets one file under `branches/`; each contributor gets one file under
`contributors/`. Update the branch file with every commit, and move open
items forward when the branch merges.

## Contributors

| Human | Handles | Record |
|---|---|---|
| Samiul (SamiulH25 · `bob2142` · BOB2142) | scaffold, build/run pipeline, merges | `contributors/samiul.md` |
| Andrew Priestley (Voicedrew11) | brain engine, circuits, pet behaviour | `contributors/andrew.md` |

## Branches

| Branch | State | Record |
|---|---|---|
| `main` | scaffold + PR #1 merged (`3d04223`) | `branches/main.md` |
| `fly-brain` | merged into `main` via PR #1 | `branches/fly-brain.md` |
| `dev` | current work branch (cut from `3d04223`) | `branches/dev.md` |

## Conventions

- Branch files list commits newest-first with one line on *why*, not just *what*.
- "Verified" means something actually run (bench, headless build, in-game),
  with the command. Unrun claims stay out.
- Known issues move with the merge: closed ones get struck, live ones get
  copied into the new branch file.
