# CLAUDE.md

## Project
Raid Debrief is a Dalamud plugin for FF14 high-end raid players.
Its core purpose is to let players quickly review what happened in the most recent pull through an immediate in-game replay, not a persistent combat-history system.
Do not introduce persistent pull history, indexing, retention, or cross-session replay unless explicitly requested.

## Product Principle
- Replay is the core product.
- Debrief Summary exists to guide the player to the important replay window.
- Prefer objective observations over inferred causes.
- Never assign blame or claim a wipe cause unless explicitly implemented as a clearly marked inference.
- Do not add features outside PROJECT_SPEC.md without explicit approval.

## Current Scope
Core:
- Combat recording
- Pull summary
- First death
- Death sequence
- 2D replay
- Timeline scrubbing
- Event jump
- Player/Boss positions
- Waymarks

Extra goal:
- Event correlation with confidence levels

Out of scope:
- Pull Compare
- Ghost Replay
- DPS analysis
- Rotation analysis
- AI blame/cause analysis
- Strategy/mechanic guidance
- Session statistics
- Progress tracking

## Architecture Rules
- Capture must be isolated from Replay and Analysis.
- RaidDebrief.Core must not depend on Dalamud.
- Replay must consume PullRecord data, not live game objects.
- Game-thread data extraction must be separated from background serialization/analysis.
- Prefer small, testable services over large plugin classes.

## Development Rules
- Implement the minimum behavior required by the current roadmap phase.
- Do not infer adjacent product requirements.
- Do not refactor unrelated systems while implementing a task.
- Preserve recorded-data compatibility where practical.
- Add or update tests for Core logic changes.
- For capture changes, record a real test pull and verify output before touching Replay logic.

## Validation
Before considering a task complete:
1. Build succeeds.
2. Relevant unit tests pass.
3. Synthetic replay data still loads.
4. No new dependency from Core to Dalamud exists.
5. The change does not introduce an out-of-scope feature.
6. Before any Dalamud hot reload, build `RaidDebrief.sln` with `--configuration Debug -p:Platform=x64`.
7. Never treat `dotnet test` or `bin/Debug/RaidDebrief.dll` as proof that the configured `bin/x64/Debug/RaidDebrief.dll` was refreshed.
8. Verify the Dalamud Dev Plugin DLL path, the x64 Debug DLL `LastWriteTime`, and a post-build unload/load log entry before accepting in-game evidence.

## Reference Docs
- docs/PROJECT_SPEC.md
- docs/ARCHITECTURE.md
- docs/ROADMAP.md
- docs/TESTING.md
