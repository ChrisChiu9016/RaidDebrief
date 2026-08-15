# Raid Debrief

高難度副本 Pull 結束後的**即時回放**插件（FFXIV / Dalamud）。

Raid Debrief 只回答一個問題：**剛才那把發生了什麼，能不能馬上重看一次？**
它不教機制、不評斷玩家、不取代 FFLogs／ACT，也不保存跨場次的戰鬥歷史。

- Debrief Summary：Pull 長度、首殺（First Death）、死亡順序。
- 2D Replay：玩家／Boss 位置、場地標記（Waymark）、目標標記、詠唱、狀態、時間軸拖曳與事件跳轉。
- 指令：`/rdebrief`、`/rdb` 開啟 Replay 主畫面。

## 安裝（Dalamud 自訂儲存庫）

1. 遊戲內輸入 `/xlsettings`，開啟 **Experimental**（實驗性功能）分頁。
2. 在 **Custom Plugin Repositories** 貼上：

   ```text
   https://raw.githubusercontent.com/ChrisChiu9016/RaidDebrief/main/repo.json
   ```

3. 按 **+**，然後 **Save and Close**。
4. `/xlplugins` → **All Plugins** 搜尋 `Raid Debrief` → **Install**。

需求：Dalamud API Level 15（Dalamud 15.x）。

## 從原始碼建置

需要 .NET 10 SDK，以及由 XIVLauncher 安裝的 Dalamud（或設定 `DALAMUD_HOME` 指向 Dalamud 目錄）。

```bash
# 開發用（Dev Plugin 熱重載使用 bin/x64/Debug/RaidDebrief.dll）
dotnet build RaidDebrief.sln --configuration Debug -p:Platform=x64

# 發布用套件
dotnet build src/RaidDebrief.Plugin/RaidDebrief.Plugin.csproj --configuration Release -p:Platform=x64
```

Release 建置會由 DalamudPackager 產生：

- `src/RaidDebrief.Plugin/bin/x64/Release/RaidDebrief/latest.zip` — 安裝用封裝。
- `src/RaidDebrief.Plugin/bin/x64/Release/RaidDebrief/RaidDebrief.json` — 由 `src/RaidDebrief.Plugin/RaidDebrief.json` 與組件版本合併出的 manifest。

測試：`dotnet test RaidDebrief.sln`。`testdata/` 是本機專用、不上傳 GitHub 的錄製資料；缺少 fixture 時，相依的錄製回歸測試會在編譯期排除並發出建置警告，其餘測試照常執行。
