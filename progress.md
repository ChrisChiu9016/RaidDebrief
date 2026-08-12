# Raid Debrief 開發進度

最後更新：2026-08-11

## Roadmap 各 Phase 狀態

| Phase | 名稱 | 狀態 |
|---|---|---|
| 0 | Capture Technical Prototype | Completed |
| 1 | Recorder | Completed |
| 2 | Offline Replay | Completed |
| 3 | In-Game Replay | Completed |
| 4 | Debrief MVP | Acceptance Pending |
| 5 | Replay UX | Not Started |
| 6 | Stability | Not Started |
| Extra Goal | Event Correlation | Not Started；Replay MVP 穩定前不得開始 |

## Current Phase

**Roadmap Phase 4 — Debrief MVP**

狀態：`Implementation Complete；Final In-Game Acceptance Pending`

Phase 3 完整進度快照已封存至 `docs/archive/progress-phase-3-2026-08-11.md`；Phase 0～3 的長期 build、test、fixture、效能與 Dalamud 實測證據集中保存在 `docs/TEST_RECORDS.md`。

Phase 4 的目標不是建立戰鬥報表，而是在 Wipe Pull 驗證完成後提供一個緊湊、客觀的 Debrief Summary，讓玩家在數秒內看到第一位死亡、死亡順序與一個有用的 Replay 時間窗，並以一次操作開啟同一個 Runtime Pull 的 Replay。

## Phase 4 範圍

Roadmap 必要輸出：

- Pull number。
- Pull duration。
- Boss HP at end（資料足夠且候選唯一時才顯示）。
- First death。
- Chronological death sequence。
- Wipe timestamp。
- Suggested replay window。
- One-click replay。

本階段不包含：

- Pull History／Recent Pulls／retention／index。
- Persistent latest Pull 或跨 session restore。
- Pull Compare、session statistics 或進度追蹤。
- DPS／rotation analysis。
- 死因、責任、mechanic correctness、策略建議或 blame。
- Phase 5 的 selected-player focus、HP/status detail、額外 event marker UX。
- Event Correlation 或任何未標示的推論。

## 可沿用的 Phase 3 基礎

- `CaptureService.GetReplaySourceSnapshot()` 已原子提供 completed generation、finalization 狀態、CaptureId、end reason 與最新成功的 in-memory `LastCompletedPull`。
- `PullRecord.Events` 保存客觀 `Death`、`AliveTransition`、`DutyWiped` 等事件及 Pull-local `StableActorId`。
- `ReplaySession` 已有一致的 Pull duration、Timeline ordering、seek 與同一 CaptureId 的背景載入防競態。
- 正式 `ReplayWindow` 已有 combat gate、runtime source identity、背景 construction、seek 與 paused session。
- 現有 Wipe prompt 已具備「每個成功 Wipe generation 一次」、等待脫戰、設定停用及 combat dismissal 的 controller 契約，可 clean-cutover 為 Debrief Summary host。
- Runtime 仍只保存 active Pull 與最新成功 completed Pull；Phase 4 不需要 repository 或歷史資料層。

## 客觀資料規則草案

以下規則先作為實作基準；若真實 Wipe fixture 證明資料不足，只能降級為「不顯示／無法取得」，不得改成名稱、戰鬥或 mechanic 猜測。

### Pull number

- 使用本次 Plugin runtime／duty session 內的 automatic Pull ordinal。
- ordinal 在 automatic Pull 開始時配置，只隨目前 active／latest completed Pull 在記憶體流動；Plugin reload 後重設，不寫入 history 或跨 session storage。
- Manual Capture 與 Developer/Test fixture 不偽造正式 Pull number，UI 顯示 `—` 或明確的 `Test` 標示。

### Duration 與 Wipe timestamp

- 抽出一個 Core timing contract，供 Replay 與 Debrief 共用，避免各自重算不同 duration。
- Duration 使用 finalized wall-clock end 與最後 recorded Frame／Event／Waymark／Action Effect／Target Marker timestamp 的最大值。
- Wipe timestamp 只接受 recorded `ObservedEventType.DutyWiped`；缺少該事件時不得以最後死亡或戰鬥脫離推測 Wipe。

### First death 與 death sequence

- 只納入能解析至 `ObjectKind == "Pc"` 的 recorded `Death` 事件。
- 依 `(TimestampMilliseconds, OriginalRecordedIndex)` 穩定排序；同時間事件保留 capture 原始順序。
- sequence 截止於 recorded Wipe timestamp；有 `AliveTransition` 後再次死亡的同一玩家可再次出現，因為那是另一筆客觀 Death。
- Actor association 缺失的事件不顯示虛構玩家；Analyzer 回報資料缺口供 UI 使用中性文字。
- 顯示沿用匿名 Player identity／Job icon，不推測 MT／OT／H1／D2 等隊伍職責。

### Boss HP at end

- 在 Wipe timestamp 解析最後一筆離散 Actor state。
- 候選只接受非 player-owned、具有效 `MaxHp > 0` 的 replay-visible `BattleNpc`。
- 只有候選唯一時才顯示客觀 HP 與百分比；零個或多個候選時顯示 `—`／不提供，不以名稱、BaseId、最高 HP 或 encounter profile 猜 Boss。

### Suggested replay window

- 基準窗口為 first death 前 8 秒至後 12 秒，並 clamp 至 `0..Duration`。
- 若 recorded Wipe 早於建議終點，終點收斂至 Wipe；若 Wipe 更晚，不把 window 無限制延長至 Wipe。
- 沒有可解析 Player Death 時，先以 Wipe 前 20 秒至 Wipe 作為客觀 fallback；沒有 recorded Wipe 時不產生 Wipe Debrief shortcut。
- Summary 顯示完整建議區間；Replay 開啟後 seek 至區間起點並保持 paused，避免自動播放造成新的干擾。

## Phase 4 工作項目與順序

以下項目已依依賴順序完成實作；最終實機驗收仍受第 6 項真實 Capture 條件約束。

### 1. Debrief Domain Contract 與 Timing Source

建立 Dalamud-independent 的 Core contract，例如 `DebriefSummary`、`DebriefDeathEntry`、`ReplayWindowRange` 與共用 Pull timing resolver。

主要工作：

- 定義 required／optional fields、資料缺口與不推論契約。
- 將 Replay 現有 duration 計算抽成 Core 共用來源，保持既有 recorded-data 行為。
- 固定 death ordering、wipe boundary、window clamp 與缺少事件時的 fallback。
- 明確區分客觀 observed values 與 unavailable values；MVP 不建立 confidence inference。

驗收：

- Core 不引用 Dalamud、ImGui、filesystem 或 JSON repository。
- Replay duration regression 不變。
- 對 empty Pull、無 Death、同 timestamp deaths、raise 後再死、缺失 Actor association、缺失 Wipe、短 Pull 與邊界 timestamp 有 deterministic unit tests。

### 2. Debrief Analyzer

以 finalized `PullRecord` 建立小型、可測試的 `DebriefAnalyzer`，一次產出 Summary，不在 ImGui Draw 中掃描完整 Pull。

主要工作：

- 找出 first Player death 與 chronological death sequence。
- 解析 recorded Wipe timestamp。
- 計算 suggested replay window。
- 依唯一候選規則解析 optional Boss HP at end。
- 不修改 Capture；不讀 live objects；不輸出死因或 wipe cause。

驗收：

- Analyzer 對相同 Pull 產生相同 immutable result。
- 合成 fixture 覆蓋單一／多重 Boss 候選、Boss 不可選取、缺 HP、player-owned BattleNpc 排除與多次玩家死亡。
- 真實 recorded fixture 的 Summary 可人工對照原始 Death／DutyWiped events。

### 3. Runtime Pull Ordinal 與 Atomic Debrief Handoff

把 session-local automatic Pull ordinal 與 Debrief result 綁定至完成 generation／CaptureId，避免 Summary 與 Replay 指向不同 Pull。

主要工作：

- 在 automatic Pull 開始時配置 in-memory ordinal；不持久化、不建立列表。
- successful validation 後原子 publish `LastCompletedPull + DebriefSummary + ordinal + end reason`。
- validation／analysis failure 保留上一個完整有效 handoff，不混用新 Summary 與舊 Pull。
- manual／Developer-Test source 保持明確標示，不污染 automatic ordinal。

驗收：

- 連續至少三個 Pull 的 ordinal、CaptureId 與 Summary 不交叉。
- finalization race、failed validation、failed analysis、Plugin reload 與 manual Capture 皆有 lifecycle regression。
- Runtime 仍只保留最新成功 Pull，沒有 history collection、repository 或 startup scan。

### 4. Compact Debrief Summary Window

以 Phase 4 Summary clean-cutover 現有 `WipeReplayPromptWindow`；不並存第二套 post-Wipe popup。

主要工作：

- 顯示 Pull number、duration、optional Boss HP、first death、death sequence、Wipe timestamp 與 suggested replay range。
- 使用既有 Job icon／匿名 Player label presentation；保持資訊層級緊湊，不變成 dashboard。
- 只有 successful `DutyWiped` handoff 顯示；clear／manual／failed finalization 不彈出。
- 沿用每個 generation 一次、等待脫戰、combat dismissal 與使用者設定；設定文案 clean-cutover 為「Wipe 後顯示 Debrief 摘要」。
- 若需改名 serialized setting，使用一次性 configuration version migration 保存既有選擇，完成後不留下兩套 alias。

驗收：

- 第一位死亡與 sequence 在不開 Replay 的情況下數秒內可讀。
- 無 Death／無 Boss HP 等缺口以中性 `—` 呈現，不出現原因推測。
- 連續 Wipe 每次各顯示一次；停用設定、combat start、clear、manual 與 failed finalization 行為不回歸。

### 5. One-Click Suggested Replay Handoff

讓 Debrief 按鈕只開啟其自身 CaptureId／generation 的 Runtime Replay，並 seek 至 suggested range 起點。

主要工作：

- 擴充 runtime open request，攜帶 expected CaptureId、completed generation 與 requested timestamp。
- 背景 session completion 只在 source identity 仍相符時採用；較新的 Pull 不得讓舊 Debrief 靜默跳至新 Replay。
- session adoption 後 clamp／seek 到建議起點並保持 paused；Replay UI 顯示建議區間但不新增 Phase 5 event-marker UX。
- 所有入口仍通過既有 combat gate；戰鬥中不開窗、不搶焦點。

驗收：

- Debrief CaptureId、載入 Replay CaptureId 與 seek timestamp 完全一致。
- 測試 blocked construction、來源 supersession、重複點擊、manual fixture 切換、combat rejection 與 window disposal。
- 沒有 JSON round-trip、latest repository lookup 或跨 session fallback。

### 6. Phase 4 End-to-End 與效能驗收

以 synthetic fixture、既有 recorded fixtures 與至少一個新的真實 Wipe 完成產品流程驗收。

必要情境：

- Wipe 含第一位死亡、多位死亡與至少一次 raise 後再死亡。
- 無玩家死亡資訊的 Wipe。
- 單一 Boss HP 可用與多候選不可安全顯示。
- 連續兩次以上 Wipe，每次 Summary／Replay 均指向同一個最新 completed Pull。
- Summary 顯示後開始下一 Pull，預設 auto-close 與 opt-out 行為皆不干擾戰鬥。

完成證據：

- Core／UI／Plugin relevant tests 與完整 tests 全部通過。
- Synthetic Replay data 仍可完整載入並通過 deterministic seek／allocation 門檻。
- Fresh `Debug -p:Platform=x64 --no-incremental` solution build 為 0 warnings／0 errors。
- 核對 Dev Plugin path、x64 Debug DLL `LastWriteTime`／hash、post-build unload／load 與無 `UiBuilder(RaidDebrief)` error。
- 實機確認 Wipe → Summary → one-click Replay suggested start，以及下一 Pull combat non-interference。

## Phase 4 實作與目前證據

- `PullTiming` 統一 Replay／Debrief duration；`DebriefAnalyzer` 在 Core 只讀 finalized `PullRecord`，輸出 immutable Summary，涵蓋客觀 Boss HP 唯一候選、Player Death／raise 後再死亡順序、Wipe timestamp、資料缺口與 suggested range。
- `CaptureService` 為 automatic Pull 配置 process-local ordinal，且只在 validation 與 analysis 皆成功後原子發布 `PullRecord + DebriefSummary + end reason + completed generation`；失敗保留上一個完整 handoff。
- 原 `WipeReplayPromptWindow` 已 clean-cutover 為唯一的 `DebriefSummaryWindow`。設定 UI 改為「Wipe 後顯示 Debrief 摘要」，serialized JSON key 保持 `ShowWipeReplayPrompt` 以保存既有選擇，不存在第二套設定或 popup。
- Summary 顯示 Pull number、duration、Boss HP／`—`、First Death Job icon／匿名 Player label、完整 death sequence、Wipe timestamp 與 Replay range。每個成功 Wipe generation 各顯示一次；disabled、combat dismissal、clear、manual、failed 或 Pull／Summary identity mismatch 不顯示。
- One-click request 攜帶 exact completed generation、CaptureId 與 range；Runtime source identity 在開始與背景完成時都必須一致，之後使用同一 `ReplaySession` seek 至 start 並保持 paused。Replay UI 顯示建議區間；沒有 JSON、repository、history 或跨 session fallback。
- 自動化目前為 Core 84／UI 4／Plugin 93 passed、0 failed；其中 Phase 4 focused Core 6 與 Plugin 20 passed。主要 synthetic fixture 完整 render 2,234 scenes、advance 2,233 frames，20,000 deterministic seeks 平均 0.02629414 ms、0 bytes／seek。
- 真實 `DutyWiped` Capture `c1a72067-1783-4fd3-9c9c-5dc97f71ed1d` 已由正式 Analyzer 解析：duration 131,883 ms、Wipe 120,679 ms、Boss `ケフカ` 16,531,144／44,109,275 HP（37.4777%）、8 位 Player Death、0 unresolved，suggested range 104,706..120,679 ms。這份 Capture 沒有 Raise／再次死亡，因此只證明真實多位死亡與 Boss HP，不足以取代 Exit Criteria 要求的新 Raise／再死 Wipe 實機驗收。
- Final source full suite: Core 84／UI 4／Plugin 93 passed、0 failed；`dotnet format --verify-no-changes` 通過。Fresh `Debug -p:Platform=x64 --no-incremental` build 為 0 warnings／0 errors；Dev Plugin path 指向 configured `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`，DLL 923,648 bytes、`LastWriteTime` 2026-08-11 23:31:07.7899372 +08:00、SHA-256 `89928c1b3239d11d88e0fc5fbfb158ed18085c97f7ba53d8a49781b9db8bb378`。Dalamud 於 23:31:08.310 unload、23:31:08.370 load，並於 23:31:08.938 完成 Replay／Debrief windows load；沒有 RaidDebrief UI error。

## Phase 5 Replay UX 實作與目前證據

- 正式 Replay UI clean-cutover 為當前使用者介面：保留播放／暫停、timeline scrubbing、玩家 Focus 與可展開的完整 Player Death 清單；死亡按鈕定位至 exact recorded timestamp 並暫停。
- 已移除 `Active recorded statuses`、timestamp HP／status detail、彩色 timeline event markers、最近事件，以及 Cast／Status／Raise／Wipe 等 event jump。Runtime／手動 JSON 來源資訊與匯入控制只存在於預設收合的「進階／開發測試」。
- Focus 只改變畫面強調：選定 Player 維持正常 opacity，其餘 Player 與 Boss／Add 淡化。使用者額外放大後，camera 在既有安全 Map pan boundary 內鎖定可見 Player；暫時 despawn 保持最後中心，手動拖曳立即取消 Focus。
- 使用者於 2026-08-12 實機確認上述功能正常，並決定目前精簡呈現可完成本次範圍；整體 layout 與 HP 呈現留待之後獨立的全介面改版。
- 自動化完整 suite 為 Core 84／UI 4／Plugin 94 passed、0 failed；Replay UX focused tests 11 passed、0 failed，涵蓋僅 Player Death 可跳轉、Focus opacity、marker lookup、camera safe-boundary clamp 與 frame policy allocation contract。`dotnet format --verify-no-changes` 通過。
- 主要 synthetic fixture 完整載入並 render 2,234 scenes、advance 2,233 frames；20,000 deterministic seeks 平均 0.0196602 ms、0 bytes／seek。
- Fresh `Debug -p:Platform=x64 --no-incremental` solution build 為 0 warnings／0 errors。Dev Plugin path 仍指向 configured `src/RaidDebrief.Plugin/bin/x64/Debug/RaidDebrief.dll`；DLL 925,696 bytes、`LastWriteTime` 2026-08-12 00:31:56.0001492 +08:00、SHA-256 `2753d7aae786334d1a2656abd1dbb3879dfbdef12b3bd83526eea457eb3ebb00`。Dalamud 於 00:31:56.615 unload、00:31:56.643 load，並於 00:31:56.925 完成 Replay／Debrief windows load。

## Phase 4 Exit Criteria

- 成功驗證的 Wipe Pull 能在脫戰後顯示同一 CaptureId 的緊湊 Summary。
- 玩家能立即辨識第一位死亡與 chronological death sequence；所有內容均為 recorded observation。
- 一鍵 Replay 在數秒內開啟同一 Pull 並定位至 useful suggested window 起點。
- 缺失或含糊資料會明確降級，不會猜 Boss、死因、責任或 mechanic。
- 下一 Pull 不產生 UI／focus／playback 干擾。
- 不引入 persistent history、session analytics、Core→Dalamud dependency 或其他 Phase 5／Extra Goal 功能。

## 依賴與風險決策

1. **先固定 Core contract，再做 UI**：若 Summary 計算藏在 ImGui Draw，會難以測試、重複掃描 Pull，且容易讓 Replay 與 Debrief 的 timing 規則分歧。
2. **Summary 與 Replay 必須用 CaptureId／generation 綁定**：只讀「當下 latest」會在下一 Pull 完成時把舊 Summary 按鈕導向錯誤 Replay。
3. **Pull number 只做 session-local ordinal**：MVP 不為顯示編號建立 persistent history、session statistics 或跨 reload state。
4. **Boss HP 寧缺勿猜**：Actor model 沒有通用 encounter boss identity；多候選時顯示 unavailable 比依名稱／BaseId／最大 HP 推測更符合 objective-first 原則。
5. **Death sequence 是 correlation，不是 causality**：只顯示 recorded ordering；不得用「第一死導致 Wipe」等措辭。
6. **現有 Wipe prompt 採 clean cutover**：Phase 4 Summary 取代 prompt，不新增另一個 popup 或第二套 generation controller。
7. **Suggested window 是 presentation rule**：固定、可測、可調整的時間窗不是 mechanic inference；本階段不新增 Phase 5 event marker 或 selected-player focus。

## 必要阻塞事項

目前沒有阻塞 Phase 4 Item 1～3 的事項。

Item 4～6 在最終驗收前需要一個包含 `DutyWiped`、多位 Player Death、可解析 Boss HP 與至少一次 raise／再次死亡的真實 Capture；在取得該資料前可先用 synthetic fixture 完成 deterministic contract 與 UI wiring，但不得把 synthetic-only 結果當作 Phase 4 實機完成證據。
