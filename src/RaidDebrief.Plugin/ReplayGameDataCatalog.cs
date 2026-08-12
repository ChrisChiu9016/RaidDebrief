using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using ActionSheet = Lumina.Excel.Sheets.Action;
using StatusSheet = Lumina.Excel.Sheets.Status;
using Lumina.Excel;

namespace RaidDebrief.Plugin;

internal sealed class ReplayGameDataCatalog
{
    // Objective presentation allow-list only. Membership never implies sufficiency or a recommendation.
    private static readonly uint[] MitigationStatusIds =
    [
        74,   // Sentinel
        89,   // Vengeance
        299,  // Sacred Soil
        746,  // Dark Mind
        849,  // Collective Unconscious
        1178, // The Blackest Night
        1191, // Rampart
        1193, // Reprisal
        1195, // Feint
        1203, // Addle
        1457, // Shake It Off
        1826, // Shield Samba
        1832, // Camouflage
        1834, // Nebula
        1872, // Temperance
        1934, // Troubadour
        1951, // Tactician
        2618, // Kerachole
        2619, // Taurochole
        2678, // Bloodwhetting
        2682, // Oblation
        2683, // Heart of Corundum
        2708, // Aquaveil
        2717, // Exaltation
        2931, // Expedient
    ];

    private readonly ExcelSheet<ActionSheet> actionSheet;
    private readonly ExcelSheet<ActionSheet> englishActionSheet;
    private readonly Dictionary<uint, string> actionNames = new();
    private readonly Dictionary<uint, string> statusNames = new();
    private readonly HashSet<uint> mitigationStatuses = new(MitigationStatusIds);

    public ReplayGameDataCatalog(IDataManager dataManager)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        this.actionSheet = dataManager.GetExcelSheet<ActionSheet>();
        this.englishActionSheet = dataManager.GetExcelSheet<ActionSheet>(ClientLanguage.English);
        foreach (var action in this.actionSheet)
        {
            var name = action.Name.ToString();
            if (action.RowId != 0 && IsResolvedName(name))
            {
                this.actionNames[action.RowId] = name;
            }
        }

        foreach (var status in dataManager.GetExcelSheet<StatusSheet>())
        {
            var name = status.Name.ToString();
            if (status.RowId != 0 && !string.IsNullOrWhiteSpace(name))
            {
                this.statusNames[status.RowId] = name;
            }
        }
    }

    public string GetActionName(uint actionId)
    {
        if (this.actionNames.TryGetValue(actionId, out var name))
        {
            return name;
        }

        string? localizedName = null;
        if (this.actionSheet.TryGetRow(actionId, out var action))
        {
            localizedName = action.Name.ToString();
        }

        string? englishName = null;
        if (!IsResolvedName(localizedName)
            && this.englishActionSheet.TryGetRow(actionId, out var englishAction))
        {
            englishName = englishAction.Name.ToString();
        }

        name = ResolveActionName(actionId, localizedName, englishName);
        this.actionNames[actionId] = name;
        return name;
    }

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

    internal static bool IsResolvedName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith("_rsv_", StringComparison.OrdinalIgnoreCase);

    public string GetStatusName(uint statusId) =>
        this.statusNames.TryGetValue(statusId, out var name)
            ? name
            : $"Status #{statusId}";

    public bool IsMitigation(uint statusId) => this.mitigationStatuses.Contains(statusId);
}
