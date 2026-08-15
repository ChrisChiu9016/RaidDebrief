# Raid Debrief — Testing Strategy

## 1. Testing Principle
Most Raid Debrief development must not require repeatedly entering a real raid.

Use real combat to capture data.
Use offline data to develop and validate Replay, Analysis, and UI behavior.

## 2. Test Layers

### Layer A — Unit Tests
Target `RaidDebrief.Core` and the Dalamud-independent `RaidDebrief.UI` renderer.

Examples:
- First death detection
- Death sequence ordering
- Replay timestamp resolution
- Position interpolation
- Actor-state reconstruction
- Schema migration/loading
- Suggested replay-window calculation

Required Replay contracts:
- Construct Replay services directly from finalized `PullRecord` domain data. JSON and filesystem access belong only to Offline/Test loading before Replay construction.
- Verify Position linear interpolation at sample start, midpoint, and end timestamps without extrapolating beyond the latest sample.
- Verify Rotation shortest-angle interpolation in radians for `350° → 10°`, `10° → 350°`, unnormalized inputs, and an exact `180°` tie. Output is normalized to $[-\pi,\pi)$.
- Verify Current HP, maximum HP, dead/alive, and targetable use the latest observation at or before the requested timestamp and never read a future discrete state backward.
- Verify timestamps before an actor's first observation return no actor state, while exact sample timestamps use that sample.
- Verify a recorded Actor despawn/spawn interval has no resolved actor state and is never bridged by Position or Rotation interpolation.
- Verify equal-timestamp Timeline events retain `(timestamp, original recorded index)` order without an event-type priority.
- Verify Waymark queries use the latest frame at or before the timestamp and return an empty state before the first frame.
- Repeat representative seeks in forward and reverse order and require identical results at the same timestamp.
- Verify world X／Z projection at arena minimum, midpoint, and maximum; deriving projection bounds must not modify recorded data.
- Verify the same timestamp produces identical Player, BattleNpc, facing, dead／targetable, and active Waymark marker order and values when rebuilt after other seeks.
- Verify the Arena scene excludes every Actor whose latest recorded state is not targetable, even if that Actor was targetable elsewhere in the Pull. An active Target Marker bound to an excluded Actor must also be omitted.
- Parse renderer output as valid SVG, require deterministic repeated output, and verify XML escaping, targetable Player／BattleNpc presentation, omission of currently untargetable Actors and their Target Markers, facing, dead state, and Waymarks.
- Verify Core's default projection produces a neutral square field containing all replay-visible observed bounds with at least a 40×40 world-unit extent. Separately verify a caller-supplied Map canvas retains its row-derived complete world bounds without changing the recorded observed bounds.
- Verify Target Marker builder／validation／JSON round-trip／reverse seek as state-change data. Scene and renderer checks must bind an active marker to the matching stable Actor position, remove it when inactive, and require all 17 embedded PNG resources.
- Use `testdata/recorded/20260810_DMU.json` as the canonical Replay UI regression fixture. Render real timestamps and visually confirm marker counts, timestamp, labels, arena composition, targetability filtering, fixed top-left enemy HUD groups, HP percentages, conditional Cast rows, and Party presentation; use another fixture only when the scenario is absent from this recording.
- Verify `ReplaySession` play／pause／advance／seek keeps its clock, scene timestamp, events-through-time, dead／targetable state, and Waymarks synchronized; playback must clamp and stop at the complete Pull duration.
- Drive the executable Offline host in a browser: load a real fixture, play, pause, scrub, click the Timeline, repeat the same seek, and play through the end. Paused time must remain fixed and repeated scene output must be identical.
- For the formal in-game Replay window, build the configured x64 Debug DLL, verify post-build Dalamud unload／load, then exercise both explicit sources: runtime `LastCompletedPull` and the manually selected advanced Developer/Test JSON fixture. Resize width and height independently down to the enforced minimum. The 320px Pull／Party and 440px Actor／Death columns must keep exactly the same width at every window size and aspect ratio, so no card, table column, or right-aligned label changes width or wraps while resizing; the Arena must absorb all extra width; the bottom panel must remain content-height; and no outer vertical scrollbar may appear. Neither side panel may ever show a scrollbar, and at the enforced minimum height the complete Death context — identity card through the mitigation container — must be visible without scrolling or clipping. Verify Party count, HP and percentage never overlap, dead state, HP／barrier bars, party rows reading as separated rows rather than a compressed list, dominant Arena, shared Actor／Death header, vitality row, aligned captured-hit rows, the default health-change table retains five blank rows without an empty-state message when no changes exist, “詳細記錄” remains visible even for an empty interval and opens a blank ten-row non-modal window, that window remains visible after focus moves elsewhere and closes only through its explicit “關閉” control while Replay remains open, entries beyond ten remain mouse-wheel scrollable, the playback toggle renders font-safe `Play`／`Pause` text without unsupported glyphs, persistent empty mitigation state, same-line HoT control, bottom-only playback／Timeline, marker hover, and Quick Jump exact paused seek.
- Verify the Replay context type hierarchy: base-size labels／section headings／health-change table headers and rows, a larger body tier for the mitigation countdown and the context timestamp, the vitality row's HP and percentage one step smaller than the stat-card metric tier, and the Killing Blow amount above all of them. Confirm the three impact card values render at one identical size and that health-change row text is vertically centred in its row rather than top-aligned. Because the context column is fixed, the three cards must stay in one row at every window size; `ResolveContextStatColumnCount` must still return two below `ContextStatThreeColumnMinimumWidth`, and that reflowed row must reserve two columns so the lone card keeps the two-column width rather than stretching full width. Then repeat the layout at Dalamud UI scale 100%, 150%, and 200%: the identity header, Job icon, vitality bar, stat cards, empty states, health-change rows, and mitigation tiles must grow with the font without clipping or overlapping, the mitigation grid must reflow to fewer columns, and `ResolveReplayMinimumWindowHeight` must grow with the scale so the Death context still fits. `ResolveScaledLength` must return the reference length unchanged for a non-finite or non-positive scale, and `ResolveHealthChangeRowTextOffsetY` must clamp to zero when the text is taller than its row.
- For P2 Arena behavior, resize the center panel through wide, tall, and large layouts. The canvas must grow beyond 720px when both axes allow it and remain centered when width or height is surplus. At clustered player positions, ordinary Job labels must disappear instead of stacking; hovering restores only the nearest label, and Party／Death selection restores that Actor at full opacity above ordinary markers. A canvas narrower than 520px must use the compact 220px-preferred Boss HUD while retaining HP, Cast progress, tooltip, group stacking, and click exclusion.
- For Death Quick Jump, verify chips display recorded Job identity rather than inferred MT／D1-style positions. All chips use the same compact height; single chips use content-sized Job-icon／abbreviation／timestamp presentation and cluster chips use a wider death-icon／title／timestamp-range／count presentation. A single death must seek directly. A death exactly five seconds after a group's first timestamp must remain in its cluster; a death one millisecond later must begin the next horizontal chip. Exactly eight grouped deaths must display `WIPED`. Activating a multi-death chip must open a Job／timestamp popup without seeking until a player is chosen; the chosen entry must focus its Actor, seek the exact recorded Death timestamp, pause playback, and open the matching Death context.
- Verify Active mitigation collects damage-down debuffs from every currently targetable BattleNpc rather than selecting one primary Boss. Concurrent Feint／Addle on distinct enemies must remain distinct target-aware entries, each tooltip must name its recorded target, and currently untargetable enemies must be excluded without discarding their recorded status events.
- Verify the Death context reconstructs mitigation at the killing blow rather than at the Death transition. With `KillingBlowCandidate` present the anchor must be its Action Effect timestamp; without one it must fall back to the Death timestamp. Given one sample that records `Death` followed by the `StatusLost` entries it caused, collecting at the Death timestamp without a recorded-index limit must return nothing, while the same query limited to `DeathOriginalRecordedIndex` must return those statuses with their recorded remaining time still counting down from their own anchor. A legacy capture without recorded remaining time must report the duration as unavailable inside the Death context instead of deriving a near-zero countdown from the death-stripped loss, while the same observed loss must remain a valid duration outside that context. The Actor snapshot must keep resolving at the playhead without a limit, so a dead Actor at the playhead shows no pre-death statuses.
- For a Map-backed in-game Replay, verify the Lumina `Map` row's `SizeFactor`／offsets and texture ID produce one shared world-to-UV transform: the background and recorded Actor／Waymark positions must align without encounter-specific profiles. Loading centers on the row-derived Map origin and uses `SizeFactor / 100` as its minimum zoom／pan region without reading Pull Actor positions; Reset restores it, and zoom remains usable through 20x.
- Verify native Waymark icon identity, especially the non-contiguous `D=61247` and `1=61244` mapping. Letter icons must use a 1.25-world-unit radius, number icons a 2.3-world-unit edge, and native icon quads a shared `1.5x` transparent-inset compensation while the fallback retains the geometric world size; each image must remain centered, scale linearly with Map zoom, and retain the labeled fallback when the shared texture is unavailable.
- Verify BattleNpc omnidirectionality resolves from either `BNpcBase.IsOmnidirectional` or status `3808`, never affects Player markers, is sampled as a discrete state, round-trips through JSON, and remains deterministic across forward and backward seeks. Current Captures select the recorded state; legacy Captures may use only the static BaseId fallback and must show a limitation warning.
- Verify both embedded Target Ring PNGs and their exact dimensions. The confirmed prominent yellow circumference in both the 480×682 directional ring and 480×480 omnidirectional ring is `0.78` of the texture canvas half-width and must match the recorded world-space hitbox through Map zoom. Both textures must remain centered and face the recorded direction.
- Verify the in-game Replay host always rejects explicit open requests while `InCombat=true`. On the out-of-combat → in-combat edge, the default-enabled auto-close setting must pause, cancel pending work, and close visible／playing／loading Replay. With the setting disabled at that edge, an existing Replay window remains visible but paused without advancing. Changing the setting after combat has already begun must not synthesize another combat-entry action. Combat end must not request an automatic reopen.
- Verify every newly completed `DutyWiped` generation offers one Debrief Summary only after validation and analysis. A completion observed during combat must wait for the first out-of-combat update; a Summary already visible when combat starts must be dismissed without reopening. Disabling the persisted Summary setting must clear visible／queued Summaries and skip generations completed while disabled; clear／manual／failed completion must not offer one.
- Verify the allocation-free frame policy denies Draw while closed, keeps any intentionally open combat window drawable but never advances it, and permits Advance only for visible out-of-combat playback. Combat-entry auto-close behavior belongs to `ReplayCombatGate`, not `ReplayFramePolicy`.
- Block session construction deterministically, then verify a newer source cancels it and Plugin/window disposal prevents completion adoption and future load starts.
- Verify Focus de-emphasizes every non-selected Actor, zoomed follow clamps to the established safe Map pan boundary, an absent selected player preserves the last center, and manual arena drag cancels Focus. Death filtering and the allocation-free frame policy must not materialize Timeline collections per frame.
- Verify Death correlation anchors on the last living HP observation and replays the latest ordered target-resolved Damage／Heal suffix that crosses zero, including effects whose callback timestamp precedes that HP sample. Verify Pull-local lag calibration uses only unique exact player HP transitions, falls back conservatively with sparse evidence, identifies the virtual-HP crossing entry, calculates estimated HP-before-hit／effective pool／Overkill, retains up to six captured incoming hits, and downgrades or reports unavailable when ActionEffect capability, identity, ordering, barrier absorption, observation lag, or HP evidence is uncertain. Confidence is ordinal and evidence-backed, never a probability or server-authored claim.
- Verify Status identity excludes RemainingTime, normal countdown produces no transition churn, refresh／Param changes create `StatusRefreshed`, and Replay computes remaining time from the latest recorded timing anchor.
- Verify Action-name capture stores one resolved Pull-local snapshot per observed Cast or Action Effect Action ID, maps unnamed Auto-attack-category rows to the localized category name, prefers Lumina／Dalamud RSV then Client RSV then an entity-matched enemy CastBar, and defers Action Effect name resolution to the next Capture Framework sample. Repeated non-cast effects must queue one resolution, retries must remain bounded, source／language metadata must round-trip, and legacy JSON must remain load-compatible. Replay must prefer the recorded name and apply the same Auto-attack fallback to older records.
- Verify `CaptureSamplingScheduler` takes the first sample immediately, follows an absolute monotonic 100 ms grid, records one real sample plus gap count after a hitch, rejects non-monotonic／overlapping preparations, and leaves cadence unchanged after cancellation.
- Verify `FrameworkScanCoordinator` performs one shared scan when Capture owns the sample, refreshes an open Probe in a duty instance at no more than 10 Hz outside recording, performs no full scan for closed Probe＋idle Capture, retries a failed scan without advancing cadence, and allocates nothing on the warmed closed path.
- Verify BarrierPercentage capture, feature-gated JSON compatibility, latest-at-or-before Replay resolution, Party／context presentation, and Death-correlation effective-pool behavior. Legacy Pulls without `BarrierState` must report unavailable rather than zero.

Example expectation:

```text
Input:
D2 Death at 51.2s
H1 Death at 54.0s

Expected:
FirstDeath = D2 @ 51.2s
DeathSequence = [D2, H1]
```

### Layer B — Plugin Service Integration

Target `RaidDebrief.Plugin` orchestration without a running game client. Use fake Dalamud service events but the real `CaptureService`, `AutomaticPullLifecycle`, `PullRecord`, validator, in-memory `LastCompletedPull`, and explicit Developer/Test JSON exporter.

Required lifecycle contracts:
- Wipe and duty completion finalize exactly once.
- Normal combat end respects debounce, and combat re-entry cancels the pending end.
- A stale `InCombat=true` after an explicit Duty end does not start a duplicate Pull.
- Consecutive Pulls use distinct capture IDs and do not share actors, events, or Action Effects.
- Player death and boss despawn/respawn events remain inside the same active Pull while `InCombat` stays true.
- Disposal fully drains pending background finalization/export before returning, and a new service starts with no cross-session `LastCompletedPull` and waits for combat clear before recording again.
- Manual mode still requires explicit start/stop, cannot change mode during a capture, and exports JSON only through the explicit Developer/Test flow.
- Validation failure preserves the previous `LastCompletedPull`, returns the automatic lifecycle to Idle, and permits a later Pull. JSON export failure must not remove an already validated `LastCompletedPull`.

### Layer C — Synthetic Pulls
Create deterministic artificial PullRecord files.

Recommended cases:
- SimpleDeath
- DoubleDeath
- FullPartyWipe
- TankDeath
- RaiseAfterDeath
- TwoDeathsSameTimestamp
- LongDelayAfterDeath
- StatusLostOnDeath
- BossUntargetable
- BossRespawn
- PhaseTransition
- PlayerDisconnect
- TenMinuteFight

Each synthetic pull should have known expected results.

### Layer D — Recorded Real Pulls
Keep selected real recordings as regression fixtures.

These fixtures support Offline Replay, Analysis, UI development, and regression testing; keeping them does not imply that the product persists Pull history.

The canonical Replay UI regression fixture is `testdata/recorded/20260810_DMU.json`. At `03:46.500`, it contains two simultaneously targetable, casting enemies and is the required in-game check for per-enemy HP／Cast Bars and Cast Bar collision avoidance.

Use them to test:
- Real movement patterns
- Real actor lifecycle behavior
- Real event ordering
- Real status behavior
- Waymarks
- Target Marker changes, including assignment and removal
- Long fights

Do not require developers to reproduce the encounter every time.

### Layer E — In-Game Validation
Use Dalamud dev loading/hot reload for final integration checks.

Validate:
- Capture correctness
- No combat stutter
- Pull lifecycle
- UI combat behavior: explicit opening remains rejected during combat. On combat entry, verify the default-enabled setting pauses and closes visible／playing／loading Replay before the same UI update draws windows; disabling it beforehand keeps the existing window visible but paused. Toggling the setting after combat has begun must not retroactively hide that window. Combat end must not reopen a closed Replay, and the next explicit open must still resolve the latest successful Pull.
- Post-Wipe Debrief Summary: repeat at least two Wipes and confirm each newly validated Pull produces one compact Summary after combat clears. Verify Pull ordinal, shared duration, objective Boss HP／unavailable state, first death, complete chronological death sequence including a raise／re-death, Wipe timestamp, and suggested range. The default-enabled setting must suppress both queued and visible Summaries without backfilling disabled Wipes. A visible Summary must dismiss before combat UI draws. One-click Replay must bind to the Summary's exact CaptureId／completion generation, reject a superseded source, open the normal combat-gated Runtime Replay paused at the suggested start, and display the suggested range.
- Target Marker capture: all 17 slots are readable without capture errors; assign and remove at least one marker during a real Pull, then verify the finalized `TargetMarkerFrames` and its position above the same Actor in Replay.
- Map canvas: load real Captures whose Lumina `Map` rows exercise distinct `SizeFactor` values, confirm the initial center corresponds to `(-OffsetX, -OffsetY)` and minimum zoom to `max(1, SizeFactor / 100)`, then verify different PC entry positions do not change framing, zoom cannot go below that value, pan cannot leave its initially visible region, Reset restores it, and zoom can reach 20x. A missing Map row must use complete-field Fit.
- Native Waymark rendering: load a fixture containing all eight field markers and confirm A／B／C／D／1／2／3／4 icon identity, center alignment, letter／number world dimensions, and proportional zoom against the Map background.
- Omnidirectional Target Ring: load a BattleNpc whose Lumina `BNpcBase.IsOmnidirectional` is true, then a Capture with a status-3808 transition. Confirm the complete ring appears for the static case, switches only at the recorded transition, returns to the directional ring when the status ends, and remains reversible under Timeline scrubbing.
- Finalized PullRecord can be handed directly to Replay
- Game-thread safety

Runtime Replay must consume the finalized PullRecord directly and must not require save-to-JSON followed by reload-from-JSON.

The in-game layer should validate integration, not replace Core tests.

### In-Game Build and Hot-Reload Preflight

Dalamud in-game validation has one authoritative development artifact:

```text
src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll
```

Immediately before every hot reload, build that exact artifact through the solution's x64 configuration:

```powershell
dotnet build RaidDebrief.sln --no-restore --configuration Debug -p:Platform=x64
```

`dotnet test` is not a deployment build. Building a test project or its `ProjectReference` may update `src/RaidDebrief.Plugin/bin/Debug/RaidDebrief.dll`, which is a different file and does not refresh a Dalamud Dev Plugin configured for `bin/x64/Debug`.

Required preflight:

1. Run the x64 Debug solution build after the final source edit.
2. Confirm the expected DLL exists and its `LastWriteTime` is newer than the source change:

   ```powershell
   Get-Item .\src\RaidDebrief.Plugin\bin\x64\Debug\RaidDebrief.dll |
       Select-Object FullName, Length, LastWriteTime
   ```

3. Confirm Dalamud Dev Plugin points to that exact absolute DLL path. Do not alternate between `bin/Debug`, `bin/x64/Debug`, and `bin/x64/Release`.
4. Hot reload only after the build completes. Confirm the Dalamud log records the unload/load after the DLL's `LastWriteTime`.
5. Reject the in-game result if its log contains behavior or messages from a removed contract. Rebuild x64 Debug, reload, and repeat only the smallest scenario needed to cover the change.

Release verification remains separate:

```powershell
dotnet build RaidDebrief.sln --no-restore --configuration Release -p:Platform=x64
```

Never treat a successful test run, a Release build, or a recently modified DLL in another output directory as proof that the DLL loaded by Dalamud is current.

## 3. Developer Replay Mode
Provide two explicit Developer/Test boundaries: the Offline executable and the manually selected JSON source inside the formal Phase 3 Replay window. Neither is persistent Pull history, and the formal Runtime source remains the in-memory `LastCompletedPull`.

Offline executable:

```powershell
dotnet run --project src/RaidDebrief.Offline -- --fixture testdata/recorded/<CaptureId>.json
```

Open the printed localhost URL to exercise Play／Pause／Scrub, event Timeline, arena state, and end-of-Pull behavior without FF14 or Dalamud.

For the deterministic full-Pull／performance gate:

```powershell
dotnet run --project src/RaidDebrief.Offline -- --fixture testdata/recorded/<CaptureId>.json --verify --seek-iterations 20000
```

Verification renders every recorded frame plus the complete duration, advances playback through the whole Pull, checks representative milestones and repeated seeks, then fails if shuffled seeks are nondeterministic, average at least 2 ms, or allocate more than 64 bytes per seek.

In a configured Dalamud development build:

```text
/rdebrief
/rdb
```

The main window opens the Replay tab by default and resolves the current in-memory `LastCompletedPull` on every open. The Settings tab owns automatic Pull capture, post-Wipe Debrief, combat auto-close, and the persisted “開發人員” switch. The Developer tab is absent by default; enabling that switch exposes live probe diagnostics, capture controls, and the explicit Developer/Test JSON source for manual interface testing. JSON and filesystem access occur only after the user selects that tab and presses the load action; the resulting `ReplaySession` uses only finalized `PullRecord` domain data. A failed manual import must not replace the current valid session.

## 4. Regression Test Data
Store reusable fixtures under:

```text
testdata/
├─ synthetic/
└─ recorded/
```

Real recordings may contain player-identifying information. New captures must:
- serialize every player-character name as a Pull-local `Player N` alias
- retain NPC names for encounter analysis
- contain no Content ID field

Historical captures created before anonymization must be reviewed before committing or sharing. Never commit a raw private-party recording.

## 5. Performance Testing
Measure at minimum:
- Capture cost per framework update
- Memory usage during a 10–15 minute fight
- Pull serialization time
- Replay load time
- Timeline scrubbing responsiveness

Avoid premature optimization.

For Capture sampling changes, separate:
- lightweight every-update callback cost from full-scan callback cost;
- recording, open-Probe idle, and closed-Probe idle paths;
- callback allocated bytes and Gen 0／Gen 1 collections;
- frame-time p50／p95／p99／maximum before and after the change.

Do not claim “zero stutter” from code inspection or fixture seek speed. It requires comparative in-game Framework callback, GC, and frame-time evidence under equivalent scenarios.
First establish a baseline, then optimize measured bottlenecks.

## 6. Completion Checklist for Core Changes
Before merging:
1. Build passes.
2. Unit tests pass.
3. Synthetic fixtures load.
4. Existing recorded fixtures still replay.
5. No new Dalamud dependency enters RaidDebrief.Core.
6. Behavior matches PROJECT_SPEC.md.

## 7. Completion Checklist for Capture Changes
Before merging:
1. Complete the x64 Debug build and hot-reload preflight above.
2. Validate in game.
3. Record at least one real pull.
4. Inspect exported data when the scenario uses the explicit Developer/Test JSON flow.
5. Confirm no obvious missing/duplicated actors.
6. Confirm timestamps are monotonic/coherent.
7. Confirm the finalized in-memory `LastCompletedPull` is valid Replay input.
8. If exported as a recorded fixture, confirm the fixture can also be loaded offline.
9. Confirm a current-build Pull records `BarrierState` and `ActionNameSnapshot`; inspect representative barrier percentages, at least one resolved static／runtime-RSV／UI-observed Action name when the encounter supplies it, and any unnamed Auto-attack-category Action Effect without accepting an `Action #ID` fallback.
10. Confirm full scan count follows Capture／Probe demand rather than game FPS, first sample occurs on the combat-edge callback, average interval remains near 100 ms, and gap count does not create duplicate frames.
11. Confirm rejected volatile Actor reads are counted and do not abort the remaining frame; investigate any repeated Actor slot failure or missing actor sequence.
12. Compare Framework callback allocation／timing and GC behavior with the documented pre-change baseline before making a stutter-reduction claim.



For automatic Pull lifecycle changes, also verify:
1. With all three Dalamud `BoundByDuty` flags false, outside-instance combat does not arm or start automatic capture.
2. Each of `BoundByDuty`, `BoundByDuty56`, and `BoundByDuty95` independently identifies an instanced duty.
3. Entering an instance while already in combat waits for an out-of-combat observation before arming.
4. A normal in-instance `InCombat=false → true` edge starts exactly one capture.
5. A short combat gap is absorbed by debounce, while a sustained gap finalizes exactly once.
6. Wipe and duty completion finalize immediately and do not restart while the old combat flag is still true.
7. Leaving the instance during an automatic Pull finalizes with `InstanceExited` and records no outside-instance frame.
8. Two consecutive Pulls produce distinct capture IDs, the second record contains no first-Pull data, and `LastCompletedPull` changes only after successful validation.
9. Reload/disposal drains pending background work, a new service does not restore `LastCompletedPull` from disk, and recording waits for combat clear.
10. Manual Developer/Test capture remains available outside an instance and still requires explicit start/stop.
