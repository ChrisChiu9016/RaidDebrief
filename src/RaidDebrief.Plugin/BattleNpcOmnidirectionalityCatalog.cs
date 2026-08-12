using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed class BattleNpcOmnidirectionalityCatalog
{
    internal const uint DirectionalDisregardStatusId = 3808;

    private readonly HashSet<uint> omnidirectionalBaseIds = [];

    public BattleNpcOmnidirectionalityCatalog(IDataManager dataManager, IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            foreach (var battleNpc in dataManager.GetExcelSheet<BNpcBase>())
            {
                if (battleNpc.IsOmnidirectional)
                {
                    this.omnidirectionalBaseIds.Add(battleNpc.RowId);
                }
            }

            this.IsAvailable = true;
            log.Information(
                "Boss/Add omnidirectionality catalog loaded {Count} rows from Lumina BNpcBase.",
                this.omnidirectionalBaseIds.Count);
        }
        catch (Exception exception)
        {
            this.omnidirectionalBaseIds.Clear();
            log.Error(exception, "Boss/Add omnidirectionality catalog failed to read Lumina BNpcBase.");
        }
    }

    public bool IsAvailable { get; }

    public bool Contains(uint baseId) => this.omnidirectionalBaseIds.Contains(baseId);

    internal static bool Resolve(
        ObjectKind objectKind,
        bool baseIsOmnidirectional,
        ReadOnlySpan<PolledStatusObservation> statuses)
    {
        if (objectKind != ObjectKind.BattleNpc)
        {
            return false;
        }

        if (baseIsOmnidirectional)
        {
            return true;
        }

        foreach (ref readonly var status in statuses)
        {
            if (status.StatusId == DirectionalDisregardStatusId)
            {
                return true;
            }
        }

        return false;
    }
}
