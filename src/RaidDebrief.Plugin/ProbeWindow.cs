using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed class ProbeWindow : Window, IDisposable
{
    private readonly LiveDataProbe probe;
    private readonly CaptureService captureService;
    private readonly ActionEffectReader actionEffectReader;
    private readonly Action openReplay;
    private ulong selectedActorId;

    public ProbeWindow(
        LiveDataProbe probe,
        CaptureService captureService,
        ActionEffectReader actionEffectReader,
        Action openReplay)
        : base("Raid Debrief — Capture 與即時資料##RaidDebriefProbe")
    {
        this.probe = probe;
        this.captureService = captureService;
        this.actionEffectReader = actionEffectReader;
        this.openReplay = openReplay;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() =>
        this.probe.SetProbeRefreshEnabled(false);

    public void SetEmbeddedVisible(bool visible) =>
        this.probe.SetProbeRefreshEnabled(visible);

    public void DrawEmbedded() =>
        this.DrawContents();

    public override void OnOpen()
    {
        this.probe.SetProbeRefreshEnabled(true);
        base.OnOpen();
    }

    public override void OnClose()
    {
        this.probe.SetProbeRefreshEnabled(false);
        base.OnClose();
    }

    public override void Draw() =>
        this.DrawContents();

    private void DrawContents()
    {
        this.DrawProbeSummary();
        this.DrawCapture();

        var localPlayer = this.FindActor(this.probe.LocalPlayerGameObjectId);
        this.DrawActor("Local Player", localPlayer);
        this.DrawParty();
        this.DrawActorPicker();

        if (this.selectedActorId == 0)
        {
            this.selectedActorId = this.probe.LocalPlayerGameObjectId;
        }

        this.DrawActor("選定 Actor", this.FindActor(this.selectedActorId));
    }

    private void DrawProbeSummary()
    {
        var isHealthy = this.probe.LastError is null;
        var statusColor = isHealthy
            ? new Vector4(0.35f, 0.85f, 0.45f, 1f)
            : new Vector4(0.95f, 0.45f, 0.35f, 1f);
        var statusText = isHealthy ? "Framework probe 正常" : "Framework probe 發生錯誤";

        ImGui.TextColored(statusColor, statusText);
        ImGui.Separator();
        ImGui.TextUnformatted($"插件版本：{Plugin.PluginInterface.Manifest.AssemblyVersion}");
        ImGui.TextUnformatted($"登入：{FormatBoolean(this.probe.IsLoggedIn)}");
        ImGui.TextUnformatted(
            $"Territory：{this.probe.TerritoryType}  Map：{this.probe.MapId}  Instance：{this.probe.Instance}");
        ImGui.TextUnformatted(
            $"Duty instance：{FormatBoolean(this.probe.IsInDutyInstance)}  InCombat：{FormatBoolean(this.probe.InCombat)}");
        ImGui.TextUnformatted($"Framework thread：{FormatBoolean(this.probe.IsOnFrameworkThread)}");
        if (this.probe.IsInDutyInstance || this.captureService.IsRecording)
        {
            ImGui.TextUnformatted(
                $"ObjectTable：Player {this.probe.PlayerCount} / Battle NPC {this.probe.BattleNpcCount}");
        }
        else
        {
            ImGui.TextDisabled(
                "閒置狀態：instance 外僅執行 lifecycle gate，不掃描 Party / Actor / Status。");
        }

        ImGui.TextUnformatted(
            $"Framework probe：{this.probe.LastCallbackMilliseconds:F3} ms  最大 {this.probe.MaximumCallbackMilliseconds:F3} ms");
        ImGui.TextUnformatted(
            $"Framework callbacks：{this.probe.FrameworkCallbackCount:N0}  " +
            $"Full scans：{this.probe.FullScanCount:N0}");
        ImGui.TextUnformatted(
            $"Errors：{this.probe.ErrorCount:N0}  " +
            $"略過失效 Actor：{this.probe.RejectedVolatileActorReadCount:N0}");

        if (this.probe.LastError is not null)
        {
            ImGui.TextWrapped($"最後錯誤：{this.probe.LastError}");
        }
    }
    private void DrawCapture()
    {
        var status = this.captureService.Status;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.45f, 0.75f, 1f, 1f), "Capture（schemaVersion 1）");
        ImGui.Separator();
        ImGui.TextUnformatted("隱私：JSON 內玩家名稱匿名化為 Player N；不擷取 Content ID。");
        if (ImGui.Button("開啟上一個 Pull Replay"))
        {
            this.openReplay();
        }

        var automaticCaptureEnabled = status.AutomaticCaptureEnabled;

        if (automaticCaptureEnabled)
        {
            ImGui.TextUnformatted(
                $"Lifecycle：{status.AutomaticState}  已完成 Pull：{status.CompletedPullCount:N0}");
            ImGui.TextUnformatted(
                $"正式擷取範圍：{(status.IsInDutyInstance ? "instance 內" : "instance 外（不會開始）")}");
            if (status.IsInDutyInstance && !status.IsArmedForCombatStart)
            {
                ImGui.TextUnformatted("等待先觀察 InCombat=false，避免從進行中的 Pull 中途開始。");
            }
            if (status.LastCompletedCaptureId is { } captureId)
            {
                ImGui.TextUnformatted($"LastCompletedPull Capture ID：{captureId}");
            }
            if (status.CombatEndRemainingMilliseconds is { } remainingMilliseconds)
            {
                ImGui.TextUnformatted(
                    $"脫戰 debounce：{remainingMilliseconds / 1000.0:F2} 秒後結束；重新進入戰鬥會取消。");
            }

            if (status.LastEndReason is { } endReason)
            {
                ImGui.TextUnformatted($"最後結束原因：{endReason}");
            }
        }
        else if (status.IsRecording)
        {
            if (ImGui.Button("停止並匯出 JSON"))
            {
                this.captureService.StopAndExport();
            }
        }
        else if (!status.IsBusy)
        {
            if (ImGui.Button("開始擷取"))
            {
                this.captureService.Start(this.probe.TerritoryType, this.probe.MapId, this.probe.Instance);
            }
        }
        else
        {
            ImGui.TextUnformatted("背景工作執行中…");
        }


        ImGui.TextWrapped(status.Message);
        ImGui.TextUnformatted(
            $"Samples：{status.SampleCount:N0}  Actors：{status.ActorCount:N0}  Gaps：{status.GapCount:N0}  Rejected：{status.RejectedActorSampleCount:N0}");
        ImGui.TextUnformatted($"Events：{status.EventCount:N0}");
        if (status.LastEvent is not null)
        {
            ImGui.TextWrapped($"最後事件：{status.LastEvent}");
        }
        ImGui.TextUnformatted(
            $"Action Effect hook：{(this.actionEffectReader.IsAvailable ? "可用" : "不可用")}  Batches：{status.ActionEffectCount:N0}  Decode failures：{this.actionEffectReader.ErrorCount:N0}");
        if (status.LastActionEffect is not null)
        {
            ImGui.TextWrapped($"最後 Action Effect：{status.LastActionEffect}");
        }

        if (this.actionEffectReader.LastError is not null)
        {
            ImGui.TextWrapped($"Action Effect hook 錯誤：{this.actionEffectReader.LastError}");
        }

        var readerStatus = !status.WaymarkReaderChecked
            ? "尚未檢查"
            : status.WaymarkReaderAvailable
                ? "可用"
                : "不可用";
        ImGui.TextUnformatted(
            $"Waymark reader：{readerStatus}  Frames：{status.WaymarkFrameCount:N0}  Failures：{status.WaymarkReadFailureCount:N0}");
        if (status.LastWaymarkError is not null)
        {
            ImGui.TextWrapped($"Waymark reader 錯誤：{status.LastWaymarkError}");
        }

        foreach (var waymark in status.LatestWaymarks)
        {
            if (waymark.Active)
            {
                ImGui.TextUnformatted(
                    $"Waymark {FormatWaymarkId(waymark.Id)}：({waymark.X:F2}, {waymark.Y:F2}, {waymark.Z:F2})");
            }
        }
        var targetMarkerReaderStatus = !status.TargetMarkerReaderChecked
            ? "尚未檢查"
            : status.TargetMarkerReaderAvailable
                ? "可用"
                : "不可用";
        ImGui.TextUnformatted(
            $"Target Marker reader：{targetMarkerReaderStatus}  Frames：{status.TargetMarkerFrameCount:N0}  Failures：{status.TargetMarkerReadFailureCount:N0}");
        if (status.LastTargetMarkerError is not null)
        {
            ImGui.TextWrapped($"Target Marker reader 錯誤：{status.LastTargetMarkerError}");
        }

        ImGui.TextUnformatted(
            $"平均間隔：{status.AverageSampleIntervalMilliseconds:F2} ms  Capture callback：{status.LastSamplingMilliseconds:F3} ms  最大 {status.MaximumSamplingMilliseconds:F3} ms");

        if (status.LastSerializationMilliseconds is { } serializationMilliseconds)
        {
            ImGui.TextUnformatted($"JSON serialization：{serializationMilliseconds:F2} ms");
        }

        ImGui.TextWrapped($"Developer/Test JSON 匯出目錄：{status.DeveloperExportDirectory}");
        if (status.LastExportPath is not null)
        {
            ImGui.TextWrapped($"最後匯出：{status.LastExportPath}");
        }

        if (this.captureService.GetReplaySourceSnapshot().LastCompletedPull is { } lastCompletedPull)
        {
            ImGui.TextUnformatted($"Runtime LastCompletedPull：{lastCompletedPull.CaptureId}");
        }
    }


    private void DrawParty()
    {
        if (!ImGui.CollapsingHeader($"Party ({this.probe.PartyMembers.Length})"))
        {
            return;
        }

        foreach (var member in this.probe.PartyMembers)
        {
            var leaderMarker = member.IsLeader ? " [Leader]" : string.Empty;
            ImGui.TextUnformatted(
                $"#{member.Index + 1} {member.Name}{leaderMarker} | Entity 0x{member.EntityId:X8} | Job {member.ClassJobId} Lv.{member.Level}");
            ImGui.TextUnformatted(
                $"    HP {member.CurrentHp:N0}/{member.MaxHp:N0} | MP {member.CurrentMp:N0}/{member.MaxMp:N0} | Pos {FormatVector(member.Position)}");
        }
    }

    private void DrawActorPicker()
    {
        if (!ImGui.CollapsingHeader($"Actor / Battle NPC ({this.probe.Actors.Length})"))
        {
            return;
        }

        foreach (var actor in this.probe.Actors)
        {
            var displayName = string.IsNullOrEmpty(actor.Name) ? "(無名稱)" : actor.Name;
            var label =
                $"[{actor.ObjectIndex}] {displayName} ({actor.ObjectKind})##Actor{actor.GameObjectId:X16}";
            if (ImGui.Selectable(label, actor.GameObjectId == this.selectedActorId))
            {
                this.selectedActorId = actor.GameObjectId;
            }
        }
    }

    private void DrawActor(string heading, ActorProbeSnapshot? actor)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.45f, 0.75f, 1f, 1f), heading);
        ImGui.Separator();

        if (actor is null)
        {
            ImGui.TextUnformatted("目前沒有可用資料。");
            return;
        }

        var value = actor.Value;
        ImGui.TextUnformatted($"{value.Name} ({value.ObjectKind}, ObjectTable #{value.ObjectIndex})");
        ImGui.TextUnformatted(
            $"Entity ID：0x{value.EntityId:X8}  GameObject ID：0x{value.GameObjectId:X16}");
        ImGui.TextUnformatted($"Data ID：{value.DataId}  Base ID：{value.BaseId}");
        ImGui.TextUnformatted(
            $"Position：{FormatVector(value.Position)}  Rotation：{value.Rotation:F3} rad  Hitbox：{value.HitboxRadius:F2}");
        ImGui.TextUnformatted(
            $"HP：{value.CurrentHp:N0}/{value.MaxHp:N0}  MP：{value.CurrentMp:N0}/{value.MaxMp:N0}  Job：{value.ClassJobId}  Lv.{value.Level}");
        ImGui.TextUnformatted(
            $"死亡：{FormatBoolean(value.IsDead)}  可選取：{FormatBoolean(value.IsTargetable)}  Status：{value.StatusCount}");
        ImGui.TextUnformatted($"Target Object ID：0x{value.TargetObjectId:X16}");

        if (value.IsCasting)
        {
            ImGui.TextUnformatted(
                $"Cast：Action {value.CastActionId}  {value.CurrentCastTime:F2}/{value.TotalCastTime:F2}s  Target 0x{value.CastTargetObjectId:X16}");
            ImGui.TextUnformatted($"可中斷：{FormatBoolean(value.IsCastInterruptible)}");
        }
        else
        {
            ImGui.TextUnformatted("Cast：無");
        }
    }

    private ActorProbeSnapshot? FindActor(ulong gameObjectId)
    {
        if (gameObjectId == 0)
        {
            return null;
        }

        foreach (var actor in this.probe.Actors)
        {
            if (actor.GameObjectId == gameObjectId)
            {
                return actor;
            }
        }

        return null;
    }

    private static string FormatBoolean(bool value) => value ? "是" : "否";

    private static string FormatWaymarkId(WaymarkId id) => id switch
    {
        WaymarkId.One => "1",
        WaymarkId.Two => "2",
        WaymarkId.Three => "3",
        WaymarkId.Four => "4",
        _ => id.ToString(),
    };

    private static string FormatVector(Vector3 value) => $"({value.X:F2}, {value.Y:F2}, {value.Z:F2})";
}
