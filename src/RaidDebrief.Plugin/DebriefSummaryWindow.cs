using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
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
    private const float SummaryMinimumWidth = 360;
    private const float SummaryMaximumWidth = 560;
    private const float JobIconSize = 24;
    private const float JobIconGap = 3;
    private const float ReplayButtonHeight = 36;

    private readonly Action<DebriefReplayRequest> openReplay;
    private readonly DebriefSummaryController controller = new();
    private readonly ISharedImmediateTexture?[] jobIcons;

    public DebriefSummaryWindow(
        Action<DebriefReplayRequest> openReplay,
        ITextureProvider textureProvider)
        : base(
            "Debrief 摘要##RaidDebriefSummary",
            ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.openReplay = openReplay;
        this.jobIcons =
            JobIconResources.LoadTextures(textureProvider, typeof(DebriefSummaryWindow).Assembly);

        // A post-Wipe summary has to be readable in seconds, so it never lets the
        // game surface behind it compete with its own text.
        this.BgAlpha = ReplayWindow.ReplayWindowBackgroundAlpha;
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

    public override void PreDraw()
    {
        var uiScale = ImGuiHelpers.GlobalScale;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(
                ReplayWindow.ResolveScaledLength(SummaryMinimumWidth, uiScale),
                0),
            MaximumSize = new Vector2(
                ReplayWindow.ResolveScaledLength(SummaryMaximumWidth, uiScale),
                float.MaxValue),
        };

        // First appearance is centred so the summary lands where the player is
        // already looking after a Wipe; a manual move is remembered afterwards.
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.WorkPos + (viewport.WorkSize * 0.5f),
            ImGuiCond.FirstUseEver,
            new Vector2(0.5f, 0.5f));
        base.PreDraw();
    }

    public override void Draw()
    {
        if (this.controller.Pending is not { } pending)
        {
            this.IsOpen = false;
            return;
        }

        var summary = pending.Summary;
        var iconSize = ReplayWindow.ResolveScaledLength(JobIconSize, ImGuiHelpers.GlobalScale);
        var pullNumber = summary.PullNumber is { } number
            ? $"Pull #{number}"
            : "Pull —";
        ImGui.TextUnformatted(
            $"{pullNumber}    {FormatTimestamp(summary.DurationMilliseconds)}");
        if (ImGui.IsItemHovered())
        {
            // Capture identity stays reachable for diagnostics without spending a
            // whole row of a compact player-facing surface on a raw GUID.
            ImGui.SetTooltip($"Capture ID: {summary.CaptureId}");
        }

        if (summary.BossHpAtEnd is { } bossHp)
        {
            ImGui.TextUnformatted($"{bossHp.ActorName}    {bossHp.Percentage:F1}%");
        }
        else
        {
            ImGui.TextDisabled("Boss HP —  未錄得可解析的目標");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("FIRST DEATH");
        if (summary.FirstDeath is { } firstDeath)
        {
            this.DrawDeathRow(firstDeath, iconSize);
        }
        else
        {
            ImGui.TextDisabled("—  未錄得可解析的玩家死亡");
        }

        // One death makes the sequence a verbatim repeat of the row above it.
        if (summary.DeathSequence.Length > 1)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("DEATH SEQUENCE");
            this.DrawDeathSequence(summary.DeathSequence, iconSize);
        }

        if (summary.WipeTimestampMilliseconds is { } wipeTimestamp)
        {
            ImGui.TextUnformatted($"WIPE    {FormatTimestamp(wipeTimestamp)}");
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
        var activated = ImGui.Button(
            label,
            new Vector2(
                -1,
                ReplayWindow.ResolveScaledLength(ReplayButtonHeight, ImGuiHelpers.GlobalScale)));

        // The one action worth taking after a Wipe is the default, so the window
        // can be confirmed without aiming at it.
        ImGui.SetItemDefaultFocus();
        if (!activated || !this.controller.TryTake(out var request))
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

    /// <summary>
    /// Presents a recorded death by its Job, matching every other Replay surface.
    /// The recorded actor name is a capture-time <c>Player N</c> alias, so it is
    /// only a fallback for an unresolved Job.
    /// </summary>
    internal static string ResolveDeathLabel(in DebriefDeathEntry death) =>
        JobIconResources.GetAbbreviation(death.ClassJobId) ?? death.ActorName;

    private void DrawDeathSequence(DebriefDeathEntry[] deaths, float iconSize)
    {
        var index = 0;
        while (index < deaths.Length)
        {
            var clusterEnd = index + 1;
            while (clusterEnd < deaths.Length
                   && ReplayWindow.IsWithinDeathCluster(
                       deaths[index].TimestampMilliseconds,
                       deaths[clusterEnd].TimestampMilliseconds))
            {
                clusterEnd++;
            }

            if (clusterEnd - index == 1)
            {
                this.DrawDeathRow(deaths[index], iconSize);
            }
            else
            {
                this.DrawDeathClusterRow(
                    deaths.AsSpan(index, clusterEnd - index),
                    iconSize);
            }

            index = clusterEnd;
        }
    }

    private void DrawDeathRow(in DebriefDeathEntry death, float iconSize)
    {
        if (this.TryDrawJobIcon(death.ClassJobId, iconSize))
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ResolveRowTextOffsetY(iconSize));
        }

        ImGui.TextUnformatted(
            $"{ResolveDeathLabel(death)}    {FormatTimestamp(death.TimestampMilliseconds)}");
    }

    /// <summary>
    /// Collapses one recorded cluster into a single row of Job icons plus the
    /// derived count title, so a full party Wipe stays one readable line instead of
    /// eight near-identical wrapped strings.
    /// </summary>
    private void DrawDeathClusterRow(ReadOnlySpan<DebriefDeathEntry> cluster, float iconSize)
    {
        var iconGap = ReplayWindow.ResolveScaledLength(JobIconGap, ImGuiHelpers.GlobalScale);
        var placed = false;
        foreach (ref readonly var death in cluster)
        {
            if (placed)
            {
                ImGui.SameLine(0, iconGap);
            }

            placed = this.TryDrawJobIcon(death.ClassJobId, iconSize) || placed;
        }

        if (placed)
        {
            ImGui.SameLine(0, iconGap * 3);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ResolveRowTextOffsetY(iconSize));
        }

        var first = FormatTimestamp(cluster[0].TimestampMilliseconds);
        var last = FormatTimestamp(cluster[^1].TimestampMilliseconds);
        var range = first == last ? first : $"{first}–{last}";
        ImGui.TextUnformatted(
            $"{ReplayWindow.FormatDeathQuickJumpClusterTitle(cluster.Length)}    {range}");
    }

    internal static float ResolveRowTextOffsetY(float iconSize) =>
        Math.Max(0, (iconSize - ImGui.GetTextLineHeight()) * 0.5f);

    private bool TryDrawJobIcon(uint classJobId, float iconSize)
    {
        if (classJobId >= this.jobIcons.Length
            || this.jobIcons[(int)classJobId] is not { } icon
            || !icon.TryGetWrap(out var texture, out _)
            || texture is null)
        {
            return false;
        }

        ImGui.Image(texture.Handle, new Vector2(iconSize));
        return true;
    }

    private static string FormatTimestamp(long timestampMilliseconds)
    {
        var totalSeconds = Math.Max(0, timestampMilliseconds) / 1_000;
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }
}
