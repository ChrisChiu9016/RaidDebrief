using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace RaidDebrief.Plugin;

internal readonly record struct HistoryReplayState(
    Guid? ActiveCaptureId,
    Guid? LoadingCaptureId);

internal enum HistoryReplayButtonState
{
    Available,
    Loading,
    Active,
}

internal sealed class HistoryWindow : Window
{
    private readonly PullHistoryStore pullHistoryStore;
    private readonly Action<PullHistoryEntry> openHistoryReplay;
    private readonly Func<HistoryReplayState> getReplayState;

    public HistoryWindow(
        PullHistoryStore pullHistoryStore,
        Action<PullHistoryEntry> openHistoryReplay,
        Func<HistoryReplayState> getReplayState)
        : base("歷史紀錄##RaidDebriefHistory")
    {
        this.pullHistoryStore = pullHistoryStore
            ?? throw new ArgumentNullException(nameof(pullHistoryStore));
        this.openHistoryReplay = openHistoryReplay
            ?? throw new ArgumentNullException(nameof(openHistoryReplay));
        this.getReplayState = getReplayState
            ?? throw new ArgumentNullException(nameof(getReplayState));
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(660, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var snapshot = this.pullHistoryStore.GetSnapshot();
        var replayState = this.getReplayState();
        if (!snapshot.IsReady)
        {
            ImGui.TextUnformatted("正在讀取 History 索引…");
        }
        else if (snapshot.Groups.Length == 0)
        {
            ImGui.TextWrapped("尚無自動 Pull 歷史。完成下一個副本 Pull 後會在此出現。");
        }
        else
        {
            foreach (var group in snapshot.Groups)
            {
                var groupFlags = ReferenceEquals(group, snapshot.Groups[0])
                    ? ImGuiTreeNodeFlags.DefaultOpen
                    : ImGuiTreeNodeFlags.None;
                if (!ImGui.CollapsingHeader(
                        $"{group.DutyRunName}##HistoryGroup{group.DutyRunId:D}",
                        groupFlags))
                {
                    continue;
                }

                var tableFlags =
                    ImGuiTableFlags.Borders
                    | ImGuiTableFlags.RowBg
                    | ImGuiTableFlags.SizingStretchProp;
                if (!ImGui.BeginTable(
                        $"##HistoryPulls{group.DutyRunId:D}",
                        5,
                        tableFlags))
                {
                    continue;
                }

                ImGui.TableSetupColumn("Pull", ImGuiTableColumnFlags.WidthFixed, Scaled(64));
                ImGui.TableSetupColumn("開始", ImGuiTableColumnFlags.WidthFixed, Scaled(112));
                ImGui.TableSetupColumn("長度", ImGuiTableColumnFlags.WidthFixed, Scaled(72));
                ImGui.TableSetupColumn("最終 BOSS HP", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Replay", ImGuiTableColumnFlags.WidthFixed, Scaled(92));
                ImGui.TableHeadersRow();
                foreach (var pull in group.Pulls)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"#{pull.PullOrdinalWithinDutyRun}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatStartedAt(pull.StartedAtUtc));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatDuration(pull));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatFinalBossHp(pull.FinalBossHpPercentage));
                    ImGui.TableNextColumn();

                    var buttonState = ResolveReplayButtonState(pull.CaptureId, replayState);
                    ImGui.BeginDisabled(buttonState != HistoryReplayButtonState.Available);
                    if (ImGui.Button(
                            $"{FormatReplayButtonLabel(buttonState)}##HistoryPull{pull.CaptureId:D}"))
                    {
                        this.openHistoryReplay(pull);
                    }

                    ImGui.EndDisabled();
                }

                ImGui.EndTable();
                ImGui.Spacing();
            }
        }
    }

    internal static string FormatStartedAt(DateTimeOffset startedAtUtc) =>
        startedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string FormatDuration(PullHistoryEntry entry)
    {
        var durationMilliseconds = Math.Max(
            0,
            (long)(entry.EndedAtUtc - entry.StartedAtUtc).TotalMilliseconds);
        return ReplayWindow.FormatTimestamp(durationMilliseconds);
    }

    internal static string FormatFinalBossHp(float? percentage) =>
        percentage is { } value
            ? $"{Math.Clamp(value, 0, 100).ToString("0.0", CultureInfo.InvariantCulture)}%"
            : "—";

    internal static HistoryReplayButtonState ResolveReplayButtonState(
        Guid captureId,
        HistoryReplayState state) =>
        state.ActiveCaptureId == captureId
            ? HistoryReplayButtonState.Active
            : state.LoadingCaptureId == captureId
                ? HistoryReplayButtonState.Loading
                : HistoryReplayButtonState.Available;

    internal static string FormatReplayButtonLabel(HistoryReplayButtonState state) =>
        state switch
        {
            HistoryReplayButtonState.Active => "查看中",
            HistoryReplayButtonState.Loading => "載入中",
            _ => "查看",
        };

    private static float Scaled(float value) => value * ImGuiHelpers.GlobalScale;
}
