using System;
using System.Globalization;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed class DutyRunTracker
{
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> utcNow;
    private readonly TimeZoneInfo displayTimeZone;
    private DutyRunState? pendingZoneRun;
    private DutyRunState? activeRun;
    private bool isBoundByDuty;

    public DutyRunTracker(
        Func<DateTimeOffset>? utcNow = null,
        TimeZoneInfo? displayTimeZone = null)
    {
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.displayTimeZone = displayTimeZone ?? TimeZoneInfo.Local;
    }

    public Guid? CurrentDutyRunId
    {
        get
        {
            lock (this.gate)
            {
                return this.activeRun?.DutyRunId;
            }
        }
    }

    public void ObserveZoneInitialized(
        uint territoryType,
        uint contentFinderConditionId,
        string? dutyName)
    {
        lock (this.gate)
        {
            // Every zone initialization is a conservative continuity boundary. This deliberately
            // splits reconnects and reload-like zone reinitialization rather than risking a false
            // merge with another visit to the same Duty.
            this.activeRun = null;
            this.pendingZoneRun = contentFinderConditionId == 0
                ? null
                : this.CreateRun(territoryType, contentFinderConditionId, dutyName);
        }
    }

    public void ObserveBoundState(
        bool isBoundByDuty,
        uint territoryType,
        uint contentFinderConditionId,
        string? dutyName)
    {
        lock (this.gate)
        {
            if (!isBoundByDuty)
            {
                if (this.isBoundByDuty)
                {
                    this.activeRun = null;
                }

                this.isBoundByDuty = false;
                return;
            }

            this.isBoundByDuty = true;
            if (this.activeRun is not null)
            {
                return;
            }

            if (this.pendingZoneRun is not null)
            {
                this.activeRun = this.pendingZoneRun;
                this.pendingZoneRun = null;
                return;
            }

            if (contentFinderConditionId != 0)
            {
                this.activeRun = this.CreateRun(
                    territoryType,
                    contentFinderConditionId,
                    dutyName);
            }
        }
    }

    public DutyPullIdentity? BeginPull()
    {
        lock (this.gate)
        {
            if (!this.isBoundByDuty || this.activeRun is not { } run)
            {
                return null;
            }

            run.PullOrdinal++;
            return new DutyPullIdentity
            {
                DutyRunId = run.DutyRunId,
                ContentFinderConditionId = run.ContentFinderConditionId,
                DutyName = run.DutyName,
                DutyRunName = run.DutyRunName,
                DutyEnteredAtUtc = run.EnteredAtUtc,
                PullOrdinalWithinDutyRun = run.PullOrdinal,
            };
        }
    }

    private DutyRunState CreateRun(
        uint territoryType,
        uint contentFinderConditionId,
        string? dutyName)
    {
        var enteredAtUtc = this.utcNow().ToUniversalTime();
        var resolvedDutyName = string.IsNullOrWhiteSpace(dutyName)
            ? contentFinderConditionId == 0
                ? $"Territory {territoryType}"
                : $"Duty {contentFinderConditionId}"
            : dutyName.Trim();
        var localEntryTime = TimeZoneInfo.ConvertTime(enteredAtUtc, this.displayTimeZone);
        var dutyRunName = string.Create(
            CultureInfo.InvariantCulture,
            $"{resolvedDutyName} · {localEntryTime:yyyy-MM-dd HH:mm:ss}");

        return new DutyRunState(
            Guid.NewGuid(),
            contentFinderConditionId,
            resolvedDutyName,
            dutyRunName,
            enteredAtUtc);
    }

    private sealed class DutyRunState(
        Guid dutyRunId,
        uint contentFinderConditionId,
        string dutyName,
        string dutyRunName,
        DateTimeOffset enteredAtUtc)
    {
        public Guid DutyRunId { get; } = dutyRunId;
        public uint ContentFinderConditionId { get; } = contentFinderConditionId;
        public string DutyName { get; } = dutyName;
        public string DutyRunName { get; } = dutyRunName;
        public DateTimeOffset EnteredAtUtc { get; } = enteredAtUtc;
        public int PullOrdinal { get; set; }
    }
}
