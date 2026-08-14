using System;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal readonly record struct DebriefReplayRequest(
    long SourceGeneration,
    Guid CaptureId,
    DebriefReplayWindow Window);

internal readonly record struct PendingDebrief(
    long SourceGeneration,
    DebriefSummary Summary);

internal sealed class DebriefSummaryController
{
    private long observedCompletedGeneration;
    private long? presentedSourceGeneration;
    private PendingDebrief? queuedDebrief;
    public PendingDebrief? Pending { get; private set; }

    public void Observe(ReplaySourceSnapshot snapshot, bool inCombat, bool enabled)
    {
        if (inCombat && this.Pending is not null)
        {
            this.Pending = null;
        }

        if (snapshot.CompletedGeneration > this.observedCompletedGeneration)
        {
            this.observedCompletedGeneration = snapshot.CompletedGeneration;
            this.Pending = null;
            this.queuedDebrief = enabled
                && snapshot.LastCompletedEndReason == PullEndReason.DutyWiped
                && snapshot.LastCompletedPull is { } record
                && snapshot.LastCompletedDebrief is { } summary
                && summary.CaptureId == record.CaptureId
                ? new PendingDebrief(snapshot.CompletedGeneration, summary)
                : null;
        }

        if (!enabled)
        {
            this.Pending = null;
            this.queuedDebrief = null;
            return;
        }

        if (!inCombat && this.queuedDebrief is { } queuedDebrief)
        {
            this.Pending = queuedDebrief;
            this.queuedDebrief = null;
        }
    }

    public bool TryBeginPresentation()
    {
        if (this.Pending is not { } pending
            || this.presentedSourceGeneration == pending.SourceGeneration)
        {
            return false;
        }

        this.presentedSourceGeneration = pending.SourceGeneration;
        return true;
    }

    public bool TryTake(out DebriefReplayRequest request)
    {
        if (this.Pending is not { } pending
            || pending.Summary.SuggestedReplayWindow is not { } replayWindow)
        {
            request = default;
            return false;
        }

        request = new DebriefReplayRequest(
            pending.SourceGeneration,
            pending.Summary.CaptureId,
            replayWindow);
        this.Pending = null;
        return true;
    }

    public void Dismiss()
    {
        this.Pending = null;
        this.queuedDebrief = null;
    }
}

internal sealed class DebriefSummaryWindow : Window
{
    private readonly Action<DebriefReplayRequest> openReplay;
    private readonly DebriefSummaryController controller = new();
    private readonly StringBuilder deathSequenceBuilder = new(256);
    private readonly ISharedImmediateTexture?[] jobIcons;

    public DebriefSummaryWindow(
        Action<DebriefReplayRequest> openReplay,
        ITextureProvider textureProvider)
        : base(
            "Pull Debrief##RaidDebriefSummary",
            ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.openReplay = openReplay;
        this.jobIcons =
            JobIconResources.LoadTextures(textureProvider, typeof(DebriefSummaryWindow).Assembly);
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 0),
            MaximumSize = new Vector2(560, float.MaxValue),
        };
    }

    internal DebriefSummaryController Controller => this.controller;

    public void Update(ReplaySourceSnapshot snapshot, bool inCombat, bool enabled)
    {
        this.controller.Observe(snapshot, inCombat, enabled);
        if (this.controller.Pending is null)
        {
            this.IsOpen = false;
        }
        else if (this.controller.TryBeginPresentation())
        {
            this.IsOpen = true;
        }
    }

    public override void Draw()
    {
        if (this.controller.Pending is not { } pending)
        {
            this.IsOpen = false;
            return;
        }

        var summary = pending.Summary;
        var pullNumber = summary.PullNumber is { } number
            ? $"Pull #{number}"
            : "Pull —";
        var bossHp = summary.BossHpAtEnd is { } hp
            ? $"{hp.Percentage:F1}%"
            : "—";
        ImGui.TextUnformatted(
            $"{pullNumber}    {FormatTimestamp(summary.DurationMilliseconds)}    Boss HP {bossHp}");
        ImGui.TextDisabled($"Capture ID: {summary.CaptureId}");
        ImGui.Separator();

        ImGui.TextUnformatted("FIRST DEATH");
        if (summary.FirstDeath is { } firstDeath)
        {
            if (this.TryDrawJobIcon(firstDeath.ClassJobId))
            {
                ImGui.SameLine();
            }

            ImGui.TextUnformatted(
                $"{firstDeath.ActorName}    {FormatTimestamp(firstDeath.TimestampMilliseconds)}");
        }
        else
        {
            ImGui.TextDisabled("—  未錄得可解析的玩家死亡");
        }
        ImGui.Spacing();
        ImGui.TextUnformatted("DEATH SEQUENCE");
        if (summary.DeathSequence.Length == 0)
        {
            ImGui.TextDisabled(
                summary.WipeTimestampMilliseconds is { } wipeTimestamp
                    ? $"—  →  WIPE {FormatTimestamp(wipeTimestamp)}"
                    : "—");
        }
        else
        {
            this.deathSequenceBuilder.Clear();
            for (var index = 0; index < summary.DeathSequence.Length; index++)
            {
                if (index > 0)
                {
                    this.deathSequenceBuilder.Append("  →  ");
                }

                var death = summary.DeathSequence[index];
                this.deathSequenceBuilder
                    .Append(death.ActorName)
                    .Append(" ☠ ")
                    .Append(FormatTimestamp(death.TimestampMilliseconds));
            }

            if (summary.WipeTimestampMilliseconds is { } wipeTimestamp)
            {
                this.deathSequenceBuilder
                    .Append("  →  WIPE ")
                    .Append(FormatTimestamp(wipeTimestamp));
            }

            ImGui.TextWrapped(this.deathSequenceBuilder.ToString());
        }

        if (summary.UnresolvedDeathEventCount > 0)
        {
            ImGui.TextDisabled(
                $"{summary.UnresolvedDeathEventCount} 筆 Death 缺少可解析玩家資料，未納入順序。");
        }

        ImGui.Spacing();
        if (summary.SuggestedReplayWindow is not { } replayWindow)
        {
            ImGui.TextDisabled("此 Pull 沒有可用的 Wipe Replay 時間窗。");
            return;
        }

        var label =
            $"Replay {FormatTimestamp(replayWindow.StartTimestampMilliseconds)} → {FormatTimestamp(replayWindow.EndTimestampMilliseconds)}";
        if (!ImGui.Button(label, new Vector2(-1, 36))
            || !this.controller.TryTake(out var request))
        {
            return;
        }

        this.IsOpen = false;
        this.openReplay(request);
    }

    public override void OnClose()
    {
        this.controller.Dismiss();
        base.OnClose();
    }

    private bool TryDrawJobIcon(uint classJobId)
    {
        if (classJobId >= this.jobIcons.Length
            || this.jobIcons[(int)classJobId] is not { } icon
            || !icon.TryGetWrap(out var texture, out _)
            || texture is null)
        {
            return false;
        }

        ImGui.Image(texture.Handle, new Vector2(24));
        return true;
    }

    private static string FormatTimestamp(long timestampMilliseconds)
    {
        var totalSeconds = Math.Max(0, timestampMilliseconds) / 1_000;
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }
}
