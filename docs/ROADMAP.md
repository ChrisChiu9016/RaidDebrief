# Raid Debrief — Roadmap

## Phase 0 — Capture Technical Prototype
Goal: prove the required FF14 data can be captured reliably.

Implement only enough to print or serialize:
- Combat start/end
- Party actors
- Boss actors
- Position
- Rotation
- HP
- Waymarks
- Death
- Action/cast events if available
- Damage if available
- Status gain/loss if available

Deliverable:
- One real test pull exported to JSON.

Exit criteria:
- Actor identities remain usable through the pull.
- Position samples are temporally coherent.
- Required data availability is documented.
- Missing/unreliable event sources are explicitly listed.

## Phase 1 — Recorder
Goal: create a stable PullRecord from a complete pull.

Implement:
- Pull lifecycle
- Actor registry
- 10 Hz sampling
- Event recording
- Waymark snapshot/updates
- Schema versioning
- Explicit Developer/Test JSON save/load for offline fixtures
- In-memory `LastCompletedPull` replacement only after successful finalization and validation

Exit criteria:
- Multiple pulls can be recorded without restarting the plugin.
- Explicitly exported Pull files can be reopened offline.
- Long pulls do not create unacceptable stutter.
- Finalization, validation, or Developer/Test export failures preserve the previous valid in-memory `LastCompletedPull`.
- Plugin reload starts a new runtime session and does not restore a previous-session Pull from disk.
- Phase completed on 2026-08-09; detailed evidence is archived in `docs/TEST_RECORDS.md`.

## Phase 2 — Offline Replay
Goal: replay recorded pulls without FF14.

Implement:
- Replay clock
- 2D arena renderer
- Player/Boss markers
- Timeline
- Play/pause
- Scrubbing
- Death state
- Waymarks

Exit criteria:
- A recorded pull can be visually replayed from start to finish.
- Scrubbing produces stable positions.
- Replay does not depend on Dalamud.

Phase completed on 2026-08-09; end-to-end, performance, browser, and Dalamud Developer/Test evidence is archived in `docs/TEST_RECORDS.md`.

## Phase 3 — In-Game Replay
Goal: replay the previous pull directly inside Dalamud.

Implement:
- Replay window integration
- Previous-pull loading
- Combat auto-hide behavior
- Minimal performance safeguards

Exit criteria:
- Wipe → open replay → inspect prior pull works reliably.
- Beginning the next pull does not cause UI interference.

Phase completed on 2026-08-11; automated, recorded-performance, fresh x64 Debug hot-reload, real Wipe-to-Replay, next-Pull combat-gate, and post-acceptance Replay correction evidence is archived in `docs/TEST_RECORDS.md` and `docs/archive/progress-phase-3-2026-08-11.md`.

## Phase 4 — Debrief MVP
Goal: deliver the first complete product experience.

Status: Implementation complete as of 2026-08-11. Core analysis, atomic Runtime handoff, compact post-Wipe Summary, and exact one-click suggested-window seek are implemented; final in-game acceptance requires a real Wipe Capture that includes multiple player deaths, resolvable Boss HP, and a raise／re-death sequence.

Implement:
- Pull number
- Duration
- Boss HP at end if available
- First death
- Chronological death sequence
- Wipe timestamp
- Suggested replay window
- One-click replay

MVP success condition:
> After a wipe, the player can identify the first death and open a useful replay window within a few seconds.

## Phase 5 — Replay UX
Status: Whole-interface redesign and Capture-side Action-name／barrier support are implemented. Automated tests, all repository recorded fixtures, deterministic seek limits, x64 Debug build, configured-path verification, post-build hot reload, one 13.9-minute current-build `DutyWiped` Pull finalization, and exact Runtime reload of that CaptureId passed on 2026-08-14. Current-build manual Replay checks also passed for DRK and GNB invulnerability statuses and, on Runtime Pull `0619f062-21aa-44d7-aa6d-f9727f614059`, playback controls, Actor／Focus, Boss HP／Cast, Death Timeline／Quick Jump, and The Blackest Night Barrier／Status presentation. Representative current-build raw Barrier／Actor／sampling output passed on manual Capture `1661a50a-7961-4b95-a0c7-c9aca5e85c2d`. Manual Capture `0a7120bb-5d4d-4887-ae30-0501ead6209f` additionally verified that a non-cast Boss auto-attack Action Effect produces the localized Pull-local `ActionName`; Replay displayed the same fallback name in Last Hits and as the killing blow for legacy data. Fresh automatic Runtime Pull `b5324338-aa3d-4b0c-afc3-2db427da2fcf` then passed the complete current-build Automatic Capture → in-memory Runtime Replay → ordinary-attack presentation path. A hot-reloaded multi-enemy regression fixture displayed all three concurrent targetable-enemy Feint entries with their retained target names, accepting multi-Boss damage-down presentation. Other status families and full visual-surface acceptance remain required.

Implemented:
- Fixed three-column hierarchy: compact Pull／Party, Boss＋Arena, and Actor／Death context
- Boss HP and recorded CurrentCastTime／TotalCastTime playback
- Job-only Party and Arena labels with selected-player focus and zoomed camera follow
- Bottom-only playback controls, speed, one Death-only Timeline, marker hover, and complete Quick Jump
- Timestamp HP／alive snapshot with the Party-style Job icon, HP values, percentage, and health／barrier bars; a compact native-icon grid shows recorded remaining time and stack counts for selected-player defensive buffs, barriers, and recovery support plus Reprisal／Feint／Addle／Dismantle damage-down debuffs from every currently targetable enemy, retaining the target Actor and naming it on hover
- Honest Death event correlation with virtual HP, Last Hits, estimated Overkill, confidence, evidence, and limitations
- Recorded party membership plus legacy player-Actor fallback
- Runtime and manual JSON source controls collapsed under an advanced development section
- Fixed side columns whose cards never resize with the window, a derived minimum window height that fits the complete Death context without a side-panel scrollbar, and an explicit label／body／metric type hierarchy over UI-scaled framed boxes
- Health-change rows for the inclusive ten-second interval before the playhead, with a separate always-available “詳細記錄” window that keeps a ten-row viewport and closes only through its own control
- Death-context status rows anchored at the killing blow's recorded Action Effect timestamp rather than at the Death transition, so death-stripped mitigation stays observable while a death-caused removal never becomes a derived countdown

Pending in-game acceptance for this phase: the fixed-column layout and derived minimum height at 100%／150%／200% UI scale, and one recorded death that carries mitigation into the killing blow to confirm the anchor against the recorded sampling lag.

Deliberately absent from the formal surface:
- Cast, status, raise, Wipe, damage, or heal event jump
- Recent-event feed, DPS／HPS, rankings, mitigation scoring, blame, strategy, or recommended action

## Phase 6 — Stability
Status: Capture performance hardening is implemented and automated. The single-owner 10 Hz scheduler, two-stage Framework scan, immediate first sample, gap accounting, closed-Probe no-scan path, per-slot Actor-name cache, single-access StatusList scan, and lazy diagnostics are in place. Current build `66043fb5-d46b-48ba-9048-a25f53190b2a` completed and validated after 8,341 frames, 14,156 events, and 6,262 Action Effects. Representative current-build manual Capture `1661a50a-7961-4b95-a0c7-c9aca5e85c2d` passed raw sampling, Player／Boss continuity, Barrier／Status, and complete observed-Cast Action-name inspection. Comparative frame-time／allocation／GC profiling and long-Pull-specific raw inspection remain pending, so no “zero stutter” causality claim is accepted.

Implemented hardening:
- Lightweight combat／duty lifecycle observation remains per Framework update.
- Full Party／ObjectTable／StatusList extraction occurs only for a Capture sample or an in-duty open Probe's 10 Hz refresh.
- Late callbacks record one real sample and report gaps rather than backfilling duplicate frames.
- Per-Actor volatile read failures are isolated and counted.

Test:
- Long fights
- 8 simultaneous deaths
- Raises
- Disconnects
- Phase transitions
- Boss disappear/respawn
- Adds
- Multiple boss actors
- Different arena shapes
- Plugin reload
- Game patch/API changes

## Extra Goal — Event Correlation
Status: Observation-anchored algorithm v2 is implemented and automated／offline regression verified. Current-build in-game Death evidence, especially a death with a recorded barrier and reliable Action-name snapshot, remains pending.

Implemented:
- Reconstruct target-resolved incoming Damage／Heal from the last living HP observation and an ordered Action Effect suffix, without assuming sampled HP content is synchronous with callback timestamps
- Calibrate a bounded Pull-local HP observation-lag window from clean single-effect HP transitions, with a conservative fallback for sparse evidence
- Preserve Last Hits and identify the virtual-HP crossing entry as a Killing Blow candidate
- Show estimated HP-before-hit／effective pool／Overkill with High／Medium／Low／Unavailable confidence; barrier uncertainty and excessive observation lag cannot remain High
- Preserve evidence, limitations, and algorithm version; never present the result as a server-authored fact

Deferred:
- Long-gap relationships, mechanic classification, missing-status causality, and responsibility analysis

## Frozen / Out of Scope
- Pull Compare
- Ghost Replay
- DPS/rotation analysis
- AI cause analysis
- Blame assignment
- Strategy guidance
- Session analytics
