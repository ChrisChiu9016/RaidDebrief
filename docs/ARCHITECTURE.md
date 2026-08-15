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
- Sample positions, rotation, HP, barrier percentage, cast state, statuses, and actor lifecycle at the recording cadence.
- Capture observable combat events and event-driven Action Effects.
- Snapshot one Pull-local name for each observed Cast or Action Effect Action ID when a reliable name source is available.
- Capture Waymarks and the 17 current Target Marker slots.
- Convert Dalamud/game objects into Core domain records without retaining live game references.

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

### Framework Sampling Pipeline

- `LiveDataProbe` reads lightweight client, combat, duty, territory, Map, and instance state on every `Framework.Update`; lifecycle edges and instance exit therefore remain frame-responsive.
- `CaptureService.BeginFrameworkUpdate` advances the automatic lifecycle first and returns a prepared `FrameworkCaptureSample` only when the active Pull's single `CaptureSamplingScheduler` requires a real sample. Pull start produces the first sample in the same Framework callback.
- `FrameworkScanCoordinator` performs at most one Party／ObjectTable／StatusList scan for that callback. A Capture sample owns the scan while recording; outside recording, an open Probe in a duty instance may request its own 10 Hz refresh; a closed Probe with no active Capture performs no full scan.
- `SubmitFrameworkSample` commits the prepared sample and synchronously copies managed Actor／Party state into the active Pull. Failure calls `CancelFrameworkSample`, which does not advance cadence or the gap baseline. A pending sample cannot overlap another update.
- The scheduler uses monotonic elapsed milliseconds and an absolute 100 ms grid. A late callback records one current sample and increments the missed-interval gap count; it never fabricates historical frames.
- Individual volatile ObjectTable reads are rejected and counted without aborting the rest of the scan. StatusList is cached once per Actor scan, and Actor names are cached by ObjectTable slot plus GameObjectId.
- Probe-only diagnostic strings are formatted lazily from the latest structured Event／Action Effect identity rather than on every capture callback.


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

Current domain collections:

```text
PullRecord
├─ Metadata / CaptureFeatures
├─ Actors[]
├─ Frames[]
│  └─ ActorStateSample[]
│     ├─ Position / Rotation / HitboxRadius
│     ├─ HP / MaxHP / BarrierPercentage
│     └─ Dead / Targetable / Omnidirectional
├─ Events[]
├─ ActionEffects[]
├─ ActionNames[]
├─ WaymarkFrames[]
├─ TargetMarkerFrames[]
├─ StartedAtUtc
└─ EndedAtUtc
```

`ActionNames[]` is a Pull-local managed snapshot keyed by `ActionId`. Capture first accepts a resolved localized Lumina Action-sheet name; an unnamed Action in the Auto-attack category uses that category's localized name. It distinguishes startup-static from runtime-RSV resolution, then tries the Client RSV resolver, and finally an entity-matched enemy CastBar. Native Action Effect callbacks only queue the observed Action ID／source entity after copying their managed record; the next Capture Framework sample performs name resolution, so non-cast damage such as Boss auto-attacks is covered without Lumina or UI access on the Action Effect callback. Unresolved IDs retry at 500 ms intervals for at most 20 attempts rather than on every frame; an ID that never resolves produces no snapshot. Replay prefers the recorded snapshot and applies the same localized Auto-attack category fallback to legacy records, so ordinary attacks do not degrade to `Action #ID` solely because their Action row has an empty name.

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

## 5. Position and State Sampling
The implemented recording cadence is 10 Hz (100 ms).

Rules:
- `CaptureSamplingScheduler` is the only owner of the active Pull timeline.
- The first Pull sample is immediate in the same Framework callback that creates the active Capture.
- Cadence uses monotonic elapsed time aligned to an absolute 100 ms grid; it does not accumulate frame delta time.
- Low FPS or a hitch produces one real current sample plus a gap count, never duplicated historical samples.
- Party, ObjectTable, StatusList, Waymark, and Target Marker extraction runs only when Capture or the open Developer Probe requests a 10 Hz full scan.
- Lightweight lifecycle and combat-gate state remains observed every Framework／UI update.
- Later compression may use delta encoding or unchanged-frame omission only after profiling and without changing Replay semantics.

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
- Current HP, maximum HP, barrier percentage, dead/alive, targetable, and omnidirectional state use the latest observation at or before the requested timestamp. Future discrete observations never backfill earlier state. `BarrierPercentage` is exposed only when `CaptureFeatures.BarrierState` declares that the Pull recorded it.
- Position and rotation are not extrapolated beyond the latest available sample. Before an actor's first observation, it has no resolved state.
- A recorded Actor despawn/spawn absence interval is not bridged by position or rotation interpolation.

- Death correlation treats Action Effect callback time and sampled HP content as separate observations on one clock. Native source-actor heal entries (`0x00000104`) are applied to the caster rather than the callback's packet target before HP deltas are correlated. It anchors on the last living HP sample before the Death transition, then selects the latest ordered Action Effect suffix whose replay crosses zero; effect position, not `effect timestamp >= frame timestamp`, defines the replay boundary. Pull-local clean single-effect HP transitions calibrate a conservative observation-lag window, with a bounded fallback when evidence is sparse. Barrier absorption and excessive observation lag prevent High confidence.

### Timeline, Waymark, and Target Marker ordering

- Timeline events sort by `(TimestampMilliseconds, OriginalRecordedIndex)`.
- The original `PullRecord.Events` collection index is the deterministic tie-breaker for equal timestamps; Replay does not define an event-type priority.
- Waymark and Target Marker resolution use the latest respective state-change frame at or before the requested timestamp. Before the first frame, the resolved state is empty.
- Target Marker capture reads the 17 ordered `MarkingController.Markers` slots on the Framework thread. Replay resolves an active marker through its Pull-local stable Actor ID; no live game object is retained or queried.

### Arena scene and rendering

- `ArenaProjection` maps recorded world X／Z into normalized 2D coordinates. Core's default `FromPullRecord` projection uses a neutral square field centered on the complete Pull's replay-visible observed bounds, with a minimum 40×40 world-unit extent. A host may instead supply explicit Core `ArenaBounds`; projection never mutates recorded samples, and recorded Rotation becomes a 2D facing unit vector.
- `ArenaSceneBuilder` reuses indexed Actor, Waymark, and Target Marker state to populate a caller-owned `ArenaRenderScene`. Player markers include recorded PCs. BattleNpc markers include actors that were objectively observed targetable at least once, which keeps bosses and interactable enemies visible through untargetable states without encounter-specific classification.
- Active Waymarks render in stable `WaymarkId` order. The Dalamud host maps A／B／C／D／1／2／3／4 to native game icons `61241／61242／61243／61247／61244／61245／61246／61248`, loads the eight shared textures once, centers each image on its recorded X／Z position, and falls back to the labeled marker if a texture is unavailable. A-D use a 1.25-world-unit radius; 1-4 use a 2.3-world-unit square edge. Both are projected through the current Map bounds and viewport zoom; native icon quads use a shared `1.5x` transparent-inset compensation while the geometric fallback retains the recorded world dimensions.
- Active Target Markers follow their resolved Actor position and render above the Actor. Actor markers preserve Pull-local stable identity and recorded HP, barrier percentage availability, dead/alive, and targetable state.
- Each sampled BattleNpc carries an objective `IsOmnidirectional` state. The Dalamud capture host resolves it from the cached Lumina `BNpcBase.IsOmnidirectional` flag or active `Directional Disregard` status `3808`; Replay never reads live status data. The in-game renderer uses the recorded state to switch between the directional 480×682 Target Circle and the 480×480 complete Target Ring while preserving recorded hitbox scale and facing. Captures without the feature use `ActorRecord.BaseId` for static Lumina fallback and explicitly warn that dynamic transitions are unavailable.
- `RaidDebrief.UI` references Core domain data only. Its offline `SvgArenaRenderer` consumes an `ArenaRenderScene` and does not load JSON, access the filesystem, use live game objects, or depend on Dalamud. The Dalamud host loads native Waymark textures through `ITextureProvider` and Target Marker PNGs as manifest resources once; Draw performs no texture filesystem I/O.

### Replay orchestration and hosts

- `ReplaySession` is the Core orchestration boundary. It receives one finalized `PullRecord`, creates the clock, indexed Timeline and resolvers, stable arena projection, scene builder, and reusable scene once, then keeps every resolved view on the same clamped timestamp for play／pause／advance／seek.
- Replay duration covers the latest recorded Frame, Event, Waymark frame, Action Effect, or non-negative finalized Pull wall-clock end, whichever is later. This preserves an observed silent tail without reading a future discrete state.
- `RaidDebrief.Offline` is an executable Developer/Test host. It owns explicit path → `CaptureJson.Load`, exposes local play／pause／advance／seek controls, and renders the Core scene through `SvgArenaRenderer`; browser code does not reimplement actor-state resolution.
- `ReplayWindow` is the single registered Dalamud main window. `/rdebrief`, the non-native short alias `/rdb`, plugin main UI, and config UI entry points open its Replay tab and resolve the current in-memory `LastCompletedPull`; reopening through the window lifecycle applies the same default unless an explicit Debrief request is already prepared. `/rd` is intentionally not registered because FFXIV owns it as the native `/ready` alias. A permanent Settings tab owns automatic Pull capture, post-Wipe Debrief, combat auto-close, and the persisted, default-disabled Developer-mode switch. The former standalone `ProbeWindow` is not registered or independently opened. Developer mode exposes a separate scrollable Developer tab containing probe diagnostics, capture controls, and the manual JSON source; disabling it removes the tab and stops probe refresh. Runtime selection waits for finalization and binds every background `ReplaySession` build to its CaptureId and source generation; completion is adopted only if a fresh atomic snapshot still identifies that request. A newer Runtime Pull or explicit Developer/Test import supersedes older work. Runtime never queries a latest repository, scans capture history, or restores a Pull across sessions.
- The Dalamud host reads the Lumina `Map` sheet once on the framework thread and builds a Pull-independent canvas catalog from each row's `SizeFactor`, `OffsetX`, `OffsetY`, and texture ID. Runtime and Developer/Test background session construction receives the matching complete Map world bounds through `ArenaProjection.FromMapBounds`; no Territory／Map profile, encounter ID, or arena geometry is hard-coded. The same row-derived transform crops the central valid Map texture region, so background UVs and Actor／Waymark world coordinates share one projection. Missing／invalid rows fall back to Core's neutral observed field.
- On session adoption, a Map-backed `ReplayWindow` centers the initial viewport on the Lumina Map origin (`world X/Z = -OffsetX/-OffsetY`) and uses that row's `SizeFactor / 100` as the clamped minimum zoom. This framing is Pull-independent and never reads Actor or Waymark positions; Reset restores the same row-derived viewport, and zoomed panning remains inside its initially visible region. Missing／invalid Map rows fall back to complete-field Fit.
- The formal Replay UI uses a 1600×1080 reference surface. Both side columns are fixed: `ReplayLeftPanelWidth` (320 reference px) for Pull／Party and `ReplayRightPanelWidth` (440 reference px) for Actor／Death, each multiplied only by the UI scale, so their cards never change width as the window is resized and the Arena absorbs every extra pixel. Only a degenerate surface — one whose content width cannot hold both sidebars — shrinks them, and then proportionally rather than letting the Arena column go negative. Both side panels render with `NoScrollbar`, so no scrollbar ever reserves width and the content width is constant; the minimum window height is therefore derived rather than fixed. `ResolveReplayMinimumWindowHeight` sums the tallest context column — the Death context's identity card, Killing Blow heading and card, impact card row, health-change heading／header／five rows, and mitigation heading plus framed container — with the window chrome and the bottom panel from live style metrics, then clamps that against the `ReplayWindowMinimumHeight` floor, so the Death context always fits without a scrollbar. Wheel scrolling stays enabled for the one unbounded case, an unusually deep mitigation grid. The bottom playback／Timeline／Quick Jump panel derives its height from its visible controls, and explicit cursor placement keeps the full layout within the window without an outer scrollbar. Its playback toggle uses font-safe ASCII `Play`／`Pause` labels instead of relying on unsupported glyph coverage. Party rows resolve timestamp HP, percentage, dead state, HP bars, and a left-anchored recorded barrier overlay, with `PartyRowBarGap` and `PartyRowBottomPadding` giving each row breathing room instead of a compressed list; if a row cannot fit both full HP and percentage, HP falls back to current-only and then hides while percentage remains visible. Actor and Death contexts share a Job／enlarged state／timestamp header and the same full-width vitality bar; the header is the only Alive／Dead state label, while an available Actor snapshot retains only its objective barrier card. Both contexts retain five body rows under the “血量變動紀錄” Time／Action／Damage／Healing table headers; missing rows remain blank, including all five rows when the interval contains no changes, and no empty-state message is rendered. “詳細記錄” is always visible for consistent layout, including when the current interval is empty, and opens an independently focused, non-modal ImGui window whose visibility is explicit state rather than popup focus state: it remains open when focus moves elsewhere and closes only through its “關閉” control while the owning Replay stays open. The window uses the same pre-indexed interval without allocation, always retains a ten-row viewport, lists every change in the inclusive `[playhead − 10 seconds, playhead]` interval, leaves unused rows blank, and enables vertical mouse-wheel scrolling for entries beyond ten. The presentation model pre-indexes target-matched damage and healing once; source-actor heal entries are reassigned from the packet target to the caster, and an objectively active Bloodbath (`Status 84` → `Action 7542`) or Bloodwhetting (`Status 2678` → `Action 25751`) labels that recovery with its localized source action rather than the triggering weaponskill. Damage rows render red and healing rows render green. Death details additionally use a confidence-labelled Killing Blow card and impact cards whose column count follows the fixed context width. A single Quick Jump death is a compact Job chip. Deaths no more than five seconds from the first death in a group collapse into one wider chronological card with Job icons; activating it opens exact Job／timestamp choices. A group of exactly eight deaths uses the derived presentation label `WIPED`.
- Replay context typography is one explicit tier set instead of per-callsite multipliers: labels, section headings, and health-change table headers and rows stay at the base size because they are CJK text that degrades below it; the mitigation countdown and the context timestamp use `ContextBodyTextScale`; the vitality row's HP and percentage use the slightly smaller `ContextVitalsTextScale` because that row is already the widest reading in the panel, while stat-card values use `ContextMetricTextScale`, the `ContextStatCell` default; the Killing Blow card keeps its higher action／amount emphasis above every other tier. Every impact card value therefore shares one scale, so the emphasized overkill card is never smaller than the HP card beside it, and colour alone carries its emphasis. Health-change rows keep base-size text vertically centred inside the reserved row height through `ResolveHealthChangeRowTextOffsetY`, which discounts `CellPadding` so the row never grows past its reserved height. Hierarchy comes from the label-to-metric size gap plus the existing secondary／section colors, never from shrinking labels or from mutating window font scale.
- Framed context boxes are reference pixel lengths resolved through `ResolveScaledLength(reference, ImGuiHelpers.GlobalScale)`: the identity header and its Job icon, the vitality row and its bar offsets, stat cards, empty states, health-change rows and fixed columns, and mitigation tiles including grid column count. A non-finite or non-positive UI scale falls back to `1`. Because text scale is relative to the current font while these boxes were previously raw pixels, the enlarged metric tier would otherwise clip at high Dalamud UI scale.
- `ContextStatThreeColumnMinimumWidth` (390 reference px, UI-scaled) remains the only impact-card column rule, but the fixed 440px context column resolves above it at every window size and UI scale, so the three cards stay in one row instead of reflowing as the window changes. The two-plus-one path survives only as a guard for a degenerate style or scale, and in that case the second row still reserves two columns so the lone card keeps the same width as the cards above it.
- The Arena canvas has no legacy 720px ceiling: it uses the largest square that fits the center panel and centers that square on whichever axis has surplus space. Actor markers retain exact projected positions; unselected labels are suppressed when marker positions overlap, while the nearest hovered Actor and selected Actor regain full-opacity labels and render after ordinary markers with selection last. Canvas widths below 520px use a compact fixed top-left enemy HUD with a 220px preferred width, reduced bars and spacing, while preserving the same recorded HP／Cast semantics, clipping, tooltips, stacking, and background-click exclusion.
- The context status grid reconstructs recorded player mitigation, barriers, defense increases, invulnerability, optional healing-over-time effects, and recorded damage-down debuffs for every currently targetable enemy. Status identity excludes countdown-only RemainingTime changes; display time counts down from the recorded timing anchor or, for legacy data, derives from the matching `StatusLost` unless an intervening refresh makes duration unavailable. Each enemy debuff retains its target Actor ID, uses the orange enemy border, and names that target on hover; identical effects on concurrent Bosses remain separate tiles. Native status icons are loaded once, and recorded stack parameters remain visible.
- The Death context resolves that grid at the killing blow instead of at the recorded Death transition, because FFXIV strips every status on death. `ResolveDeathMitigationAnchorTimestamp` prefers `KillingBlowCandidate.TimestampMilliseconds`, which capture stamps synchronously inside the client Action Effect hook on the same Pull-local clock as `PositionFrame`, so it carries no 10 Hz HP／status sampling lag; a Pull with no resolved candidate falls back to the Death timestamp. Because capture appends `Death` before the status removals of the same sample and both share that sample timestamp, the anchor alone cannot separate them: `IsAtOrAfterRecordedIndexLimit` additionally discards every entry whose `OriginalRecordedIndex` is at or after `DeathOriginalRecordedIndex`. The same limit is applied to the countdown resolver, so a legacy capture without recorded remaining time reports the duration as unavailable rather than deriving a uniform near-zero countdown from a removal that only death caused. This is an observation of the last recorded status sample, not a claim that the mitigation was or was not sufficient; 10 Hz status polling still cannot separate a status that expired within the same sample window from one death removed, so the residual ambiguity is bounded by the sampling interval rather than by the sampling lag. The Actor snapshot keeps resolving at the playhead with no limit.
- Every currently targetable BattleNpc receives a fixed top-left Arena HUD group with HP percentage and a conditional recorded Cast row. Recorded Action names override current-session game data; unresolved `_rsv_` placeholders are never presented as user-facing names.
- Selecting a Party member focuses Actor opacity and, above the Map-derived minimum zoom, follows that visible player's projected position within the safe pan boundary. Absence preserves the last center, and manual drag clears Focus.
- `Plugin.DrawUi` passes `LiveDataProbe.InCombat` and the persisted, default-enabled `CloseReplayOnCombatStart` setting to the window before `WindowSystem.Draw`; Replay does not read Dalamud conditions or live actors. `ReplayCombatGate` always rejects new explicit Replay opens during combat. On the out-of-combat → in-combat edge, auto-close enabled pauses, invalidates pending work, and hides an active Replay; disabled auto-close keeps an existing window visible but pauses playback and pending work. Changing the setting after combat has already begun does not retroactively hide the window. Combat end never automatically reopens Replay.
- Every successfully validated automatic Pull is analyzed on the finalization background task before publication. `ReplaySourceSnapshot` atomically exposes the `PullRecord`, its `DebriefSummary`, end reason, and monotonically increasing completion generation; validation or analysis failure preserves the previous complete pair. Pull ordinals are process-local automatic-Pull ordinals and are neither serialized nor restored across sessions.
- `DebriefSummaryController` applies the existing per-generation post-Wipe lifecycle to the Summary: a generation finalized during combat is queued until the first out-of-combat UI update; every later Wipe is independent; combat entry dismisses a visible Summary; clear, manual, failed, disabled, or mismatched Pull／Summary pairs never appear. The persisted, default-enabled user setting retains the legacy JSON key `ShowWipeReplayPrompt` while its UI contract is cleanly renamed to “Wipe 後顯示 Debrief 摘要”. The window itself is player-facing, not diagnostic: the Capture ID moves to the header tooltip, the recorded Boss name accompanies its objective HP percentage, and each death presents its recorded Job through `ResolveDeathLabel`, falling back to the capture-time `Player N` alias only when the Job is unresolved. A single recorded death renders one row because the sequence would repeat it verbatim; two or more group through `ReplayWindow.IsWithinDeathCluster`, the one predicate Death Quick Jump also uses, so a clustered Wipe collapses into a Job-icon row plus the derived count title and timestamp range instead of a wrapped string of aliases. The window carries the Replay background alpha, centres on first appearance, scales its framed lengths by the UI scale, and marks the suggested-window Replay button as the default item.
- `ReplayFramePolicy` is the allocation-free Draw boundary: a closed Replay never draws; any open Replay remains drawable, including a window intentionally kept visible in combat, but only visible out-of-combat playback may advance. Combat-entry hiding is performed separately by `ReplayCombatGate`. `ReplayWindow.UpdateUiState` pauses and cancels pending adoption after manual close or combat suspension.
- `ReplayLoadCoordinator` owns request cancellation and disposal. Supersession, source invalidation, combat hiding, manual closing, or Plugin disposal cancels the old request and checks cancellation before and after record loading and session construction. A background task returns only a completion value; it never writes window state directly. Disposed windows reject new work and cannot adopt a later completion.
- Formal Replay Draw uses the prebuilt presentation Death array and health-change index, and reconstructs bounded active-status rows into stack memory without materializing Timeline collections. Runtime Draw performs no JSON serialization, filesystem access, live actor traversal, complete-Timeline materialization, health-change collection, or new cache-layer lookup. Manual-source diagnostics, JSON import, and the live probe are isolated in the Developer tab; full probe refresh is enabled only while Developer mode is enabled and that tab is visible, and is disabled when another tab or the main window is hidden.
- `ReplayStatusEffectDatabase` classifies the cached English Lumina Status descriptions once into recorded-effect categories. Target kind remains part of the filter: player buffs and Boss damage-down debuffs are not interchangeable, Haima／Panhaima display only their recorded reserve stacks, and healing-over-time effects are hidden by default through persisted configuration.


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
All game-facing reads occur on the Dalamud Framework thread:
- lightweight client／condition state on every Framework update;
- Party, ObjectTable, StatusList, Waymark, and Target Marker extraction only for a prepared full scan;
- Action-name resolution for sampled Cast IDs and Action Effect IDs queued since the prior Capture sample, including Lumina／Client RSV, localized Auto-attack category, and enemy CastBar fallback;
- synchronous validation and managed copying of native Action Effect callback data, plus primitive Action ID／source-entity queuing, before the original hook returns;

Background work may include:
- Developer/test serialization;
- Replay preprocessing and indexed session construction;
- Debrief and Death-correlation analysis.

Background tasks consume only finalized managed `PullRecord` data. They never access live game structures, Lumina rows, Dalamud services, or ImGui state, and they return completion values rather than mutating windows directly.

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
