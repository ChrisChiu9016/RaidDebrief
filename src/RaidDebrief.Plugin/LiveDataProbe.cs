using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed class LiveDataProbe : IDisposable
{
    private const uint NonNetworkedEntityId = 0xE000_0000;

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IPartyList partyList;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly CaptureService captureService;
    private readonly BattleNpcOmnidirectionalityCatalog omnidirectionalityCatalog;
    private readonly ActorProbeSnapshot[] actors;
    private readonly PolledStatusObservation[]?[] actorStatuses;
    private readonly PartyMemberProbeSnapshot[] partyMembers = new PartyMemberProbeSnapshot[24];

    private int actorCount;
    private int partyMemberCount;
    private bool disposed;

    public LiveDataProbe(
        IFramework framework,
        ICondition condition,
        IClientState clientState,
        IPartyList partyList,
        IObjectTable objectTable,
        IPluginLog log,
        CaptureService captureService,
        BattleNpcOmnidirectionalityCatalog omnidirectionalityCatalog)
    {
        this.framework = framework;
        this.condition = condition;
        this.clientState = clientState;
        this.partyList = partyList;
        this.objectTable = objectTable;
        this.log = log;
        this.captureService = captureService;
        this.omnidirectionalityCatalog = omnidirectionalityCatalog
            ?? throw new ArgumentNullException(nameof(omnidirectionalityCatalog));
        this.actors = new ActorProbeSnapshot[objectTable.Length];
        this.actorStatuses = new PolledStatusObservation[objectTable.Length][];

        this.framework.Update += this.OnFrameworkUpdate;
    }

    public bool IsLoggedIn { get; private set; }

    public uint TerritoryType { get; private set; }

    public uint MapId { get; private set; }

    public uint Instance { get; private set; }

    public bool IsInDutyInstance { get; private set; }

    public bool InCombat { get; private set; }
    public ulong LocalPlayerGameObjectId { get; private set; }
    public bool IsOnFrameworkThread { get; private set; }

    public int PlayerCount { get; private set; }

    public int BattleNpcCount { get; private set; }

    public double LastCallbackMilliseconds { get; private set; }

    public double MaximumCallbackMilliseconds { get; private set; }

    public long UpdateCount { get; private set; }

    public long ErrorCount { get; private set; }
    public long RejectedVolatileActorReadCount { get; private set; }


    public string? LastError { get; private set; }

    public ReadOnlySpan<ActorProbeSnapshot> Actors => this.actors.AsSpan(0, this.actorCount);

    public ReadOnlySpan<PartyMemberProbeSnapshot> PartyMembers => this.partyMembers.AsSpan(0, this.partyMemberCount);

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.framework.Update -= this.OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            this.IsOnFrameworkThread = this.framework.IsInFrameworkUpdateThread;
            this.CaptureClientState();
            this.CaptureParty();
            this.CaptureActors();
            this.captureService.RecordFrameworkSnapshot(
                this.Actors,
                this.PartyMembers,
                this.InCombat,
                this.TerritoryType,
                this.MapId,
                this.Instance,
                isInDutyInstance: this.IsInDutyInstance);
            this.UpdateCount++;
            this.LastError = null;
        }
        catch (Exception exception)
        {
            this.ErrorCount++;
            this.LastError = exception.Message;
            if (this.ErrorCount == 1 || this.ErrorCount % 300 == 0)
            {
                this.log.Error(exception, "Raid Debrief live data probe failed during Framework.Update.");
            }
        }
        finally
        {
            this.LastCallbackMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            this.MaximumCallbackMilliseconds = Math.Max(this.MaximumCallbackMilliseconds, this.LastCallbackMilliseconds);
        }
    }

    private void CaptureClientState()
    {
        this.IsLoggedIn = this.clientState.IsLoggedIn;
        this.TerritoryType = this.clientState.TerritoryType;
        this.MapId = this.clientState.MapId;
        this.Instance = this.clientState.Instance;
        this.IsInDutyInstance = IsBoundByDuty(
            this.condition[ConditionFlag.BoundByDuty],
            this.condition[ConditionFlag.BoundByDuty56],
            this.condition[ConditionFlag.BoundByDuty95]);
        this.InCombat = this.condition[ConditionFlag.InCombat];
        this.LocalPlayerGameObjectId = this.objectTable.LocalPlayer?.GameObjectId ?? 0;
    }

    private void CaptureParty()
    {
        var count = Math.Min(this.partyList.Length, this.partyMembers.Length);
        this.partyMemberCount = count;

        for (var index = 0; index < count; index++)
        {
            var member = this.partyList[index];
            if (member is null)
            {
                this.partyMembers[index] = default;
                continue;
            }

            var previous = this.partyMembers[index];
            var name = previous.EntityId == member.EntityId
                ? previous.Name
                : member.Name.TextValue;

            this.partyMembers[index] = new PartyMemberProbeSnapshot(
                index,
                name,
                member.EntityId,
                member.GameObject?.GameObjectId ?? 0,
                member.ClassJob.RowId,
                member.Level,
                member.CurrentHP,
                member.MaxHP,
                member.CurrentMP,
                member.MaxMP,
                member.Position,
                index == this.partyList.PartyLeaderIndex);
        }
    }
    internal static bool IsBoundByDuty(
        bool boundByDuty,
        bool boundByDuty56,
        bool boundByDuty95) =>
        boundByDuty || boundByDuty56 || boundByDuty95;


    internal static bool IsRecordablePlayerEntity(uint entityId) =>
        entityId != 0 && entityId != NonNetworkedEntityId;

    private void CaptureActors()
    {
        var nextActorCount = 0;
        var playerCount = 0;
        var battleNpcCount = 0;

        for (var objectIndex = 0; objectIndex < this.objectTable.Length; objectIndex++)
        {
            if (nextActorCount >= this.actors.Length)
            {
                break;
            }

            try
            {
                var gameObject = this.objectTable[objectIndex];
                if (gameObject is null
                    || !this.TryCaptureActor(objectIndex, gameObject, nextActorCount, out var isPlayer))
                {
                    continue;
                }

                if (isPlayer)
                {
                    playerCount++;
                }
                else
                {
                    battleNpcCount++;
                }

                nextActorCount++;
            }
            catch (Exception exception)
            {
                this.RecordVolatileActorReadFailure(objectIndex, exception);
            }
        }

        this.actorCount = nextActorCount;
        this.PlayerCount = playerCount;
        this.BattleNpcCount = battleNpcCount;
    }

    private bool TryCaptureActor(
        int objectIndex,
        IGameObject gameObject,
        int destinationIndex,
        out bool isPlayer)
    {
        isPlayer = gameObject is IPlayerCharacter;
        if (isPlayer)
        {
            if (!IsRecordablePlayerEntity(gameObject.EntityId))
            {
                return false;
            }
        }
        else if (gameObject.ObjectKind != ObjectKind.BattleNpc)
        {
            return false;
        }

        var previous = this.actors[destinationIndex];
        var name = previous.GameObjectId == gameObject.GameObjectId
            ? previous.Name
            : gameObject.Name.TextValue;

        uint currentHp = 0;
        uint maxHp = 0;
        uint currentMp = 0;
        uint maxMp = 0;
        uint classJobId = 0;
        byte level = 0;

        if (gameObject is ICharacter character)
        {
            currentHp = character.CurrentHp;
            maxHp = character.MaxHp;
            currentMp = character.CurrentMp;
            maxMp = character.MaxMp;
            classJobId = character.ClassJob.RowId;
            level = character.Level;
        }

        var isCasting = false;
        var isCastInterruptible = false;
        uint castActionId = 0;
        ulong castTargetObjectId = 0;
        float currentCastTime = 0;
        float totalCastTime = 0;
        var statuses = this.actorStatuses[destinationIndex];
        var statusCount = 0;
        var hasDirectionalDisregard = false;

        if (gameObject is IBattleChara battleChara)
        {
            isCasting = battleChara.IsCasting;
            isCastInterruptible = battleChara.IsCastInterruptible;
            castActionId = battleChara.CastActionId;
            castTargetObjectId = battleChara.CastTargetObjectId;
            currentCastTime = battleChara.CurrentCastTime;
            totalCastTime = battleChara.TotalCastTime;
            if (statuses is null || statuses.Length < battleChara.StatusList.Length)
            {
                statuses = new PolledStatusObservation[battleChara.StatusList.Length];
                this.actorStatuses[destinationIndex] = statuses;
            }

            for (var statusIndex = 0; statusIndex < battleChara.StatusList.Length; statusIndex++)
            {
                var status = battleChara.StatusList[statusIndex];
                if (status is not null && status.StatusId != 0)
                {
                    var remainingTime = float.IsFinite(status.RemainingTime)
                        ? Math.Max(0, status.RemainingTime)
                        : 0;
                    statuses[statusCount] = new PolledStatusObservation(
                        status.StatusId,
                        status.SourceId,
                        remainingTime,
                        status.Param);
                    hasDirectionalDisregard |=
                        status.StatusId == BattleNpcOmnidirectionalityCatalog.DirectionalDisregardStatusId;
                    statusCount++;
                }
            }
        }

        statuses ??= [];
        var baseIsOmnidirectional = this.omnidirectionalityCatalog.Contains(gameObject.BaseId);
        var isOmnidirectional = BattleNpcOmnidirectionalityCatalog.Resolve(
            gameObject.ObjectKind,
            baseIsOmnidirectional,
            statuses.AsSpan(0, statusCount));
        var isOmnidirectionalityKnown = isPlayer
            || this.omnidirectionalityCatalog.IsAvailable
            || baseIsOmnidirectional
            || hasDirectionalDisregard;
        this.actors[destinationIndex] = new ActorProbeSnapshot(
            objectIndex,
            name,
            gameObject.ObjectKind,
            gameObject.EntityId,
            gameObject.GameObjectId,
            gameObject.OwnerId,
            gameObject.BaseId,
            gameObject.BaseId,
            gameObject.Position,
            gameObject.Rotation,
            gameObject.HitboxRadius,
            gameObject.IsDead,
            gameObject.IsTargetable,
            gameObject.TargetObjectId,
            currentHp,
            maxHp,
            currentMp,
            maxMp,
            classJobId,
            level,
            isCasting,
            isCastInterruptible,
            castActionId,
            castTargetObjectId,
            currentCastTime,
            totalCastTime,
            isOmnidirectional,
            isOmnidirectionalityKnown,
            statuses,
            statusCount);
        return true;
    }

    private void RecordVolatileActorReadFailure(int objectIndex, Exception exception)
    {
        this.RejectedVolatileActorReadCount++;
        if (this.RejectedVolatileActorReadCount == 1
            || this.RejectedVolatileActorReadCount % 300 == 0)
        {
            this.log.Warning(
                "Raid Debrief skipped volatile ObjectTable actor {ObjectIndex}: {ExceptionType}: {Message}",
                objectIndex,
                exception.GetType().Name,
                exception.Message);
        }
    }
}

internal readonly record struct PartyMemberProbeSnapshot(
    int Index,
    string Name,
    uint EntityId,
    ulong GameObjectId,
    uint ClassJobId,
    byte Level,
    uint CurrentHp,
    uint MaxHp,
    ushort CurrentMp,
    ushort MaxMp,
    Vector3 Position,
    bool IsLeader);

internal readonly record struct ActorProbeSnapshot(
    int ObjectIndex,
    string Name,
    ObjectKind ObjectKind,
    uint EntityId,
    ulong GameObjectId,
    ulong OwnerId,
    uint DataId,
    uint BaseId,
    Vector3 Position,
    float Rotation,
    float HitboxRadius,
    bool IsDead,
    bool IsTargetable,
    ulong TargetObjectId,
    uint CurrentHp,
    uint MaxHp,
    uint CurrentMp,
    uint MaxMp,
    uint ClassJobId,
    byte Level,
    bool IsCasting,
    bool IsCastInterruptible,
    uint CastActionId,
    ulong CastTargetObjectId,
    float CurrentCastTime,
    float TotalCastTime,
    bool IsOmnidirectional,
    bool IsOmnidirectionalityKnown,
    PolledStatusObservation[] Statuses,
    int StatusCount);
