# Raid Debrief 開發進度

最後更新：2026-08-11

## Roadmap 各 Phase 狀態

| Phase | 名稱 | 狀態 |
|---|---|---|
| 0 | Capture Technical Prototype | Completed |
| 1 | Recorder | Completed |
| 2 | Offline Replay | Completed |
| 3 | In-Game Replay | Completed |
| 4 | Debrief MVP | Not Started |
| 5 | Replay UX | Not Started |
| 6 | Stability | Not Started |
| Extra Goal | Event Correlation | Not Started；Replay MVP 穩定前不得開始 |

## Current Phase

**Roadmap Phase 3 — In-Game Replay**

狀態：`Completed`

Phase 2 的完整進度快照已原樣封存至 `docs/archive/progress-phase-2-2026-08-09.md`；本文件為 Phase 3 於 2026-08-11 收尾後的完整進度封存。詳細 build、test、效能、瀏覽器與 Dalamud 實測證據集中保存於 `docs/TEST_RECORDS.md`。

Phase 3 只處理「在 Dalamud 內可靠檢視上一個成功完成的 Pull」：正式 Replay window、in-memory previous-pull handoff、預設戰鬥自動隱藏／可明確停用但仍暫停的 combat gate，以及最低限度效能保護。開發期間保留明確的 Developer/Test 手動 JSON 匯入入口供介面測試調整，但正式 runtime flow 不依賴該入口。未提前加入 Phase 4 Debrief Summary、Phase 5 Replay UX、Pull History／Recent Pulls、persistent latest Pull、跨 session restore 或 Event Correlation。

## 已完成能力摘要

### Roadmap Phase 0 — Capture Technical Prototype

- Dalamud／FF14 live data source、Framework-thread probe 與真實 Pull 可擷取性已驗證。

### Roadmap Phase 1 — Recorder

- Automatic／Manual capture、Pull lifecycle、10 Hz sampling、actor identity、events、Waymarks、Action Effects、schema／validator 與 Developer/Test JSON fixture 已完成。
- Runtime 保持 `ActivePull` 與最新成功 finalized／validated 的 in-memory `LastCompletedPull`；沒有 persistent Pull history 或跨 session restore。

### Roadmap Phase 2 — Offline Replay

- `ReplaySession`、clock、timestamp actor-state resolution、stable timeline、Waymark state、arena projection／scene、play／pause／seek 與 Dalamud-independent renderer 已完成。
- 真實八人 fixture 已完成完整正反向 replay、browser、效能及 Dalamud Developer/Test adapter 驗收。
- Phase 2 詳細工作與完成證據見 `docs/archive/progress-phase-2-2026-08-09.md` 與 `docs/TEST_RECORDS.md`。

## Phase 3 現況分析

### 可直接沿用的基礎

- `CaptureService.GetReplaySourceSnapshot()` 在同一 lock 內提供 finalization generation／state／CaptureId／error、completed generation 與 `LastCompletedPull` reference；成功 validation 才替換 record，失敗仍保留上一個有效 Pull。
- `ReplaySession` 直接接收 finalized `PullRecord`，不依賴 Dalamud、JSON 或 filesystem。
- 正式 `ReplayWindow` 已具備 ImGui arena、播放控制、scrubbing、event jump 與具 generation 防護的背景 session construction。
- Core seek 使用預建索引、二分搜尋與可重用 scene buffer；主要 fixture 的既有效能門檻為平均 seek `< 2 ms`、每次配置 `< 64 bytes`，實測遠低於門檻。
- `LiveDataProbe.InCombat` 已由 Dalamud `ConditionFlag.InCombat` 在 Framework update 中更新，可作為 UI 戰鬥干擾保護的單一狀態來源。

### 完成狀態

- 正式 Replay window、Previous-Pull handoff、可設定的 Combat Auto-Hide、Minimal Performance Safeguards 與 Phase 3 End-to-End 驗收皆已完成。
- 真實 Wipe 後載入最新 completed Pull、下一 Pull auto-hide／pause、戰鬥中拒絕開窗、combat end 不自動重開，以及 fresh x64 Debug hot reload 均有 2026-08-10 實機與 Dalamud log 證據。
- 後續 Replay 修正已完成 Map canvas／SizeFactor、場地與 Target Circle 世界比例、Job／Waymark／Target Marker 原生圖示、Boss 身位圈、每次 Wipe 提示、提示開關及戰鬥自動關窗開關；最終 Core 78／UI 4／Plugin 91 tests 與 x64 Debug build 全部通過。

## Phase 3 工作項目與順序

以下依賴順序執行；項目 1～5 與後續 Replay 修正皆已全部完成。

### 1. Replay Window Integration — Completed

將 `ReplayTestWindow` clean-cutover 為正式 `ReplayWindow`，沿用已驗證的 ImGui arena 與基本 play／pause／scrub 能力；同一視窗內保留明確標示且需手動操作的 Developer/Test JSON 匯入區，供開發期間載入 fixture 調整介面。

主要設計：
- `Plugin`／`WindowSystem` 只註冊一個正式 Replay window；`/rdebrief replay` 與 Capture 視窗入口都走同一個 runtime open request。
- 視窗提供兩個明確資料來源模式：預設的 `Runtime LastCompletedPull`，以及使用者主動選擇的 `Developer/Test JSON Fixture`。不得在兩者之間 silent fallback。
- 正式 runtime open 只接受 finalized in-memory `PullRecord`，不接觸 `CaptureJson`、filesystem、live game objects 或 persistent repository。
- Developer/Test 區保留 fixture path 與手動「載入 JSON」操作；只有按下該操作後才可在背景執行 `CaptureJson.Load`。載入後 UI 必須顯示目前來源為 fixture 與 CaptureId。
- 手動匯入的 fixture session 保持選定狀態，直到使用者明確切回 Runtime `LastCompletedPull` 或再次匯入；不得因背景輪詢 runtime source 而被無聲取代。
- 首次載入新 Runtime CaptureId 時從 `00:00` 建立 session；單純關閉再開啟同一 CaptureId 可保留當前檢視時間。Phase 4 的 suggested replay window 不在本階段實作。
- 不另建第二套 Developer/Test replay window；正式與測試來源共用同一套播放、arena 與 combat gate，避免兩套 UI 行為分歧。

主要驗收條件：
- 外掛可透過單一正式入口開啟 Replay，並顯示 play／pause／scrub、Player／Boss、dead state 與 Waymarks。
- 正式 runtime open 不會執行 JSON／filesystem 呼叫；Developer/Test JSON 只在使用者明確按下手動匯入時讀取。
- 手動匯入有效 fixture 可完整操作同一套 In-Game Replay UI；無效 path／JSON 顯示 load error，不取代目前有效 session。
- 相同 `PullRecord` 在 Offline 與 In-Game 產生一致的 timestamp、actor／Waymark 數量與 replay end state。

完成證據：
- `ReplayTestWindow` 已 clean-cutover 為單一正式 `ReplayWindow`；舊類別、欄位、測試標題與入口文字已全部移除。
- `/rdebrief replay` 與 Capture 視窗按鈕都呼叫 `ReplayWindow.OpenRuntime(inCombat)`；Runtime 無 Pull 時顯示明確狀態，已載入同一 Runtime CaptureId 時保留目前 session／timestamp。
- 視窗明確顯示目前來源；正式 Runtime 路徑只讀取 `CaptureService.GetReplaySourceSnapshot()` 提供的 in-memory record，Developer/Test 區則保留 fixture path 與手動 `CaptureJson.Load`。來源只在仍符合目前 CaptureId／generation 的背景 load 成功後切換，load error 不取代現有 session。
- Dalamud x64 Debug smoke 透過正式 `ReplayWindow` 的 Developer/Test path 載入 Capture `6fe1b80f-567a-41a3-8912-6d013c137aa7`（2,233 frames／1,495 events）；隨後載入不存在的 `.missing` path，UI error path 被捕捉，既有 Capture session 保持不變。臨時自動開窗／載入 instrumentation 已移除。
- Player marker 已改為依 `ActorRecord.ClassJobId` 顯示 `src/JobIcons` 的 21 種 embedded PNG；圖示未支援或尚未 ready 時保留圓點 fallback，BattleNpc 維持紅色圓點，dead／untargetable 狀態仍套用於 marker。
- Arena projection 保留 world X／Z 的等比例尺度：既有方形 profile 仍使用其 recorded bounds；已知 Territory／Map profile 使用明確的實際場地 bounds，未知的圓形資料才以 Replay 實際呈現的 Player、曾可選取 Boss／Add 與 active Waymarks 建立等半徑 fallback，避免場外不可選取 mechanic actor 污染場地尺度。
- Dalamud 顯示 smoke 在 `3,335 ms` 實際執行正式 Draw path，載入主要 fixture 並確認 8/8 Player marker 使用 Job icon、arena inset 為 8、arena size 為 544；沒有 texture、Replay load 或 `UiBuilder(RaidDebrief)` error。臨時 smoke instrumentation 已移除。
- 方形場地修正後，主要 fixture 的 projection bounds 由 `60..140` 收斂為實際觀測範圍 `65..135`；完整 Offline Replay 重新 render 2,234 個 SVG、依序 advance 2,233 frames 並通過 20,000 次 deterministic seek，0 bytes/seek。
- `LiveDataProbe` 取得的 Dalamud `IGameObject.HitboxRadius` 逐 frame 保存至 `ActorStateSample.HitboxRadius`；resolver 使用請求 timestamp 當下最後一筆離散值，validator 拒絕負值與非 finite 半徑。Boss／Add Target Circle 直接以此 world-space 半徑與 arena bounds 計算；Player Target Circle 則依需求維持固定 UI 尺寸。
- 新完成的 Capture 會把 `IGameObject.OwnerId` 保存至 `ActorRecord.OwnerId`；Arena scene 以 OwnerID 是否指向 Pull 內 `Pc` 的 EntityId／GameObjectId 排除 player-owned BattleNpc，同時保留無 owner 的 Boss／Add。舊 fixture 缺少 OwnerID 時維持原顯示，不使用名稱或 BaseId 猜測。
- Replay arena 已支援游標錨定的 `1x`～`6x` 滾輪縮放、左鍵拖曳平移與 `Fit` 重設；viewport 受場地 bounds 限制，grid、Waymarks、Actors、標籤與場地邊界共用 transform 並裁切於 arena，Player icon 尺寸不隨 zoom 放大。新 session 載入時回到 Fit。
- Dalamud zoom smoke 在 `3,335 ms`、`1.728x`、非中心 viewport 實際完成 10 actor 的 ImGui Draw path；無 Replay／UI error，臨時自動載入、seek 與 logging instrumentation 已移除。
- `src/TargetCircle.png`（480×682 RGBA）已作為 manifest resource 嵌入 Plugin；Replay 直接透過 `ITextureProvider.GetFromManifestResource` 取得 shared texture，舊有 runtime 產生的 128×128 白色環形 texture、藍／紅 tint 與獨立 facing line 已完整移除。
- Job Icon 固定為 `28 px`；Player Target Circle PNG 也固定為 `56 px` 寬，兩者均不隨 recorded radius 或 Replay zoom 改變。Boss／Add Target Circle 尺寸仍獨立依 recorded `HitboxRadius` 投影；所有 PNG 頂部箭頭皆透過 `AddImageQuad` 旋轉至 recorded facing。
- PNG 的黃色外圈水平範圍約為原圖 X `40..440`，只佔 480 px 畫布寬度的 `5/6`。Boss／Add quad 使用 `6/5` 補償，使黃色外圈半徑精確對應 projected `HitboxRadius`；Player 則固定整張 PNG 為 `56 px`。
- Territory 1363／Map 79 的圓形場地依確認值使用中心 `(100, 100)`、半徑 `17.83` world units，固定 world bounds 為 X／Z `82.17..117.83`。
- 在 `544 px` arena 中，Player Target Circle PNG 固定寬 `56 px`，其可見黃色外圈半徑約 `23.333 px`；Boss `6.000` radius 的可見外圈半徑仍為 `91.531 px`。場地半徑／Boss 外圈半徑維持 `17.83 / 6 = 2.9717`。
- 新 PNG 自帶環形與 facing arrow，因此 Player 前景 stroke 與獨立白色 facing line均已移除；dead cross、untargetable opacity、Player label 與 Job Icon fallback 維持既有行為。Legacy Pull 沒有 `HitboxRadius` 時仍能顯示固定 Player Target Circle，只缺少無法證實大小的 Boss／Add Target Circle。
- 回歸測試驗證 Player PNG 固定 `56 px` 且不受 radius／zoom 影響、Boss／Add world-space zoom scaling、17.83 arena radius、所有場地黃色填色、PNG `5/6` 外圈比例補償，以及 PNG 頂部旋轉至 facing。Core 72、UI 4、Plugin 34 passed，0 failed。
- 圓形場地 Capture `00c7163c-7e2a-493d-8222-27ac5b27a097` 完整 render 1,300 scenes、advance 1,299 frames；20,000 次 deterministic seek 平均 0.0220 ms、0 bytes／seek。
- 最新 `Debug -p:Platform=x64 --no-incremental` solution build 已成功完成；configured `RaidDebrief.dll` 為 581,120 bytes，SHA-256 `129e4cf69e1ce4ec9e512c303324cfef95ffc97e6860c00b50d4930b932691c4`，`LastWriteTime` 2026-08-10 18:59:36 +08:00。Dalamud Dev Plugin path 已核對為同一 x64 Debug DLL；log 於 18:59:36 完成 unload／load，18:59:37 記錄 `Finished loading RaidDebrief`。使用者隨後依手動步驟完成 Replay 顯示 smoke 並回報確認正常；該確認未使用自動滑鼠或鍵盤操作。
- Wipe／場景切換期間，Dalamud ObjectTable 可能仍保留已失效的 `BattleChara` wrapper；先前讀取其 `IsCasting` 會拋出 `NullReferenceException` 並中止整個 Framework probe。`LiveDataProbe` 現在逐 Actor 建立原子快照，單一失效 wrapper 只增加「略過失效 Actor」計數並受限頻率記錄 warning，不會清空既有有效 Actor，也不會中止 Capture。
- 實機修正驗證在 ObjectTable index 73 失效時連續命中保護路徑，未再產生 `live data probe failed`／Replay／UI error；其後手動 Capture `be34f692-77a0-4d21-ae1e-ab14d0534e7a` 跨越 `DutyWiped` 與 `DutyRecommenced`，以 257 frames、130 events、8 Action Effects 完成 validation／JSON export。第二次 Capture `24431f80-1fdb-4ead-9326-540ef59768b6` 隨即以 53 frames 再次完成 validation／export，Runtime Replay 亦成功載入該 CaptureId。
- 兩份新 Capture 均完成 Offline Replay 全程 render／advance 與各 20,000 次 deterministic seek；平均 seek 分別為 0.0156 ms、0.0120 ms，均為 0 bytes/seek。手動測試前後 `AutomaticCaptureEnabled` 維持 `false`，未留下 smoke instrumentation。
- 指定真實 Capture `00c7163c-7e2a-493d-8222-27ac5b27a097`（Territory 1363／Map 79）共 64 Actors、1,299 Frames、1,610 Events、980 Action Effects。全部 sample 的污染範圍為 X `75.251..126.000`、Z `25.000..117.235`；Z 25 的來源是從未可選取的場外 `BattleNpc`，不是 Replay 場地。實際可呈現 Actor 與 active Waymarks 的 observed bounds 為 X `82.170..117.235`、Z `82.961..117.235`。
- Territory 1363／Map 79 profile 以 `Circle`、中心 `(100, 100)`、半徑 `17.83` 呈現，固定 world bounds 為 X／Z `82.17..117.83`；observed bounds僅作診斷顯示。Boss／Add Target Circle、Waymarks 與 movement 共用 world scale，Player Target Circle 固定為 `56 px`。
- Dalamud fixed-circle smoke 透過正式 ImGui Draw path 載入 Capture `00c7163c-7e2a-493d-8222-27ac5b27a097`，顯示 9 Actors／8 Waymarks。實機畫面確認各 Player PNG 固定 `56 px`，Boss `6.000` 外圈與場地比例維持 `1 : 2.9717`；沒有 Replay／UI error，臨時自動開窗／載入／seek instrumentation 已移除。
- Territory 1149／Map 834 的最新自動 Pull `e7360741-6064-4fcd-a38b-3a420e4eeb3a`、`390e99a3-4970-47fe-bf21-ea2b6b40b72c` 與手動 Pull `f8263cb6-32b5-4af5-97da-b22a68ab62f2` 均因 `An Action Effect contains a null, invalid, or duplicate target` 被拒。Reader 已先拒絕 null／zero target，因此實際根因是原生 Action Effect callback 合法地在多個 target slots 重複相同 Object ID，而 validator 錯誤要求每個 ID 唯一。
- `ActionEffectRecord.Targets` 現在明確保存原生 target slots 的觀測順序並允許重複 Object ID；每個 slot 的 actor association、0 ID、entry count／index／raw decode 驗證仍完整保留。Core regression 先以相同 target ID、不同 effect entries 重現原錯誤，再確認修正；Plugin lifecycle regression 同時驗證 automatic DutyWiped 與 manual StopAndExport 都完成、保留兩個 slots 及各自 amount。
- 修正版實機手動 Pull `0a162e4d-4734-4a45-a056-1ed4e482e839` 在同一 Territory 1149／Map 834 錄得 378 Frames、125 Events、42 Action Effects；其中 `20,038 ms`／Action 3566 含 3 個相同 TargetObjectId `1073802236`，仍成功 finalized、validated、匯出並載入 Runtime Replay。Offline 全程 render 379 scenes／advance 378 frames；20,000 seeks 平均 0.0156 ms、0 bytes／seek。
- Arena yellow-fill cutover：ImGui 的 Circle 與 Square 都使用同一 `observedRange` 黃色 `#7a6110` 等價色；SVG 的 `.arena` base style 也統一為 `#7a6110`，因此目前 profile、由 Pull 觀測範圍推導的未知場地，以及後續新增 shape 共用黃色可活動區域，外部區域仍維持深色。
- 指定 `testdata/recorded/P10S.json`（Territory 1149／Map 834）被解析為 Square，觀測／world bounds X `71.719..143.126`、Z `65.000..114.000`。瀏覽器實際載入 Offline Replay 前的場地為深色；修正後 computed fill 為 `rgb(122, 97, 16)`，並以原始約 `1.457:1` 長寬比顯示黃色矩形。專用 fixture regression 驗證 `ObservedWorldBounds == WorldBounds`、rect arena 與共用黃色 style。
- P10S Offline verification 全程 render 379 scenes／advance 378 frames；20,000 deterministic seeks 平均 0.0120 ms、0 bytes／seek。臨時 P10S 自動載入／seek smoke instrumentation 已移除。



### 2. Previous-Pull Loading — Completed

建立明確且可測的 runtime handoff，處理「背景 finalize 尚未完成」與「上一個有效 Pull 已存在」的競態。

主要設計：
- 由 `CaptureService` 在同一 lock 內提供 atomic replay-source snapshot，至少包含 finalization 狀態、completed generation／count 與 `LastCompletedPull` reference。
- 玩家以 Runtime 模式在非戰鬥中要求開啟時，若當前 Pull 正在 finalizing，視窗顯示等待狀態；finalization 結束後才選取最新成功 validated record，避免錯載更早的 Pull。
- Runtime `ReplaySession` 仍在背景建立；每次 load 綁定 CaptureId／generation，UI thread 只採用仍為目前 runtime request 的完成結果。Developer/Test import 使用獨立 request generation，兩種模式都不得採用 stale result。
- 新的成功 completed Pull 在 Runtime 模式取代 session 並回到 `00:00`；validation 失敗時不得清除先前有效 `LastCompletedPull`，UI 必須明確表示此次 Pull 不可用，而不是假裝舊 Pull 是剛結束的 Pull。
- Runtime 沒有 completed Pull 時提供明確 empty state且不得自動從 disk 恢復；使用者仍可明確切換至 Developer/Test 模式手動匯入 fixture。

主要驗收條件：
- Wipe 後立即 open，即使 background finalization 尚未結束，最終載入的 CaptureId 仍是剛完成且通過 validation 的 Pull。
- 連續兩個 Pull 不會被較舊 background load 覆蓋；第二個成功完成後開啟會得到第二個 CaptureId。
- Plugin reload 後 Runtime 模式沒有 in-memory Pull 時顯示 empty state，不自動讀取前一 session 的 JSON；只有明確 Developer/Test 匯入操作可讀檔。
- finalization／validation 失敗仍保留前一個有效 record，且 UI 狀態不誤報來源。

完成證據：
- `CaptureService` 以單一 atomic `ReplaySourceSnapshot` 公布 finalization 狀態、finalizing／failed CaptureId、錯誤、completed generation 與 immutable `LastCompletedPull` reference；Plugin 端不再分開讀取 `Status` 與 record。
- `ReplayWindow` 在 finalizing 時維持等待狀態，不載入較舊 record；成功後依最新 generation／CaptureId 背景建立並採用 session。採用前會重新核對 atomic snapshot，因此 finalize 與 UI completion 同 frame 交錯時仍不會短暫採用 stale result。
- `ReplayLoadCoordinator` 為 Runtime 與 Developer/Test 載入發出遞增 request generation；較新的 Runtime Pull 或明確 fixture 選擇可 supersede 舊 task，舊 task 即使稍後完成也無法取代目前 session。
- validation 失敗 snapshot 保留前一個有效 record，Replay source 明確顯示 `previous valid Pull` 與失敗 CaptureId；沒有有效 record 時顯示 empty state，正式 Runtime 路徑不讀取 filesystem。
- 新增 7 個聚焦 regression，涵蓋 finalizing atomic snapshot、成功／失敗狀態、等待舊 Pull、failure source 標示、reload empty policy、連續 Runtime generation 與 Runtime／Developer source supersession；全部通過。
- 完整測試為 Core 72、UI 4、Plugin 40 passed，0 failed。`Debug -p:Platform=x64` solution build 為 0 warnings／0 errors；Core 無 Dalamud reference，變更沒有加入 history／recent／retention／compare、persistent latest 或 cross-session restore。
- `CaptureJsonTests` 與 `ReplaySessionTests` 的 14 個 synthetic tests 全部通過。P10S recorded smoke 實際載入 Capture `0a162e4d-4734-4a45-a056-1ed4e482e839`，render 379 scenes、advance 378 frames，1,000 deterministic seeks 平均 0.0172 ms 且 0 bytes／seek。

### 3. Combat Auto-Hide Behavior — Completed

以 `LiveDataProbe.InCombat` 作為唯一 live combat gate；UI orchestration 只接收 boolean 狀態，不讓 Replay engine 讀取 Dalamud condition 或 live actors。

主要設計：
- 戰鬥中收到 `/rdebrief replay`、Capture 視窗按鈕或其他 open request 時拒絕開窗，不 steal focus。
- 已開啟 Replay 遇到 `InCombat=false → true` 時，在該 UI update 立即 `Pause` 並關閉；hidden／combat 期間不 advance clock、不 rebuild scene。
- 戰鬥結束後不自動重新開窗，避免下一 Pull 中或 Pull 結束瞬間造成意外干擾；玩家必須再次明確開啟。
- Combat gate 與 window/session state transition 抽成不依賴 ImGui 的小型可測協調邏輯，Dalamud window 只負責呈現。

主要驗收條件：
- Combat 中任何入口都不能讓 Replay window 出現或取得焦點。
- Replay 播放中開始下一 Pull，window 立即隱藏且 clock 停止；戰鬥結束後不自行重開。
- Auto-hide 不清除 `LastCompletedPull`；下一次明確開啟仍可檢視最新成功完成的 Pull。

完成證據：
- `Plugin.DrawUi()` 在 `WindowSystem.Draw()` 前只把 `LiveDataProbe.InCombat` boolean 傳給 `ReplayWindow.UpdateUiState()`；Replay window／engine 不讀取 Dalamud condition、ObjectTable 或 live actors。
- `/rdebrief replay` 與 Capture 視窗按鈕共用 `OpenReplayUi()`，並將同一個 boolean 傳入 `ReplayWindow.OpenRuntime(inCombat)`。Combat 中的 request 不設定 `IsOpen`，因此不顯示視窗或 steal focus。
- `ReplayCombatGate` 是不依賴 ImGui／Dalamud 的小型狀態協調器。Combat 中只要 Replay visible、playing 或 loading，就回傳 `HideAndPause`；window 會 pause session、close、清除自動 refresh intent、invalidate pending load 與 elapsed remainder。
- `Draw()` 有 combat guard；Plugin 又在同一 UI update 的 window draw 前先套用 gate，因此下一 Pull 開始時不會 advance clock、採用 load result、重建 scene 或執行 arena／timeline Draw。Combat end 只解除 open prohibition，不會自行重開。
- Auto-hide 不清除 active session 或 `CaptureService` 的 `LastCompletedPull`；戰鬥結束後只有新的明確 open request 才會重新顯示並選取最新成功 Pull。
- 新增 6 個 `ReplayCombatGateTests`，涵蓋 Combat open rejection、visible／playing／loading 三種 hide transition、combat end 不自動重開，以及 hidden 後重複 combat observation 不產生工作；全部通過。Previous-Pull 與 Combat focused tests 合計 11 passed。
- 完整測試為 Core 72、UI 4、Plugin 46 passed，0 failed。`Debug -p:Platform=x64` solution build 為 0 warnings／0 errors；Replay live-data boundary 與 out-of-scope audit均無違規。
- P10S recorded smoke render 379 scenes、advance 378 frames；1,000 deterministic seeks 平均 0.0168 ms 且 0 bytes／seek。本項未以舊 Dalamud log 充當新的實機證據；完整 hot-reload／next-Pull 行為留在 Phase 3 End-to-End 驗收。

### 4. Minimal Performance Safeguards — Completed

只加入 Phase 3 必要保護，不建立新的快取層、history、預載 repository 或 encounter-specific optimization。

主要設計：
- `ReplaySession` construction 留在 background task；UI／Framework thread 只取得 atomic source snapshot、採用完成結果與繪圖。
- Closed、hidden 或 combat 狀態不 advance、不 seek、不 rebuild scene；只有可見且播放中的 window 依 `ImGui.GetIO().DeltaTime` advance。
- 沿用 Core caller-owned scene buffer與二分搜尋；event 顯示維持固定可見筆數，不在每 frame 掃描或 materialize 完整 timeline。
- 以 CaptureId／generation 丟棄 stale task result；dispose 後不得讓完成中的工作回寫 UI state。
- 不引入 per-frame serialization、filesystem I/O 或 live actor traversal。

主要驗收條件：
- 主要八人 fixture 在正式 In-Game window 可 load、seek、play-to-end，且沒有 UI error 或可見戰鬥卡頓。
- 既有 Offline replay seek／allocation regression 仍通過。
- 自動化測試可證明 hidden／combat 狀態不推進 replay，stale load 不會取代最新 CaptureId。
- 實際下一 Pull 開始後，Replay 不在 Draw path 執行 arena／timeline 更新。

完成證據：
- `ReplayFramePolicy` 以 `IsOpen`、combat 與 playing 三個 boolean 決定 Draw／Advance。Closed 或 combat 一律不 Draw／Advance；只有 visible、out-of-combat、playing 才讀取 ImGui delta 並推進 `ReplaySession`。
- `ReplayWindow.UpdateUiState()` 在 hidden 時 pause session、清除 source refresh intent與 elapsed remainder，並取消 pending load adoption；combat auto-hide 走同一條 suspend path。active session 與 `LastCompletedPull` 保留供下次明確開啟。
- `ReplayLoadCoordinator` 為每個 request 建立 cancellation token。新 request、invalidated source、combat hide 或 dispose 都會 cancel 舊工作；token 在 record load 前後、session construction 前後檢查，避免已 supersede 的 load 繼續建立或回寫 session。
- `ReplayWindow` 現在明確實作 `IDisposable`；Plugin 取消 UI callbacks、移除 windows 後先 dispose Replay，再 dispose Probe／live capture。Disposed coordinator拒絕新 load，已完成或稍後完成的舊 task 都無法被採用。
- 最近事件直接使用 `ReplaySession.EventsThroughCurrentTime` 的 `ReadOnlySpan`，只迭代最後固定 12 筆；frame policy 與 recent-window index 的 100,000 次熱路徑測試配置 0 bytes。沒有 per-frame JSON、filesystem、live actor traversal 或完整 timeline materialization。
- 新增 8 個 `ReplayPerformanceSafeguardTests`，涵蓋 closed／combat／paused／playing frame policy、12 筆事件上限、zero-allocation 熱路徑、superseded construction cancellation 與 dispose 後不可採用／不可重新開始；全部通過。Previous-Pull／Combat／Performance focused tests 合計 19 passed。
- 完整測試為 Core 72、UI 4、Plugin 54 passed，0 failed。`Debug -p:Platform=x64` solution build 為 0 warnings／0 errors；Core 無 Dalamud dependency，變更未加入 history／recent／retention／compare、persistent latest、cross-session restore、Debrief 或 Phase 5 UX。
- P10S recorded verification 實際 render 379 scenes、advance 378 frames，20,000 deterministic seeks 平均 0.0160 ms、0 bytes／seek，低於 `< 2 ms`／`< 64 bytes` 門檻。本項未將舊 Dalamud log 當作新證據；正式 In-Game load／seek／play-to-end、next-Pull Draw 停止與 hot reload 留在 Phase 3 End-to-End 驗收。

### 5. Phase 3 End-to-End 驗收 — Completed

完成證據：
- Fresh `Debug -p:Platform=x64 --no-incremental` solution build 為 0 warnings／0 errors。Dev Plugin path 已核對為 `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`；DLL 為 603,648 bytes，`LastWriteTime` 2026-08-10 19:56:51.981878 +08:00，SHA-256 `c56d668bdb805fb5331cc1f6025dc6ead0568d5575597f91a5d7c42635353217`。
- Dalamud 在 build 後於 20:05:33.928 開始 unload、20:05:34.955 完成 unload，並於 20:05:35.792 載入該 DLL、20:05:36.419 完成 Plugin load；之後沒有 `RaidDebrief`／`UiBuilder(RaidDebrief)` error。
- 真實 instanced-duty Wipe Capture `e08965bf-5829-416b-a016-a3a406a329a9` 於 20:08:18.145 開始 finalize，20:08:18.150 完成 validation；Runtime Replay 於 20:08:27.240 載入同一 CaptureId（37 frames／3 events）。實機確認畫面來源 CaptureId 正確且 Play／Pause／Scrub 可操作。
- 下一 Pull 於 20:09:16.592 開始；Replay 在 20:09:16.596 因 `InCombat=true` pause 並 hide。20:09:22.866、20:09:24.414、20:09:26.034 的明確開窗要求均被拒絕；實機確認未搶焦點、遊戲輸入正常，戰鬥結束後沒有自動重開。第二個 Wipe Capture `e95f967e-df85-4582-a0f4-0879a7665501` 完成 validation 後，20:09:51.040 的 post-combat 明確開啟載入同一最新 CaptureId；20:11:43.464 的手動 Developer/Test 操作亦成功載入主要八人 fixture。
- Fresh 完整測試為 Core 72、UI 4、Plugin 54 passed，0 failed。主要八人 recorded Capture `6fe1b80f-567a-41a3-8912-6d013c137aa7` 完整 render 2,234 scenes、advance 2,233 frames，20,000 deterministic seeks 平均 0.024298 ms、0 bytes／seek。
- Scope audit：`RaidDebrief.Core` 仍無 Dalamud dependency；未加入 history／recent／retention／compare、persistent latest Pull、cross-session restore、Debrief、Phase 5 UX 或其他越界功能。Phase 3 exit criteria 全部通過。

### Phase 3 後續 Replay 修正 — Completed

- **通用場地 fallback**：Core 不含任何 Territory／Map profile 或 encounter geometry；`ArenaProjection.FromPullRecord` 以完整 replay-visible observed bounds 建立最小 40×40 world units 的中性正方形，只在 Dalamud 無法取得有效 Lumina Map row 時使用。正式 In-Game Replay 改由下方 Map 表完整畫布路徑提供 bounds。
- **Wipe 一鍵入口**：`ReplaySourceSnapshot` 保留最近成功 Pull 的客觀 `PullEndReason`。新 completed generation 只有 `DutyWiped` 才顯示一次小型 Replay prompt；clear／manual／failed finalize 不顯示，combat start 立即關閉。按鈕仍走既有 `OpenRuntime(InCombat)` gate。
- **Target Marker**：Framework-thread capture 讀取 `MarkingController.Markers` 的 17 個固定 native slot，並依遊戲實際順序 `Attack1-5／Bind1-3／Stop1-2／Square／Circle／Plus／Triangle／Attack6-8` 映射為 Core ID；只在狀態變化時追加 `TargetMarkerFrame`。JSON、validator、binary-search resolver、`ReplaySession` duration、reusable arena scene、Offline SVG 與 Dalamud PNG rendering 均已接通。舊錯誤 slot 順序的 Capture 會在 `CaptureJson.Deserialize` 依 feature flag 一次性轉為 canonical order；新 Capture 明確寫入 canonical-order feature。
- 自動化回歸為 Core 76、UI 4、Plugin 58 passed，0 failed；新增 native 17-slot 順序、legacy JSON slot 遷移，以及舊 `"all"` 但無 Marker frame 不誤報 Target Marker capture 的回歸。Core 無 Dalamud dependency，沒有 history／recent／retention／compare、persistent latest、cross-session restore、Debrief 或因果推論。
- P10S recorded smoke 完整 render 379 scenes／advance 378 frames，20,000 seeks 平均 0.014472 ms、0 bytes／seek；主要八人 fixture render 2,234 scenes／advance 2,233 frames，20,000 seeks 平均 0.019615 ms、0 bytes／seek。實機 Marker Capture `c1a72067-1783-4fd3-9c9c-5dc97f71ed1d` 亦完整 render 1,320 scenes／advance 1,319 frames，20,000 seeks 平均 0.018908 ms、0 bytes／seek。
- 最終 fresh x64 Debug build 為 0 warnings／0 errors。Dev Plugin path 指向 `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`；Plugin DLL 699,392 bytes、`LastWriteTime` 2026-08-10 22:01:10.560637 +08:00、SHA-256 `066901a38d07345c184c0d4432621a560d2ff0703ae1ad8db7bc378b374f417f`；同目錄 Core DLL 105,984 bytes、`LastWriteTime` 22:01:10.106232、SHA-256 `334e12c01bdf02f6b98f710c0ebf0bda048aece2052c5446ef3ffdb5bacae261`。Dalamud 於 22:01:11.205 unload、22:01:11.216 load，22:01:11.514 確認 17 個 Target Marker textures並完成 Plugin load。
- 真實 manual Capture `c1a72067-1783-4fd3-9c9c-5dc97f71ed1d` 於 21:28:07.809 開始、21:30:19.695 validation 成功，共 1,319 frames／1,799 events／912 Action Effects／7 個狀態變化 Target Marker frames；內容包含指派、移除及重新指派多個 marker。Runtime LastCompletedPull 於 21:30:20.149 載入同一 CaptureId。修正 native slot 順序並 hot reload 後，21:57:45.415 重新載入同一 JSON；實機確認 Bind／Stop 圖示種類正確且仍位於原 Actor。Wipe prompt 的真實 DutyWiped 開啟路徑另已由本節 Item 5 證據通過。
- **Map 表完整畫布**：Dalamud host 在 framework thread 一次讀取 Lumina `Map` 表並建立 1,232 筆可用 canvas definition；正式 Runtime 與 Developer/Test session 均以 Capture 的 `MapId` 選取 `SizeFactor／OffsetX／OffsetY／texture ID`，由同一 transform 建立完整 Map world bounds 與背景 UV。所有 Territory／Map profile 及 encounter hard-code 已移除；缺失或無效 Map row 才退回 Core 的 neutral observed field。
- **視角約束**：Map-backed Replay 載入時以 Lumina row 的 `(-OffsetX, -OffsetY)` 對應畫面中心，並以 `max(1, SizeFactor / 100)` 作為最小 zoom 與初始 pan boundary；不再讀取 PC、Boss 或 Waymark 位置決定 Focus。Reset 回復同一 row-derived 中心／倍率，最大倍率維持 20x；缺失或無效 Map row 使用完整場地 Fit。
- **Lumina 原始值核對**：直接讀取本機 game data 的 1,232 筆可用 `Map` rows，`SizeFactor` 分布為 `95×6／100×246／180×2／200×580／300×2／400×380／800×16`。`Map 79／792／834／926` 均為 `400` 與 offsets `(-100,-100)`，證實不同 Map 不保證不同 `SizeFactor`；Replay 仍逐 Map row 使用其實際值，不建立 encounter profile。
- **最終驗證**：完整自動化測試 Core 77／UI 4／Plugin 67 passed，0 failed。P10S recorded Replay 載入後完整 render 379 scenes／advance 378 frames，20,000 deterministic seeks 平均 0.013730205 ms、0 bytes／seek。Core source audit 無 Dalamud／Lumina reference，production source 無 Territory／Map arena profile。
- Fresh `Debug -p:Platform=x64 --no-incremental` solution build 為 0 warnings／0 errors。Dev Plugin path 已核對為 `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`；DLL 714,752 bytes、`LastWriteTime` 2026-08-10 23:39:46.1257233 +08:00、SHA-256 `0c35abd4ac4b224a135c68de60cb237477ca83b6db72067104d8c8300105f318`。Dalamud 於 23:39:46.725 unload、23:39:46.734 load，23:39:46.916 載入 1,232 筆 Map canvas，23:39:46.917 完成 Plugin load。
- **原生 Waymark 貼圖**：Dalamud Replay 將 A／B／C／D／1／2／3／4 映射至遊戲 icon `61241／61242／61243／61247／61244／61245／61246／61248`，啟動時透過 `ITextureProvider.GetFromGameIcon` 載入並保留 shared texture；Draw 未載入檔案。每張圖以 recorded X／Z 的 screen position 為中心，貼圖未 ready／不可用時保留原有文字 fallback。
- **Waymark 世界比例與驗證**：A-D 依玩家提供的實際尺寸使用 `1.25` world-unit 半徑；1-4 使用 `2.3` world-unit 方形邊長，兩者均沿用 Map bounds 與 viewport zoom 的 world-to-pixel 比例。新增 icon identity、非連號 D／1 mapping、中心 quad 與 zoom regression；完整測試 Core 77／UI 4／Plugin 69 passed，0 failed。Fresh x64 Debug build 0 warnings／0 errors，DLL 716,800 bytes、`LastWriteTime` 2026-08-11 00:10:11.0397427 +08:00、SHA-256 `dabfcef3dca082a98becfb4a9bee0feddbc28030c8d92e8c247024da773df75c`；Dalamud 於 00:10:11.657 load、00:10:11.896 確認 8 張 native Waymark icons 與世界尺寸、00:10:11.897 完成 Plugin load。玩家以含 8 個 Waymark 的 Replay fixture 實機確認圖示種類、中心位置與縮放比例全部正確。
- **Boss／Add 身位無效 Target Ring**：Framework-thread capture 以一次載入的 Lumina `BNpcBase.IsOmnidirectional` catalog，加上 Actor 當下的 `Directional Disregard` status `3808`，記錄每個 sample 的 `IsOmnidirectional`。Core 僅消費 `PullRecord`，Replay 不讀 live StatusList；只有 BattleNpc 會在 480×682 有缺口 Target Circle 與 480×480 完整 Target Ring 間切換，Player 不受影響。舊 Capture 依 `BaseId` 做靜態 fallback，並明示無法還原動態切換。
- **身位圓圈驗證**：完整測試 Core 78／UI 4／Plugin 74 passed，0 failed；涵蓋 Lumina flag／status 3808 precedence、Player 排除、sample boundary、backward seek、Capture／JSON round-trip、current／legacy 選圖、兩張 embedded PNG 尺寸及不同外圈比例。P10S recorded smoke render 379 scenes／advance 378 frames，20,000 seeks 平均 0.013550945 ms、0 bytes／seek。
- Fresh x64 Debug build 0 warnings／0 errors；configured DLL 911,872 bytes、`LastWriteTime` 2026-08-11 00:36:48.1107789 +08:00、SHA-256 `da413f0ab17bab2768134da631463312d0740806936a955027b01af57777dcd7`。Dalamud 於 00:36:48.716 unload、00:36:48.724 load、00:36:48.924 載入 4,216 筆 BNpcBase 身位資料、00:36:48.935 確認 directional／omnidirectional embedded textures，並於 00:36:48.936 完成 Plugin load。
- **SizeFactor Focus 驗證**：Plugin tests `84 passed／0 failed`，涵蓋實際 Lumina 倍率集合、Map 中心、最小 zoom、pan boundary、無效值與缺少 Map 的 Fit 契約。Fresh `Debug -p:Platform=x64 --no-incremental` solution build 為 `0 warnings／0 errors`；configured DLL 為 912,896 bytes、`LastWriteTime` 2026-08-11 02:13:16.5026600 +08:00、SHA-256 `e054f3f519ed7dd8f17aec96701e5ccb0e68019fdecc02605be71c76f6590c65`。Dalamud 於 02:13:17.015 unload、02:13:17.025 load、02:13:17.345 載入 1,232 筆 Map rows，並於 02:13:17.346 完成 Plugin load。
- **每次 Wipe Replay 提示與開關**：`WipeReplayPromptController` 不再於 completed generation 抵達但 `InCombat` 尚未清除時永久消耗該 Wipe；每個驗證成功的 `DutyWiped` generation 會排隊至首次脫戰 UI update，已顯示的提示進入新戰鬥時仍永久關閉。新增預設啟用且持久化的「Wipe 後顯示 Replay 提示按鈕」開關；停用會清除待顯示／已顯示提示且不補顯停用期間的舊 Wipe。提示 regression 4／4、完整 Core 78／UI 4／Plugin 86 passed，0 failed；主要 fixture 完整 render 2,234 scenes／advance 2,233 frames，20,000 seeks 平均 0.02191021 ms、0 bytes／seek。Fresh x64 Debug build 0 warnings／0 errors；configured DLL 913,408 bytes、`LastWriteTime` 2026-08-11 22:33:05.3120808 +08:00、SHA-256 `d87ba17d8be5c0ba2f1c81193ea82b96a08a2255fdd958d739dd5eb76f518960`。Dalamud 於 22:33:05.837 unload、22:33:05.872 load，並於 22:33:06.161 完成 Plugin load；該次載入無 RaidDebrief UI error。
- **戰鬥自動關閉 Replay 開關**：新增預設啟用且持久化的「進入戰鬥時自動關閉 Replay 視窗」設定。啟用時保留既有 pause／cancel pending load／close；停用時既有 Replay 視窗保持可見，但戰鬥期間必定暫停且不 advance，新開 Replay 仍一律拒絕；戰鬥中重新啟用會於下一 UI update 關閉。戰鬥 gate／frame policy／設定 regression 23 passed，完整 Core 78／UI 4／Plugin 91 passed，0 failed；frame policy 維持 0 bytes allocation。Fresh x64 Debug build 0 warnings／0 errors；configured DLL 914,432 bytes、`LastWriteTime` 2026-08-11 22:42:25.9193460 +08:00、SHA-256 `2cc98efb2cdb7ff621157e213ea2ecf825d15b7c2efc683440b52bfab1b7d1dc`。Dalamud 於 22:42:26.439 unload、22:42:26.459 load，並於 22:42:26.714 完成 Plugin load；該次載入無 RaidDebrief UI error。

## Phase 3 收尾結論

- 完成日期：2026-08-11。
- Roadmap exit criteria「Wipe → 開啟 Replay → 檢視上一 Pull」與「下一 Pull 不造成 UI 干擾」均已通過；戰鬥自動關窗允許明確停用，但仍保持暫停、禁止戰鬥中新開與禁止 frame advance。
- 最終 accepted baseline：Core 78／UI 4／Plugin 91 passed，0 failed；x64 Debug solution build 0 warnings／0 errors；configured DLL 914,432 bytes，SHA-256 `2cc98efb2cdb7ff621157e213ea2ecf825d15b7c2efc683440b52bfab1b7d1dc`，Dalamud 於 2026-08-11 22:42:26.714 完成載入且無 RaidDebrief UI error。
- Phase 3 詳細實作、決策與逐次驗證由本文件封存；長期測試證據另保留於 `docs/TEST_RECORDS.md`。後續 `progress.md` 僅追蹤 Phase 4 Debrief MVP，不把已完成的 Replay 修正重新列為未完成工作。


## 依賴與風險決策

1. **先修 source race，再接正式 UI**：若只把測試窗改名，Wipe 後立即開啟仍可能顯示前一 Pull，不能滿足 Phase 3 exit criteria。
2. **不以 JSON 解 runtime 競態**：runtime 的正確來源是 atomic in-memory state；保留的手動 JSON 匯入只供 Developer/Test 介面測試，不能成為 previous-pull fallback。
3. **Combat start 預設 close＋pause，允許明確 opt-out**：預設行為仍最不干擾戰鬥且 combat end 不自動搶焦點；玩家停用自動關閉時只保留既有視窗可見，Replay 仍暫停、取消 pending load、禁止戰鬥中新開與 frame advance，Developer/Test fixture 模式同樣受此 gate 約束。
4. **不提前做 event marker／selected-player focus／Debrief jump**：現有基本 event jump 可保留為既有能力，但 Phase 3 不擴充 Phase 5 UX。
5. **保留單一 Replay window 與兩個明確 source mode**：正式 runtime 與 Developer/Test 手動匯入共用同一套 UI，但載入入口、目前來源與切換操作必須清楚，避免 silent fallback 或建立第二套 replay 行為。

## 必要阻塞事項

目前無阻塞事項。
