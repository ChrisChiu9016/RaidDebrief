using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using RaidDebrief.Core;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace RaidDebrief.Plugin;

internal sealed class CaptureActionNameResolver
{
    private readonly ExcelSheet<ActionSheet> actionSheet;
    private readonly HashSet<uint> initiallyResolvedActionIds = new();
    private readonly EnemyCastBarNameReader enemyCastBarNameReader;
    private readonly IPluginLog log;
    private readonly string language;
    private readonly HashSet<uint> failedActionIds = new();

    public CaptureActionNameResolver(
        IDataManager dataManager,
        IGameGui gameGui,
        IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        ArgumentNullException.ThrowIfNull(gameGui);
        ArgumentNullException.ThrowIfNull(log);

        this.actionSheet = dataManager.GetExcelSheet<ActionSheet>();
        this.enemyCastBarNameReader = new EnemyCastBarNameReader(gameGui);
        this.log = log;
        this.language = dataManager.Language.ToString();
        foreach (var action in this.actionSheet)
        {
            if (action.RowId != 0 && ReplayGameDataCatalog.IsResolvedName(action.Name.ToString()))
            {
                this.initiallyResolvedActionIds.Add(action.RowId);
            }
        }
    }

    public RecordedActionName? TryResolve(uint actionId, uint sourceEntityId)
    {
        if (actionId == 0)
        {
            return null;
        }

        try
        {
            string? gameDataName = null;
            if (this.actionSheet.TryGetRow(actionId, out var action))
            {
                gameDataName = action.Name.ToString();
            }
            string? clientRsvName = null;
            string? uiObservedName = null;
            if (!ReplayGameDataCatalog.IsResolvedName(gameDataName))
            {
                clientRsvName = TryResolveClientRsv(gameDataName);
                if (!ReplayGameDataCatalog.IsResolvedName(clientRsvName))
                {
                    uiObservedName = this.enemyCastBarNameReader.TryRead(sourceEntityId);
                }
            }

            return Resolve(
                actionId,
                gameDataName,
                this.initiallyResolvedActionIds.Contains(actionId),
                clientRsvName,
                uiObservedName,
                this.language);
        }
        catch (Exception exception)
        {
            if (this.failedActionIds.Add(actionId))
            {
                this.log.Warning(
                    exception,
                    "Raid Debrief could not snapshot Action {ActionId} name; Capture continues without it.",
                    actionId);
            }

            return null;
        }
    }
    private static unsafe string? TryResolveClientRsv(string? rsvName)
    {
        if (string.IsNullOrWhiteSpace(rsvName)
            || !rsvName.StartsWith("_rsv_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld is null)
        {
            return null;
        }

        var resolved = layoutWorld->ResolveRsvString(rsvName);
        if (!resolved.HasValue)
        {
            return null;
        }

        var name = resolved.ToString();
        return ReplayGameDataCatalog.IsResolvedName(name) ? name : null;
    }


    internal static RecordedActionName? Resolve(
        uint actionId,
        string? gameDataName,
        bool wasResolvedAtStartup,
        string? clientRsvName,
        string? uiObservedName,
        string language)
    {
        if (actionId == 0 || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        if (ReplayGameDataCatalog.IsResolvedName(gameDataName))
        {
            return new RecordedActionName
            {
                ActionId = actionId,
                Name = gameDataName!,
                Language = language,
                Source = wasResolvedAtStartup
                    ? ActionNameSource.StaticExcel
                    : ActionNameSource.RuntimeRsv,
            };
        }

        if (ReplayGameDataCatalog.IsResolvedName(clientRsvName))
        {
            return new RecordedActionName
            {
                ActionId = actionId,
                Name = clientRsvName!,
                Language = language,
                Source = ActionNameSource.RuntimeRsv,
            };
        }

        if (!ReplayGameDataCatalog.IsResolvedName(uiObservedName))
        {
            return null;
        }

        return new RecordedActionName
        {
            ActionId = actionId,
            Name = uiObservedName!,
            Language = language,
            Source = ActionNameSource.UiObserved,
        };
    }
}

internal sealed unsafe class EnemyCastBarNameReader
{
    private readonly IGameGui gameGui;

    public EnemyCastBarNameReader(IGameGui gameGui)
    {
        ArgumentNullException.ThrowIfNull(gameGui);
        this.gameGui = gameGui;
    }

    public string? TryRead(uint sourceEntityId)
    {
        if (sourceEntityId == 0)
        {
            return null;
        }

        var addon = this.gameGui.GetAddonByName("CastBarEnemy");
        if (addon.IsNull || !addon.IsReady)
        {
            return null;
        }

        var castBarEnemy =
            (FFXIVClientStructs.FFXIV.Client.UI.AddonCastBarEnemy*)addon.Address;
        foreach (ref readonly var castBar in castBarEnemy->CastBarInfo)
        {
            if (castBar.EntityId != sourceEntityId || !castBar.CastName.HasValue)
            {
                continue;
            }

            var name = castBar.CastName.ToString();
            return ReplayGameDataCatalog.IsResolvedName(name) ? name : null;
        }

        return null;
    }
}
