# Raid Debrief 開發進度

最後更新：2026-08-09

## Roadmap 各 Phase 狀態

| Phase | 名稱 | 狀態 |
|---|---|---|
| 0 | Capture Technical Prototype | Completed |
| 1 | Recorder | Completed |
| 2 | Offline Replay | Completed |
| 3 | In-Game Replay | Not Started |
| 4 | Debrief MVP | Not Started |
| 5 | Replay UX | Not Started |
| 6 | Stability | Not Started |
| Extra Goal | Event Correlation | Not Started；Replay MVP 穩定前不得開始 |

## Current Phase

**Roadmap Phase 2 — Offline Replay**

狀態：`Completed`

結果：finalized `PullRecord`／明確的 Developer/Test JSON fixture 已可在不啟動 FF14 或 Dalamud 的情況下，從 00:00 完整重播至 Pull 結束；replay clock、2D arena、Player／Boss markers、timeline、play／pause、scrubbing、death state 與 Waymarks 均已整合並完成端到端、代表時間點與效能驗收。

下一階段為 **Roadmap Phase 3 — In-Game Replay**，狀態仍是 `Not Started`；不得把 Phase 2 Developer/Test adapter 視為 Phase 3 正式 UI。

Phase 2 不包含 Phase 3 In-Game Replay、Debrief MVP、後續 Replay UX、Event correlation、Pull History／Recent Pulls、Pull Compare 或其他未列於 Roadmap Phase 2 的功能。

## 已完成能力摘要

### Roadmap Phase 0 — Capture Technical Prototype

- Dalamud plugin、Framework-thread live probe 與遊戲內資料來源驗證完成。
- Party／Actor／Boss、位置、旋轉、HP、Cast、Status、Waymark、Action Effect 與 Duty lifecycle 的可用性和已知限制均已確認。

### Roadmap Phase 1 — Recorder

狀態：`Completed`

- 完成 Pull lifecycle、Pull-local Actor registry、10 Hz sampling、客觀事件、Waymark timeline、Action Effect、schema versioning、validator 與 Developer/Test JSON save/load。
- Runtime 使用 `ActivePull → finalized/validated PullRecord → LastCompletedPull`；不依賴 JSON repository、persistent index、跨 session Latest 或 Pull History。
- Automatic Capture 僅在 Dalamud duty instance 內 armed／開始；Manual Capture 保持不受限的 Debug/Test 工具。
- 多個連續 Pull、Wipe／Clear、combat-end debounce、reload/dispose、Boss phase transition、驗證失敗保護與 Pull 隔離均有自動化或遊戲內證據。
- 真實八人 Pull 已驗證長時間 Position／Rotation／HP、Boss、Cast、Status、Death／復活觀察、Damage／Heal、Waymark snapshot 與 DutyCompleted；無可見戰鬥卡頓。
- 非網路玩家代理 `EntityId == 0xE0000000` 已在 recorder 邊界排除；後續八人 frames 為八名有效玩家，無重複 EntityId／GameObjectId。
- 詳細 build、test、Capture 與遊戲內證據已封存於 `docs/TEST_RECORDS.md`。

## Phase 2 工作目標與完成進度

七項工作已依賴順序完成。

### 1. Replay Clock — Completed

實作純 Core `ReplayClock`，作為所有離線播放控制的唯一時間來源。

主要驗收條件：
- `Play`／`Pause`／`Advance` 行為可由單元測試決定性重現。
- `Seek` 將時間限制在 `0..DurationMilliseconds`。
- 到達 Pull 結尾時停止播放，且超大 elapsed value 不溢位。
- 零長度 Pull 不開始播放；負 duration／elapsed 明確拒絕。
- 不依賴 Dalamud、牆鐘或背景執行緒。

### 2. 真實八人 Fixture 基準 — Completed

使用修正後 Recorder 產生的 Phase 1 Capture `6fe1b80f-567a-41a3-8912-6d013c137aa7`，以不變更位元組的方式建立隱私審查後 `testdata/recorded` fixture；來源與 fixture SHA-256、byte length 及轉換規則均保存於 provenance manifest。

主要驗收條件：
- 可由 `CaptureJson.Load` 離線載入並通過 `PullRecordValidator`。
- 不含原始玩家名稱或 Content ID。
- 保留八名有效玩家、完整 Pull 時序、Boss、事件、Waymark 與 Action Effects。
- fixture 的關鍵數量、duration、timestamp 單調性及 provenance 由測試固定。

完成證據：
- `CaptureJson.Load` 與 `PullRecordValidator` 通過；fixture regression 固定 CaptureId、SHA-256、37,652,192-byte 長度、223.202 秒 duration、2,233 frames、88 actors、1,495 events、1,432 Action Effect batches 與一個 Waymark frame。
- 八名 PC 均為 Pull-local `Player 1..8` alias；沒有 Content ID、無效玩家 EntityId、重複 StableActorId 或重複 GameObjectId。
- Frame timestamps 嚴格遞增，Events／Action Effects timestamps 單調；DutyCompleted 前每個 frame 均有八名有效玩家。
- 此 Pull 沒有玩家 Death／AliveTransition，且 Waymark frame 內沒有 active marker；這些狀態仍須由合成 fixture 覆蓋，真實死亡／復活觀察可由隱私處理後的既有 Capture `64c68b21-490a-458c-b59a-fabc3ea5c9b7` 補充。

### 3. Timestamp Actor-State Resolver — Completed

以 finalized `PullRecord` domain data 建立可在任意 timestamp 查詢的 replay state，初始化時建立 Actor sample 索引並以二分搜尋解析；Resolver 不載入 JSON 或存取 filesystem。

主要驗收條件：
- Start／middle／end 與超出邊界時間均有明確結果。
- Position 在同一 recorded presence interval 的相鄰 samples 間使用 linear interpolation，不在最後 sample 之後外插。
- Rotation 使用 radians shortest-angle interpolation，處理 $2\pi$ wrap-around並正規化至 $[-\pi,\pi)$；精確 $\pi$ tie 經由 $-\pi$。
- HP／MaxHP／dead／targetable 使用 timestamp 以前最後一次有效 observation，不以未來離散狀態回推。
- Actor 暫時缺席、spawn／despawn 與 Boss phase transition 不產生虛假位置。
- 合成 fixture 與真實八人 fixture 的代表時間點結果穩定。

完成證據：
- `ActorStateResolver` 只接收 finalized `PullRecord`，為各 Actor 預建 sample／lifecycle 索引；單一 Actor 與全場查詢均以二分搜尋定位 timestamp，不載入 JSON 或存取 filesystem。
- `ResolveAll` 使用 caller-provided `Span<ResolvedActorState>`，可重用輸出 buffer；Actor metadata 保持 Pull-local stable identity。
- 合成測試固定 Position linear interpolation、Rotation shortest-angle wrap-around與 $\pi$ tie、離散狀態不讀取未來、查詢邊界及 recorded despawn／spawn interval。
- 真實八人 fixture 在 100.000 秒解析出八名 PC，跨其他 timestamp 後重查同一時間得到完全相同結果；Pull 結束後的 recorded player despawn 亦正確反映。
- Focused Resolver／recorded fixture regression：11 passed，0 failed。

### 4. Replay Timeline 與 Waymark State — Completed

建立指定時間的 events、death state、timeline markers 與 Waymark snapshot 查詢。

主要驗收條件：
- Events 依 `(timestamp, original recorded index)` 穩定排序；相同 timestamp 保留來源 collection 順序，不建立事件類型優先級。
- Death／alive state 與 recorded observations 一致，不推論 Raise 原因或責任。
- Waymark 使用指定時間之前最後一個有效 frame；尚未有 frame 時回傳明確空狀態。
- Scrub 前後重複查詢得到相同結果。

完成證據：
- `ReplayTimeline` 只接收 finalized `PullRecord`，保存每筆 Event 的 original recorded index，初始化後以二分搜尋提供 through／exact／inclusive range 的無配置 `ReadOnlySpan` 查詢。
- 相同 timestamp 依 original recorded index 排序；Death／AliveTransition 保持原始 observed event，當下 dead／alive state 由 `ActorStateResolver` 的 recorded discrete observation 提供，不推論 Raise 或事件因果。
- `WaymarkStateResolver` 以二分搜尋回傳 timestamp 以前最後一個 Waymark frame；第一個 frame 前回傳空 state，重複正向／反向查詢不配置或改變結果。
- 合成測試固定相同 timestamp 原始順序、inclusive range、Death／AliveTransition 與 actor state 一致，以及 Waymark before／exact／between／after semantics。
- 真實八人 fixture 的 1,495 Events 皆符合 `(timestamp, original index)`，包含 31 筆 BattleNpc Death、無 AliveTransition；唯一 Waymark frame 可在 timestamp 1 解析，反向 scrub 結果一致。
- Focused Timeline／recorded fixture regression：7 passed，0 failed。

### 5. 2D Arena Scene 與 Renderer — Completed

建立不依賴 Dalamud 的 arena projection／render scene，再接入 Offline UI renderer，呈現 Player、Boss、朝向、death state 與 Waymarks。

主要驗收條件：
- 世界座標至 2D arena 座標轉換可測且不改變 recorded data。
- Player／Boss／Waymark markers 使用穩定 identity 與可辨識狀態。
- 同一 timestamp 產生決定性的 render scene。
- Renderer 專案不引用 Dalamud、live game objects、`CaptureJson` 或 filesystem；只接收 resolved scene domain data。

完成證據：
- `ArenaProjection` 將 recorded world X／Z 映射至 normalized 2D 座標，依完整 Pull 與 active Waymarks 一次建立穩定 bounds；投影不修改 recorded samples，facing 以 recorded radians 轉為 2D unit vector。
- `ArenaSceneBuilder` 直接消費 finalized `PullRecord`，重用 actor-state scratch buffer 與 caller-owned `ArenaRenderScene`；同一 timestamp 產生相同 marker order、位置、朝向、HP、dead／targetable 與 Waymark state。
- Player marker 使用所有 recorded PC；BattleNpc marker 使用 Pull 中曾被客觀觀察為 targetable 的 BattleNpc，因此包含 Boss／可互動敵人並排除從未 targetable 的 helper actors，不加入 encounter-specific 分類。
- 新增不依賴 Dalamud 的 `RaidDebrief.UI` 與 `SvgArenaRenderer`；Renderer 只接收 `ArenaRenderScene`，以 SVG 顯示 arena grid、P1–P8、BattleNpc、facing、dead／untargetable 與 active Waymarks，不讀取 JSON 或 filesystem。
- 合成 Arena／fixture regression：6 passed，0 failed；`RaidDebrief.UI.Tests`：2 passed，0 failed。
- 真實八人 fixture 在 100.000 秒產生固定 `60,60..140,140` world bounds、八名 Player 與兩名曾 targetable BattleNpc、零 active Waymark；瀏覽器實際載入 SVG，確認 10 個 actor markers、時間 `01:40.000` 與匿名 P1–P8 labels。

### 6. Offline Timeline 與播放控制整合 — Completed

由 Offline host 將 Developer/Test JSON 載入為 `PullRecord`，再將 ReplayClock、state resolver、timeline、arena renderer、play／pause 與 scrubbing 組成可執行的 Offline Replay；Replay 元件本身不載入 JSON 或存取 filesystem。

主要驗收條件：
- 可選擇 Developer/Test fixture 並從 00:00 播放到結束。
- Play／pause／scrub 同步更新 arena、death state、Waymarks 與 timeline。
- 重複 scrub 至同一 timestamp 顯示相同位置與狀態。
- 不需要啟動 FF14 或載入 Dalamud。

完成證據：
- `ReplaySession` 直接接收 finalized `PullRecord`，一次建立 `ReplayClock`、`ReplayTimeline`、`ArenaProjection`、Actor／Waymark resolver 與可重用 `ArenaRenderScene`；play／pause／advance／seek 均保持 scene、death／targetable、Waymarks 與 events-through-time 使用同一 timestamp。
- 新增可執行的 `RaidDebrief.Offline` local web host；只有 host 邊界使用 `CaptureJson.Load`，瀏覽器提供 Play、Pause、Scrub、全事件 timeline、event playhead 與 2D SVG arena。
- 真實八人 fixture 的完整 Replay duration 為 `223,226 ms`。Chromium 在 `100,000 ms` 顯示八名 Player、兩名 BattleNpc、694 筆已發生事件；Pause 後 timestamp 保持不變，重複 seek 至 `100,000 ms` 產生完全相同 SVG，並由結束前播放至 `223,226 ms` 後自動停止。
- 新增 Developer/Test-only 遊戲內 `ReplayTestWindow`，可使用 in-memory `LastCompletedPull` 或明確 JSON path 背景載入，提供 Play／Pause／Scrub／event jump 與 ImGui arena；`/rdebrief replay` 或 Capture 視窗按鈕可開啟。此畫面是 Phase 2 測試 adapter，不是 Phase 3 正式 In-Game Replay UI。
- Dalamud x64 Debug hot reload 後，真實 fixture 在遊戲內測試視窗成功背景載入 2,233 Frames／1,495 Events，並於 `100,000 ms` 實際執行 ImGui render path，繪製 10 個 Actor markers，無 load／UI error。
- `ReplaySessionTests`：2 passed，0 failed；完整 Core／UI／Plugin tests：65／2／16 passed，0 failed；x64 Debug solution build：0 warnings，0 errors。

### 7. Phase 2 End-to-End 與效能驗收 — Completed

使用 Phase 1 真實八人 fixture 完成 Offline Replay 全流程回歸。

主要驗收條件：
- 完整 recorded Pull 可從開始視覺重播至結束。
- Start／death／復活觀察／Boss transition／end 等代表時間點可重現。
- Scrubbing 穩定且不因長 Pull 每次進行全量掃描。
- Build、Core tests、fixture regression 與 Offline Replay smoke test 全部通過。

完成證據：
- `ReplayEndToEndTests` 將 2,233 個 recorded frame 逐一 Seek／建構 scene，再反向逐一 Seek 並比較 state hash；Add spawn／death／despawn、Boss defeated／untargetable、DutyCompleted 與 end state 均通過。合成完整 Pull 另固定 player Death／AliveTransition、Waymark active／inactive、Boss despawn／respawn 與播放至結束。
- `RaidDebrief.Offline --verify --seek-iterations 20000` 對主要 fixture 完成 2,234 次 SVG render 與 2,233 次 playback advance，完整 replay checksum 為 `14221742015303217744`；最終 20,000 次決定性亂序 Seek 平均 `0.0244 ms`、每次配置 `0 bytes`，低於 `2 ms`／`64 bytes` 上限。
- 同一驗證模式對既有隱私審查真實 Capture `64c68b21-490a-458c-b59a-fabc3ea5c9b7` 重現 `203,200 ms` 玩家 Death 與 `218,204 ms` AliveTransition；20,000 次 Seek 平均 `0.0189 ms`、每次配置 `0 bytes`。
- Chromium 對主要 fixture 實際驗收 start、首 frame、first death、Add spawn／death／despawn、DutyCompleted、Boss defeated 及 end；反向後重查 `32,300 ms` 產生完全相同 SVG。次要真實 Capture 在 death／alive 時間分別呈現 1／0 個 dead marker，反向後重查 alive scene 亦完全相同。
- Dalamud x64 Debug 實際載入主要 fixture，ImGui Draw path 依序重現 `0`、`1`、`9,004`、`10,602`、`32,300`、`36,303`、`215,373`、`216,403`、`223,226 ms`；各點 Player／BattleNpc／dead／events 數量與 Offline 結果一致，無 Replay load 或 UI error。驗收用自動開窗／逐點 instrumentation 已移除。
- 最終完整測試：Core 67 passed、UI 2 passed、Plugin 16 passed，0 failed。
- 最終 `Debug -p:Platform=x64` solution build：0 warnings、0 errors；configured `RaidDebrief.dll` 為 109,568 bytes，`LastWriteTime` 2026-08-09 17:02:03 +08:00，Dalamud 於 17:02:04 完成 unload／load。
- 依賴與範圍稽核：Replay Core／`RaidDebrief.UI` renderer 不引用 Dalamud、`CaptureJson`、filesystem、JSON、live game objects；JSON 僅存在於 Offline／Developer-Test 載入邊界。沒有新增 history／recent／retention／compare、Debrief、因果推論、encounter-specific logic 或 Phase 3 正式 Replay flow。

## 必要阻塞事項

目前無阻塞事項。
