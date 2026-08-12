# Raid Debrief — Architecture

## 1. High-Level Data Flow

```text
Dalamud / FFXIV
      │
      ▼
CaptureService
      │
      ▼
ActivePull
      │
      │ successful finalize + validation
      ▼
Finalized PullRecord
      │
      ├──────────────► Dev/Test JSON Export
      │
      ▼
LastCompletedPull
      │
      ▼
Debrief / Replay
```

`ActivePull` is the fresh, Pull-local data container for the combat currently being recorded. `LastCompletedPull` is the most recent `PullRecord` that finalized successfully and passed validation. Debrief and Replay consume `LastCompletedPull` directly as a finalized `PullRecord`; they do not depend on JSON persistence.

The central architectural rule is that Replay, Debrief, and Analysis consume recorded data, never live game objects. JSON export belongs to development and testing infrastructure, not the runtime replay path.

## 2. Suggested Solution Layout

```text
RaidDebrief.sln

src/
├─ RaidDebrief.Plugin/
│  ├─ Plugin bootstrap
│  ├─ Dalamud services
│  ├─ Combat capture
│  ├─ Game-thread adapters
│  └─ Developer/test JSON export
│
├─ RaidDebrief.Core/
│  ├─ Domain models
│  ├─ PullRecord
│  ├─ Replay timeline model
│  ├─ Debrief analysis
│  └─ Serialization contracts
│
├─ RaidDebrief.UI/
│  └─ Dalamud-independent arena renderer
│
└─ RaidDebrief.Offline/
   └─ Executable Developer/Test replay host

tests/
└─ RaidDebrief.*.Tests/

testdata/
├─ synthetic/
└─ recorded/
```

## 3. Capture Layer
Responsibilities:
- Detect pull start/end.
- Track party/boss actors.
- Sample positions and rotation.
- Capture HP/state snapshots.
- Capture observable combat events.
- Capture Waymarks and the 17 current Target Marker slots.
- Convert Dalamud/game objects into Core domain records.

Capture must not perform replay rendering or wipe-cause analysis.

### Automatic Pull Lifecycle

- `RaidDebrief.Core.AutomaticPullLifecycle` owns the deterministic transition contract and has no Dalamud dependency:

```text
Idle
→ ActivePull created
→ Recording
→ Finalize
→ Validate
→ LastCompletedPull replace
→ Idle
```

- `RaidDebrief.Plugin.CaptureService` observes `InCombat` on the Framework thread, executes lifecycle commands, and owns `ActivePull` plus the in-memory Replay source state.
- Production automatic capture is scoped to an instanced duty. `LiveDataProbe` derives this gate from the union of Dalamud `BoundByDuty`, `BoundByDuty56`, and `BoundByDuty95`; `IClientState.Instance` is only the current zone-instance number and is not a duty-membership signal.
- Outside an instanced duty, automatic lifecycle observation is not armed and combat cannot start a Pull. Entering while already in combat still requires a later `InCombat=false → true` edge.
- Leaving the instanced duty during an automatic Pull finalizes immediately with `InstanceExited` before any outside-instance frame is recorded.
- Automatic capture must observe `InCombat=false` before it is armed. Loading or reloading the plugin during an active Pull therefore waits for the next combat edge instead of creating a partial duplicate.
- Normal combat end uses a three-second debounce. Re-entering combat during that window cancels the pending end; `DutyWiped` and `DutyCompleted` finalize immediately.
- Finalization and validation complete before `LastCompletedPull` is replaced. A new Pull cannot reuse or mutate a finalized `PullRecord`.
- Every Pull creates a completely new data container. Actors, Frames, Events, Waymarks, Target Marker frames, and Action Effects must never be shared across Pulls.
- Starting a new Pull does not clear the previous `LastCompletedPull`. The previous valid record remains available for Debrief and Replay while the new Pull is recording.
- Only a Pull that finalizes successfully and passes validation replaces `LastCompletedPull`. If finalization or validation fails, the previous valid `LastCompletedPull` remains available and the lifecycle returns to `Idle`.
- Replay reads `GetReplaySourceSnapshot()`, which returns finalization generation／state／CaptureId／error, completed generation, and the `LastCompletedPull` reference under the same lock. Consumers must not combine separately timed status and record reads.
- Plugin disposal unsubscribes callbacks and releases runtime state. Startup or reload begins a new runtime session and does not restore combat state from disk.
- Manual capture is unrestricted Developer/Test infrastructure. It remains an explicit alternative and never shares an active record with automatic mode; production instance gating must not change its behavior.

### Runtime State and Development/Test Serialization

Runtime state is intentionally in-memory and limited to:

```text
Runtime:
ActivePull
ReplaySourceSnapshot
├─ FinalizationGeneration / State / CaptureId / Error
└─ CompletedGeneration / LastCompletedPull

Development / Testing:
PullRecord ↔ JSON fixture
```

- Runtime does not use disk to determine the last Pull. The atomic Replay source snapshot replaces its `LastCompletedPull` reference in memory only after successful finalization and validation.
- Plugin startup or reload does not restore the previous session's last Pull. It may initialize with `ActivePull = null` and `LastCompletedPull = null`.
- Runtime requires no `capture-index.json`, persistent latest-Pull lookup, sequence ordering, startup scan of historical Pulls, or cross-session combat-history recovery.
- JSON output is an explicit developer/test export of a finalized `PullRecord`, using the existing export flow's selected path or a normal fixture path such as `testdata/real-pull-001.json`.
- There is no special “Latest Pull JSON” file and no runtime dependency on `last-capture.json` or any replacement for it.
- Real FFXIV Pulls can be exported as fixtures; Offline Replay can load those fixtures; regression tests can reuse the same Pull; and JSON round-trip and validator tests remain supported.

JSON persistence is a development and testing facility, not a prerequisite for runtime replay.

### Capture Privacy

- Serialized `ActorRecord.Name` values for player characters are generated as Pull-local aliases (`Player 1`, `Player 2`, …); NPC names remain intact for encounter analysis.
- Capture snapshots and `PullRecord` do not contain Content IDs. Entity and game object IDs are ephemeral combat-instance identifiers used only for source/target correlation.
- Existing captures remain load-compatible, but any historical file created before this rule may still contain raw player names and must not be shared without review.


## 4. Core Domain

The recorder/Core boundary remains:

```text
Dalamud-specific Capture
        ↓
PullRecord
        ↓
Core / Replay / Analyzer
```

`RaidDebrief.Core` must not depend on Dalamud. Replay, Debrief, and Analyzer consume a pure finalized `PullRecord`; they never read live game objects directly.

Initial conceptual models:

```text
PullRecord
├─ Metadata
├─ Actors[]
├─ PositionFrames[]
├─ CombatEvents[]
├─ StatusEvents[]
├─ WaymarkFrames[]
├─ TargetMarkerFrames[]
├─ StartTime
└─ EndTime
```

```text
Actor
├─ StableId
├─ Name/DisplayName policy
├─ Job
├─ Role
├─ IsPlayer
└─ IsBoss
```

```text
PositionFrame
├─ Timestamp
├─ ActorId
├─ X
├─ Y
├─ Z
├─ Rotation
└─ HP
```

```text
CombatEvent
├─ Timestamp
├─ SourceActorId
├─ TargetActorId
├─ EventType
├─ ActionId
└─ Value
```

Initial EventType candidates:
- Damage
- Heal
- CastStart
- CastEnd
- Death
- Raise
- StatusGain
- StatusLoss
- TargetChange

Do not finalize fields that cannot be reliably captured during Phase 0.

## 5. Position Sampling
Initial target: 10 Hz (100 ms).

Rules:
- Do not sample every rendered frame.
- Start simple; optimize only after profiling.
- Later compression may use delta encoding or unchanged-frame omission.

## 6. Actor Identity
Actor identity must survive:
- Death and raise
- Boss despawn/respawn
- Phase transitions
- Adds appearing/disappearing

Do not rely on a single transient object reference as the persisted identity.

## 7. Replay Engine
Responsibilities:
- Consume the atomic Runtime Replay source snapshot and construct Replay from its finalized `LastCompletedPull`.
- Load a JSON `PullRecord` fixture only in explicit offline or manually selected Developer/Test flows.
- Resolve actor state for an arbitrary timestamp.
- Interpolate position between samples when appropriate.
- Expose current actor state to UI.
- Expose event markers.
- Support play/pause/scrub/jump.

Replay Engine must work without Dalamud. Runtime Replay does not require a JSON file, repository lookup, or disk persistence.

### Replay input boundary

Offline and test hosts may use `CaptureJson` and the filesystem to load a fixture into a finalized `PullRecord`. Runtime Replay selects the in-memory record through one atomic `ReplaySourceSnapshot`. While its latest generation is finalizing, Runtime waits instead of selecting an older record; a failed generation may expose the preserved record only as the explicitly labeled previous valid Pull. After either boundary, `ReplayClock`, actor-state resolution, Timeline, Waymark resolution, scene construction, and rendering consume only domain data; they must not load JSON or access the filesystem.

```text
Offline/Test: Path → CaptureJson.Load → PullRecord → Replay
Runtime:      ReplaySourceSnapshot → PullRecord → Replay
```

### Replay timestamp semantics

- Position uses linear interpolation between adjacent samples within the same recorded presence interval.
- Rotation is measured in radians and uses shortest-angle interpolation across the $2\pi$ wrap-around. Resolved rotation is normalized to $[-\pi,\pi)$; an exact $\pi$ tie resolves through $-\pi$.
- Current HP, maximum HP, dead/alive, and targetable use the latest observation at or before the requested timestamp. Future discrete observations never backfill earlier state.
- Position and rotation are not extrapolated beyond the latest available sample. Before an actor's first observation, it has no resolved state.
- A recorded Actor despawn/spawn absence interval is not bridged by position or rotation interpolation.

### Timeline, Waymark, and Target Marker ordering

- Timeline events sort by `(TimestampMilliseconds, OriginalRecordedIndex)`.
- The original `PullRecord.Events` collection index is the deterministic tie-breaker for equal timestamps; Replay does not define an event-type priority.
- Waymark and Target Marker resolution use the latest respective state-change frame at or before the requested timestamp. Before the first frame, the resolved state is empty.
- Target Marker capture reads the 17 ordered `MarkingController.Markers` slots on the Framework thread. Replay resolves an active marker through its Pull-local stable Actor ID; no live game object is retained or queried.

### Arena scene and rendering

- `ArenaProjection` maps recorded world X／Z into normalized 2D coordinates. Core's default `FromPullRecord` projection uses a neutral square field centered on the complete Pull's replay-visible observed bounds, with a minimum 40×40 world-unit extent. A host may instead supply explicit Core `ArenaBounds`; projection never mutates recorded samples, and recorded Rotation becomes a 2D facing unit vector.
- `ArenaSceneBuilder` reuses indexed Actor, Waymark, and Target Marker state to populate a caller-owned `ArenaRenderScene`. Player markers include recorded PCs. BattleNpc markers include actors that were objectively observed targetable at least once, which keeps bosses and interactable enemies visible through untargetable states without encounter-specific classification.
- Active Waymarks render in stable `WaymarkId` order. The Dalamud host maps A／B／C／D／1／2／3／4 to native game icons `61241／61242／61243／61247／61244／61245／61246／61248`, loads the eight shared textures once, centers each image on its recorded X／Z position, and falls back to the labeled marker if a texture is unavailable. A-D use a 1.25-world-unit radius; 1-4 use a 2.3-world-unit square edge. Both are projected through the current Map bounds and viewport zoom; native icon quads use a shared `1.5x` transparent-inset compensation while the geometric fallback retains the recorded world dimensions.
- Active Target Markers follow their resolved Actor position and render above the Actor. Actor markers preserve Pull-local stable identity and recorded HP, dead/alive, and targetable state.
- Each sampled BattleNpc carries an objective `IsOmnidirectional` state. The Dalamud capture host resolves it from the cached Lumina `BNpcBase.IsOmnidirectional` flag or active `Directional Disregard` status `3808`; Replay never reads live status data. The in-game renderer uses the recorded state to switch between the directional 480×682 Target Circle and the 480×480 complete Target Ring while preserving recorded hitbox scale and facing. Captures without the feature use `ActorRecord.BaseId` for static Lumina fallback and explicitly warn that dynamic transitions are unavailable.
- `RaidDebrief.UI` references Core domain data only. Its offline `SvgArenaRenderer` consumes an `ArenaRenderScene` and does not load JSON, access the filesystem, use live game objects, or depend on Dalamud. The Dalamud host loads native Waymark textures through `ITextureProvider` and Target Marker PNGs as manifest resources once; Draw performs no texture filesystem I/O.

### Replay orchestration and hosts

- `ReplaySession` is the Core orchestration boundary. It receives one finalized `PullRecord`, creates the clock, indexed Timeline and resolvers, stable arena projection, scene builder, and reusable scene once, then keeps every resolved view on the same clamped timestamp for play／pause／advance／seek.
- Replay duration covers the latest recorded Frame, Event, Waymark frame, Action Effect, or non-negative finalized Pull wall-clock end, whichever is later. This preserves an observed silent tail without reading a future discrete state.
- `RaidDebrief.Offline` is an executable Developer/Test host. It owns explicit path → `CaptureJson.Load`, exposes local play／pause／advance／seek controls, and renders the Core scene through `SvgArenaRenderer`; browser code does not reimplement actor-state resolution.
- `ReplayWindow` is the formal Dalamud Replay host. Runtime selection waits for finalization and binds every background `ReplaySession` build to its CaptureId and source generation; completion is adopted only if a fresh atomic snapshot still identifies that request. A newer Runtime Pull or explicit Developer/Test import supersedes older work. The manual JSON path remains isolated behind the Developer/Test action, and Runtime never queries a latest repository, scans capture history, or restores a Pull across sessions.
- The Dalamud host reads the Lumina `Map` sheet once on the framework thread and builds a Pull-independent canvas catalog from each row's `SizeFactor`, `OffsetX`, `OffsetY`, and texture ID. Runtime and Developer/Test background session construction receives the matching complete Map world bounds through `ArenaProjection.FromMapBounds`; no Territory／Map profile, encounter ID, or arena geometry is hard-coded. The same row-derived transform crops the central valid Map texture region, so background UVs and Actor／Waymark world coordinates share one projection. Missing／invalid rows fall back to Core's neutral observed field.
- On session adoption, a Map-backed `ReplayWindow` centers the initial viewport on the Lumina Map origin (`world X/Z = -OffsetX/-OffsetY`) and uses that row's `SizeFactor / 100` as the clamped minimum zoom. This framing is Pull-independent and never reads Actor or Waymark positions; Reset restores the same row-derived viewport, and zoomed panning remains inside its initially visible region. Missing／invalid Map rows fall back to complete-field Fit.
- The formal Replay UI exposes a chronological list of recorded player `Death` events only; selecting one seeks to that exact timestamp and pauses. It does not render colored Timeline markers, recent events, cast／status／raise／Wipe jump categories, or timestamp HP／status detail. Selecting a player changes only Actor opacity. At zoom above the Map-derived minimum, the viewport follows that visible player's projected position while honoring the existing safe pan boundary; absence preserves the last camera center, and manual drag clears the selection.
- `Plugin.DrawUi` passes `LiveDataProbe.InCombat` and the persisted, default-enabled `CloseReplayOnCombatStart` setting to the window before `WindowSystem.Draw`; Replay does not read Dalamud conditions or live actors. `ReplayCombatGate` always rejects new explicit Replay opens during combat. With auto-close enabled, visible／playing／loading Replay work is paused, invalidated, and hidden. With it disabled, the existing window remains visible while playback and pending loads are suspended, and enabling auto-close during combat closes that visible window on the next UI update. Combat end never automatically reopens a window that was closed; the finalized source and paused session remain available for a later explicit request.
- Every successfully validated automatic Pull is analyzed on the finalization background task before publication. `ReplaySourceSnapshot` atomically exposes the `PullRecord`, its `DebriefSummary`, end reason, and monotonically increasing completion generation; validation or analysis failure preserves the previous complete pair. Pull ordinals are process-local automatic-Pull ordinals and are neither serialized nor restored across sessions.
- `DebriefSummaryController` applies the existing per-generation post-Wipe lifecycle to the Summary: a generation finalized during combat is queued until the first out-of-combat UI update; every later Wipe is independent; combat entry dismisses a visible Summary; clear, manual, failed, disabled, or mismatched Pull／Summary pairs never appear. The persisted, default-enabled user setting retains the legacy JSON key `ShowWipeReplayPrompt` while its UI contract is cleanly renamed to “Wipe 後顯示 Debrief 摘要”.
- `ReplayFramePolicy` is the allocation-free Draw boundary: closed Replay never draws; auto-close-enabled combat Replay neither draws nor advances; auto-close-disabled combat Replay may remain visible but never advances. Only visible, out-of-combat playback consumes ImGui delta time and rebuilds the reusable scene. `ReplayWindow.UpdateUiState` also pauses and cancels pending adoption after a manual close or combat suspension.
- `ReplayLoadCoordinator` owns request cancellation and disposal. Supersession, source invalidation, combat hiding, manual closing, or Plugin disposal cancels the old request and checks cancellation before and after record loading and session construction. A background task returns only a completion value; it never writes window state directly. Disposed windows reject new work and cannot adopt a later completion.
- Formal Draw iterates the indexed Timeline only when the collapsed death list is open and filters recorded player `Death` entries without materializing a new collection. Runtime Draw performs no JSON serialization, filesystem access, live actor traversal, complete-Timeline materialization, or new cache-layer lookup. Runtime／manual-source diagnostics and JSON import remain collapsed under the advanced development section.

## 8. Debrief Analyzer

`DebriefAnalyzer` is a Dalamud-independent Core service over one finalized `PullRecord`. It returns an immutable `DebriefSummary` containing:
- the optional process-local automatic Pull ordinal;
- duration from the shared `PullTiming` maximum-timestamp contract;
- the final observed HP only when exactly one replay-visible, non-player-owned BattleNpc candidate has valid `MaxHp`;
- first player death and the complete chronological player death sequence, ordered by timestamp and original event index, including repeated deaths after an `AliveTransition`;
- the last recorded `DutyWiped` timestamp; and
- a suggested replay range from 8 seconds before first death through 12 seconds after it, clamped to Pull duration and shortened at Wipe. A Pull with no resolvable player death falls back to the final 20 seconds before the recorded Wipe.

Missing actor links remain explicit through `UnresolvedDeathEventCount`. Ambiguous Boss candidates produce unavailable HP. The analyzer never names a cause, assigns blame, recognizes mechanics, or claims strategy correctness.

The post-Wipe `DebriefSummaryWindow` consumes only the atomically published Summary. Its one-click request carries completion generation, CaptureId, and the suggested range. `ReplayWindow` rejects the request if either identity changed, builds the normal Runtime `ReplaySession`, seeks to the suggested start, and remains paused. It never reloads JSON or searches older Pulls.

## 9. Threading
Game-facing reads should happen on the appropriate Dalamud/framework thread.

Background work may include:
- Developer/test serialization
- Compression
- Replay preprocessing
- Debrief analysis

Never access live game structures from arbitrary background threads.

## 10. Development and Testing Serialization

JSON is not runtime state or a source of truth for the last completed Pull. Its development and testing uses are:

- Exporting a real FFXIV Pull as a reproducible fixture.
- Loading a fixture into Offline Replay.
- Reusing the same Pull in regression tests.
- Exercising JSON round-trip and validator tests.

Recommended fixture format:
- Human-readable JSON first for debugging.
- Version the schema from the beginning.

Example:

```text
schemaVersion: 1
```

A compact binary format can be introduced later if file size or load time becomes a real problem.

JSON persistence is a development and testing facility, not a prerequisite for runtime replay.

## 11. Scope Boundary

Raid Debrief is a post-combat tool for immediately replaying the Pull that just ended. It is not a persistent combat history system.

The current architecture does not include:

- Persistent Pull History
- Recent Pulls
- Pull History UI
- Pull Compare
- Session Statistics
- Retention Management
- Cross-session Replay History
- DPS / Rotation analysis
- AI responsibility judgment

Whether Recent 2–3 Pulls are needed will be decided only after feedback from real high-end encounter use. Do not pre-build history or index infrastructure for that possibility.
