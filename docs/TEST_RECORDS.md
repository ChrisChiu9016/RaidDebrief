# Raid Debrief — Test Records

Last updated: 2026-08-14

## Purpose

This document preserves observed build, automated-test, capture, and in-game validation evidence. `docs/TESTING.md` defines testing strategy; it does not carry execution logs. The legacy internal stages 0–6 below collectively delivered Roadmap Phase 0 and Phase 1.

Historical entries are preserved even when the architecture later changed. In particular, the temporary Capture repository/index implementation was subsequently removed from Runtime: the current contract is in-memory `LastCompletedPull`, with JSON limited to explicit Developer/Test fixture export. Treat the original entries as chronological evidence, not as the current architecture specification.

## Roadmap Phase 1 Completion Addendum

### Current runtime contract

- Automatic Pulls produce a fresh Pull-local `PullRecord`; validation must succeed before the in-memory `LastCompletedPull` is replaced.
- Runtime Replay does not use a Capture repository, persistent latest index, or cross-session restore.
- Manual Capture remains unrestricted Developer/Test infrastructure and can export JSON fixtures.
- Production automatic Capture is gated by Dalamud `BoundByDuty`, `BoundByDuty56`, or `BoundByDuty95`; outside-instance combat cannot arm or start a Pull. Leaving an instance finalizes with `InstanceExited` before an outside frame is recorded.

### Real eight-player Pull evidence

- Capture `64c68b21-490a-458c-b59a-fabc3ea5c9b7`: Territory 1326, Map 1168, DutyCompleted, 384.451 seconds, 3,845 frames, 3,149 events, 2,568 Action Effect batches, and one Waymark frame.
- Export size: 70,069,726 bytes. SHA-256: `2a4a9522a91677484a5366c3242c993ae202d283e13e99410aa0f99cc1718bc6`.
- Frame timestamps were monotonic and coherent: 3,845/3,845 frames present, 105 ms maximum interval, approximately 100.0003 ms average interval.
- The fixture contains eight valid party players with stable movement/rotation/HP data, Boss actors, casts, statuses, damage/heal Action Effects, a death, subsequent resurrection observations, and DutyCompleted single-finalize. No visible combat stutter was reported.
- The original export exposed an ObjectTable artifact: eight valid party PCs plus eight non-networked player proxies with `EntityId == 0xE0000000`. The recorder now rejects player `EntityId` values `0` and `0xE0000000` before registration.
- Post-fix Capture `268a437c-1e6b-4273-8849-749fca94a066` contained no invalid or duplicate PC EntityId/GameObjectId. After its zone transition, 396 frames each contained exactly eight valid PCs rather than sixteen. The same manual fixture also confirmed why production automatic Capture must be instance-gated; manual Debug capture intentionally remains unrestricted.
- This Capture remains the secondary real-data source for player Death／AliveTransition coverage if it is proxy-filtered and privacy-reviewed; the clean post-fix primary recorded fixture is documented under Roadmap Phase 2 below.

### Final automated and build verification

- `RaidDebrief.Core.Tests`: 38 passed, 0 failed.
- `RaidDebrief.Plugin.Tests`: 16 passed, 0 failed.
- x64 Debug solution build: 0 warnings, 0 errors.
- The x64 Debug DLL loaded successfully in Dalamud at 2026-08-09 13:15:49 +08:00 with no load error.

## Roadmap Phase 2 Kickoff

- First implementation item: pure Core `ReplayClock`.
- Focused `ReplayClockTests`: 7 passed, 0 failed.
- Full Release verification after the Phase 2 kickoff: `RaidDebrief.Core.Tests` 45 passed and `RaidDebrief.Plugin.Tests` 16 passed; 0 failed.
- x64 Debug solution build after the final source edit: 0 warnings, 0 errors.
- Scope audit: this increment adds only clock play/pause/seek/advance behavior in `RaidDebrief.Core`; it adds no Dalamud Replay window, Debrief, Replay UX extensions, Event correlation, history, Recent Pulls, or comparison feature.

### Recorded fixture baseline

- Primary fixture: `testdata/recorded/6fe1b80f-567a-41a3-8912-6d013c137aa7.json`, captured manually with the post-fix Recorder in Territory 1229, Map 926.
- The source was copied byte-for-byte after privacy review. Source and fixture SHA-256: `0a2aa6d519a5a602b04df8347e9985e43dec145527aa0ee071e43d7b26b194e4`; byte length: 37,652,192. The adjacent provenance manifest records the no-transformation rule and privacy review.
- `CaptureJson.Load` and the current `PullRecordValidator` accepted the repository fixture: DutyCompleted, 223.202 seconds, 88 actors, 2,233 frames, 1,495 events, 1,432 Action Effect batches, and one Waymark frame.
- Exactly eight valid PC actors are present, named only `Player 1` through `Player 8`; the fixture contains no Content ID field, invalid player EntityId, duplicate StableActorId, or duplicate GameObjectId.
- Frame timestamps are strictly increasing with approximately 100.0004 ms average and 113 ms maximum intervals. Event and Action Effect timestamps are nondecreasing. Every frame through DutyCompleted contains all eight players; later missing actors occur only in the post-completion tail.
- The baseline has no player Death／AliveTransition and no active Waymark marker. Synthetic fixtures must cover those states; the older privacy-reviewed, proxy-filtered `64c68b21-490a-458c-b59a-fabc3ea5c9b7` Capture remains available as secondary real Death／AliveTransition evidence.
- Focused `RecordedFixtureTests`: 3 passed, 0 failed.
- Full `RaidDebrief.Core.Tests`: 47 passed, 0 failed.
- x64 Debug solution build: 0 warnings, 0 errors.

### Timestamp Actor-State Resolver

- `ActorStateResolver` consumes only a finalized `PullRecord`. It pre-indexes samples and recorded Actor lifecycle events by stable Actor identity; Replay query APIs do not use `CaptureJson` or filesystem access.
- `TryResolveActor` and caller-buffered `ResolveAll` use binary search at query time. Position uses linear interpolation, Rotation uses normalized radians shortest-angle interpolation with a deterministic $-\pi$ tie, and HP／MaxHP／dead／targetable use the latest observation at or before the timestamp.
- Recorded despawn／spawn intervals are not bridged by Position or Rotation interpolation. Before the first sample there is no resolved state; after the final sample the last observed Position is held without extrapolation while the Actor remains present.
- Focused `ActorStateResolverTests`: 8 passed, 0 failed. Combined Resolver／recorded fixture regression: 11 passed, 0 failed.
- The real eight-player fixture resolves all eight PCs at 100.000 seconds, returns an identical state after reverse/forward seeks, and reflects the three remaining PCs at the post-completion final frame.
- Full `RaidDebrief.Core.Tests`: 56 passed, 0 failed.
- x64 Debug solution build: 0 warnings, 0 errors.
- Scope audit: this increment adds only PullRecord-based timestamp Actor-state reconstruction and its contracts; it adds no persistence, history, Dalamud UI, Debrief, event causality, or encounter-specific logic.

### Replay Timeline and Waymark State

- `ReplayTimeline` consumes only finalized `PullRecord.Events`, retains each original recorded index, and performs one-time `(timestamp, original index)` ordering. Through／exact／inclusive-range queries use binary search and return allocation-free `ReadOnlySpan` views.
- Equal-timestamp events preserve source collection order without an event-type priority. Death／AliveTransition remain objective timeline markers; current dead/alive state continues to come from the recorded discrete observation resolved by `ActorStateResolver`.
- `WaymarkStateResolver` consumes only finalized `PullRecord.WaymarkFrames`, uses binary search, returns the latest frame at or before the timestamp, and returns an empty state before the first frame.
- Focused `ReplayTimelineTests`: 4 passed, 0 failed. Combined Timeline／recorded fixture regression: 7 passed, 0 failed.
- The real eight-player fixture preserves all 1,495 Events in deterministic order, including 31 BattleNpc Death events and no AliveTransition. Its all-inactive Waymark frame resolves at timestamp 1, and reverse/forward queries reproduce identical results.
- Full `RaidDebrief.Core.Tests`: 60 passed, 0 failed.
- x64 Debug solution build: 0 warnings, 0 errors.
- Scope audit: this increment adds only PullRecord-based Timeline, observed death/alive markers, and Waymark timestamp resolution; it adds no persistence, history, Dalamud UI, Debrief, event causality, event-type priority, or encounter-specific logic.

### 2D Arena Scene and Offline Renderer

- `ArenaProjection` maps recorded world X／Z to normalized 2D coordinates using stable whole-Pull bounds plus active Waymarks. Projection and 2D facing calculation leave recorded samples unchanged.
- `ArenaSceneBuilder` reuses `ActorStateResolver`, `WaymarkStateResolver`, a scratch Actor buffer, and a caller-owned `ArenaRenderScene`. Player markers include all PCs; BattleNpc markers include actors objectively observed targetable at least once, keeping bosses/interactable enemies visible during untargetable states while excluding never-targetable helpers.
- `RaidDebrief.UI` is a new Dalamud-independent project referencing only `RaidDebrief.Core`. `SvgArenaRenderer` consumes scene domain data and renders arena grid, anonymous Player labels, BattleNpc, facing, dead／untargetable state, and active Waymarks without JSON or filesystem access.
- Focused Arena／recorded fixture Core regression: 6 passed, 0 failed. `RaidDebrief.UI.Tests`: 2 passed, 0 failed.
- The real eight-player fixture at 100.000 seconds produced stable world bounds `60,60..140,140`, eight Player markers, two BattleNpc markers, and no active Waymark marker. Chromium loaded the generated 3,775-byte SVG and verified 10 Actor elements, P1–P8 labels, both recorded BattleNpc labels, and timestamp `01:40.000`; the rendered arena was visually inspected.
- Full `RaidDebrief.Core.Tests`: 63 passed, 0 failed. Full `RaidDebrief.UI.Tests`: 2 passed, 0 failed.
- x64 Debug solution build, including Core, UI, Plugin, and all test projects: 0 warnings, 0 errors.
- Scope audit: this increment adds only deterministic arena projection, render-scene construction, and an offline SVG renderer; it adds no persistence, history, Dalamud UI integration, playback controls, Debrief, event causality, or encounter-specific mechanic logic.

### Offline Replay Integration and Developer In-Game Test Window

- `ReplaySession` receives only a finalized `PullRecord` and owns one `ReplayClock`, pre-indexed `ReplayTimeline`／Actor／Waymark resolvers, stable `ArenaProjection`, `ArenaSceneBuilder`, and reusable `ArenaRenderScene`. Its play／pause／advance／seek operations keep all resolved outputs on one clamped timestamp.
- Replay duration is the maximum of the finalized wall-clock duration and the latest Frame／Event／Waymark／Action Effect timestamp, retaining a silent post-observation tail. Focused `ReplaySessionTests`: 2 passed, 0 failed.
- `RaidDebrief.Offline` is a new executable local web host. Its explicit fixture path is the JSON／filesystem boundary; browser Play／Pause／Advance／Seek calls use the server-side `ReplaySession`, while Timeline and SVG presentation reuse existing Core／UI contracts rather than reimplementing state resolution in JavaScript.
- Chromium loaded the real eight-player fixture. At `100,000 ms` the host showed eight Player markers, two BattleNpc markers, zero Waymarks, and 694 of 1,495 Events. Pause held `100,109 ms` unchanged, a repeated seek to `100,000 ms` returned identical SVG, and playback from the final 100 ms reached `223,226 ms` then remained stopped. The full host layout, arena, marker labels, controls, Timeline, and statistics were visually inspected.
- The Developer/Test-only `ReplayTestWindow` can use in-memory `LastCompletedPull` or background-load one explicit JSON path. It provides ImGui Play／Pause／Scrub, event jump, arena markers, facing, dead state, targetable opacity, and Waymarks; `/rdebrief replay` and the Capture window button open it.
- Dalamud auto-reloaded the configured `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`. The real fixture background-loaded with 2,233 Frames and 1,495 Events; the live ImGui Draw path rendered timestamp `100,000 ms` with 10 Actor markers. No fixture-load or `UiBuilder(RaidDebrief)` error was logged. Temporary auto-open／auto-load smoke instrumentation was removed afterward.
- Full tests: Core 65 passed, UI 2 passed, Plugin 16 passed; 0 failed. x64 Debug solution build: 0 warnings, 0 errors.
- Scope audit: `ReplaySession`, Offline host, and the explicitly requested Developer/Test in-game adapter add no production Phase 3 Replay flow, Debrief, history／recent／retention／compare, event causality, or encounter-specific mechanic logic.

### Phase 2 End-to-End and Performance Completion

- `ReplayEndToEndTests.RecordedPullReplaysEveryFrameAndReverseScrubsDeterministically` sought all 2,233 recorded frame timestamps in ascending order, saved each resolved scene hash, then sought every frame in reverse and reproduced every hash. It also fixed the observed Add spawn／death／despawn lifecycle, defeated／untargetable Boss state, DutyCompleted presence, and final event count.
- `ReplayEndToEndTests.CompositeReplaySynchronizesDeathRevivalWaymarksAndBossLifecycle` fixed one complete synthetic Pull across player Death／AliveTransition, active／inactive Waymarks, Boss despawn／respawn, events-through-time, and playback clamping／stop at the complete duration.
- The primary fixture passed the final executable `RaidDebrief.Offline --verify --seek-iterations 20000` run: duration 223,226 ms, 2,234 rendered SVG scenes, 2,233 playback advances, full replay checksum `14221742015303217744`, and deterministic start／first-frame／Death／DutyCompleted／end milestones. The 20,000 shuffled seeks averaged 0.0244 ms each, allocated 0 bytes per seek, and remained below the explicit 2 ms／64 byte limits.
- The same verifier accepted privacy-reviewed real Capture `64c68b21-490a-458c-b59a-fabc3ea5c9b7`: duration 384,452 ms, 3,846 rendered SVG scenes, 3,845 playback advances, player Death at 203,200 ms, AliveTransition at 218,204 ms, and DutyCompleted at 384,449 ms. Its 20,000 shuffled seeks averaged 0.0189 ms each with 0 bytes allocated per seek.
- Chromium exercised the primary fixture at `0`, `1`, `9,004`, `10,602`, `32,300`, `36,303`, `215,373`, `216,403`, and `223,226 ms`. The observed Player／BattleNpc／dead／event counts matched the verifier; Add 59 appeared, died, and despawned; DutyCompleted and defeated Boss states appeared; seeking away and back to `32,300 ms` reproduced identical SVG.
- Chromium also exercised the real player death fixture at `203,200 ms` and `218,204 ms`: the same Player 6 marker changed from dead to alive, the dead marker count changed from one to zero, and seeking away then back reproduced identical alive-state SVG.
- Dalamud auto-reloaded the configured x64 Debug DLL and the Developer/Test ImGui path loaded the primary fixture. Temporary smoke instrumentation rendered the same nine representative timestamps in order; Player／BattleNpc／dead／event counts matched Offline results, including the empty start, eight-player first frame, Add lifecycle, DutyCompleted, defeated Boss, and final state. No Replay load or UI error was logged, and the instrumentation was removed before the final build.
- Final automated verification: `RaidDebrief.Core.Tests` 67 passed, `RaidDebrief.UI.Tests` 2 passed, and `RaidDebrief.Plugin.Tests` 16 passed; 0 failed.
- Final `Debug -p:Platform=x64` solution build: 0 warnings and 0 errors. The configured 109,568-byte `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll` had `LastWriteTime` 2026-08-09 17:02:03 +08:00; Dalamud logged unload, load, and successful completion at 17:02:04.
- Scope audit: Phase 2 closes with finalized-`PullRecord` Replay, a Developer/Test Offline host, and an explicitly test-only Dalamud adapter. It adds no persistent Pull history, recent／retention／compare feature, Debrief, causality inference, encounter-specific mechanic logic, or production Phase 3 Replay flow.

## Roadmap Phase 3 — In-Game Replay

### Item 1 — Replay Window Integration

- `ReplayTestWindow` was cleanly replaced by one formal `ReplayWindow`; `/rdebrief replay` and the Capture window entry both call its Runtime open request. The registered window title, command help, capture button, and plugin load log now describe the formal In-Game Replay flow rather than the Phase 2 test adapter.
- The window exposes two explicit sources without maintaining duplicate Replay UIs: default Runtime `LastCompletedPull`, and a collapsible Developer/Test manual JSON import. Runtime construction receives the in-memory finalized record directly; filesystem／`CaptureJson.Load` is reachable only from the explicit Developer/Test action.
- Source mode and source details are committed only after background `ReplaySession` construction succeeds. An invalid JSON path reports the load error and leaves the current valid session selected.
- Dalamud x64 Debug smoke loaded privacy-reviewed fixture `6fe1b80f-567a-41a3-8912-6d013c137aa7` through the formal window's Developer/Test source: 2,233 Frames and 1,495 Events. A subsequent `.missing` path produced the expected `FileNotFoundException`, and the window logged that the same Capture session remained active. No `UiBuilder(RaidDebrief)` error was observed. Temporary auto-open／load smoke instrumentation was removed.
- Replay display refinement embeds all 21 supplied `src/JobIcons` PNG files into the plugin assembly and maps the supported combat-job `ClassJobId` values without filesystem access during Draw. Every supplied image decoded as 96×96 RGBA; `JobIconResourcesTests` require every mapped manifest resource and reject unsupported ClassJob IDs.
- Player circles were replaced by 24×24 Job icons when the shared texture is ready, with the prior player circle retained only as a load／unsupported fallback. BattleNpc circles, facing lines, untargetable opacity, dead crosses, and stable P1–P8 labels remain.
- The normalized square replay arena now renders inside an 8-pixel visual margin with a high-contrast double border, while world-space projection uses the observed bounds without the former 5-unit padding. Grid lines, Waymarks, Actors, and timestamp all use the bounded inner rectangle.
- Dalamud display smoke loaded the primary fixture, sought `3,335 ms`, and exercised the formal ImGui Draw path. It reported 8／8 Player Job icons with arena inset 8 and arena size 544; no texture, Replay-load, or `UiBuilder(RaidDebrief)` error was logged. Temporary auto-open／seek／logging instrumentation was removed.
- Square-arena regression verification changed the primary fixture projection from padded `60..140` bounds to observed `65..135` bounds. Core 67／67 passed; full Offline verification rendered 2,234 SVG scenes, advanced 2,233 frames, and completed 20,000 deterministic seeks at 0 allocated bytes per seek.
- Live capture now copies `IGameObject.OwnerId` into the backward-compatible optional `ActorRecord.OwnerId`. Arena construction excludes a targetable BattleNpc only when its nonzero OwnerID matches a recorded Pc EntityId／GameObjectId, retaining unowned Boss／Add actors. Core regression covers player + unowned Boss + player-owned summon; Plugin lifecycle regression verifies OwnerID survives finalization.
- The ImGui arena viewport supports cursor-anchored wheel zoom from `1x` to `6x`, left-button pan constrained to the square field, and Fit reset. Grid, Waymarks, actors, labels, and the real field border share one clipped transform; markers retain fixed pixel size. Pure viewport tests cover Fit projection, cursor anchoring, zoom bounds, pan bounds, and Fit immobility.
- Dalamud zoom smoke loaded the primary fixture, sought `3,335 ms`, applied `1.728x` zoom plus an off-center pan, and completed the formal Draw path with 10 actors. No Replay／UI error was observed; all temporary auto-load／seek／logging instrumentation was removed before the final build.
- Live capture persists `IGameObject.HitboxRadius` on every `ActorStateSample`; validation and latest-discrete resolution retain the recorded world-space value. Boss／Add Target Circles consume that value. Player Target Circle is intentionally fixed at `56 px` wide and does not consume recorded radius for presentation.
- `src/TargetCircle.png`（480×682 RGBA）以 manifest resource 嵌入 Plugin，並透過 `ITextureProvider.GetFromManifestResource` 取得 shared texture。舊 `TargetCircleTexture.CreateFromRaw`、128×128 白色環形、Player 前景 stroke、藍／紅 tint 與獨立 facing line 已移除。
- PNG 黃色外圈約位於 X `40..440`，其直徑為 480 px texture canvas 的 `5/6`。Boss／Add 的 `ResolveTargetCircleQuad` 以 `6/5` 補償透明邊界，使可見黃色外圈精確對應 `HitboxRadius`；Player quad 固定整張 texture 寬度為 `56 px`。
- Territory 1363／Map 79 使用中心 `(100, 100)`、半徑 `17.83`，即 X／Z `82.17..117.83`。在 `544 px` arena 中，Player PNG 固定寬 `56 px`、可見外圈半徑約 `23.333 px`；Boss `6.000` radius 的可見外圈半徑維持 `91.531 px`。
- Legacy Actor 沒有 `HitboxRadius` 時仍顯示固定 Player Target Circle；Capture feature warning 只指出 Boss／Add world-space circles unavailable。
- Regression coverage verifies fixed `56 px` Player texture width across arbitrary recorded radii and Replay zoom, Boss／Add world-space zoom scaling, the 17.83 arena radius, yellow fill for every arena shape, PNG `5/6` outer-ring compensation, and facing rotation. Core 72, UI 4, and Plugin 34 tests passed; 0 failed.
- Capture `00c7163c-7e2a-493d-8222-27ac5b27a097` rendered 1,300 scenes and advanced 1,299 frames. Its 20,000 deterministic seeks averaged 0.0220 ms and allocated 0 bytes per seek.
- Dalamud fixed-circle smoke loaded the circular Capture and exercised the formal ImGui Draw path with 9 Actors／8 Waymarks. Player textures rendered at fixed `56 px`; the Boss outer-ring ratio remained correct. No Replay／UI error occurred, and temporary instrumentation was removed.
- A fresh `Debug -p:Platform=x64 --no-incremental` solution build completed successfully. The configured 581,120-byte Plugin DLL had SHA-256 `129e4cf69e1ce4ec9e512c303324cfef95ffc97e6860c00b50d4930b932691c4` and `LastWriteTime` 2026-08-10 18:59:36 +08:00. `dalamudConfig.json` pointed the enabled Dev Plugin location at that same x64 Debug DLL with automatic reloading enabled. Dalamud logged unload at 18:59:36.649, load at 18:59:36.687, the Action Effect hook and Phase 3 Replay window initialization, and `Finished loading RaidDebrief` at 18:59:37.079. The user then manually completed the instructed Replay display smoke and reported it as OK; no automated mouse or keyboard input was used for that confirmation.
- Automatic captures `e7360741-6064-4fcd-a38b-3a420e4eeb3a` and `390e99a3-4970-47fe-bf21-ea2b6b40b72c`, plus manual capture `f8263cb6-32b5-4af5-97da-b22a68ab62f2`, reproduced finalization failure in Territory 1149／Map 834. `ActionEffectReader` had already rejected null／zero IDs, so the validator exception isolated the native callback behavior: multiple target slots may legitimately repeat one TargetObjectId.
- `ActionEffectRecord.Targets` now documents and preserves native target-slot order without imposing Object-ID uniqueness. Validation still rejects null／zero targets and independently validates every slot's actor association, entry count, unique entry indices, raw type, decoded amount, and flags. The focused Core test failed with the production exception before the change and passes afterward; Plugin lifecycle theory covers both automatic DutyWiped and manual StopAndExport completion with repeated target slots and distinct effects.
- Real post-reload manual Capture `0a162e4d-4734-4a45-a056-1ed4e482e839` recorded 37,792 ms／94 Actors／378 Frames／125 Events／42 Action Effects in the same Territory 1149／Map 834. Action 3566 at 20,038 ms retained three target slots with the same TargetObjectId `1073802236`; the Pull finalized, validated, exported, and loaded as Runtime LastCompletedPull. Offline verification rendered 379 scenes, advanced all 378 frames, and completed 20,000 deterministic seeks at 0.0156 ms average／0 bytes per seek.
- Arena presentation now uses one yellow active-area contract for every shape: ImGui Square and Circle fills share `observedRange`; SVG `.arena` supplies `#7a6110` to both `<rect>` and `<circle>`. The exterior canvas remains dark, borders and grid remain unchanged, and no encounter-specific fill branch is required for future captured arenas.
- `testdata/recorded/P10S.json`（Territory 1149／Map 834）exercises the unknown rectangular path with observed／world bounds X `71.719..143.126`, Z `65.000..114.000`. Browser reproduction showed the old dark rectangle; after the change the actual computed SVG fill was `rgb(122, 97, 16)` and retained the fixture's approximately `1.457:1` aspect ratio. A dedicated UI fixture regression asserts Square shape, observed bounds as world bounds, rectangular arena output, and shared yellow style.
- P10S Offline verification rendered 379 scenes, advanced all 378 frames, and completed 20,000 deterministic seeks at 0.0120 ms average／0 bytes per seek. Temporary P10S auto-load／seek smoke instrumentation was removed before the final build.
- Root cause of the post-Wipe Probe failure was a volatile Dalamud ObjectTable `BattleChara` wrapper that remained enumerable while `IsCasting` already threw `NullReferenceException`. `LiveDataProbe` now constructs each actor snapshot atomically; one volatile actor is skipped and counted without aborting the Framework callback or replacing the valid actor prefix with stale data. Logging is rate-limited to the first rejection and every 300th rejection.
- In-game verification exercised the guard repeatedly for ObjectTable index 73 after a transition. Only the expected `skipped volatile ObjectTable actor` warnings were emitted; no subsequent `Raid Debrief live data probe failed`, Replay-load, or `UiBuilder(RaidDebrief)` error occurred.
- Manual Capture `be34f692-77a0-4d21-ae1e-ab14d0534e7a` then recorded 25,622 ms across `DutyWiped` and `DutyRecommenced`: 109 registered Actors, 257 Frames, 130 Events, 8 Action Effects, and 1 Waymark frame. It finalized, validated, and exported 4,476,793 bytes successfully. Immediate follow-up Capture `24431f80-1fdb-4ead-9326-540ef59768b6` recorded 5,222 ms／55 Actors／53 Frames, finalized, validated, exported 925,563 bytes, and loaded through Runtime Replay as the new `LastCompletedPull`.
- Offline end-to-end verification loaded both exported Capture files, rendered／advanced every frame, and completed 20,000 deterministic seeks each. The Wipe capture averaged 0.0156 ms／seek and the follow-up capture 0.0120 ms／seek; both allocated 0 bytes／seek. `AutomaticCaptureEnabled` remained `false`, and the temporary forced-auto smoke was removed before the final build.
- Capture `00c7163c-7e2a-493d-8222-27ac5b27a097` identified the aspect-ratio defect: all 71,883 Actor samples span X `75.251..126.000` and Z `25.000..117.235`, but the Z 25 extrema come from never-targetable off-arena `BattleNpc` objects. Replay-visible Players, ever-targetable Boss／Add actors, and active Waymarks instead produce observed bounds X `82.170..117.235`, Z `82.961..117.235`.
- Territory 1363／Map 79 的 profile 已依確認值固定為 `Circle`、中心 `(100, 100)`、半徑 `17.83` world units，即 X／Z `82.17..117.83`。其他未定義 profile 仍維持既有 square behavior。
- In-Game Replay 的 `544 px` arena 將 `6.000` Boss radius 投影為 `91.531 px` 可見黃色外圈半徑；PNG canvas 半寬會先放大至 `109.837 px`，其 `5/6` 外圈內容最後回到 `91.531 px`。場地 `272 px` 半徑與 Boss 外圈比例為 `2.9717`，等於 `17.83 / 6`。
- The circular Capture retained full Offline verification and completed the current Dalamud scale smoke at `2,278 ms` with 9 Actors／8 Waymarks. The formal Replay window visually confirmed the corrected Boss outer-ring ratio and produced no Replay／UI error; temporary instrumentation was removed.
- Scope audit: actual arena bounds, transparent-inset correction, and world-space Target Circle sizing are Replay truth-preserving presentation corrections. They do not implement previous-Pull selection, combat auto-hide, Debrief, history／recent／retention／compare, or other Phase 5 additions.

### Item 2 — Previous-Pull Loading

- `CaptureService.GetReplaySourceSnapshot()` now returns finalization generation／state／CaptureId／error, completed generation, and the immutable `LastCompletedPull` reference under one lock. Finalization publishes `Finalizing` before background validation; success atomically advances the completed generation and record; failure publishes the failed CaptureId／error without clearing the previous valid record.
- `ReplayWindow` resolves Runtime input only from that snapshot. `Finalizing` yields an explicit wait state rather than loading the prior Pull. `Failed` either labels the preserved source as `Runtime previous valid Pull` with the failed CaptureId, or yields an empty state. A default post-reload snapshot explicitly states that Runtime will not restore from disk.
- `ReplayLoadCoordinator` gives every Runtime or Developer/Test load a request generation and captures its source generation／CaptureId. New requests supersede old tasks. Runtime completion is rechecked against a fresh atomic snapshot immediately before UI adoption, preventing an older build from replacing a newer completed Pull even when finalization and task completion interleave within one Draw.
- Focused regression: 7 passed, 0 failed. Coverage blocks validation to observe the atomic `Finalizing → Succeeded` transition, verifies failed finalization preserves and correctly labels the prior record, verifies empty Runtime does not fall back to disk, and proves both a newer Runtime generation and explicit Developer/Test selection supersede an older blocked Runtime build.
- Full solution tests: Core 72 passed, UI 4 passed, Plugin 40 passed; 0 failed. Focused synthetic `CaptureJsonTests`／`ReplaySessionTests`: 14 passed, 0 failed. `Debug -p:Platform=x64` solution build: 0 warnings, 0 errors.
- Recorded smoke loaded `testdata/recorded/P10S.json` as Capture `0a162e4d-4734-4a45-a056-1ed4e482e839`, rendered 379 scenes, advanced all 378 frames, and completed 1,000 deterministic seeks at 0.0172 ms average／0 bytes per seek.
- Scope audit: Runtime remains a single in-memory latest-success reference with no history／recent／retention／compare, persistent latest lookup, startup scan, or cross-session restore. `RaidDebrief.Core` contains no Dalamud reference. Combat auto-hide, Phase 4 Debrief, and Phase 5 Replay UX remain unimplemented.

### Item 3 — Combat Auto-Hide Behavior

- `Plugin.DrawUi()` samples the already-extracted `LiveDataProbe.InCombat` boolean and calls `ReplayWindow.UpdateUiState()` before `WindowSystem.Draw()`. Replay receives no Dalamud condition service, ObjectTable, or live actor reference.
- `/rdebrief replay` and the Capture window callback share `OpenReplayUi()`, which passes that same boolean to `ReplayWindow.OpenRuntime(inCombat)`. An in-combat request is rejected without setting `IsOpen`, so it cannot display the Replay window or steal focus.
- `ReplayCombatGate` is independent of ImGui and Dalamud. Combat plus any visible, playing, or loading Replay state yields `HideAndPause`; the window pauses its `ReplaySession`, closes, invalidates pending load adoption, clears Runtime auto-refresh intent and elapsed playback remainder, then skips the Draw path while combat remains true.
- Combat end only restores permission for a later explicit open. It does not reopen the window. Auto-hide preserves the active paused session and `CaptureService` Replay source, so the next explicit request can still resolve the latest successful Pull.
- Focused `ReplayCombatGateTests`: 6 passed, 0 failed. Coverage includes combat open rejection; visible, playing, and pending-load hide transitions; combat end without automatic reopen; and repeated in-combat observation after hiding without new work. Combined Previous-Pull／Combat focused regression: 11 passed, 0 failed.
- Full solution tests: Core 72 passed, UI 4 passed, Plugin 46 passed; 0 failed. `Debug -p:Platform=x64` solution build: 0 warnings, 0 errors.
- Recorded P10S smoke rendered 379 scenes, advanced all 378 frames, and completed 1,000 deterministic seeks at 0.0168 ms average／0 bytes per seek.
- Scope audit: no `RaidDebrief.Core` Dalamud dependency, Replay live-condition or live-actor access, history／recent／retention／compare, persistent latest Pull, cross-session restore, Debrief, or Phase 5 UX was added. This item records automated and Offline evidence only; post-build hot reload and real next-Pull UI behavior remain part of Phase 3 end-to-end validation.

### Item 4 — Minimal Performance Safeguards

- `ReplayFramePolicy` makes the hot Draw decision from three booleans without allocation. Closed or combat Replay cannot Draw or Advance; visible paused Replay may Draw but cannot Advance; visible out-of-combat playback is the only state that consumes ImGui delta time and rebuilds the reusable scene.
- `ReplayWindow.UpdateUiState()` suspends hidden work after manual close as well as combat hide: it pauses playback, clears source refresh intent and elapsed remainder, and invalidates pending load adoption without clearing the active session or `LastCompletedPull`.
- `ReplayLoadCoordinator` now owns a cancellation token per request. Supersession, invalidation, hide, or dispose cancels prior work; cancellation is checked before／after record loading and before／after `ReplaySession` construction. `ReplayWindow.Dispose()` is called immediately after WindowSystem removal, prevents new work, and makes later completion adoption impossible.
- Recent-event rendering consumes `ReplaySession.EventsThroughCurrentTime` as a `ReadOnlySpan` and iterates at most the final 12 entries. A 100,000-iteration regression over frame policy and event-window calculation allocated 0 bytes.
- Focused `ReplayPerformanceSafeguardTests`: 8 passed, 0 failed. Coverage includes all Draw／Advance state combinations, fixed recent-event limit, allocation-free hot decisions, cancellation of superseded construction, and disposal preventing adoption or a later start. Combined Previous-Pull／Combat／Performance focused regression: 19 passed, 0 failed.
- Full solution tests: Core 72 passed, UI 4 passed, Plugin 54 passed; 0 failed. `Debug -p:Platform=x64` solution build: 0 warnings, 0 errors.
- Recorded P10S verification rendered 379 scenes, advanced all 378 frames, and completed 20,000 deterministic seeks at 0.0160 ms average／0 bytes per seek, below the 2 ms／64-byte limits.
- Scope audit: Runtime Draw adds no serialization, filesystem I/O, live actor traversal, complete-Timeline materialization, cache layer, history／recent／retention／compare, persistent latest Pull, cross-session restore, Debrief, or Phase 5 UX. `RaidDebrief.Core` remains free of Dalamud. New In-Game load／play-to-end and next-Pull Draw evidence remains part of Phase 3 end-to-end validation.

### Item 5 — Phase 3 End-to-End Acceptance

- A fresh `dotnet test RaidDebrief.sln --configuration Debug --no-restore` run passed Core 72／UI 4／Plugin 54 tests with 0 failures. A fresh `dotnet build RaidDebrief.sln --configuration Debug -p:Platform=x64 --no-incremental` completed with 0 warnings and 0 errors.
- The configured Dalamud Dev Plugin path is `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`. The accepted DLL is 603,648 bytes, has `LastWriteTime` 2026-08-10 19:56:51.981878 +08:00, and SHA-256 `c56d668bdb805fb5331cc1f6025dc6ead0568d5575597f91a5d7c42635353217`.
- Post-build Dalamud evidence: unload began at 20:05:33.928 and completed at 20:05:34.955; the configured DLL began loading at 20:05:35.792 and Plugin load completed at 20:05:36.419. No subsequent `RaidDebrief` or `UiBuilder(RaidDebrief)` error was present during acceptance.
- Automatic capture entered a real instanced duty and recorded Wipe Pull `e08965bf-5829-416b-a016-a3a406a329a9`. Finalization began at 20:08:18.145 and successful validation published the completed Pull at 20:08:18.150. The formal Runtime Replay loaded that exact CaptureId at 20:08:27.240 with 37 frames and 3 events; manual in-game observation confirmed the displayed source CaptureId and working Play／Pause／Scrub controls.
- The next real Pull started at 20:09:16.592. Replay logged `paused and hidden because InCombat=true` at 20:09:16.596. Explicit open attempts at 20:09:22.866, 20:09:24.414, and 20:09:26.034 were rejected while in combat. Manual observation confirmed no Replay window or focus interference, normal game input, and no automatic reopen after combat ended.
- The second Wipe Pull `e95f967e-df85-4582-a0f4-0879a7665501` finalized and validated at 20:09:29.106 with 126 frames, 6 events, and 8 Action Effects. After manual observation confirmed that combat end did not reopen Replay, an explicit post-combat request loaded that exact latest CaptureId at 20:09:51.040. A later explicit Developer/Test action loaded the primary eight-player fixture at 20:11:43.464.
- Primary eight-player recorded Capture `6fe1b80f-567a-41a3-8912-6d013c137aa7` rendered 2,234 scenes, advanced all 2,233 frames, and completed 20,000 deterministic seeks at 0.024298 ms average／0 bytes per seek, below the 2 ms／64-byte limits.
- Scope audit: `RaidDebrief.Core` remains free of Dalamud. Acceptance added no history／recent／retention／compare, persistent latest Pull, cross-session restore, Debrief, Phase 5 UX, or encounter-specific behavior. Both Phase 3 exit criteria passed.

### Post-Phase 3 Replay Corrections

- Universal fallback regression verifies that Core contains no Territory／Map arena profile: `ArenaProjection.FromPullRecord` uses a neutral square containing replay-visible observations with a minimum 40×40 world-unit extent. The formal in-game host now overrides that fallback only with row-derived Lumina Map canvas bounds, never encounter-specific geometry.
- `WipeReplayPromptControllerTests` verify one prompt per newly completed out-of-combat `DutyWiped` generation, no prompt for `DutyCompleted`, consumption／dismissal behavior, and combat dismissal without reopening.
- Target Marker regression covers 17-slot state-change recording, duplicate-frame omission, validation, JSON round-trip, backward seeks, moving-Actor scene binding, inactive removal, SVG output, and all 17 embedded PNG resources. The Framework-thread reader maps the public `MarkingController.Markers` fixed array in native order `Attack1-5／Bind1-3／Stop1-2／Square／Circle／Plus／Triangle／Attack6-8`; the Core JSON boundary upgrades Captures written with the earlier incorrect slot order and new records carry an explicit canonical-order feature.
- Final automated verification: Core 76／UI 4／Plugin 58 passed, 0 failed. Native slot-order, legacy JSON slot migration, and legacy `"all"`-without-marker-frame regressions are included. The scope audit found no Dalamud reference in Core and no history／recent／retention／compare, persistent latest Pull, cross-session restore, Debrief, or inferred wipe cause.
- `ReplayEndToEndTests` synthetic smoke passed 2／2. P10S recorded verification rendered 379 scenes, advanced 378 frames, and completed 20,000 seeks at 0.014472 ms average／0 bytes per seek. Primary eight-player verification rendered 2,234 scenes, advanced 2,233 frames, and completed 20,000 seeks at 0.019615 ms average／0 bytes per seek. Real Marker Capture `c1a72067-1783-4fd3-9c9c-5dc97f71ed1d` rendered 1,320 scenes, advanced 1,319 frames, and completed 20,000 seeks at 0.018908 ms average／0 bytes per seek.
- Final fresh `Debug -p:Platform=x64 --no-incremental` build completed with 0 warnings／0 errors. The configured Dev Plugin DLL was 699,392 bytes with `LastWriteTime` 2026-08-10 22:01:10.560637 +08:00 and SHA-256 `066901a38d07345c184c0d4432621a560d2ff0703ae1ad8db7bc378b374f417f`; the adjacent Core DLL was 105,984 bytes with `LastWriteTime` 22:01:10.106232 and SHA-256 `334e12c01bdf02f6b98f710c0ebf0bda048aece2052c5446ef3ffdb5bacae261`. Dalamud logged unload at 22:01:11.205, load at 22:01:11.216, all 17 Target Marker textures at 22:01:11.514, and completed Plugin load at 22:01:11.514.
- Real-game Marker evidence: manual Capture `c1a72067-1783-4fd3-9c9c-5dc97f71ed1d` started at 21:28:07.809 and finalized／validated at 21:30:19.695 with 1,319 frames, 1,799 events, 912 Action Effects, and 7 state-change-only Target Marker frames containing assignment, removal, and reassignment. Runtime LastCompletedPull loaded the same CaptureId at 21:30:20.149. After the native-order correction and hot reload, Developer/Test reloaded that same fresh Capture at 21:57:45.415; manual in-game observation confirmed Bind／Stop icon identity and placement above the original Actors. The real DutyWiped prompt route is independently covered by the Item 5 acceptance evidence.
- Map-canvas regression removes every Territory／Map arena profile from production. The Dalamud host reads 1,232 usable Lumina `Map` rows once, derives complete world bounds and background UVs from each row's `SizeFactor／OffsetX／OffsetY／texture ID`, and injects that Core-only projection into both Runtime and Developer/Test `ReplaySession` construction. Core retains a neutral observed-field fallback and has no Dalamud／Lumina dependency.
- For Map 834 `n5ra/00`, the row values `SizeFactor=400, OffsetX=-100, OffsetY=-100` resolve the central valid texture canvas to world bounds `-28..228`. Automated checks cover Map path construction, bidirectional world／texture projection, complete canvas bounds, clipping, invalid rows, injected Core bounds, initial observed-area focus, minimum zoom, pan limits, Reset state, and a 20x maximum.
- Manual in-game observation after the 23:33:54 hot reload confirmed the same Capture's background and recorded positions align, the initial observed-area focus is useful, zoom cannot go below that initial view, panning cannot leave it, Reset restores it, and zoom reaches 20x. Dalamud logged the 1,232-row catalog load and completed Plugin load without a RaidDebrief UI error.
- Final Map-canvas automated verification passed Core 77／UI 4／Plugin 67 tests with 0 failures. The P10S recorded fixture loaded, rendered 379 scenes, advanced 378 frames, and completed 20,000 deterministic seeks at 0.013730205 ms average／0 bytes per seek. Source audit found no Dalamud／Lumina reference in Core and no production Territory／Map arena profile.
- The final fresh `Debug -p:Platform=x64 --no-incremental` build completed with 0 warnings／0 errors. The configured Dev Plugin path still targets `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`; the DLL was 714,752 bytes with `LastWriteTime` 2026-08-10 23:39:46.1257233 +08:00 and SHA-256 `0c35abd4ac4b224a135c68de60cb237477ca83b6db72067104d8c8300105f318`. Dalamud logged unload at 23:39:46.725, load at 23:39:46.734, the 1,232-row Map catalog at 23:39:46.916, and completed Plugin load at 23:39:46.917.
- Native Waymark rendering maps A／B／C／D／1／2／3／4 to game icons `61241／61242／61243／61247／61244／61245／61246／61248` and acquires all eight shared textures once through `ITextureProvider.GetFromGameIcon`. The image quad is centered on the recorded X／Z position; missing／not-ready textures keep the labeled fallback. A-D use a 1.25-world-unit radius and 1-4 a 2.3-world-unit square edge, both scaled by the same Map bounds and viewport zoom.
- Automated verification passed Core 77／UI 4／Plugin 69 tests with 0 failures, including exact icon identity, the non-contiguous D／1 mapping, centered bounds, letter／number world dimensions, and proportional zoom. The fresh x64 Debug build completed with 0 warnings／0 errors; the configured DLL was 716,800 bytes with `LastWriteTime` 2026-08-11 00:10:11.0397427 +08:00 and SHA-256 `dabfcef3dca082a98becfb4a9bee0feddbc28030c8d92e8c247024da773df75c`. Dalamud logged load at 00:10:11.657, the eight native icons and dimensions at 00:10:11.896, and completed Plugin load at 00:10:11.897. Manual replay of a fixture containing all eight Waymarks confirmed correct icon identity, center alignment, and scale.
- Omnidirectional Target Ring capture now caches 4,216 `BNpcBase.IsOmnidirectional` rows from Lumina and combines that static flag with active `Directional Disregard` status `3808` on the Framework thread. The combined boolean is recorded per Actor sample, carried through Core resolution, and selects the complete 480×480 `TargetRing.png` only for BattleNpc markers. Players retain the directional ring. Legacy Captures use only their recorded BaseId for static fallback and receive an explicit dynamic-state warning.
- Automated verification passed Core 78／UI 4／Plugin 74 tests with 0 failures. Coverage includes static/status resolution precedence, Player exclusion, per-sample transition and reverse seek, Capture propagation, JSON round-trip, current-versus-legacy texture selection, both embedded PNG dimensions, and distinct outer-radius ratios. P10S recorded verification rendered 379 scenes, advanced 378 frames, and completed 20,000 deterministic seeks at 0.013550945 ms average／0 bytes per seek.
- The fresh `Debug -p:Platform=x64 --no-incremental` solution build completed with 0 warnings／0 errors. The configured Dev Plugin path is `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`; the DLL was 911,872 bytes with `LastWriteTime` 2026-08-11 00:36:48.1107789 +08:00 and SHA-256 `da413f0ab17bab2768134da631463312d0740806936a955027b01af57777dcd7`. Dalamud logged unload at 00:36:48.716, load at 00:36:48.724, the 4,216-row BNpcBase catalog at 00:36:48.924, both embedded Target Ring textures at 00:36:48.935, and completed Plugin load at 00:36:48.936.

### Phase 3 Final Closeout

- The Wipe prompt controller now retains every newly validated `DutyWiped` generation that completes before `InCombat` clears, then offers that exact Pull once on the first out-of-combat UI update. The persisted, default-enabled prompt setting clears queued／visible prompts when disabled and never backfills Wipes completed while disabled.
- Historical Phase 3 contract, superseded by the 2026-08-14 entry-edge rule below: the persisted, default-enabled combat auto-close setting preserved pause／cancel／close behavior; explicit opt-out kept an existing Replay window visible and paused; re-enabling during combat previously closed it on the next UI update.
- Final Phase 3 verification passed Core 78／UI 4／Plugin 91 tests with 0 failures; focused combat gate／frame policy／settings coverage passed 23 tests and retained 0-byte policy allocation. The fresh x64 Debug solution build completed with 0 warnings／0 errors. The configured 914,432-byte DLL had `LastWriteTime` 2026-08-11 22:42:25.9193460 +08:00 and SHA-256 `2cc98efb2cdb7ff621157e213ea2ecf825d15b7c2efc683440b52bfab1b7d1dc`; Dalamud logged unload at 22:42:26.439, load at 22:42:26.459, and successful Plugin completion at 22:42:26.714 with no RaidDebrief UI error.
- Scope closeout: Runtime still retains only the active Pull and latest successful in-memory completed Pull. Phase 3 added no persistent history／recent／retention／compare, cross-session restore, Debrief analysis, mechanic inference, blame, session statistics, or Core dependency on Dalamud. The complete working-progress snapshot is archived at `docs/archive/progress-phase-3-2026-08-11.md`.

## Roadmap Phase 4 — Debrief MVP

### Implementation and Automated Evidence

- Core now owns `PullTiming`, immutable Debrief contracts, and `DebriefAnalyzer`. Recorded player Death events retain timestamp／original-index ordering and repeat after raise; missing actor links stay explicit; Boss HP is unavailable unless exactly one valid replay-visible non-player-owned BattleNpc remains; suggested ranges are clamped objective windows rather than inferred causes.
- Automatic Pull finalization publishes `PullRecord + DebriefSummary + end reason + completed generation` atomically after validation and analysis. Process-local ordinals are assigned at automatic Pull start. Validation／analysis failures preserve the previous complete handoff and consume no persistent storage.
- The former Wipe Replay prompt was cleanly replaced by one compact post-Wipe `DebriefSummaryWindow`. It shows Pull number, shared duration, objective Boss HP／unavailable state, First Death with the existing Job icon, complete death sequence, Wipe timestamp, and the suggested range. The default-enabled setting keeps its legacy serialized JSON key but exposes only the new “Wipe 後顯示 Debrief 摘要” contract.
- One-click Replay requests carry exact completion generation, CaptureId, and suggested range. The formal Runtime path rejects identity supersession before and after background session construction, seeks the matching session to the suggested start, remains paused, and displays the range. No JSON round-trip, repository lookup, persistent latest, history, or cross-session fallback was added.
- Final automated verification passed Core 84／UI 4／Plugin 93 tests with 0 failures; focused Phase 4 verification passed Core 6 and Plugin 20 tests. Formatting verification passed. The main recorded fixture still rendered 2,234 scenes, advanced 2,233 frames, and completed 20,000 deterministic seeks at 0.02629414 ms average／0 bytes per seek.
- Direct analysis of real `DutyWiped` Capture `c1a72067-1783-4fd3-9c9c-5dc97f71ed1d` produced duration 131,883 ms, Wipe 120,679 ms, Boss ケフカ at 16,531,144／44,109,275 HP (37.4777%), eight ordered Player deaths, zero unresolved deaths, and suggested range 104,706..120,679 ms. This Capture has no Raise／re-death, so it is evidence for real multi-death／Boss-HP analysis but does not satisfy final Phase 4 in-game acceptance by itself.
- The fresh `Debug -p:Platform=x64 --no-incremental` build completed with 0 warnings／0 errors. The configured Dev Plugin path targets `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`; the 923,648-byte DLL has `LastWriteTime` 2026-08-11 23:31:07.7899372 +08:00 and SHA-256 `89928c1b3239d11d88e0fc5fbfb158ed18085c97f7ba53d8a49781b9db8bb378`. Dalamud logged unload at 23:31:08.310, load at 23:31:08.370, and successful Replay／Debrief window completion at 23:31:08.938 with no RaidDebrief UI error.
- Death correlation algorithm v2 removes the false content-synchrony assumption between Action Effect callback timestamps and ObjectTable HP samples. It anchors on the last living sample, chooses the latest ordered effect suffix that crosses zero, includes effects observed before that sample timestamp, calibrates a bounded Pull-local lag threshold from unique exact player HP transitions, and blocks High confidence for barrier absorption or excessive observation lag. Two focused delayed-HP regressions pass; full automated verification passed Core 99／UI 4／Plugin 138 with 0 failures.
- The x64 Debug solution build succeeded and refreshed `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`. Canonical fixture `20260810_DMU.json` loaded and rendered 5,885 SVG scenes, advanced all 5,884 frames, and completed 20,000 deterministic seeks at 0.02660664 ms average／0 bytes per seek.


### Remaining Final Acceptance

- Obtain one new real `DutyWiped` Capture containing multiple player deaths, resolvable Boss HP, and at least one raise followed by a repeated death. In game, verify Wipe → Summary → exact one-click suggested-start Replay and next-Pull combat non-interference. Until that external gameplay evidence exists, Phase 4 remains `Acceptance Pending`, not `Completed`.

## Roadmap Phase 5 — Replay UX

### Current Functional Surface

- The formal Replay UI now exposes playback／scrubbing, a complete recorded Player Death list, selected-player Focus, and the arena. A death button seeks to the exact recorded timestamp and pauses.
- Historical Phase 5 surface, superseded by the 2026-08-14 redesign below: colored Timeline markers, recent events, active-status and timestamp-vitals detail, and non-Death jump categories had been removed. Runtime and manual JSON controls remained collapsed under the advanced development section.
- Focus changes opacity only. Above the Map-derived minimum zoom, the camera follows the visible selected player within the established safe pan boundary; temporary absence retains the last center and manual arena dragging cancels Focus.

### Verification

- Focused Replay UX verification passed 11／11 tests. The full suite passed Core 84／UI 4／Plugin 94 with 0 failures, and `dotnet format --verify-no-changes` passed.
- The main synthetic fixture loaded and rendered 2,234 scenes, advanced 2,233 frames, and completed 20,000 deterministic seeks at 0.0196602 ms average／0 bytes per seek.
- The fresh `Debug -p:Platform=x64 --no-incremental` build completed with 0 warnings／0 errors. The configured 925,696-byte DLL had `LastWriteTime` 2026-08-12 00:31:56.0001492 +08:00 and SHA-256 `2753d7aae786334d1a2656abd1dbb3879dfbdef12b3bd83526eea457eb3ebb00`; Dalamud logged unload at 00:31:56.615, load at 00:31:56.643, and successful window load at 00:31:56.925.
- In-game verification confirmed the implemented behavior is normal. The current compact presentation is accepted for this scope; overall layout and HP presentation are deferred to a separate whole-interface redesign.

## 2026-08-14 Capture and Replay Technical Refresh

### Implemented contract

- Capture now uses a two-stage Framework contract: `BeginFrameworkUpdate` advances lifecycle and prepares the active Pull's single 100 ms sample; `LiveDataProbe` scans Party／ObjectTable／StatusList only when Capture or an in-duty open Probe requires it; `SubmitFrameworkSample` commits that managed snapshot. Pull start samples in the same callback, late callbacks record one real sample plus gaps, cancellation does not advance cadence, and overlapping preparations are rejected.
- The open Developer Probe refreshes at 10 Hz outside recording only while in a duty instance. Closed Probe＋idle Capture performs no full scan. A Capture-owned sample and Probe demand share one scan. StatusList is obtained once per Actor scan, Actor names are cached by ObjectTable slot＋GameObjectId, volatile Actor failures are isolated and counted, and Event／Action Effect diagnostic strings are formatted lazily.
- Current Captures record `BarrierPercentage` under `CaptureFeatures.BarrierState` and one sorted Pull-local `RecordedActionName` per resolved observed Cast Action ID under `ActionNameSnapshot`. Resolution priority is resolved localized Action sheet, Client RSV, then entity-matched enemy CastBar; unresolved names retry at bounded 500 ms intervals and produce no snapshot if all attempts fail. Replay prefers the recorded name and legacy JSON remains load-compatible.
- Replay now presents role-ordered Party rows with Job icon, timestamp HP values／percentage, dead state, health bar, and a left-anchored barrier overlay; the context shows Actor vitality, barrier availability, recorded defensive／barrier／invulnerability effects, optional HoT effects, active-Boss damage-down debuffs, native icons, remaining time, and stacks. Targetable enemy HUD groups show HP percentage and a conditional recorded Cast row. Timeline and Quick Jump remain player-Death-only.
- Combat auto-close is an entry-edge action. Auto-close enabled hides and pauses active Replay on combat entry; disabled keeps it visible but paused. Changing the setting after combat has begun does not synthesize a second entry action. Explicit opens remain rejected throughout combat.
- Death correlation algorithm v2 anchors on the last living HP observation, admits effects whose callback timestamp precedes that sample, selects the latest ordered suffix crossing zero, calibrates a bounded Pull-local observation-lag threshold, and includes recorded barrier state in the estimated effective pool. Barrier accounting and excessive lag remain explicit confidence limitations.

### Automated and offline evidence

- Fresh individual project runs passed `RaidDebrief.Core.Tests` 99／99, `RaidDebrief.UI.Tests` 4／4, and `RaidDebrief.Plugin.Tests` 169／169 with 0 failures. Focused synthetic `ReplayEndToEndTests` passed 2／2. `dotnet format RaidDebrief.sln --verify-no-changes --no-restore` passed, and `git diff --check` reported no whitespace errors.
- `testdata/recorded/20260810_DMU.json` rendered 5,885 scenes, advanced all 5,884 frames, and completed 20,000 deterministic seeks at 0.02354187 ms average and 0 bytes per seek.
- `testdata/recorded/6fe1b80f-567a-41a3-8912-6d013c137aa7.json` rendered 2,234 scenes, advanced all 2,233 frames, and completed 20,000 deterministic seeks at 0.017818665 ms average and 0 bytes per seek.
- `testdata/recorded/P10S.json` rendered 379 scenes, advanced all 378 frames, and completed 20,000 deterministic seeks at 0.01292965 ms average and 0 bytes per seek.
- The fresh `Debug -p:Platform=x64 --no-incremental` build completed successfully. The authoritative DLL is `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`, 998,912 bytes, `LastWriteTime` 2026-08-14 22:16:33.3553004 +08:00, SHA-256 `c6ffa06c57bb89fd2d1db4182bb98f680db236dbad2398b6ac42fd554daeda51`. Dalamud configuration points to that exact path; unload began at 22:16:33.933, reload began at 22:16:33.963, and Plugin load completed at 22:16:34.266 after the DLL timestamp.
- Source audit confirmed `RaidDebrief.Core.csproj` has no external／project reference and the Core tree contains no Dalamud or Lumina reference. Scope audit found no persistent Pull history, compare, DPS／rotation analysis, blame, mechanic guidance, or session analytics.

### Remaining current-build acceptance

- Post-reload automatic Pull `66043fb5-d46b-48ba-9048-a25f53190b2a` started at 22:19:30.835 in Territory 1363／Map 79, finalized after `DutyWiped` at 22:33:24.844 with 8,341 frames, 14,156 events, and 6,262 Action Effects, and completed validation at 22:33:24.890. Runtime Replay loaded that exact CaptureId at 22:36:01.728 with the same 8,341 frames and 14,156 events, proving current-build finalization, publication, and in-memory handoff without JSON. Barrier values, Action-name snapshots, exact sampling metrics, and Actor completeness have not yet been inspected.
- Existing Replay／Probe screenshots were captured before the accepted DLL build. They are visual development evidence only, not fresh current-build visual acceptance.
- The current-build Pull isolated one volatile ObjectTable Actor `NullReferenceException` at slot 15 and continued to successful finalization; a pre-final-build Pull similarly isolated one slot 71 failure. The per-Actor containment path is observed, but Actor continuity still requires output inspection.
- Comparative in-game callback allocation, GC, and frame-time profiling has not been recorded. The code removes known high-frequency work, but no “zero stutter” causal or completion claim is accepted.
- Phase 4 still requires the documented real `DutyWiped` scenario with multiple player deaths, resolvable Boss HP, and a raise followed by repeated death, followed by exact Summary → suggested-start Replay and next-Pull non-interference. Phase 5 still requires fresh current-build visual acceptance.


## Original Development Record

The following content is the complete pre-close `progress.md` record retained verbatim.

---

# Raid Debrief 開發進度

最後更新：2026-08-09

## 狀態定義

- `已完成`：功能已實作，且完成對應的建置與遊戲內驗證。
- `進行中`：目前正在實作或等待本階段測試結果。
- `待開始`：前置階段尚未通過，或尚未開始實作。
- `阻塞`：因外部資訊、Dalamud／遊戲版本或無法取得的資料而不能繼續。

## 整體進度

| 階段 | 名稱 | 狀態 | 完成證據 |
|---|---|---|---|
| 0 | Dalamud 插件載入驗證 | 已完成 | Debug／Release 建置成功；Dalamud 遊戲內載入成功；`/rdebrief` 與 ImGui 視窗正常 |
| 1 | 公開 API 即時資料探針 | 已完成 | Debug／Release 建置與格式檢查通過；Dalamud 遊戲內資料讀取正常 |
| 2 | 手動擷取與 JSON | 已完成 | Core 9 項測試、Debug／Release 建置、安全區與解除限制副本實際擷取通過 |
| 3 | 輪詢型事件 | 已完成 | Core 16 項測試；木人、Death／Wipe／Recommence 與 Duty start／complete 實測通過 |
| 4A | Waymark 擷取 | 已完成 | Core 20 項測試、只讀 API audit、Debug／Release 建置與八 Waymark 遊戲內擷取通過 |
| 4B | Action Effect 擷取 | 已完成 | Core 24 項測試、x64 Debug／Release 建置、單體／AoE／八人群療、DoT／HoT 資料來源與 reload／dispose 實測通過 |
| 5 | 自動 Pull 生命週期 | 已完成 | Core 36 項＋Plugin 9 項測試、x64 Debug／Release 建置、正常脫戰、連續 Pull、Wipe／Clear、Recording reload 與 Boss 型態轉換遊戲內驗證通過 |
| 6 | 真實八人 Pull 驗證 | 進行中 | 玩家匿名化、Content ID 缺席與 Capture repository 契約已完成；Core 40＋Plugin 10 項測試通過，等待真實八人高難度 Pull |

目前階段：**階段 6 — 真實八人 Pull 驗證（進行中）**

---

## 階段 0 — Dalamud 插件載入驗證

狀態：`已完成`

### 目標

建立最小可載入的 Dalamud 插件，確認開發環境與基本整合正常。

### 已完成

- 建立 `RaidDebrief.sln`。
- 使用 `Dalamud.NET.Sdk/15.0.0` 建立 `RaidDebrief.Plugin`。
- 建立 `RaidDebrief.json` manifest。
- 註冊 `/rdebrief` 指令。
- 建立 ImGui 導入測試視窗。
- 顯示插件版本、登入狀態與 Territory ID。
- 正確解除註冊指令與 UI callback。
- 產生 Debug DLL 與 Release `latest.zip`。

### 驗證紀錄

- `dotnet build RaidDebrief.sln --configuration Debug`：通過。
- `dotnet build RaidDebrief.sln --configuration Release`：通過。
- `dotnet format RaidDebrief.sln --verify-no-changes --no-restore`：通過。
- Dalamud Dev Plugin 成功辨識並啟用 `RaidDebrief.dll`。
- 遊戲內 `/rdebrief` 成功開啟測試視窗。
- 視窗成功讀取登入狀態與 Territory ID。

---

## 階段 1 — 公開 API 即時資料探針

狀態：`已完成`

### 目標

只使用 Dalamud 公開 API 即時讀取資料並顯示於測試視窗；本階段不保存 Pull，也不使用 unsafe hook。

### 預定資料來源

- `IFramework.Update`：Framework 更新與取樣時機。
- `ICondition`：`ConditionFlag.InCombat`。
- `IClientState`：登入、Territory、Map 與 Instance。
- `IPartyList`：Party 成員與隊伍資料。
- `IObjectTable`：本機玩家、其他玩家與 Battle NPC。
- `IGameObject`：ID、類型、位置、旋轉、死亡、可選取狀態與 hitbox。
- `ICharacter`：HP、MP、職業與等級。
- `IBattleChara`：Cast 與 Status 資料。

### 預定 UI

- Local Player 狀態。
- Party 人數與成員摘要。
- ObjectTable Actor／Battle NPC 數量。
- 選定 Actor 的 Entity ID、GameObject ID、Data ID、Base ID。
- Position、Rotation、HP、死亡與可選取狀態。
- Cast action、進度與目標。
- Status 數量。
- `InCombat` 狀態。
- Framework probe 執行時間。

### 已實作

- 建立 `LiveDataProbe`，由 `IFramework.Update` 在 Framework thread 擷取純值快照。
- 接入 `ICondition`、`IClientState`、`IPartyList` 與 `IObjectTable`。
- 擷取登入、Territory、Map、Instance、`InCombat` 與 Framework callback 執行時間。
- 擷取 Party 成員名稱、ID、職業、等級、HP、MP、位置與隊長狀態。
- 擷取 Player／Battle NPC 數量，以及 Actor 的 ID、位置、旋轉、hitbox、HP、MP、死亡與可選取狀態。
- 擷取 `IBattleChara` 的 Cast action、進度、目標、可中斷狀態與有效 Status 數量。
- `/rdebrief` 視窗提供 Local Player、Party、Actor 選擇與選定 Actor 詳細資料。
- Framework callback 不建立背景工作；例外記錄已節流，避免每幀重複輸出相同錯誤。

### 已知限制

- Dalamud 15 已將 `IGameObject.DataId` 標記為 `BaseId` 的過時別名；探針目前在 Data ID 與 Base ID 欄位顯示相同的公開 `BaseId` 值。
- 2026-08-09 已於 Dalamud 遊戲內確認即時資料可正常讀取，未觀察到持續錯誤。

### 遊戲內測試

1. 在安全區開啟 `/rdebrief`，確認本機玩家資料。
2. 移動與旋轉，確認 Position／Rotation 即時變化。
3. 前往木人並進入戰鬥。
4. 使用讀條技能並中斷一次讀條。
5. 使用疾跑或其他 Status，確認 Status 數量變化。
6. 停止攻擊並等待脫戰，確認 `InCombat` 轉換。

### 完成條件

- 插件可建置並重新載入。
- Local Player、Party 與 Battle NPC 資料合理。
- Position、Rotation、HP、Cast、Status 與 `InCombat` 能反映遊戲畫面。
- 沒有使用背景執行緒讀取 live game object。
- 沒有明顯遊戲卡頓或持續錯誤。

---

## 階段 2 — 手動擷取與 JSON

狀態：`已完成`

### 目標

加入手動開始／停止、10 Hz 取樣、Actor registry、版本化 JSON 匯出與重新載入驗證。

### 預定內容

- 建立不依賴 Dalamud 的 `RaidDebrief.Core` models。
- `schemaVersion: 1`。
- 手動「開始擷取」與「停止並匯出」。
- Position、Rotation、HP 與 Actor state 以 100 ms 間隔取樣。
- 使用單調時鐘產生 Pull 相對時間戳。
- 建立 Pull 內部 `StableActorId`。
- 遊戲執行緒只複製純值資料。
- 停止後在背景序列化 JSON。
- UI 顯示 sample、actor、gap 與 callback time counters。
- 提供透過 Capture repository 載入 Latest 或指定 CaptureId 的驗證路徑。

### 已實作

- 建立獨立的 `RaidDebrief.Core`，僅依賴 .NET，並定義 `schemaVersion: 1` 的 `PullRecord`、Actor 與 Position frame models。
- 建立手動開始、停止與 CaptureId 獨立 JSON 匯出流程。
- 使用 `Stopwatch` 單調時鐘，以 100 ms 排程在 Framework thread 擷取純值 Actor snapshot。
- 以 Pull 內 `StableActorId` registry 避免同一 `GameObjectId` 重複註冊。
- 過濾無效 GameObject ID、非有限 Position／Rotation，以及 Current HP 大於 Max HP 的 sample。
- 停止後由背景工作執行 JSON 驗證、序列化與原子檔案替換。
- UI 顯示 sample、actor、gap、rejected sample、平均間隔、callback 與 serialization time counters。
- UI 可透過 repository 背景載入 Latest Capture，並顯示 CaptureId、schema、actor、sample、duration 與平均取樣間隔；程式入口亦可指定 CaptureId。
- 加入 9 項 Core 測試，涵蓋 JSON round-trip、離線檔案載入、時間戳、100 ms cadence、有限座標、HP、Actor identity 與 Dalamud dependency。

### 驗證與限制

- 2026-08-09 已完成 Dalamud 安全區與解除限制副本實際擷取；兩次輸出皆通過 JSON 重新載入與離線資料驗證。
- persistence 已遷移為 `captures/<CaptureId>.json` 與 deterministic `capture-index.json`；不覆寫 Pull 檔，也沒有特殊 latest 檔案。

### 遊戲內測試

1. 在安全區開始擷取。
2. 原地等待、移動、旋轉約 30 秒。
3. 停止並匯出 JSON。
4. 使用插件重新載入該 JSON。
5. 檢查 actor、frame、時間戳與取樣間隔。
6. 在解除限制的低等副本重複測試一次。

### 完成條件

- 所有時間戳單調不倒退。
- 平均取樣間隔約 100 ms。
- Position 不含 NaN／Infinity。
- HP 範圍合理。
- Actor 不會在每次取樣時重複註冊。
- JSON 可以離線重新讀取。
- `RaidDebrief.Core` 不依賴 Dalamud。

---

## 階段 3 — 輪詢型事件

狀態：`已完成`

### 目標

從公開狀態的前後差異建立客觀事件，不加入 damage／heal hook。

### 預定事件

- Cast start。
- Cast end。
- Cast interrupted。
- Death。
- Alive transition。
- Status gain。
- Status loss。
- `InCombat` transition。
- Duty start／wipe／recommence／complete。
- Actor spawn／despawn。

### 原則

- 每個事件保存來源，例如 `PolledCastState` 或 `DutyState`。
- 只觀察到 `IsDead: true → false` 時記錄 `AliveTransition`，不可直接宣稱為 Raise。
- 不推論死亡原因、機制錯誤或玩家責任。

### 已實作

- 新增 `ObservedEvent`、`ObservedEventType` 與 `ObservedEventSource`；事件以 additive `events` 欄位加入 `schemaVersion: 1`，舊階段 2 JSON 缺少此欄位時仍可載入。
- 建立不依賴 Dalamud 的 `PolledEventDetector`，以相鄰 10 Hz snapshot 產生 Cast、Death／Alive、Status、`InCombat` 與 Actor spawn／despawn 事件。
- 初始 snapshot 只建立 baseline，不將 Capture 開始時已存在的 Actor、Cast 或 Status 誤記為新事件。
- Cast 消失時依最後觀察到的進度分類為 `CastEnded` 或 `CastInterrupted`；action 切換會先結束前一個 Cast，再記錄新 Cast。
- Status identity 使用 status ID 與來源 object ID，避免不同來源的同一 Status 互相覆蓋。
- 接入公開 `IDutyState` 的 start、wipe、recommence 與 complete callbacks；只在手動 Capture 期間保存。
- 每個事件保存明確 observation source；死亡恢復只記為 `AliveTransition`，資料模型沒有 Raise、死亡原因、機制錯誤或玩家責任欄位。
- Capture UI 顯示事件總數與最後事件，重新載入摘要顯示事件數。
- Core 測試增至 16 項，涵蓋事件順序、去重、Cast 完成／中斷、spawn／despawn、死亡恢復 identity、JSON round-trip 與舊 Capture 相容性。

### 已知限制

- 事件 detector 使用 10 Hz snapshot；在兩次取樣之間開始且結束、持續時間短於約 100 ms 的 Cast、Status 或 Actor 存在狀態可能完全遺失。
- `CastEnded`／`CastInterrupted` 依 Cast 消失前最後進度與 150 ms 容差分類；極接近完成時間的取消可能分類不準確，因此來源明確標記為 `PolledCastState`。
- Actor spawn／despawn 表示 ObjectTable 在相鄰 snapshot 中的存在狀態轉換，不代表其遊戲機制原因。
- Duty lifecycle 由 `IDutyState` callback 取得；若事件發生時未進行手動 Capture，不會補記。
- Wipe 後觀察到 47 個 NPC ObjectTable entry 同時 despawn，隨後以新 GameObject ID／StableActorId spawn；目前只記錄客觀 lifecycle，不推論新舊 NPC 為同一 Actor。玩家死亡與恢復則維持同一 StableActorId。
- `DutyStarted` 必須在 callback 發生前開始 Capture；已透過進本前開始 Capture 完成驗證。
- 手動 Capture 若在進本前開始，`PullRecord` 的 Territory／Map metadata 保留 Capture 開始位置；事件與 Actor snapshot 仍會持續記錄跨區域狀態。自動 Pull 階段需在 Duty／Pull 開始時建立正確 metadata。

### 遊戲內測試

- 木人讀條、完成與中斷。
- Status 取得與消失。
- 解除限制副本中的死亡與 Wipe。
- 若有協助者，測試兩人依序死亡及一人復活。

### 完成條件

- 事件順序與人工記錄一致。
- 沒有相同事件重複產生。
- Actor identity 在死亡與恢復後仍一致。
- 輪詢可能遺失的短事件有明確註記。

---

## 階段 4A — Waymark 擷取

狀態：`已完成`

### 目標

透過 FFXIVClientStructs 只讀取 A、B、C、D、1、2、3、4 Waymark 狀態。

### 預定內容

- 使用 `MarkingController.Instance()->FieldMarkers`。
- 只讀取 `Active` 與 `Position`。
- 不呼叫放置、移動或清除 Waymark 的遊戲函式。
- Waymark reader 與公開 API capture 分離。
- Waymark reader 失效時不能中斷其他擷取。

### 已實作

- 新增獨立 `WaymarkReader`，在 Framework thread 透過 `MarkingController.Instance()->FieldMarkers` 讀取八個 marker。
- Reader 只存取 `FieldMarker.Active` 與 `FieldMarker.Position`；程式碼未引用 `PlaceFieldMarker`、`PlacePreset`、`ClearFieldMarker` 或 `ClearFieldMarkers`。
- Reader 驗證 controller pointer、FieldMarkers 數量與有限座標；失敗時標記 unavailable、節流記錄錯誤並讓 Actor／事件 Capture 繼續。
- Core 新增 `WaymarkId`、`WaymarkState`、`WaymarkFrame` 與 `WaymarkTimelineBuilder`；`waymarkFrames` 以 additive 欄位加入 `schemaVersion: 1`，舊 JSON 仍可載入。
- Timeline 保存初始八個 marker 狀態，之後只在 Active 或 Position 改變時新增 frame。
- Capture UI 顯示 reader 可用性、失敗次數、Waymark frame 數與目前啟用 marker 的世界座標。
- Core 測試增至 20 項，涵蓋初始無 Waymark、放置、移動、清除、去除未變狀態、有限座標、八 marker identity、JSON round-trip 與舊 Capture 相容性。

### 已知限制

- `FieldMarker.Position` 在 `Active: false` 時可能保留先前座標；是否存在 marker 必須以 `Active` 為準。
- Reader 依賴目前編譯的 FFXIVClientStructs layout。Runtime 會檢查 null pointer、marker 數量與座標，但若上游 ABI 在未更新 assembly contract 的情況下改變，仍需重新建置與遊戲內驗證。

### 遊戲內測試

1. 戰鬥前放置多個 Waymark。
2. 開始擷取。
3. 依序走到各 Waymark 附近。
4. 修改或清除 Waymark後重新擷取。
5. 比較 Waymark 與玩家世界座標。

### 完成條件

- Active 狀態與遊戲畫面一致。
- Position 與實際位置一致。
- 無 Waymark 的區域不會崩潰。
- Dalamud／FFXIVClientStructs 更新後可安全判定 reader 是否可用。

---

## 階段 4B — Action Effect 擷取

狀態：`已完成`

### 目標

研究並驗證 Action、Damage、Heal 與其他 Action Effect。這是獨立、可停用且版本敏感的功能。

### 預定內容

- 使用 `IGameInteropProvider` 建立受控 hook。
- 優先研究已解碼的 `ActionEffectHandler.Receive`，避免直接解析原始網路封包 opcode。
- 記錄 action ID、source、targets 與 effect entries。
- 明確區分 damage、heal、miss 與其他效果。
- Hook 失效時降級為沒有 Action Effect，不影響位置與狀態擷取。

### 已完成實作

- 透過 `IGameInteropProvider` hook FFXIVClientStructs 已解碼的 `ActionEffectHandler.Receive`，不解析原始網路 opcode。
- 每個 callback 保存 global sequence、action ID／type、完整 source／animation target GameObject ID，以及最多 32 個 targets。
- 每個 target 保存最多八個非空 effect entries；保留 raw type、五個 params 與 value，並解碼 damage／blocked damage／parried damage、heal、miss、amount、critical 與 direct hit。
- Action Effect 以 additive `actionEffects` 欄位加入 `schemaVersion: 1`；舊 Capture 缺少欄位時仍載入為空陣列。
- Capture 完成時以完整 GameObject ID 回填 source／target stable actor ID；無法對應的外部 Actor 保留原始 GameObject ID 與空 stable ID。
- Hook address 無法解析或 callback layout 不相容時，只停用 Action Effect 解碼並顯示明確錯誤；位置、狀態與 Waymark 擷取繼續運作。
- Plugin dispose 先停用並解除 hook，再 dispose Capture service，避免 reload 後 callback 指向已釋放服務。
- UI 顯示 hook availability、batch 數、decode failures、最後 Action Effect 與離線載入數量。

### 目前驗證

- Core 24/24 測試通過，涵蓋 effect 分類／amount 與旗標解碼、多 target／多 entry JSON round-trip、raw／decoded 一致性、重複 target 拒絕與舊 `schemaVersion: 1` 相容性。
- Debug 與 Release plugin 建置皆為 0 warnings／0 errors。
- 木人單體、AoE 多目標、直接自我治療、八人群體治療與 reload／dispose 已通過；DoT／HoT 初始套用與週期 tick 限制已確認。Miss／無效效果無安全重現場景，raw type 1 的分類由 Core 測試覆蓋。

### 已確認限制

- `ActionEffectHandler.Receive` 會保存 DoT／HoT 的初始 status application，但本次 22.32 秒 Capture 未收到任何週期 damage／heal tick；週期 HP 效果不經此 callback，若產品需要每次 tick 的數值，必須另行研究 `EffectResult` 或其他客觀資料來源。

### 遊戲內測試

- 木人單體技能。
- AoE 多目標技能。
- 自我治療與他人治療。
- Miss／無效效果（若可安全重現）。
- 多段攻擊與 DoT／HoT 是否會產生預期事件。

### 完成條件

- Source、target 與 action ID 可正確對應。
- 多目標與多 effect 不會遺失或重複。
- Hook 可在 plugin dispose／reload 時安全解除。
- 版本不相容時安全停用並輸出明確錯誤。

### 完成判定

- 現行遊戲版本可解析 Hook，單體、八人多目標、damage、heal 與其他 effect entries 均有遊戲內證據。
- Source、target、action ID、global sequence、critical／direct hit 與 stable actor identity 均通過離線驗證。
- 多 target／多 entry 無缺失或重複；所有實測 Capture 均通過 Core validator。
- Reload 明確記錄舊 Hook disable／dispose 後才建立新 Hook；reload 後三次跨職業治療只產生三個唯一 batches。
- 位址或 callback layout 不相容時的安全停用與明確錯誤路徑已實作；位置、狀態與 Waymark 擷取不依賴此 Hook。
- Miss／無效效果因無安全重現場景未做遊戲內測試；此項原規格為條件式測試，不阻擋完成。

---

## 階段 5 — 自動 Pull 生命週期

狀態：`已完成`

### 目標

根據前面實測結果，以 `InCombat`、Duty events 與 debounce 建立自動 Pull state machine，同時保留手動模式。

### 預定狀態

```text
Idle
  → Recording
  → Finalizing
  → Saved
  → Idle
```

### 需要處理

- 正常開怪。
- Wipe。
- Clear。
- 脫戰後重新開怪。
- Boss 暫時消失／重生。
- 階段轉換。
- 玩家死亡或斷線。
- Plugin reload。
- 連續多個 Pull。

### 已完成實作

- 新增不依賴 Dalamud 的 `AutomaticPullLifecycle`，狀態為 `Idle → Recording → Finalizing → Saved → Idle`。
- `InCombat: false → true` 自動開始 Capture；正常脫戰使用 3 秒 debounce，期間重新進戰會取消結束並延續同一 Pull。
- 自動模式必須先觀察一次 `InCombat=false` 才會 armed；plugin 在戰鬥中載入／reload 或 Duty 結束後 combat flag 尚未清除時，不會從既有 Pull 中途建立重複 Capture。
- `DutyWiped`／`DutyCompleted` 先保存 Duty event，再立即結束目前 Pull；重複 callback 或稍後的脫戰觀察不會再次輸出。
- Finalizing 期間先原子建立 CaptureId 獨立 Pull 檔，重新載入驗證後才原子更新 index；完成登錄後才允許下一個 Pull，並顯示 session 已保存 Pull 數與最後 Capture ID。
- 每個自動 Pull 以開始戰鬥當下的 Territory／Map／Instance 建立新 `ActiveCapture`，Actors、Frames、Events、Waymarks 與 Action Effects 不跨 Pull 共用。
- Plugin reload 時停止 Framework 與 Action Effect callbacks 後，同步完成並保存仍在進行的 Capture；若已有背景保存，`Dispose` 必須完整等待 atomic save 結束而非逾時放行，避免舊 plugin instance 在 reload 後覆寫新 Pull。
- UI 提供持久化「自動 Pull 擷取」切換；擷取或背景工作中禁止切換，手動開始／停止與離線載入模式保留。
- Dalamud log 記錄 armed、debounce start／cancel、finalize reason、Capture ID、保存耗時與回到 Idle，讓遊戲內 lifecycle 測試可由持久化紀錄稽核。
- 原 `ManualCaptureService` 已改名為 `CaptureService`，避免服務名稱與自動模式語意衝突。

### 目前驗證

- Core 36/36 測試通過；生命週期契約覆蓋單次啟動、3 秒 debounce、短暫脫戰取消、初次載入與 Duty 結束後重新 armed、Finalizing 期間觀察 combat clear、Wipe／Clear／reload immediate finalize、Saved 後下一 Pull 與 timestamp invariants。
- Plugin integration 9/9 測試使用 fake `IDutyState` 與真實 `CaptureService`／validator／atomic JSON persistence，證明正常脫戰 debounce 與重新進戰取消、Wipe／Clear single-finalize、舊 combat flag 不會重開、玩家死亡與 Boss despawn／respawn 不切 Pull、連續 Pull 的 Capture ID／actors／events／Action Effects 與 source／target stable identity 隔離、reload 同步保存、`Dispose` 完整等待受控阻塞中的背景 save、新 service 重新 armed、手動模式保留、JSON 保存失敗復原，以及 lifecycle 診斷 log 完整。
- x64 Debug／Release plugin 建置皆為 0 warnings／0 errors；最新 Debug DLL 寫入時間為 04:02:22.231，Dalamud 於 04:02:22.742 卸載舊 instance、04:02:23.046 載入該版本，並於 04:02:23.055 記錄 lifecycle armed；reload／hook dispose／重新載入均無錯誤。
- 遊戲內正常脫戰、連續三個自動 Pull、兩次 Duty complete、一次 Wipe、脫戰後重新 armed、玩家死亡、Recording 中 reload、Boss 暫時無法選取與型態轉換全部通過。

### 遊戲內實測結果

- 2026-08-09 03:26，自動模式在 Territory 134／Map 15 建立 Capture `657cd03a-d016-410c-9e6c-0eb70820af84`，8.98 秒、90 frames、10 actors。
- `InCombatChanged(false)` 記錄於 6,003 ms，最後 position frame 為 8,903 ms，符合約 3 秒 debounce 後單次結束；Core `CaptureJson.Load` validator 載入通過。
- 此次僅觸發開怪／脫戰生命週期，Action Effect batches 為 0；不把它列為 Action Effect 完整性證據。
- 2026-08-09 10:58:23，Territory 1046／Map 33 自動開始 Capture `87d676ba-dfb3-4b19-9293-e08cc66db690`；3.30 秒後因 `DutyCompleted` single-finalize，34 frames／3 events／4 Action Effects，83.84 ms 保存完成，隨後回到 Idle。
- 2026-08-09 10:59:12，未 reload 即在 Territory 446／Map 404 開始下一份 Capture `6bf29d1a-b489-4184-9038-cb39548490aa`；7.01 秒後因 `DutyCompleted` single-finalize，71 frames／10 events／10 Action Effects，2.45 ms 保存完成並重新 armed。
- 2026-08-09 10:59:50，同一 plugin instance 再於 Territory 296／Map 403 開始 Capture `2a3a4679-9a7d-4cc7-88ce-c1023fbe7f83`；15.65 秒後因 `DutyWiped` single-finalize，157 frames／20 events／5 Action Effects，3.33 ms 保存完成並回到 Idle。
- 最新 Wipe JSON 含 18 actors、157 frames、20 events、5 Action Effects、1 Waymark frame；恰有一個 `DutyWiped` 與玩家 `Les Cent` 的一個 `Death`，無 `DutyCompleted`；event／frame timestamps 單調、5/5 Action Effect global sequence 唯一且 source stable identity 全部映射。Core `CaptureJson.Load`／`PullRecordValidator` 載入通過。
- 三份 Capture ID、Territory／Map、frame／event／Action Effect 數量均不同，且 10:58:23 至 11:00:05 間沒有 Raid Debrief reload；證明多 Pull 可在同一 plugin instance 連續保存，每個 Duty callback 只輸出一份紀錄。
- 2026-08-09 11:04:47，Territory 134／Map 15 自動開始 Capture `582f0073-c357-49bc-b317-b85d0b8671aa`；Recording 30.76 秒時 reload，11:05:17.966 先解除 Action Effect hook，再以 `PluginReload` 同步完成 Capture，2.08 ms 保存後才於 11:05:18.973 結束卸載。
- 新 instance 於 11:05:21.550 載入時仍未 armed，既有 `InCombat=true` 未建立 duplicate Pull；直到 11:05:49.216 首次觀察 `InCombat=false` 才重新 armed。其間沒有第二個 capture start。
- Reload Capture 經 Core validator 載入通過：7 actors、308 frames、1 event、8 Action Effects、1 Waymark frame，最後 frame 30,701 ms／總長 30,761 ms；frame timestamps 嚴格遞增、event timestamps 單調、8/8 global sequence 唯一且 source stable identity 全部映射。
- 2026-08-09 11:09:06，Territory 638／Map 353 自動開始 Capture `ae4d7aba-20d5-4227-86e3-00354422bcf6`；跨越 Zurvan 型態轉換後，11:10:04 因 `DutyCompleted` 僅 single-finalize 一次，586 frames／56 events／42 Action Effects，90.22 ms 保存完成並回到 Idle。Core validator 載入通過。
- 舊型態 Zurvan stable actor 19／BaseId 6448 在 0–48,301 ms 可選取，48,400 ms 起不可選取；新型態 stable actor 17／BaseId 6449 至 57,300 ms 仍不可選取，57,400 ms 起可選取。約 9 秒兩型態皆不可選取期間，Capture 未 debounce、未 finalize、未建立第二份紀錄。
- 同一份 Capture 的 586 個 frames 從 0–58,503 ms 連續存在，sampling interval 96–104 ms；舊型態最後可確認 Action Effect 為 46,126 ms，新型態於 57,755 ms 產生 action 7255，玩家於 58,106 ms 以 action 24378／7 命中新型態 stable actor 17，證明轉場前後資料位於同一 Pull。
- Capture 恰含一個 `DutyCompleted`、無 `InCombatChanged(false)`；42/42 Action Effect global sequence 唯一且 source stable identity 全部映射。Boss actor 未 despawn，因兩型態物件全程存在於 ObjectTable；本次轉場由 BaseId 6448→6449 與 `IsTargetable` 時序客觀呈現。

### 遊戲內測試

- 啟用自動模式後，未進戰時維持 `Idle`；開怪後自動轉為 `Recording`，不需按開始。
- 正常脫戰後顯示 3 秒 debounce，再經 `Finalizing → Saved → Idle`，session 保存數只增加一次。
- debounce 期間重新進戰時繼續原 Pull，不輸出中間 Capture。
- 連續完成兩個 Pull；保存數增加兩次，第二份 Capture ID 不同且不含第一 Pull 的早期事件／Actor lifecycle。
- Wipe 與 Duty complete 各自立即結束一次，離線載入可看到對應 `DutyWiped`／`DutyCompleted`。
- Recording 中 reload plugin；reload 前 Pull 被保存，reload 後設定仍為自動模式且下一 Pull 可正常開始。

### 完成條件

- 多次 Pull 不需重新載入插件。
- 一次 Pull 只輸出一份紀錄。
- Wipe／Clear 後可正確結束。
- Boss 暫時消失不會誤切 Pull。
- 下一次 Pull 不會混入前一次資料。

---

## 階段 6 — 真實八人 Pull 驗證

狀態：`進行中`

### 目標

擷取一場真實八人高難度 Pull，完成 Phase 0 的資料可用性驗證。

### 測試內容

- 8 名 Party。
- Boss 與 Add。
- Position、Rotation、HP。
- Death 與恢復。
- Cast 與 Status。
- Waymark。
- Action Effect（若階段 4B 已證明可靠）。
- Wipe 或 Clear lifecycle。
- 擷取 callback cost 與 JSON serialization time。

### 隱私要求

- 玩家名稱預設匿名化為 Pull 內代號。
- 不輸出 Content ID。
- 分享前移除不必要的玩家識別資料。
- Recorded fixture 不直接提交未匿名化原始檔。

### 遊戲內驗證流程

1. Reload Raid Debrief，開啟 `/rdebrief`，確認「自動 Pull 擷取」已啟用，且狀態為 `Idle`／已 armed。
2. 進入一場八人高難度 Duty；最好選擇會出現 Add，且可由測試隊安全安排一名玩家死亡後 Raise 的場景。
3. 開怪前放置實際會使用的 Waymarks，確認 Party 為八人。
4. 完成一個有意義的 Pull：八名玩家需有移動／轉向；讓 Boss 執行 casts 並施加 statuses；攻擊 Boss／Add，且至少施放一次 AoE heal。
5. 在隊伍同意且不影響他人的前提下，安排一名玩家死亡並 Raise；若無法安全安排，不要為了測試破壞公開隊伍，改以本次自然發生事件為準並回報缺項。
6. 以一次 Wipe 或 Clear 結束；不要手動停止 Capture。等待 UI 回到 `Idle` 並顯示已保存。
7. 回報戰鬥中是否有肉眼可見卡頓，以及本次是 Wipe 或 Clear。保留本機 `captures/` 與 `capture-index.json`，不要自行分享原始檔。

此流程只需一個完整 Pull。若該戰鬥沒有 Add、死亡／Raise 或其他必要資料，先檢查 Capture，再只補缺少的場景，不重做已證明的項目。

### 階段 6 已完成的準備

- 玩家角色名稱在新 Capture 中固定序列化為 Pull-local `Player 1`、`Player 2`…；NPC 名稱保持原值。
- `PullRecord` 不擷取 Content ID；Plugin integration test 會直接檢查輸出 JSON 不含原始玩家名稱與 `contentId`。
- UI 明確顯示匿名化政策；舊 Capture 可能仍含原始名稱，不可直接分享。
- Capture persistence 已改由 repository 統一處理；每個 Pull 為 CaptureId 獨立檔案，Latest 由持久化 sequence index 查詢。

### 完成條件

- Actor identity 在完整 Pull 內可用。
- Position samples 時序一致。
- Party／Boss／Add 沒有明顯缺失或重複。
- JSON 可離線重新載入。
- 沒有可見戰鬥卡頓。
- 已記錄每種資料來源的可靠性。
- 已明確列出缺失或不可靠的事件來源。

---

## 進度文件維護規則

後續每次開發都必須同步更新本文件：

1. 開始一個階段時，先將狀態改為 `進行中`。
2. 實作過程發現的新限制、風險或資料來源，記錄在對應階段。
3. 每次遊戲內測試後，追加日期、測試場景、結果與失敗項目。
4. 只有建置與對應遊戲內測試都通過後，才能標記 `已完成`。
5. 階段未通過時不得提前開始依賴它的後續階段；4A 與 4B 可各自獨立進行。
6. 發生阻塞時標記 `阻塞`，並寫明缺少的資料、已嘗試方法及解除條件。
7. 規格或實作順序改變時，同步更新「整體進度」及受影響階段，不保留失效計畫。
8. 所有完成證據必須是實際觀察結果，不以「應該可以」代替驗證。
9. 每次提交階段成果前，確認本文件的「最後更新」日期與目前階段正確。

## 測試紀錄

| 日期 | 階段 | 場景 | 結果 | 備註 |
|---|---|---|---|---|
| 2026-08-09 | 0 | Dalamud Dev Plugin 載入 | 通過 | `/rdebrief` 成功開啟測試視窗，登入狀態與 Territory ID 正常 |
| 2026-08-09 | 1 | Debug／Release 建置、格式與 Dalamud 即時資料讀取 | 通過 | 使用者確認遊戲內資料讀取沒有問題 |
| 2026-08-09 | 2 | Core contract tests、Debug／Release 建置與格式檢查 | 通過 | 9/9 測試通過；Release `latest.zip` 包含 Plugin 與 Core |
| 2026-08-09 | 2 | Dalamud 安全區手動擷取、匯出與重新載入 | 通過 | 20.41 秒、205 samples、83 actors、0 gaps、0 rejected；JSON 離線檢查確認時間戳嚴格遞增、平均 100.02 ms、座標有限、HP 合理、Actor ID 唯一 |
| 2026-08-09 | 2 | Dalamud 解除限制副本手動擷取與匯出 | 通過 | Territory 1141／Map 830；40.70 秒、408 samples、49 actors、19,624 actor samples；時間戳嚴格遞增、平均 100.002 ms、95–105 ms、0 gaps、座標有限、HP 合理、Actor ID 與 frame reference 正常 |
| 2026-08-09 | 3 | Core polled event detector、JSON 相容性與 Release 建置 | 通過 | 16/16 測試通過；事件順序、去重、Cast end／interrupt、死亡恢復 identity、spawn／despawn、來源與舊 schemaVersion 1 Capture 載入皆通過；等待遊戲內驗證 |
| 2026-08-09 | 3 | Dalamud 木人 Cast、Status 與 `InCombat` 擷取 | 通過 | 22.60 秒、227 samples、9 actors、25 events；9 CastStarted 對應 4 CastEnded＋5 CastInterrupted，3 StatusGained 對應 3 StatusLost，1 次脫戰；事件時間戳單調、0 重複、0 未知 Actor reference |
| 2026-08-09 | 3 | Dalamud 解除限制副本死亡、Wipe 與 Recommence | 通過 | 52.20 秒、523 samples、96 actors、117 events；InCombat true → Death(actor 1) → DutyWiped → InCombat false → AliveTransition(actor 1) → DutyRecommenced 順序正確；0 重複、0 未知 Actor reference，Cast／Status 全部配對 |
| 2026-08-09 | 3 | Dalamud Duty start、戰鬥與 complete | 通過 | 20.11 秒、202 samples、81 actors、173 events；DutyStarted 8.431s → InCombat true 10.803s → DutyCompleted 11.330s → InCombat false 11.402s；0 重複、0 未知 Actor reference |
| 2026-08-09 | 4A | Core Waymark timeline、只讀 API audit 與 Debug／Release 建置 | 通過 | 20/20 測試通過；只使用 `Instance`、`FieldMarkers`、`Active`、`Position`，未引用放置／移動／清除函式；等待遊戲內驗證 |
| 2026-08-09 | 4A | Dalamud 八 Waymark 放置、移動與清除 | 通過 | 33.90 秒、340 samples、13 Waymark frames；初始／最終皆無 active marker，A/B/C/D/1/2/3/4 依序啟用，A 清除後於新座標重放並再清除，最後清除其餘七個；每 frame 八 ID 唯一且座標有限，時間戳嚴格遞增、無未變 frame；玩家最近距離 A 0.90m、B 2.29m、C 2.54m；Dalamud log 無 reader error |
| 2026-08-09 | 4B | Core Action Effect model、JSON 相容性與 Debug／Release 建置 | 通過 | 24/24 測試通過；damage／heal／miss／other 分類、大數值 amount、critical／direct hit、多 target／多 entry、raw decoded consistency、重複 target 拒絕與舊 schemaVersion 1 載入皆通過；等待遊戲內驗證 |
| 2026-08-09 | 4B | Dalamud 4B x64 Debug 熱重載與 Hook 初始化 | 通過 | Territory 129／Map 12；Framework probe 正常，Action Effect hook 顯示可用、0 batches、0 decode failures；Dalamud log 確認 `ActionEffectHandler.Receive` hook 啟用且 phase 4B plugin 載入 |
| 2026-08-09 | 4B | Dalamud 木人單體 Action Effect 擷取、匯出與離線載入 | 通過 | 29.69 秒、297 samples、12 actors、30 Action Effect batches；本機玩家 18 batches／18 damage entries，action 24373×5、24374×2、24375×2、auto attack 7×9，全部命中 stable actor 5 木人；damage 7,667–80,307，9 critical、7 direct hit；另保存 resource raw type 62×9 與 combo raw type 27×9；timestamps 單調、global sequence 嚴格遞增、0 完全重複 batch、30/30 source 與 target identity 對應、Core validator 載入通過、Dalamud log 無 Action Effect reader error |
| 2026-08-09 | 4B | Dalamud 木人 AoE 多目標 Action Effect 擷取 | 通過 | 22.32 秒、224 samples、12 actors、15 batches／19 targets／28 entries；本機 action 24315×3 每次皆在單一 batch 保存 stable actor 4＋5 兩個木人，共 6 damage entries（15,797–26,711），另 action 37032 同時保存兩 target 的 status raw type 14 與 combo raw type 27；4 個 multi-target batches、target ID 全部唯一、timestamps 單調、global sequence 嚴格遞增、0 完全重複 batch、15/15 source 與 19/19 target identity 對應、Core validator 通過、Dalamud log 無 reader error |
| 2026-08-09 | 4B | DoT／HoT 初始套用與週期 tick 驗證 | 部分通過 | DoT action 37032 對 stable actor 4＋5 各保存 status raw type 14，PolledStatus 隨後觀察到 status 3897；HoT action 24302 對本機保存兩個 status raw type 14，PolledStatus 隨後觀察到 status 2620＋2621；套用後至 Capture 結束未出現任何週期 damage／heal Action Effect，確認 `ActionEffectHandler.Receive` 不提供本次 DoT／HoT tick，已列為資料來源限制 |
| 2026-08-09 | 4B | 自我單體治療與群體治療技能擷取 | 部分通過 | 19.33 秒、194 samples、9 actors、14 batches／20 entries；本機 action 24284×3 各保存 self-target heal 33,795／52,982／53,963（後兩次 critical），action 24286×3 各保存 self-target heal 22,374／21,865／21,959，所有 heal direct-hit=false；0 完全重複 batch、14/14 source 與 target identity 對應、Core validator 通過、Dalamud log 無 reader error。群體技能本次每 batch 只有本機一個 target，且 194 frames 皆為滿血 182,723／182,723，因此尚未驗證多人治療 target fan-out 或有效 HP 恢復量 |
| 2026-08-09 | 4B | 八人群體治療 Action Effect 擷取 | 通過 | Territory 1295／Map 1045；24.32 秒、244 samples、46 actors、101 batches／143 targets／237 entries。Action 24286×2 每次皆在單一 batch 保存八個唯一 targets（stable actors 1–8），共 16 heal entries：第一次 23,004／36,654／22,837／22,024／37,053／35,954／37,449／22,036（4 critical），第二次 22,938／22,141／36,943／21,951／22,215／22,857／36,468／36,964（3 critical），全部 direct-hit=false；16/16 群療 target identity 對應、0 重複 target／entry／batch、timestamps 單調、global sequence 嚴格遞增、Core validator 通過、Dalamud log 無 reader error |
| 2026-08-09 | 4B | Plugin reload／Hook dispose 與跨職業治療去重 | 通過 | 03:08:43 明確記錄舊 Hook `disabled and disposed`，03:08:47 僅建立一個新 Hook；reload 後 12.66 秒 Capture 使用 class job 28 的 action 185×3，正好產生 3 batches／3 targets／3 heal entries（21,453／22,206／22,519），timestamps 2,194／5,486／9,071 ms、global sequence 236125／236135／236140，3/3 source 與 target identity 對應、0 重複 batch、Core validator 通過、0 decode failures；最終 x64 Debug／Release 建置 0 warnings／0 errors |
| 2026-08-09 | 5 | Core 自動 Pull state machine、持久化模式 UI 與 x64 建置 | 通過 | 32/32 Core 測試通過；3 秒 debounce、短暫脫戰取消、DutyWiped／DutyCompleted／PluginReload immediate finalize、Saved 後連續 Pull 與 timestamp invariants 均有契約測試；x64 Debug／Release 0 warnings／0 errors，Dalamud phase 5 plugin 熱重載成功；等待遊戲內 lifecycle 驗證 |
| 2026-08-09 | 5 | Dalamud 正常開怪、脫戰 debounce 與自動保存 | 通過 | AutomaticCaptureEnabled 持久化為 true；Territory 134／Map 15，Capture `657cd03a-d016-410c-9e6c-0eb70820af84`，8.98 秒、90 frames、10 actors；`InCombatChanged(false)` 於 6,003 ms，最後 frame 8,903 ms，約 3 秒後單次結束；Core CaptureJson validator 載入通過。此輪 Action Effect 0 batches，不列為 Action Effect 證據 |
| 2026-08-09 | 5 | CaptureService debounce／Duty／Actor lifecycle／連續 Pull／reload 整合測試 | 通過 | RaidDebrief.Plugin.Tests 9/9；使用 fake IDutyState 驅動真實 CaptureService、PullRecord validator 與 atomic JSON save，正常脫戰 debounce／重新進戰取消、Wipe／Clear single-finalize、玩家死亡、Boss despawn／respawn 同一 Pull、兩 Pull Capture ID／actor／event／Action Effect 隔離及 source／target stable identity 重建、reload 保存、Dispose 等待受控阻塞中的背景 save、partial duplicate 防護、手動模式保留、JSON save failure 復原全部通過；全 solution 45/45 |
| 2026-08-09 | 5 | Dalamud 連續 Clear／Clear／Wipe 自動 Pull | 通過 | 同一 plugin instance 依序保存 Capture `87d676ba-dfb3-4b19-9293-e08cc66db690`（DutyCompleted，34 frames／3 events／4 Action Effects）、`6bf29d1a-b489-4184-9038-cb39548490aa`（DutyCompleted，71／10／10）與 `2a3a4679-9a7d-4cc7-88ce-c1023fbe7f83`（DutyWiped，157／20／5）；保存耗時 83.84／2.45／3.33 ms，各自 single-finalize、重新 armed 並回 Idle。最新 Wipe JSON 恰含一個 DutyWiped、一個玩家 Death，Core validator 通過，timestamps 單調、5/5 Action source identity 映射 |
| 2026-08-09 | 5 | Dalamud Recording 中 plugin reload | 通過 | Capture `582f0073-c357-49bc-b317-b85d0b8671aa` 在 Recording 30.76 秒時 reload；Hook 先解除，active record 以 PluginReload 保存（2.08 ms）後才完成 unload。新 instance 在既有 InCombat=true 時未建立 duplicate Pull，觀察 InCombat=false 後才 armed；JSON 為 7 actors／308 frames／1 event／8 Action Effects／1 Waymark frame，Core validator 通過 |
| 2026-08-09 | 5 | Dalamud Boss 暫時不可選取與型態轉換 | 通過 | Territory 638／Map 353，Capture `ae4d7aba-20d5-4227-86e3-00354422bcf6`，58.56 秒、586 frames／56 events／42 Action Effects。舊 Zurvan actor 19／BaseId 6448 於 48,400 ms 失去 targetable，新型態 actor 17／BaseId 6449 於 57,400 ms 才可選取；約 9 秒轉場未切 Pull，frames 維持 96–104 ms interval。轉場前後 Action Effects 分別映射 actor 19／17，最終 DutyCompleted single-finalize，Core validator 通過 |
| 2026-08-09 | 6 | 玩家匿名化與八人 Pull 驗證準備 | 通過 | 新 Capture 將所有 `ObjectKind.Pc` 名稱改為 Pull-local `Player N`，NPC 名稱保持原值；integration test 直接檢查原始玩家名稱與 `contentId` 均不在 JSON。Core 36/36、Plugin 10/10（全 solution 46/46）通過；x64 Release 0 warnings／0 errors。UI 與本文件已提供真實八人 Pull 操作流程，等待遊戲內 Capture。 |
| 2026-08-09 | 6 | Capture repository／index persistence refactor | 通過 | 每個 Pull 原子建立 `captures/<CaptureId>.json`，驗證後才原子更新僅含 metadata 的 `capture-index.json`；Latest 使用可跨 reload 延續的 sequence，不看 mtime、CaptureId 排序或資料夾掃描。Core repository 契約以三份完整合成 Pull 驗證 Actors／Frames／Events／Waymarks／Action Effects 隔離、GetLatest／GetById／GetRecent、Pull write failure、index write failure、immutable CaptureId 與 reload；Plugin integration 以同一 `CaptureService` 連續保存三 Pull，驗證 single-finalize、獨立檔與指定 CaptureId／Latest 載入。Core 40/40、Plugin 10/10（全 solution 50/50）通過，x64 Release 0 warnings／0 errors；現有 58.56 秒 Capture 已一次性遷移為 sequence 1，Latest 與指定 CaptureId 離線 validator 均通過，舊 latest 檔已移除。未新增 History／Recent／Retention／Compare UI。 |
