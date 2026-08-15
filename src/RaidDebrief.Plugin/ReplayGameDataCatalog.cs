using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using ActionSheet = Lumina.Excel.Sheets.Action;
using ActionCategorySheet = Lumina.Excel.Sheets.ActionCategory;
using StatusSheet = Lumina.Excel.Sheets.Status;
using Lumina.Excel;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed class ReplayGameDataCatalog
{
    private const uint AutoAttackActionCategoryId = 1;

    private readonly ExcelSheet<ActionSheet> actionSheet;
    private readonly ExcelSheet<ActionSheet> englishActionSheet;
    private readonly string? localizedAutoAttackName;
    private readonly string? englishAutoAttackName;
    private readonly Dictionary<uint, string> actionNames = new();
    private readonly Dictionary<uint, string> recordedActionNames = new();
    private readonly Dictionary<uint, string> statusNames = new();
    private readonly Dictionary<uint, uint> statusIconIds = new();
    private readonly HashSet<uint> stackedStatusIds = [];
    public ReplayStatusEffectDatabase StatusEffects { get; }

    public ReplayGameDataCatalog(IDataManager dataManager)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        this.actionSheet = dataManager.GetExcelSheet<ActionSheet>();
        this.englishActionSheet = dataManager.GetExcelSheet<ActionSheet>(ClientLanguage.English);
        this.localizedAutoAttackName = ReadAutoAttackName(
            dataManager.GetExcelSheet<ActionCategorySheet>());
        this.englishAutoAttackName = ReadAutoAttackName(
            dataManager.GetExcelSheet<ActionCategorySheet>(ClientLanguage.English));
        this.StatusEffects = new ReplayStatusEffectDatabase(
            ReadStatusDescriptions(
                dataManager.GetExcelSheet<StatusSheet>(ClientLanguage.English)));
        foreach (var action in this.actionSheet)
        {
            var name = ResolveGameDataName(
                action.Name.ToString(),
                action.ActionCategory.RowId,
                this.localizedAutoAttackName);
            if (action.RowId != 0 && IsResolvedName(name))
            {
                this.actionNames[action.RowId] = name!;
            }
        }

        foreach (var status in dataManager.GetExcelSheet<StatusSheet>())
        {
            if (status.RowId == 0)
            {
                continue;
            }

            var name = status.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                this.statusNames[status.RowId] = name;
            }
            if (status.MaxStacks > 1)
            {
                this.stackedStatusIds.Add(status.RowId);
            }


            var iconId = (uint)status.Icon;
            if (iconId != 0)
            {
                this.statusIconIds[status.RowId] = iconId;
            }
        }
    }

    public void UseRecordedActionNames(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        this.recordedActionNames.Clear();
        foreach (var actionName in record.ActionNames)
        {
            this.recordedActionNames.Add(actionName.ActionId, actionName.Name);
        }
    }


    public string GetActionName(uint actionId)
    {
        if (this.recordedActionNames.TryGetValue(actionId, out var recordedName))
        {
            return recordedName;
        }

        if (this.actionNames.TryGetValue(actionId, out var name))
        {
            return name;
        }

        string? localizedName = null;
        if (this.actionSheet.TryGetRow(actionId, out var action))
        {
            localizedName = ResolveGameDataName(
                action.Name.ToString(),
                action.ActionCategory.RowId,
                this.localizedAutoAttackName);
        }

        string? englishName = null;
        if (!IsResolvedName(localizedName)
            && this.englishActionSheet.TryGetRow(actionId, out var englishAction))
        {
            englishName = ResolveGameDataName(
                englishAction.Name.ToString(),
                englishAction.ActionCategory.RowId,
                this.englishAutoAttackName);
        }

        name = ResolveActionName(actionId, localizedName, englishName);
        if (ShouldCacheActionName(localizedName, englishName))
        {
            this.actionNames[actionId] = name;
        }

        return name;
    }

    internal static string? ResolveGameDataName(
        string? actionName,
        uint actionCategoryId,
        string? actionCategoryName)
    {
        if (IsResolvedName(actionName))
        {
            return actionName;
        }

        return actionCategoryId == AutoAttackActionCategoryId
            && IsResolvedName(actionCategoryName)
                ? actionCategoryName
                : actionName;
    }

    internal static string? ReadAutoAttackName(ExcelSheet<ActionCategorySheet> actionCategories) =>
        actionCategories.TryGetRow(AutoAttackActionCategoryId, out var autoAttackCategory)
            ? autoAttackCategory.Name.ToString()
            : null;

    internal static string ResolveActionName(
        uint actionId,
        string? recordedName,
        string? localizedName,
        string? englishName) =>
        IsResolvedName(recordedName)
            ? recordedName!
            : ResolveActionName(actionId, localizedName, englishName);

    internal static string ResolveActionName(
        uint actionId,
        string? localizedName,
        string? englishName)
    {
        if (IsResolvedName(localizedName))
        {
            return localizedName!;
        }

        return IsResolvedName(englishName)
            ? englishName!
            : $"Action #{actionId}";
    }

    internal static bool ShouldCacheActionName(string? localizedName, string? englishName) =>
        IsResolvedName(localizedName) || IsResolvedName(englishName);

    internal static bool IsResolvedName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith("_rsv_", StringComparison.OrdinalIgnoreCase);

    public string GetStatusName(uint statusId) =>
        this.statusNames.TryGetValue(statusId, out var name)
            ? name
            : $"Status #{statusId}";
    public bool TryGetStatusIconId(uint statusId, out uint iconId) =>
        this.statusIconIds.TryGetValue(statusId, out iconId);
    public bool HasStacks(uint statusId) =>
        this.stackedStatusIds.Contains(statusId);

    private static IEnumerable<ReplayStatusDescription> ReadStatusDescriptions(
        ExcelSheet<StatusSheet> statuses)
    {
        foreach (var status in statuses)
        {
            yield return new ReplayStatusDescription(
                status.RowId,
                status.Description.ToString());
        }
    }

}
