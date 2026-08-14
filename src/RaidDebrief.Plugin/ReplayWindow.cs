using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal enum ReplaySourceMode
{
    RuntimeLastCompletedPull,
    DeveloperTestFixture,
}

internal enum TargetCircleVariant
{
    Directional,
    Omnidirectional,
}
internal enum MitigationTargetKind
{
    Player,
    Boss,
}

internal readonly record struct ActiveMitigationStatus(
    uint StatusId,
    int ActiveEventIndex,
    MitigationTargetKind TargetKind);


internal enum RuntimeReplaySourceDecisionKind
{
    WaitForFinalization,
    Empty,
    LoadLatest,
    LoadPreviousAfterFailure,
}

internal readonly record struct RuntimeReplaySourceDecision(
    RuntimeReplaySourceDecisionKind Kind,
    long SourceGeneration,
    PullRecord? Record,
    string? SourceDetail,
    string Message);

internal readonly record struct EnemyHudLayout(
    Vector2 PanelMinimum,
    Vector2 PanelMaximum,
    Vector2 HeaderPosition,
    Vector2 HealthBarMinimum,
    Vector2 HealthBarMaximum,
    Vector2 CastHeaderPosition,
    Vector2 CastBarMinimum,
    Vector2 CastBarMaximum,
    float NextTopOffset);


internal readonly record struct ArenaViewport
{
    private const float MaximumZoom = 20;
    private const float ZoomStep = 1.2f;

    private ArenaViewport(
        float zoom,
        Vector2 center,
        float minimumZoom,
        Vector2 panMinimum,
        Vector2 panMaximum)
    {
        this.Zoom = zoom;
        this.Center = center;
        this.MinimumZoom = minimumZoom;
        this.PanMinimum = panMinimum;
        this.PanMaximum = panMaximum;
    }

    public static ArenaViewport Fit { get; } = new(
        1,
        new Vector2(0.5f, 0.5f),
        1,
        Vector2.Zero,
        Vector2.One);

    public static ArenaViewport FromMapSizeFactor(float sizeFactor)
    {
        if (!float.IsFinite(sizeFactor) || sizeFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeFactor),
                sizeFactor,
                "Map SizeFactor must be finite and positive.");
        }

        var zoom = Math.Clamp(sizeFactor / 100, 1, MaximumZoom);
        var center = new Vector2(0.5f, 0.5f);
        var halfExtent = new Vector2(0.5f / zoom);
        return new ArenaViewport(
            zoom,
            center,
            zoom,
            center - halfExtent,
            center + halfExtent);
    }
    public static ArenaViewport FromMapSizeFactorOrFit(float? sizeFactor) =>
        sizeFactor is { } value
            ? FromMapSizeFactor(value)
            : Fit;


    public float Zoom { get; }

    public Vector2 Center { get; }

    public float MinimumZoom { get; }

    private Vector2 PanMinimum { get; }

    private Vector2 PanMaximum { get; }

    public ArenaViewport ZoomAt(Vector2 cursor, float wheelDelta)
    {
        if (wheelDelta == 0)
        {
            return this;
        }

        cursor = Vector2.Clamp(cursor, Vector2.Zero, Vector2.One);
        var worldAnchor = this.Center + ((cursor - new Vector2(0.5f, 0.5f)) / this.Zoom);
        var zoom = Math.Clamp(
            this.Zoom * MathF.Pow(ZoomStep, wheelDelta),
            this.MinimumZoom,
            MaximumZoom);
        if (zoom == this.Zoom)
        {
            return this;
        }

        var center = worldAnchor - ((cursor - new Vector2(0.5f, 0.5f)) / zoom);
        return new ArenaViewport(
            zoom,
            ClampCenter(center, zoom, this.PanMinimum, this.PanMaximum),
            this.MinimumZoom,
            this.PanMinimum,
            this.PanMaximum);
    }

    public ArenaViewport PanBy(Vector2 screenDelta)
    {
        if (this.Zoom == this.MinimumZoom)
        {
            return this;
        }

        return new ArenaViewport(
            this.Zoom,
            ClampCenter(
                this.Center - (screenDelta / this.Zoom),
                this.Zoom,
                this.PanMinimum,
                this.PanMaximum),
            this.MinimumZoom,
            this.PanMinimum,
            this.PanMaximum);
    }

    public ArenaViewport CenterOn(ArenaPoint point) =>
        new(
            this.Zoom,
            ClampCenter(
                new Vector2(point.X, point.Y),
                this.Zoom,
                this.PanMinimum,
                this.PanMaximum),
            this.MinimumZoom,
            this.PanMinimum,
            this.PanMaximum);

    public Vector2 Project(ArenaPoint point) =>
        ((new Vector2(point.X, point.Y) - this.Center) * this.Zoom) + new Vector2(0.5f, 0.5f);

    private static Vector2 ClampCenter(
        Vector2 center,
        float zoom,
        Vector2 panMinimum,
        Vector2 panMaximum)
    {
        var halfExtent = new Vector2(0.5f / zoom);
        return Vector2.Clamp(
            center,
            panMinimum + halfExtent,
            panMaximum - halfExtent);
    }
}

internal sealed class ReplayWindow : Window, IDisposable
{
    private const string PrimaryFixtureCaptureId = "6fe1b80f-567a-41a3-8912-6d013c137aa7";
    internal const string TargetCircleResourceName = "RaidDebrief.TargetCircle.png";
    internal const string OmnidirectionalTargetRingResourceName = "RaidDebrief.TargetRing.png";
    internal const float PlayerIconHalfSize = 14;
    internal const float PlayerTargetCircleHalfWidth = PlayerIconHalfSize * 2;
    internal const float TargetCircleOuterRingRadiusRatio = 0.78f;
    internal const float OmnidirectionalTargetRingOuterRadiusRatio = 0.78f;
    internal const float TargetMarkerHalfSize = 18;
    internal const float TargetMarkerVerticalOffset = 24;
    internal const float LetterWaymarkWorldRadius = 1.25f;
    internal const float NumberWaymarkWorldHalfSize = 2.3f / 2;
    internal const float WaymarkTextureScale = 1.5f;
    internal const float EnemyHudWidth = 280;
    internal const float EnemyHudTextHeight = 18;
    internal const float EnemyHudBarHeight = 10;
    internal const float EnemyHudRowGap = 4;
    internal const float EnemyHudGroupGap = 12;
    internal const float EnemyHudHorizontalInset = 12;
    internal const float EnemyHudTopInset = 28;
    private const long CastCompletionToleranceMilliseconds = 150;
    private const long DeathContextWindowMilliseconds = 8_000;
    internal const float ReplayWindowBackgroundAlpha = 0.96f;
    private const float PanelRounding = 6;
    private static readonly Vector4 PanelBackgroundColor = new(0.09f, 0.09f, 0.11f, 0.55f);
    private static readonly Vector4 SectionHeaderColor = new(0.74f, 0.78f, 0.88f, 1f);
    private const float PartyJobTextScale = 1.2f;
    private const float PartyValueTextScale = 0.95f;
    private const float KillingBlowActionTextScale = 1.15f;
    private const float KillingBlowAmountTextScale = 1.6f;
    private const float KillingBlowSourceTextScale = 0.95f;
    private const float KillingBlowCardPadding = 8;
    private const float KillingBlowCardRounding = 4;
    private static readonly Vector4 KillingBlowCardColor = new(0.22f, 0.09f, 0.10f, 0.75f);
    private static readonly Vector4 KillingBlowCardBorderColor = new(0.62f, 0.24f, 0.24f, 0.85f);
    private static readonly Vector4 KillingBlowAmountColor = new(1f, 0.52f, 0.46f, 1f);
    private const float PartyIconSize = 22;
    private const float PartyJobTextOffset = 27;
    private const float PartyHpTextOffset = 78;
    private const float PartyRowBarHeight = 8;
    private const float PartyBarrierBarHeight = 6;
    private const float PartyBarrierOverhang = 3;
    private const float PartyRowBarGap = 5;
    private const float PartyRowBottomPadding = 5;
    private const float PartyBarRounding = 1.5f;
    private const float PartyDeadRowAlpha = 0.45f;
    private static readonly Vector4 PartyBarTrackColor = new(0.08f, 0.10f, 0.14f, 0.95f);
    private static readonly Vector4 PartyBarrierColor = new(0.96f, 0.86f, 0.24f, 1f);
    private static readonly Vector4 PartyHpColor = new(0.30f, 0.72f, 0.38f, 1f);
    private static readonly Vector4 PartyDeadTextColor = new(0.86f, 0.30f, 0.28f, 1f);
    private static readonly Vector4 PartyDeadBarColor = new(0.42f, 0.16f, 0.16f, 1f);
    private const float MitigationIconSize = 30;
    private const float MitigationTileWidth = 38;
    private const float MitigationTileGap = 6;
    private const int MaximumDisplayedMitigations = 64;
    private readonly CaptureService captureService;
    private readonly ISharedImmediateTexture targetCircleTexture;
    private readonly ISharedImmediateTexture omnidirectionalTargetRingTexture;
    private readonly BattleNpcOmnidirectionalityCatalog omnidirectionalityCatalog;
    private readonly ISharedImmediateTexture?[] jobIcons;
    private readonly ISharedImmediateTexture[] targetMarkerTextures;
    private readonly ISharedImmediateTexture?[] waymarkTextures;
    private readonly Dictionary<uint, ISharedImmediateTexture> statusEffectIcons;
    private readonly ReplayMapBackgroundResolver mapBackgroundResolver;
    private readonly ReplayGameDataCatalog gameDataCatalog;
    private readonly Action<bool> saveShowHotEffectsSetting;
    private ReplayMapBackground? mapBackground;
    private string fixturePath;
    private ReplaySession? session;
    private ReplayPresentationModel? presentation;
    private readonly ReplayLoadCoordinator loadCoordinator;
    private readonly ReplayCombatGate combatGate = new();
    private ReplaySourceMode? requestedSourceMode;
    private DebriefReplayRequest? requestedDebriefReplay;
    private long? requestedInitialSeekTimestamp;
    private DebriefReplayWindow? activeSuggestedReplayWindow;
    private ReplaySourceMode? activeSourceMode;
    private string? activeSourceDetail;
    private long? activeRuntimeSourceGeneration;
    private long developerSourceGeneration;
    private ReplaySourceMode? failedLoadSourceMode;
    private long failedLoadSourceGeneration;
    private Guid failedLoadCaptureId;
    private string? failedLoadMessage;
    private string? captureFeatureWarning;
    private string statusMessage = "尚未載入 Replay；請使用 Runtime LastCompletedPull 或 Developer/Test fixture。";
    private double elapsedRemainderMilliseconds;
    private float playbackSpeed = 1;
    private ArenaViewport arenaViewport = ArenaViewport.Fit;
    private ArenaViewport minimumArenaViewport = ArenaViewport.Fit;
    private bool disposed;
    private bool closeReplayOnCombatStart = true;
    private bool showHotEffects;
    private int? selectedPlayerStableActorId;

    public ReplayWindow(
        CaptureService captureService,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        BattleNpcOmnidirectionalityCatalog omnidirectionalityCatalog,
        bool showHotEffects,
        Action<bool> saveShowHotEffectsSetting,
        string pluginConfigDirectory,
        string pluginAssemblyPath)
        : base("Raid Debrief — Replay##RaidDebriefReplay")
    {
        this.captureService = captureService;
        this.omnidirectionalityCatalog = omnidirectionalityCatalog
            ?? throw new ArgumentNullException(nameof(omnidirectionalityCatalog));
        this.showHotEffects = showHotEffects;
        this.saveShowHotEffectsSetting = saveShowHotEffectsSetting
            ?? throw new ArgumentNullException(nameof(saveShowHotEffectsSetting));
        this.jobIcons = JobIconResources.LoadTextures(textureProvider, typeof(ReplayWindow).Assembly);
        this.targetCircleTexture = LoadTargetCircle(textureProvider);
        this.omnidirectionalTargetRingTexture = LoadTargetCircle(
            textureProvider,
            OmnidirectionalTargetRingResourceName);
        this.targetMarkerTextures = LoadTargetMarkers(textureProvider);
        this.waymarkTextures = LoadWaymarkTextures(textureProvider);
        this.gameDataCatalog = new ReplayGameDataCatalog(dataManager);
        this.statusEffectIcons = LoadStatusEffectIcons(
            textureProvider,
            this.gameDataCatalog);
        var mapCanvasCatalog = new ReplayMapCanvasCatalog(dataManager, Plugin.Log);
        this.mapBackgroundResolver = new ReplayMapBackgroundResolver(
            mapCanvasCatalog,
            textureProvider,
            Plugin.Log);
        this.loadCoordinator = new ReplayLoadCoordinator(
            (record, _) => new ReplaySession(
                record,
                mapCanvasCatalog.CreateProjection(record)));
        Plugin.Log.Information(
            "Replay uses directional and omnidirectional embedded Target Ring textures, 8 native Waymark icons, {TargetMarkerCount} Target Marker textures, and {StatusEffectIconCount}/{StatusEffectCount} native defensive/HoT Status icons classified once from English Lumina descriptions; Waymark A-D use a {LetterWaymarkRadius:F2}-unit world radius, Waymark 1-4 use a {NumberWaymarkEdge:F2}-unit world edge, Player Target Circle is fixed at {PlayerCircleWidth}px, and Boss/Add rings use recorded world-space HitboxRadius.",
            this.targetMarkerTextures.Length,
            this.statusEffectIcons.Count,
            this.gameDataCatalog.StatusEffects.Count,
            LetterWaymarkWorldRadius,
            NumberWaymarkWorldHalfSize * 2,
            PlayerTargetCircleHalfWidth * 2);
        this.fixturePath = FindDefaultFixturePath(pluginConfigDirectory, pluginAssemblyPath);
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1_040, 680),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.BgAlpha = ReplayWindowBackgroundAlpha;
    }

    public void OpenRuntime(bool inCombat)
    {
        if (this.disposed)
        {
            return;
        }

        this.UpdateUiState(inCombat, this.closeReplayOnCombatStart);

        this.IsOpen = true;
        this.RequestRuntimeSource();
    }

    public void OpenDebriefReplay(DebriefReplayRequest request, bool inCombat)
    {
        if (this.disposed)
        {
            return;
        }

        this.UpdateUiState(inCombat, this.closeReplayOnCombatStart);

        var snapshot = this.captureService.GetReplaySourceSnapshot();
        if (!IsDebriefReplayRequestCurrent(snapshot, request))
        {
            this.statusMessage = "Debrief 所屬 Pull 已不再是目前的 in-memory completed Pull。";
            Plugin.Log.Warning(
                "Debrief Replay request rejected for generation {SourceGeneration}, Capture {CaptureId}; Runtime source changed.",
                request.SourceGeneration,
                request.CaptureId);
            return;
        }

        this.IsOpen = true;
        this.requestedSourceMode = ReplaySourceMode.RuntimeLastCompletedPull;
        this.requestedDebriefReplay = request;
        this.requestedInitialSeekTimestamp = request.Window.StartTimestampMilliseconds;
        this.activeSuggestedReplayWindow = request.Window;
        this.ApplyRuntimeSource(snapshot);
    }

    public void UpdateUiState(bool inCombat, bool closeReplayOnCombatStart)
    {
        if (this.disposed)
        {
            return;
        }

        this.closeReplayOnCombatStart = closeReplayOnCombatStart;
        if (!inCombat && !this.IsOpen)
        {
            this.SuspendReplayWork();
        }

        var decision = this.combatGate.Observe(
            inCombat,
            this.IsOpen,
            this.session?.IsPlaying == true,
            this.loadCoordinator.IsLoading,
            closeReplayOnCombatStart);
        if (decision.Action == ReplayCombatAction.None)
        {
            return;
        }

        this.SuspendReplayWork();
        if (decision.Action == ReplayCombatAction.Pause)
        {
            this.statusMessage =
                "戰鬥開始；Replay 已暫停，並依設定保持視窗開啟。";
            Plugin.Log.Information(
                "Replay paused but kept visible because combat auto-close is disabled.");
            return;
        }

        this.IsOpen = false;
        this.statusMessage =
            "戰鬥開始；Replay 已暫停並自動關閉。脫離戰鬥後不會自動重開。";
        Plugin.Log.Information("Replay paused and hidden because InCombat=true.");
    }

    private void SuspendReplayWork()
    {
        this.session?.Pause();
        this.requestedSourceMode = null;
        this.requestedDebriefReplay = null;
        this.requestedInitialSeekTimestamp = null;
        if (this.loadCoordinator.IsLoading)
        {
            this.loadCoordinator.Invalidate();
        }

        this.elapsedRemainderMilliseconds = 0;
    }
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.session?.Pause();
        this.IsOpen = false;
        this.requestedSourceMode = null;
        this.elapsedRemainderMilliseconds = 0;
        this.loadCoordinator.Dispose();
    }



    public override void Draw()
    {
        var framePolicy = ReplayFramePolicy.Resolve(
            this.IsOpen,
            this.combatGate.InCombat,
            this.session?.IsPlaying == true);
        if (this.disposed || !framePolicy.ShouldDraw)
        {
            this.IsOpen = false;
            return;
        }

        this.RefreshRuntimeSource();
        this.CompletePendingLoad();
        if (framePolicy.ShouldAdvance)
        {
            this.AdvancePlayback();
        }
        this.DrawSourceControls();

        if (this.session is null || this.presentation is null)
        {
            return;
        }

        if (this.captureFeatureWarning is { } warning)
        {
            ImGui.TextColored(new Vector4(1, 0.64f, 0.2f, 1), "Legacy recording limitations");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(warning);
            }
        }

        this.DrawReplayLayout(this.session, this.presentation);
    }



    private static ISharedImmediateTexture LoadTargetCircle(
        ITextureProvider textureProvider,
        string resourceName = TargetCircleResourceName)
    {
        ArgumentNullException.ThrowIfNull(textureProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        return textureProvider.GetFromManifestResource(
            typeof(ReplayWindow).Assembly,
            resourceName);
    }

    private static ISharedImmediateTexture[] LoadTargetMarkers(ITextureProvider textureProvider)
    {
        ArgumentNullException.ThrowIfNull(textureProvider);
        var markerIds = Enum.GetValues<TargetMarkerId>();
        var textures = new ISharedImmediateTexture[markerIds.Length];
        foreach (var id in markerIds)
        {
            textures[(int)id] = textureProvider.GetFromManifestResource(
                typeof(ReplayWindow).Assembly,
                TargetMarkerResources.GetManifestResourceName(id));
        }

        return textures;
    }

    private static ISharedImmediateTexture?[] LoadWaymarkTextures(ITextureProvider textureProvider)
    {
        ArgumentNullException.ThrowIfNull(textureProvider);
        var textures = new ISharedImmediateTexture?[WaymarkReader.MarkerCount];
        foreach (var id in Enum.GetValues<WaymarkId>())
        {
            try
            {
                textures[(int)id] = textureProvider.GetFromGameIcon(
                    new GameIconLookup(
                        ResolveWaymarkIconId(id),
                        itemHq: false,
                        hiRes: true));
            }
            catch (Exception exception)
            {
                Plugin.Log.Error(
                    exception,
                    "Replay failed to load native Waymark icon {WaymarkId}; the fallback marker will be used.",
                    id);
            }
        }

        return textures;
    }
    private static Dictionary<uint, ISharedImmediateTexture> LoadStatusEffectIcons(
        ITextureProvider textureProvider,
        ReplayGameDataCatalog gameDataCatalog)
    {
        ArgumentNullException.ThrowIfNull(textureProvider);
        ArgumentNullException.ThrowIfNull(gameDataCatalog);
        var statusIds = gameDataCatalog.StatusEffects.StatusIds;
        var textures = new Dictionary<uint, ISharedImmediateTexture>(
            statusIds.Length);
        foreach (var statusId in statusIds)
        {
            if (!gameDataCatalog.TryGetStatusIconId(statusId, out var iconId))
            {
                Plugin.Log.Warning(
                    "Replay Status {StatusId} ({StatusName}) has no native icon in the Lumina Status sheet; a placeholder will be used.",
                    statusId,
                    gameDataCatalog.GetStatusName(statusId));
                continue;
            }

            try
            {
                textures.Add(
                    statusId,
                    textureProvider.GetFromGameIcon(
                        new GameIconLookup(
                            iconId,
                            itemHq: false,
                            hiRes: true)));
            }
            catch (Exception exception)
            {
                Plugin.Log.Error(
                    exception,
                    "Replay failed to load native Status icon {IconId} for Status {StatusId}; a placeholder will be used.",
                    iconId,
                    statusId);
            }
        }

        return textures;
    }

    internal static string? BuildCaptureFeatureWarning(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var missingOwnerId = (record.Features & CaptureFeatures.ActorOwnerId) == 0;
        var missingHitboxRadius = (record.Features & CaptureFeatures.HitboxRadius) == 0;
        var warning = (missingOwnerId, missingHitboxRadius) switch
        {
            (true, true) =>
                "此 Pull 不包含 OwnerID 與 HitboxRadius；無法可靠排除召喚物，也無法依真實 world-space 大小繪製 Boss／Add Target Circle。Player Target Circle 仍使用固定大小。",
            (true, false) =>
                "此 Pull 不包含 OwnerID；無法可靠排除召喚物。",
            (false, true) =>
                "此 Pull 不包含 HitboxRadius；無法依真實 world-space 大小繪製 Boss／Add Target Circle。Player Target Circle 仍使用固定大小。",
            _ => null,
        };
        if ((record.Features & CaptureFeatures.TargetMarkers) == 0)
        {
            warning = warning is null
                ? "此 Pull 不包含 Target Marker。"
                : $"{warning} 此 Pull 也不包含 Target Marker。";
        }
        if ((record.Features & CaptureFeatures.OmnidirectionalState) == 0)
        {
            const string omnidirectionalityWarning =
                "此 Pull 未記錄戰鬥中的身位無效狀態；Boss／Add 僅使用 BNpcBase 靜態判定。";
            warning = warning is null
                ? omnidirectionalityWarning
                : $"{warning} {omnidirectionalityWarning}";
        }
        if ((record.Features & CaptureFeatures.PartyMembership) == 0)
        {
            warning = AppendWarning(
                warning,
                "Party membership is unavailable; Replay falls back to recorded player Actors.");
        }

        if ((record.Features & CaptureFeatures.CastTiming) == 0)
        {
            warning = AppendWarning(
                warning,
                "Exact cast progress is unavailable.");
        }

        if ((record.Features & CaptureFeatures.StatusTiming) == 0)
        {
            warning = AppendWarning(
                warning,
                "Exact status remaining time is unavailable.");
        }

        if ((record.Features & CaptureFeatures.ActionEffectCapture) == 0)
        {
            warning = AppendWarning(
                warning,
                "Killing Blow correlations cannot confirm complete ActionEffect capture.");
        }
        if ((record.Features & CaptureFeatures.ActionNameSnapshot) == 0)
        {
            warning = AppendWarning(
                warning,
                "Recorded Action names are unavailable; reserved casts may fall back to Action #ID.");
        }



        return warning is null ? null : $"{warning} 請使用目前版本重新錄製 Pull。";
    }
    private static string AppendWarning(string? current, string addition) =>
        current is null ? addition : $"{current} {addition}";


    private static string FindDefaultFixturePath(string pluginConfigDirectory, string pluginAssemblyPath)
    {
        var assemblyDirectory = Path.GetDirectoryName(pluginAssemblyPath)
            ?? throw new ArgumentException("Plugin assembly path has no directory.", nameof(pluginAssemblyPath));
        var developmentFixturePath = Path.GetFullPath(Path.Combine(
            assemblyDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "testdata",
            "recorded",
            $"{PrimaryFixtureCaptureId}.json"));
        return File.Exists(developmentFixturePath)
            ? developmentFixturePath
            : Path.Combine(pluginConfigDirectory, "capture.json");
    }

    private void CompletePendingLoad()
    {
        if (!this.loadCoordinator.TryTakeCompleted(out var completion))
        {
            return;
        }
        if (completion.Mode == ReplaySourceMode.RuntimeLastCompletedPull)
        {
            var snapshot = this.captureService.GetReplaySourceSnapshot();
            if (this.requestedDebriefReplay is { } requestedDebrief
                && !IsDebriefReplayRequestCurrent(snapshot, requestedDebrief))
            {
                this.statusMessage =
                    "Debrief 所屬 Pull 已在 Replay 載入完成前被新的 completed Pull 取代。";
                return;
            }

            var currentSource = ResolveRuntimeSource(snapshot);
            if (this.requestedSourceMode != ReplaySourceMode.RuntimeLastCompletedPull
                || !IsRuntimeLoadCurrent(
                    currentSource,
                    completion.SourceGeneration,
                    completion.CaptureId))
            {
                if (this.requestedSourceMode == ReplaySourceMode.RuntimeLastCompletedPull
                    && this.requestedDebriefReplay is null)
                {
                    this.ApplyRuntimeSource(snapshot);
                }

                return;
            }
        }


        if (completion.Error is { } error)
        {
            this.statusMessage = $"Replay 載入失敗：{error.GetBaseException().Message}";
            this.failedLoadSourceMode = completion.Mode;
            this.failedLoadSourceGeneration = completion.SourceGeneration;
            this.failedLoadCaptureId = completion.CaptureId;
            this.failedLoadMessage = this.statusMessage;
            Plugin.Log.Error(
                error,
                "Replay window failed to load {SourceMode} source generation {SourceGeneration}, Capture {CaptureId}.",
                completion.Mode,
                completion.SourceGeneration,
                completion.CaptureId);
            return;
        }

        var loadedSession = completion.Session
            ?? throw new InvalidOperationException("Replay load completed without a session or error.");
        this.session?.Pause();
        this.session = loadedSession;
        this.gameDataCatalog.UseRecordedActionNames(loadedSession.Record);
        this.selectedPlayerStableActorId = null;
        var sourceSnapshot = this.captureService.GetReplaySourceSnapshot();
        var sourceSummary = sourceSnapshot.LastCompletedPull?.CaptureId == loadedSession.Record.CaptureId
            ? sourceSnapshot.LastCompletedDebrief
            : null;
        this.presentation = ReplayPresentationModel.Create(loadedSession.Record, sourceSummary);
        this.activeSourceMode = completion.Mode;
        this.activeSourceDetail = completion.SourceDetail;
        this.activeRuntimeSourceGeneration =
            completion.Mode == ReplaySourceMode.RuntimeLastCompletedPull
                ? completion.SourceGeneration
                : null;
        this.failedLoadSourceMode = null;
        this.failedLoadMessage = null;
        this.elapsedRemainderMilliseconds = 0;
        this.mapBackground = this.mapBackgroundResolver.Resolve(this.session.Record.MapId);
        this.minimumArenaViewport = ArenaViewport.FromMapSizeFactorOrFit(
            this.mapBackground?.Definition.SizeFactor);
        this.arenaViewport = this.minimumArenaViewport;
        this.captureFeatureWarning = BuildCaptureFeatureWarning(this.session.Record);
        this.statusMessage =
            $"{completion.SuccessMessage} Replay 長度 {FormatTimestamp(this.session.DurationMilliseconds)}。";
        if (completion.Mode == ReplaySourceMode.RuntimeLastCompletedPull)
        {
            this.ApplyRequestedInitialSeek();
        }
        Plugin.Log.Information(
            "Replay window loaded {Source} Pull {CaptureId} with {Frames} frames and {Events} events.",
            this.activeSourceDetail,
            this.session.Record.CaptureId,
            this.session.Record.Frames.Length,
            this.session.Record.Events.Length);
        Plugin.Log.Information(
            "Replay arena geometry {Shape}; world X {MinX:F3}..{MaxX:F3}, Z {MinZ:F3}..{MaxZ:F3}; " +
            "observed X {ObservedMinX:F3}..{ObservedMaxX:F3}, Z {ObservedMinZ:F3}..{ObservedMaxZ:F3}.",
            this.session.Projection.Shape,
            this.session.Projection.Bounds.MinX,
            this.session.Projection.Bounds.MaxX,
            this.session.Projection.Bounds.MinZ,
            this.session.Projection.Bounds.MaxZ,
            this.session.Projection.ObservedBounds.MinX,
            this.session.Projection.ObservedBounds.MaxX,
            this.session.Projection.ObservedBounds.MinZ,
            this.session.Projection.ObservedBounds.MaxZ);
        if (this.mapBackground is { } resolvedBackground)
        {
            Plugin.Log.Information(
                "Replay initial focus uses Lumina Map {MapRowId} SizeFactor {SizeFactor}; " +
                "world center ({CenterX:F3}, {CenterZ:F3}), zoom {Zoom:F3}.",
                resolvedBackground.MapRowId,
                resolvedBackground.Definition.SizeFactor,
                -resolvedBackground.Definition.OffsetX,
                -resolvedBackground.Definition.OffsetY,
                this.minimumArenaViewport.Zoom);
        }
        else
        {
            Plugin.Log.Information(
                "Replay initial focus uses complete-field Fit because Map {MapRowId} has no usable Lumina row.",
                this.session.Record.MapId);
        }
    }


    private void AdvancePlayback()
    {
        if (this.session is not { IsPlaying: true } session)
        {
            return;
        }

        this.elapsedRemainderMilliseconds += ImGui.GetIO().DeltaTime * 1000.0 * this.playbackSpeed;
        var elapsedMilliseconds = (long)this.elapsedRemainderMilliseconds;
        if (elapsedMilliseconds <= 0)
        {
            return;
        }

        this.elapsedRemainderMilliseconds -= elapsedMilliseconds;
        session.Advance(elapsedMilliseconds);
    }

    private void DrawSourceControls()
    {
        var snapshot = this.captureService.GetReplaySourceSnapshot();
        if (this.session is null)
        {
            if (this.requestedSourceMode == ReplaySourceMode.RuntimeLastCompletedPull
                && snapshot.FinalizationState == ReplaySourceFinalizationState.Finalizing)
            {
                ImGui.TextUnformatted("正在等候 Pull 完成處理…");
            }
            else if (this.loadCoordinator.IsLoading)
            {
                ImGui.TextUnformatted("正在載入 Replay…");
            }
            else
            {
                ImGui.TextWrapped(this.statusMessage);
            }
        }

        if (ImGui.CollapsingHeader("進階／開發測試"))
        {
            ImGui.TextWrapped(
                this.activeSourceMode is null
                    ? "目前來源：尚未載入"
                    : $"目前來源：{this.activeSourceDetail}");
            if (ImGui.Button("重新載入目前 Runtime Pull"))
            {
                this.RequestRuntimeSource();
            }

            ImGui.TextDisabled("手動 JSON 匯入僅供開發測試，不是正式 Replay 資料來源。");
            ImGui.SetNextItemWidth(-100);
            ImGui.InputText("##ReplayFixturePath", ref this.fixturePath, 1_024);
            ImGui.SameLine();
            if (ImGui.Button("載入 JSON"))
            {
                this.StartDeveloperFixtureLoad();
            }

            ImGui.TextWrapped(this.statusMessage);
        }

        ImGui.Separator();
    }

    private void DrawReplayLayout(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        var available = ImGui.GetContentRegionAvail();
        var bottomHeight = Math.Clamp(available.Y * 0.24f, 150, 210);
        var mainHeight = Math.Max(380, available.Y - bottomHeight - 8);
        if (ImGui.BeginTable(
                "##ReplayMainLayout",
                3,
                ImGuiTableFlags.Resizable
                    | ImGuiTableFlags.BordersInnerV
                    | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, mainHeight)))
        {
            ImGui.TableSetupColumn(
                "Pull and Party",
                ImGuiTableColumnFlags.WidthFixed,
                285);
            ImGui.TableSetupColumn(
                "Replay Arena",
                ImGuiTableColumnFlags.WidthStretch,
                1);
            ImGui.TableSetupColumn(
                "Context",
                ImGuiTableColumnFlags.WidthFixed,
                310);
            ImGui.TableNextRow();

            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, PanelRounding);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackgroundColor);

            ImGui.TableSetColumnIndex(0);
            if (ImGui.BeginChild("##ReplayLeftPanel", new Vector2(0, mainHeight - 4), true))
            {
                this.DrawLeftPanel(replay, presentation);
            }

            ImGui.EndChild();

            ImGui.TableSetColumnIndex(1);
            if (ImGui.BeginChild("##ReplayCenterPanel", new Vector2(0, mainHeight - 4), true))
            {
                this.DrawArena(replay.Scene);
            }

            ImGui.EndChild();

            ImGui.TableSetColumnIndex(2);
            if (ImGui.BeginChild("##ReplayContextPanel", new Vector2(0, mainHeight - 4), true))
            {
                this.DrawContextPanel(replay, presentation);
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
            ImGui.EndTable();
        }

        this.DrawBottomTimeline(replay, presentation);
    }

    private static void DrawSectionHeader(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, SectionHeaderColor);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private void DrawLeftPanel(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        DrawPullSummary(presentation.Summary);
        ImGui.Spacing();
        ImGui.Separator();
        DrawSectionHeader("PARTY");
        this.DrawPartyList(replay, presentation);
    }

    private static void DrawPullSummary(DebriefSummary summary)
    {
        DrawSectionHeader("PULL SUMMARY");
        var pullNumber = summary.PullNumber is { } number
            ? $"Pull #{number}"
            : "Pull —";
        ImGui.TextUnformatted(pullNumber);
        ImGui.SameLine();
        ImGui.TextDisabled(FormatTimestamp(summary.DurationMilliseconds));

        var bossHp = summary.BossHpAtEnd is { } hp
            ? $"{hp.Percentage:F1}%"
            : "—";
        ImGui.TextDisabled("Final Boss HP");
        ImGui.SameLine();
        ImGui.TextUnformatted(bossHp);
    }

    private void DrawPartyList(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        if (presentation.PartyActors.Length == 0)
        {
            ImGui.TextDisabled("No recorded party members.");
            return;
        }

        var rowHeight = ResolveActorVitalsRowHeight();
        foreach (var actor in presentation.PartyActors)
        {
            var stateAvailable = TryFindActorMarker(
                replay.Scene.Actors,
                actor.StableActorId,
                out var marker);
            var origin = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            if (ImGui.Selectable(
                    $"##ReplayParty{actor.StableActorId}",
                    this.selectedPlayerStableActorId == actor.StableActorId,
                    ImGuiSelectableFlags.None,
                    new Vector2(0, rowHeight)))
            {
                this.selectedPlayerStableActorId = actor.StableActorId;
            }

            if (stateAvailable && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"{ActorDisplayLabel(actor)}\n{FormatPartyHp(marker)}{FormatPartyBarrierSuffix(marker)}");
            }

            this.DrawActorVitalsRow(actor, stateAvailable, marker, origin, width);
        }
    }

    private static float ResolveActorVitalsRowHeight()
    {
        var textHeight = ImGui.GetTextLineHeight() * PartyJobTextScale;
        return textHeight + PartyRowBarGap + PartyRowBarHeight + PartyRowBottomPadding;
    }

    private void DrawActorVitalsRow(
        ActorRecord actor,
        bool stateAvailable,
        in ArenaActorMarker marker,
        Vector2 origin,
        float width)
    {
        width = Math.Max(0, width);
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var lineHeight = ImGui.GetTextLineHeight();
        var jobFontSize = ImGui.GetFontSize() * PartyJobTextScale;
        var valueFontSize = ImGui.GetFontSize() * PartyValueTextScale;
        var textHeight = lineHeight * PartyJobTextScale;
        var valueOffsetY = (textHeight - (lineHeight * PartyValueTextScale)) * 0.5f;
        var isDead = stateAvailable && marker.IsDead;
        var alpha = isDead ? PartyDeadRowAlpha : 1f;
        var textColor = ImGui.GetColorU32(new Vector4(1, 1, 1, alpha));
        var dimColor = ImGui.GetColorU32(new Vector4(0.62f, 0.64f, 0.70f, alpha));

        var iconMinimum = new Vector2(
            origin.X,
            origin.Y + ((textHeight - PartyIconSize) * 0.5f));
        if (actor.ClassJobId < this.jobIcons.Length
            && this.jobIcons[(int)actor.ClassJobId] is { } icon
            && icon.TryGetWrap(out var texture, out _)
            && texture is not null)
        {
            drawList.AddImage(
                texture.Handle,
                iconMinimum,
                iconMinimum + new Vector2(PartyIconSize),
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(new Vector4(1, 1, 1, alpha)));
        }

        var job = JobIconResources.GetAbbreviation(actor.ClassJobId) ?? "PC";
        drawList.AddText(
            font,
            jobFontSize,
            new Vector2(origin.X + PartyJobTextOffset, origin.Y),
            textColor,
            job);

        var barMinimum = new Vector2(origin.X, origin.Y + textHeight + PartyRowBarGap);
        var barMaximum = new Vector2(origin.X + width, barMinimum.Y + PartyRowBarHeight);
        drawList.AddRectFilled(
            barMinimum,
            barMaximum,
            ImGui.GetColorU32(PartyBarTrackColor),
            PartyBarRounding);

        if (!stateAvailable)
        {
            drawList.AddText(
                font,
                valueFontSize,
                new Vector2(origin.X + PartyHpTextOffset, origin.Y + valueOffsetY),
                dimColor,
                "—");
            return;
        }

        var (hpFraction, barrierFraction) = ResolvePartyBarFractions(
            marker.CurrentHp,
            marker.MaxHp,
            marker.BarrierPercentage);
        if (hpFraction > 0)
        {
            var hpColor = ResolvePartyHpColor(isDead);
            hpColor.W *= alpha;
            drawList.AddRectFilled(
                barMinimum,
                new Vector2(barMinimum.X + (width * hpFraction), barMaximum.Y),
                ImGui.GetColorU32(hpColor),
                PartyBarRounding);
        }

        // The barrier is anchored at the left edge and protrudes above the health
        // bar, so it stays visible at full health like the in-game party list.
        if (barrierFraction > 0)
        {
            var barrierColor = PartyBarrierColor;
            barrierColor.W *= alpha;
            var barrierTop = barMinimum.Y - PartyBarrierOverhang;
            drawList.AddRectFilled(
                new Vector2(barMinimum.X, barrierTop),
                new Vector2(
                    barMinimum.X + (width * barrierFraction),
                    barrierTop + PartyBarrierBarHeight),
                ImGui.GetColorU32(barrierColor),
                PartyBarRounding);
        }

        if (marker.MaxHp == 0)
        {
            drawList.AddText(
                font,
                valueFontSize,
                new Vector2(origin.X + PartyHpTextOffset, origin.Y + valueOffsetY),
                dimColor,
                "—");
            return;
        }

        var hpText = isDead
            ? "DEAD"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{marker.CurrentHp:N0} / {marker.MaxHp:N0}");
        drawList.AddText(
            font,
            valueFontSize,
            new Vector2(origin.X + PartyHpTextOffset, origin.Y + valueOffsetY),
            isDead ? ImGui.GetColorU32(PartyDeadTextColor) : textColor,
            hpText);

        var percentage = marker.CurrentHp * 100d / marker.MaxHp;
        var percentText = string.Create(CultureInfo.InvariantCulture, $"{percentage:F1}%");
        var percentWidth = ImGui.CalcTextSize(percentText).X * PartyValueTextScale;
        drawList.AddText(
            font,
            valueFontSize,
            new Vector2(origin.X + width - percentWidth, origin.Y + valueOffsetY),
            dimColor,
            percentText);
    }

    private void DrawActorVitalsPanel(
        ActorRecord actor,
        bool stateAvailable,
        in ArenaActorMarker marker)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.Dummy(new Vector2(width, ResolveActorVitalsRowHeight()));
        this.DrawActorVitalsRow(actor, stateAvailable, marker, origin, width);
    }


    private void DrawContextPanel(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        // Focus wins. Without a focused actor the panel follows the playhead and
        // surfaces the death that is currently in scope.
        var focusedActorId = this.selectedPlayerStableActorId;
        if (ResolveDeathAtTimestamp(
                presentation.Deaths,
                focusedActorId,
                replay.CurrentTimeMilliseconds,
                DeathContextWindowMilliseconds) is { } recordedIndex
            && presentation.TryGetDeath(recordedIndex, out var death))
        {
            this.DrawDeathDetails(replay, death, presentation.BossActor);
            return;
        }

        if (focusedActorId is { } selectedActorId
            && replay.TryGetActor(selectedActorId, out var actor))
        {
            this.DrawActorSnapshot(replay, actor, presentation.BossActor);
            return;
        }

        DrawSectionHeader("CONTEXT");
        ImGui.TextWrapped(
            "Scrub to a death, or select a party member to pin the recorded state.");
    }

    /// <summary>
    /// Resolves the death that the context panel should show at a playhead position:
    /// the most recent death at or before the timestamp, within the trailing window.
    /// When <paramref name="focusedActorStableId"/> is set only that actor's deaths
    /// qualify, so a focused party member never shows somebody else's death.
    /// </summary>
    /// <remarks>Deaths are ordered by ascending timestamp.</remarks>
    internal static int? ResolveDeathAtTimestamp(
        ReadOnlySpan<ReplayDeathItem> deaths,
        int? focusedActorStableId,
        long timestampMilliseconds,
        long windowMilliseconds)
    {
        int? resolved = null;
        foreach (ref readonly var death in deaths)
        {
            var deathTimestamp = death.Correlation.DeathTimestampMilliseconds;
            if (deathTimestamp > timestampMilliseconds)
            {
                break;
            }

            if (timestampMilliseconds - deathTimestamp > windowMilliseconds)
            {
                continue;
            }

            if (focusedActorStableId is { } actorId
                && death.Actor.StableActorId != actorId)
            {
                continue;
            }

            resolved = death.Correlation.DeathOriginalRecordedIndex;
        }

        return resolved;
    }

    private void DrawActorSnapshot(
        ReplaySession replay,
        ActorRecord actor,
        ActorRecord? bossActor)
    {
        var stateAvailable = TryFindActorMarker(
            replay.Scene.Actors,
            actor.StableActorId,
            out var marker);
        this.DrawActorVitalsPanel(actor, stateAvailable, marker);
        ImGui.Separator();

        if (stateAvailable)
        {
            ImGui.TextDisabled("Barrier");
            if (marker.BarrierPercentage is { } barrierPercentage)
            {
                ImGui.TextUnformatted(
                    FormatBarrierAmount(marker.MaxHp, barrierPercentage));
            }
            else
            {
                ImGui.TextDisabled("—  Not recorded");
            }

            ImGui.Spacing();
            ImGui.TextDisabled("State");
            ImGui.TextUnformatted(marker.IsDead ? "Dead" : "Alive");
        }
        else
        {
            ImGui.TextDisabled("Actor was not present at this replay timestamp.");
        }

        ImGui.Spacing();
        this.DrawActiveMitigations(
            replay,
            actor.StableActorId,
            replay.CurrentTimeMilliseconds,
            bossActor);
    }

    private void DrawDeathDetails(
        ReplaySession replay,
        in ReplayDeathItem death,
        ActorRecord? bossActor)
    {
        var correlation = death.Correlation;
        var job = JobIconResources.GetAbbreviation(death.Actor.ClassJobId) ?? "Player";
        ImGui.TextUnformatted($"{job}  ·  DEATH");
        ImGui.SameLine();
        ImGui.TextColored(
            new Vector4(1, 0.3f, 0.3f, 1),
            FormatTimestamp(correlation.DeathTimestampMilliseconds));
        ImGui.Separator();

        var confidenceColor = correlation.Confidence switch
        {
            CorrelationConfidence.High => new Vector4(0.35f, 0.85f, 0.55f, 1),
            CorrelationConfidence.Medium => new Vector4(1, 0.72f, 0.25f, 1),
            CorrelationConfidence.Low => new Vector4(1, 0.5f, 0.28f, 1),
            _ => new Vector4(0.65f, 0.65f, 0.65f, 1),
        };
        var heading = correlation.Confidence switch
        {
            CorrelationConfidence.High => "Correlated Killing Blow · High",
            CorrelationConfidence.Medium => "Likely Killing Blow · Medium",
            CorrelationConfidence.Low => "Possible Final Hit · Low",
            _ => "Killing Blow · Unavailable",
        };
        ImGui.TextColored(confidenceColor, heading);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Derived from recorded HP, Action Effects, and Death transition.\n" +
                $"Evidence: {correlation.Evidence}\n" +
                $"Limitations: {correlation.Limitations}\n" +
                "FFXIV does not provide a direct Killing Blow field.");
        }

        if (correlation.KillingBlowCandidate is { } candidate)
        {
            this.DrawKillingBlowCard(replay, candidate);
        }
        else
        {
            ImGui.TextDisabled("No target-resolved incoming damage was recorded.");
        }

        if (correlation.EstimatedHpBeforeHit is { } hpBefore)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("HP Before Hit");
            ImGui.TextUnformatted($"{hpBefore:N0}");
        }

        DrawBarrierDisposition(correlation.Barrier);

        if (correlation.EstimatedEffectivePoolBeforeHit is { } pool
            && correlation.Barrier.StoodAgainstTheKillingBlow)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Effective Pool");
            ImGui.TextUnformatted($"{pool:N0}");
        }

        if (correlation.EstimatedOverkill is { } overkill)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(
                correlation.Barrier.StoodAgainstTheKillingBlow
                    ? "Estimated Overkill (vs pool)"
                    : "Estimated Overkill");
            ImGui.TextUnformatted($"~{overkill:N0}");
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawSectionHeader("LAST CAPTURED HITS");
        if (correlation.LastHits.Length == 0)
        {
            ImGui.TextDisabled("—");
        }
        else
        {
            foreach (var hit in correlation.LastHits)
            {
                var offset = (hit.TimestampMilliseconds
                    - correlation.DeathTimestampMilliseconds) / 1000f;
                ImGui.TextDisabled($"{offset,6:F1}s");
                ImGui.SameLine();
                ImGui.TextUnformatted(this.gameDataCatalog.GetActionName(hit.ActionId));
                ImGui.SameLine();
                ImGui.TextUnformatted($"{hit.Amount:N0}");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        this.DrawActiveMitigations(
            replay,
            death.Actor.StableActorId,
            correlation.DeathTimestampMilliseconds,
            bossActor);
    }

    /// <summary>
    /// States what the recorded barrier did. "Consumed" and "Expired" are read from the
    /// status duration remaining when the barrier ended, not inferred from the damage.
    /// </summary>
    private static void DrawBarrierDisposition(in DeathBarrierObservation barrier)
    {
        if (barrier.Disposition == BarrierDisposition.None)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Barrier");
        if (barrier.Disposition == BarrierDisposition.NotRecorded)
        {
            ImGui.TextDisabled("—  Not recorded by this capture");
            return;
        }

        ImGui.TextUnformatted($"~{barrier.AmountAtDeath:N0}  ({barrier.PercentageAtDeath}%)");
        ImGui.TextColored(
            new Vector4(0.98f, 0.82f, 0.35f, 1),
            "Standing on the last sample before death");
    }

    /// <summary>
    /// Renders the recorded killing blow as the focal element of the death panel:
    /// the action, the recorded amount, and the recorded source actor.
    /// </summary>
    private void DrawKillingBlowCard(ReplaySession replay, in CorrelatedDamageEvent candidate)
    {
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var baseFontSize = ImGui.GetFontSize();
        var lineHeight = ImGui.GetTextLineHeight();
        var actionHeight = lineHeight * KillingBlowActionTextScale;
        var amountHeight = lineHeight * KillingBlowAmountTextScale;
        var sourceHeight = lineHeight * KillingBlowSourceTextScale;
        var cardHeight = (KillingBlowCardPadding * 2) + actionHeight + amountHeight + sourceHeight;

        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.Dummy(new Vector2(width, cardHeight));

        drawList.AddRectFilled(
            origin,
            origin + new Vector2(width, cardHeight),
            ImGui.GetColorU32(KillingBlowCardColor),
            KillingBlowCardRounding);
        drawList.AddRect(
            origin,
            origin + new Vector2(width, cardHeight),
            ImGui.GetColorU32(KillingBlowCardBorderColor),
            KillingBlowCardRounding);

        var textX = origin.X + KillingBlowCardPadding;
        var textY = origin.Y + KillingBlowCardPadding;
        drawList.AddText(
            font,
            baseFontSize * KillingBlowActionTextScale,
            new Vector2(textX, textY),
            ImGui.GetColorU32(new Vector4(1, 1, 1, 1)),
            this.gameDataCatalog.GetActionName(candidate.ActionId));

        textY += actionHeight;
        drawList.AddText(
            font,
            baseFontSize * KillingBlowAmountTextScale,
            new Vector2(textX, textY),
            ImGui.GetColorU32(KillingBlowAmountColor),
            candidate.Amount.ToString("N0", CultureInfo.InvariantCulture));

        // Recorded source actor, or an explicit statement that it was not resolved.
        var source = candidate.SourceStableActorId is { } sourceStableActorId
            && replay.TryGetActor(sourceStableActorId, out var sourceActor)
                ? $"from  {ActorDisplayLabel(sourceActor)}"
                : "Source actor not resolved";
        textY += amountHeight;
        drawList.AddText(
            font,
            baseFontSize * KillingBlowSourceTextScale,
            new Vector2(textX, textY),
            ImGui.GetColorU32(new Vector4(0.68f, 0.70f, 0.76f, 1)),
            source);
    }

    private void DrawActiveMitigations(
        ReplaySession replay,
        int actorId,
        long timestampMilliseconds,
        ActorRecord? bossActor)
    {
        DrawSectionHeader("ACTIVE MITIGATION / HoT");
        if (ImGui.Checkbox("顯示 HoT 效果", ref this.showHotEffects))
        {
            this.saveShowHotEffectsSetting(this.showHotEffects);
        }

        Span<ActiveMitigationStatus> activeStatuses =
            stackalloc ActiveMitigationStatus[MaximumDisplayedMitigations];
        var events = replay.Timeline.Events;
        var activeBossActorId = ResolveActiveBossActorId(
            replay.Scene.Actors,
            bossActor);
        var activeCount = CollectActiveMitigations(
            events,
            actorId,
            activeBossActorId,
            timestampMilliseconds,
            this.gameDataCatalog.StatusEffects,
            this.showHotEffects,
            activeStatuses);
        var columns = ResolveMitigationGridColumnCount(
            ImGui.GetContentRegionAvail().X);
        for (var index = 0; index < activeCount; index++)
        {
            if (index > 0 && index % columns != 0)
            {
                ImGui.SameLine(0, MitigationTileGap);
            }

            var activeStatus = activeStatuses[index];
            float? remaining = null;
            if (TryResolveStatusRemainingSeconds(
                    events,
                    activeStatus.ActiveEventIndex,
                    timestampMilliseconds,
                    out var remainingSeconds))
            {
                remaining = remainingSeconds;
            }

            var statusParam = this.gameDataCatalog.HasStacks(activeStatus.StatusId)
                ? events[activeStatus.ActiveEventIndex].ObservedEvent.StatusParam ?? 0
                : (ushort)0;
            this.DrawMitigationStatusTile(
                activeStatus.StatusId,
                statusParam,
                remaining,
                activeStatus.TargetKind);
        }

        if (activeCount == 0)
        {
            ImGui.TextDisabled(
                this.showHotEffects
                    ? "No recorded mitigation or HoT active."
                    : "No recorded mitigation active.");
        }
    }

    internal static int ResolveMitigationGridColumnCount(float availableWidth) =>
        Math.Max(
            1,
            (int)MathF.Floor(
                (Math.Max(0, availableWidth) + MitigationTileGap)
                / (MitigationTileWidth + MitigationTileGap)));

    internal static int CollectActiveMitigations(
        ReadOnlySpan<ReplayTimelineEntry> events,
        int playerActorId,
        int? bossActorId,
        long timestampMilliseconds,
        ReplayStatusEffectDatabase statusEffects,
        bool showHealingOverTime,
        Span<ActiveMitigationStatus> destination)
    {
        if (timestampMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampMilliseconds));
        }
        ArgumentNullException.ThrowIfNull(statusEffects);

        var count = CollectActiveMitigationsForActor(
            events,
            playerActorId,
            timestampMilliseconds,
            MitigationTargetKind.Player,
            statusEffects,
            showHealingOverTime,
            destination);
        if (bossActorId is { } bossId
            && bossId != playerActorId
            && count < destination.Length)
        {
            count += CollectActiveMitigationsForActor(
                events,
                bossId,
                timestampMilliseconds,
                MitigationTargetKind.Boss,
                statusEffects,
                showHealingOverTime,
                destination[count..]);
        }

        return count;
    }

    private static int CollectActiveMitigationsForActor(
        ReadOnlySpan<ReplayTimelineEntry> events,
        int actorId,
        long timestampMilliseconds,
        MitigationTargetKind targetKind,
        ReplayStatusEffectDatabase statusEffects,
        bool showHealingOverTime,
        Span<ActiveMitigationStatus> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        Span<uint> seenStatusIds = stackalloc uint[64];
        Span<ulong> seenSources = stackalloc ulong[64];
        var seenCount = 0;
        var activeCount = 0;
        for (var index = events.Length - 1;
             index >= 0 && seenCount < seenStatusIds.Length;
             index--)
        {
            var entry = events[index];
            if (entry.TimestampMilliseconds > timestampMilliseconds
                || entry.ObservedEvent.StableActorId != actorId
                || entry.ObservedEvent.StatusId is not { } statusId
                || (targetKind == MitigationTargetKind.Player
                    ? !statusEffects.ShouldDisplayForPlayer(
                        statusId,
                        showHealingOverTime)
                    : !statusEffects.ShouldDisplayForBoss(statusId)))
            {
                continue;
            }

            var sourceId = entry.ObservedEvent.RelatedObjectId ?? 0;
            var alreadySeen = false;
            for (var seenIndex = 0; seenIndex < seenCount; seenIndex++)
            {
                if (seenStatusIds[seenIndex] == statusId
                    && seenSources[seenIndex] == sourceId)
                {
                    alreadySeen = true;
                    break;
                }
            }

            if (alreadySeen)
            {
                continue;
            }

            seenStatusIds[seenCount] = statusId;
            seenSources[seenCount] = sourceId;
            seenCount++;
            if (entry.ObservedEvent.Type is not (
                    ObservedEventType.StatusGained
                    or ObservedEventType.StatusRefreshed))
            {
                continue;
            }

            destination[activeCount++] = new ActiveMitigationStatus(
                statusId,
                index,
                targetKind);
            if (activeCount == destination.Length)
            {
                break;
            }
        }

        return activeCount;
    }
    private void DrawMitigationStatusTile(
        uint statusId,
        ushort statusParam,
        float? remainingSeconds,
        MitigationTargetKind targetKind)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight();
        var tileHeight = MitigationIconSize + lineHeight + 2;
        ImGui.Dummy(new Vector2(MitigationTileWidth, tileHeight));

        var iconOrigin = origin
            + new Vector2((MitigationTileWidth - MitigationIconSize) * 0.5f, 0);
        var iconMaximum = iconOrigin + new Vector2(MitigationIconSize);
        if (this.statusEffectIcons.TryGetValue(statusId, out var icon)
            && icon.TryGetWrap(out var texture, out _)
            && texture is not null)
        {
            drawList.AddImage(
                texture.Handle,
                iconOrigin,
                iconMaximum,
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(Vector4.One));
        }
        else
        {
            drawList.AddRectFilled(
                iconOrigin,
                iconMaximum,
                ImGui.GetColorU32(new Vector4(0.12f, 0.13f, 0.16f, 1)),
                PartyBarRounding);
            drawList.AddRect(
                iconOrigin,
                iconMaximum,
                ImGui.GetColorU32(new Vector4(0.34f, 0.36f, 0.42f, 1)),
                PartyBarRounding);
        }

        if (targetKind == MitigationTargetKind.Boss)
        {
            drawList.AddRect(
                iconOrigin - Vector2.One,
                iconMaximum + Vector2.One,
                ImGui.GetColorU32(new Vector4(1, 0.52f, 0.28f, 1)),
                PartyBarRounding);
        }

        if (statusParam > 0)
        {
            var stackText = statusParam.ToString(CultureInfo.InvariantCulture);
            var stackSize = ImGui.CalcTextSize(stackText);
            var stackPosition = iconMaximum - stackSize - new Vector2(1, 0);
            drawList.AddText(
                stackPosition + Vector2.One,
                ImGui.GetColorU32(new Vector4(0, 0, 0, 1)),
                stackText);
            drawList.AddText(
                stackPosition,
                ImGui.GetColorU32(Vector4.One),
                stackText);
        }

        var durationText = remainingSeconds is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"{value:F1}")
            : "—";
        var durationSize = ImGui.CalcTextSize(durationText);
        drawList.AddText(
            new Vector2(
                origin.X + ((MitigationTileWidth - durationSize.X) * 0.5f),
                iconMaximum.Y + 2),
            ImGui.GetColorU32(new Vector4(0.72f, 0.76f, 0.84f, 1)),
            durationText);

        if (ImGui.IsItemHovered())
        {
            var target = targetKind == MitigationTargetKind.Boss
                ? "Boss debuff"
                : "Player buff";
            var duration = remainingSeconds is { } remaining
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{remaining:F1}s remaining")
                : "Duration unavailable";
            var stack = statusParam > 0
                ? $"\nStacks: {statusParam}"
                : string.Empty;
            ImGui.SetTooltip(
                $"{this.gameDataCatalog.GetStatusName(statusId)}\n" +
                $"{duration} · {target}{stack}");
        }
    }


    internal static bool TryResolveStatusRemainingSeconds(
        ReadOnlySpan<ReplayTimelineEntry> events,
        int activeEventIndex,
        long timestampMilliseconds,
        out float remainingSeconds)
    {
        if ((uint)activeEventIndex >= (uint)events.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(activeEventIndex));
        }

        var activeEntry = events[activeEventIndex];
        var activeEvent = activeEntry.ObservedEvent;
        if (timestampMilliseconds < activeEntry.TimestampMilliseconds
            || activeEvent.Type is not (
                ObservedEventType.StatusGained
                or ObservedEventType.StatusRefreshed)
            || activeEvent.StableActorId is not { } actorId
            || activeEvent.StatusId is not { } statusId)
        {
            remainingSeconds = 0;
            return false;
        }

        if (activeEvent.StatusRemainingTime is { } observedRemaining
            && float.IsFinite(observedRemaining)
            && observedRemaining > 0)
        {
            var elapsedSeconds =
                (timestampMilliseconds - activeEntry.TimestampMilliseconds) / 1_000f;
            remainingSeconds = Math.Max(0, observedRemaining - elapsedSeconds);
            return true;
        }

        var sourceId = activeEvent.RelatedObjectId ?? 0;
        for (var index = activeEventIndex + 1; index < events.Length; index++)
        {
            var candidate = events[index];
            var candidateEvent = candidate.ObservedEvent;
            if (candidateEvent.StableActorId != actorId
                || candidateEvent.StatusId != statusId
                || (candidateEvent.RelatedObjectId ?? 0) != sourceId)
            {
                continue;
            }

            if (candidateEvent.Type == ObservedEventType.StatusLost)
            {
                remainingSeconds = Math.Max(
                    0,
                    (candidate.TimestampMilliseconds - timestampMilliseconds) / 1_000f);
                return true;
            }

            if (candidateEvent.Type is (
                ObservedEventType.StatusGained
                or ObservedEventType.StatusRefreshed))
            {
                break;
            }
        }

        remainingSeconds = 0;
        return false;
    }

    internal static bool TryResolveActiveCast(
        ReplaySession replay,
        int actorId,
        long timestampMilliseconds,
        out ObservedEvent cast)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var events = replay.Timeline.Events;
        for (var index = events.Length - 1; index >= 0; index--)
        {
            var entry = events[index];
            if (entry.TimestampMilliseconds > timestampMilliseconds
                || entry.ObservedEvent.StableActorId != actorId)
            {
                continue;
            }

            if (entry.ObservedEvent.Type == ObservedEventType.CastStarted)
            {
                cast = entry.ObservedEvent;
                if (cast.ActionId is not > 0)
                {
                    break;
                }

                if (cast.CurrentCastTime is { } current
                    && cast.TotalCastTime is { } total)
                {
                    var remainingMilliseconds =
                        Math.Max(0, total - current) * 1_000d;
                    var expiresAt = cast.TimestampMilliseconds
                        + remainingMilliseconds
                        + CastCompletionToleranceMilliseconds;
                    if (timestampMilliseconds > expiresAt)
                    {
                        break;
                    }
                }

                return true;
            }

            if (entry.ObservedEvent.Type is ObservedEventType.CastEnded
                or ObservedEventType.CastInterrupted)
            {
                break;
            }
        }

        cast = null!;
        return false;
    }

    internal static bool TryResolveCastProgress(
        ObservedEvent cast,
        long timestampMilliseconds,
        out float progress,
        out float total)
    {
        ArgumentNullException.ThrowIfNull(cast);
        if (timestampMilliseconds < cast.TimestampMilliseconds
            || cast.CurrentCastTime is not { } recordedCurrent
            || cast.TotalCastTime is not { } recordedTotal
            || !float.IsFinite(recordedCurrent)
            || !float.IsFinite(recordedTotal)
            || recordedCurrent < 0
            || recordedTotal <= 0)
        {
            progress = 0;
            total = 0;
            return false;
        }

        var elapsed =
            (timestampMilliseconds - cast.TimestampMilliseconds) / 1_000f;
        total = recordedTotal;
        progress = Math.Clamp(recordedCurrent + elapsed, 0, recordedTotal);
        return true;
    }

    internal static bool TryResolveCastProgress(
        ReplaySession replay,
        int actorId,
        ObservedEvent cast,
        long timestampMilliseconds,
        out float progress,
        out float total)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(cast);
        if (TryResolveCastProgress(cast, timestampMilliseconds, out progress, out total))
        {
            return true;
        }

        var foundCast = false;
        foreach (ref readonly var entry in replay.Timeline.Events)
        {
            if (!foundCast)
            {
                foundCast = ReferenceEquals(entry.ObservedEvent, cast);
                continue;
            }

            if (entry.ObservedEvent.StableActorId != actorId)
            {
                continue;
            }

            if (entry.ObservedEvent.Type == ObservedEventType.CastStarted)
            {
                break;
            }

            if (entry.ObservedEvent.Type is not (
                    ObservedEventType.CastEnded
                    or ObservedEventType.CastInterrupted)
                || entry.TimestampMilliseconds <= cast.TimestampMilliseconds)
            {
                continue;
            }

            total = (entry.TimestampMilliseconds - cast.TimestampMilliseconds) / 1_000f;
            progress = Math.Clamp(
                (timestampMilliseconds - cast.TimestampMilliseconds) / 1_000f,
                0,
                total);
            return true;
        }

        progress = 0;
        total = 0;
        return false;
    }

    private void DrawArena(ArenaRenderScene scene)
    {
        const float arenaInset = 8;
        ImGui.TextUnformatted($"{this.arenaViewport.Zoom:F2}x");
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##ReplayArenaReset"))
        {
            this.arenaViewport = this.minimumArenaViewport;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Wheel: zoom · Drag: pan");


        var available = ImGui.GetContentRegionAvail();
        var availableHeight = available.Y > 100 ? available.Y : available.X;
        var canvasSize = MathF.Max(
            280,
            MathF.Min(MathF.Min(available.X, availableHeight), 720));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##ReplayArena", new Vector2(canvasSize, canvasSize));

        var drawList = ImGui.GetWindowDrawList();
        var maximum = origin + new Vector2(canvasSize, canvasSize);
        var arenaOrigin = origin + new Vector2(arenaInset, arenaInset);
        var arenaSize = canvasSize - (arenaInset * 2);
        var arenaMaximum = arenaOrigin + new Vector2(arenaSize, arenaSize);
        var io = ImGui.GetIO();
        var cursor = (io.MousePos - arenaOrigin) / arenaSize;
        var isArenaHovered = ImGui.IsItemHovered()
            && cursor.X >= 0
            && cursor.X <= 1
            && cursor.Y >= 0
            && cursor.Y <= 1;
        var arenaClicked = isArenaHovered
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (isArenaHovered && io.MouseWheel != 0)
        {
            this.arenaViewport = this.arenaViewport.ZoomAt(cursor, io.MouseWheel);
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            this.selectedPlayerStableActorId = null;
            this.arenaViewport = this.arenaViewport.PanBy(io.MouseDelta / arenaSize);
        }

        if (this.selectedPlayerStableActorId is { } focusedPlayerId
            && this.arenaViewport.Zoom > this.arenaViewport.MinimumZoom
            && TryFindActorMarker(scene.Actors, focusedPlayerId, out var focusedPlayer))
        {
            this.arenaViewport = this.arenaViewport.CenterOn(focusedPlayer.Position);
        }

        var outside = ImGui.GetColorU32(new Vector4(0.018f, 0.025f, 0.038f, 1));
        var fieldColor = ImGui.GetColorU32(new Vector4(0.09f, 0.15f, 0.23f, 1));
        var border = ImGui.GetColorU32(new Vector4(0.72f, 0.78f, 0.88f, 1));
        drawList.AddRectFilled(origin, maximum, outside);
        var cursorAfterCanvas = ImGui.GetCursorScreenPos();
        drawList.PushClipRect(arenaOrigin, arenaMaximum, true);

        var fieldMinimum = ProjectToCanvas(new ArenaPoint(0, 0), arenaOrigin, arenaSize, this.arenaViewport);
        var fieldMaximum = ProjectToCanvas(new ArenaPoint(1, 1), arenaOrigin, arenaSize, this.arenaViewport);
        drawList.AddRectFilled(fieldMinimum, fieldMaximum, fieldColor);
        if (this.mapBackground is { } background
            && background.Texture.TryGetWrap(out var mapTexture, out _)
            && mapTexture is not null
            && background.Projection.TryCreateDrawRegion(
                scene.WorldBounds,
                out var mapRegion))
        {
            var mapMinimum = ProjectToCanvas(
                new ArenaPoint(mapRegion.FieldMinimum.X, mapRegion.FieldMinimum.Y),
                arenaOrigin,
                arenaSize,
                this.arenaViewport);
            var mapMaximum = ProjectToCanvas(
                new ArenaPoint(mapRegion.FieldMaximum.X, mapRegion.FieldMaximum.Y),
                arenaOrigin,
                arenaSize,
                this.arenaViewport);
            drawList.AddImage(
                mapTexture.Handle,
                mapMinimum,
                mapMaximum,
                mapRegion.TextureMinimum,
                mapRegion.TextureMaximum,
                ImGui.GetColorU32(new Vector4(0.82f, 0.82f, 0.82f, 1)));
        }

        var directionalReady = this.targetCircleTexture.TryGetWrap(
            out var directionalTexture,
            out _);
        var omnidirectionalReady = this.omnidirectionalTargetRingTexture.TryGetWrap(
            out var omnidirectionalTexture,
            out _);
        var hasRecordedOmnidirectionality =
            (this.session?.Record.Features & CaptureFeatures.OmnidirectionalState) != 0;
        foreach (ref readonly var actor in scene.Actors)
        {
            var targetCircleRadius = ResolveTargetCircleRadius(
                actor.Kind,
                actor.HitboxRadius,
                scene.WorldBounds,
                arenaSize,
                this.arenaViewport);
            var variant = ResolveTargetCircleVariant(
                actor.Kind,
                actor.IsOmnidirectional,
                hasRecordedOmnidirectionality,
                this.omnidirectionalityCatalog.Contains(actor.Actor.BaseId));
            var targetCircleTexture = variant == TargetCircleVariant.Omnidirectional
                ? omnidirectionalTexture
                : directionalTexture;
            var targetCircleReady = variant == TargetCircleVariant.Omnidirectional
                ? omnidirectionalReady
                : directionalReady;
            if (targetCircleRadius <= 0)
            {
                continue;
            }

            var position = ProjectToCanvas(actor.Position, arenaOrigin, arenaSize, this.arenaViewport);
            if (targetCircleReady && targetCircleTexture is not null)
            {
                var outerRadiusRatio = variant == TargetCircleVariant.Omnidirectional
                    ? OmnidirectionalTargetRingOuterRadiusRatio
                    : TargetCircleOuterRingRadiusRatio;
                var quad = ResolveTargetCircleQuad(
                    position,
                    actor.Facing,
                    targetCircleRadius,
                    targetCircleTexture.Height / (float)targetCircleTexture.Width,
                    outerRadiusRatio);
                var opacity =
                    (actor.IsTargetable ? 0.9f : 0.4f)
                    * ResolveFocusedActorOpacity(this.selectedPlayerStableActorId, actor);
                drawList.AddImageQuad(
                    targetCircleTexture.Handle,
                    quad.TopLeft,
                    quad.TopRight,
                    quad.BottomRight,
                    quad.BottomLeft,
                    Vector2.Zero,
                    new Vector2(1, 0),
                    Vector2.One,
                    new Vector2(0, 1),
                    ImGui.GetColorU32(new Vector4(1, 1, 1, opacity)));
            }

        }


        foreach (ref readonly var waymark in scene.Waymarks)
        {
            var position = ProjectToCanvas(waymark.Position, arenaOrigin, arenaSize, this.arenaViewport);
            var halfSize = ResolveWaymarkHalfSize(
                waymark.Id,
                scene.WorldBounds,
                arenaSize,
                this.arenaViewport);
            var imageBounds = ResolveCenteredWaymarkBounds(
                position,
                halfSize * WaymarkTextureScale);
            var icon = this.waymarkTextures[(int)waymark.Id];
            if (icon is not null
                && icon.TryGetWrap(out var texture, out _)
                && texture is not null)
            {
                drawList.AddImage(
                    texture.Handle,
                    imageBounds.Minimum,
                    imageBounds.Maximum,
                    Vector2.Zero,
                    Vector2.One,
                    0xffffffff);
                continue;
            }

            var color = WaymarkColor(waymark.Id);
            drawList.AddCircleFilled(position, halfSize, color);
            drawList.AddCircle(position, halfSize, border);
            drawList.AddText(position + new Vector2(-4, -7), 0xffffffff, WaymarkLabel(waymark.Id));
        }
        var enemyHudCount = 0;
        var enemyHudTop = EnemyHudTopInset;
        foreach (ref readonly var actor in scene.Actors)
        {
            var position = ProjectToCanvas(actor.Position, arenaOrigin, arenaSize, this.arenaViewport);
            var targetCircleRadius = ResolveTargetCircleRadius(
                actor.Kind,
                actor.HitboxRadius,
                scene.WorldBounds,
                arenaSize,
                this.arenaViewport);
            var opacity = ResolveFocusedActorOpacity(this.selectedPlayerStableActorId, actor);

            var markerExtent = 7f;
            var iconDrawn = false;
            var classJobId = actor.Actor.ClassJobId;
            if (actor.Kind == ArenaActorMarkerKind.Player
                && classJobId < this.jobIcons.Length
                && this.jobIcons[(int)classJobId] is { } icon
                && icon.TryGetWrap(out var texture, out _)
                && texture is not null)
            {
                markerExtent = PlayerIconHalfSize;
                var iconOffset = new Vector2(markerExtent);
                drawList.AddImage(
                    texture.Handle,
                    position - iconOffset,
                    position + iconOffset,
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.GetColorU32(new Vector4(1, 1, 1, opacity)));
                iconDrawn = true;
            }

            if (!iconDrawn)
            {
                var baseColor = actor.Kind == ArenaActorMarkerKind.Player
                    ? new Vector4(0.25f, 0.7f, 1f, opacity)
                    : new Vector4(1f, 0.34f, 0.35f, opacity);
                var color = ImGui.GetColorU32(baseColor);
                drawList.AddCircleFilled(position, markerExtent, color);
                drawList.AddCircle(position, markerExtent, 0xffffffff);
            }

            if (actor.Actor.StableActorId == this.selectedPlayerStableActorId)
            {
                drawList.AddCircle(
                    position,
                    markerExtent + 5,
                    ImGui.GetColorU32(new Vector4(0.2f, 0.9f, 1, 1)),
                    0,
                    3);
            }

            if (actor.IsDead)
            {
                var deadExtent = markerExtent + 2;
                drawList.AddLine(
                    position + new Vector2(-deadExtent, -deadExtent),
                    position + new Vector2(deadExtent, deadExtent),
                    0xff6060ff,
                    3);
                drawList.AddLine(
                    position + new Vector2(-deadExtent, deadExtent),
                    position + new Vector2(deadExtent, -deadExtent),
                    0xff6060ff,
                    3);
            }

            drawList.AddText(
                position + new Vector2(markerExtent + 3, -8),
                ImGui.GetColorU32(new Vector4(1, 1, 1, Math.Max(0.35f, opacity))),
                ActorLabel(actor));
        }

        foreach (ref readonly var targetMarker in scene.TargetMarkers)
        {
            var textureSource = this.targetMarkerTextures[(int)targetMarker.Id];
            if (!textureSource.TryGetWrap(out var texture, out _) || texture is null)
            {
                continue;
            }

            var actorPosition = ProjectToCanvas(
                targetMarker.Position,
                arenaOrigin,
                arenaSize,
                this.arenaViewport);
            var markerCenter = actorPosition - new Vector2(0, TargetMarkerVerticalOffset);
            var markerOffset = new Vector2(TargetMarkerHalfSize);
            drawList.AddImage(
                texture.Handle,
                markerCenter - markerOffset,
                markerCenter + markerOffset);
        }

        foreach (ref readonly var actor in scene.Actors)
        {
            if (!ShouldDrawEnemyVitals(actor))
            {
                continue;
            }
            enemyHudCount++;

            enemyHudTop = this.DrawEnemyHud(
                actor,
                arenaOrigin,
                arenaMaximum,
                scene.TimestampMilliseconds,
                enemyHudTop);
        }

        if (arenaClicked
            && IsArenaBackgroundPoint(
                io.MousePos,
                arenaOrigin,
                arenaMaximum,
                arenaSize,
                this.arenaViewport,
                scene.Actors,
                enemyHudCount,
                enemyHudTop))
        {
            this.selectedPlayerStableActorId = null;
        }



        drawList.AddRect(fieldMinimum, fieldMaximum, border);
        drawList.AddRect(fieldMinimum + Vector2.One, fieldMaximum - Vector2.One, border);
        drawList.PopClipRect();
        ImGui.SetCursorScreenPos(cursorAfterCanvas);

        drawList.AddText(
            arenaOrigin + new Vector2(8, 7),
            0xffffffff,
            FormatTimestamp(scene.TimestampMilliseconds));
    }



    private float DrawEnemyHud(
        in ArenaActorMarker actor,
        Vector2 arenaMinimum,
        Vector2 arenaMaximum,
        long timestampMilliseconds,
        float topOffset)
    {
        var replay = this.session;
        ObservedEvent? activeCast = null;
        if (replay is not null
            && TryResolveActiveCast(
                replay,
                actor.Actor.StableActorId,
                timestampMilliseconds,
                out var resolvedCast))
        {
            activeCast = resolvedCast;
        }

        var layout = ResolveEnemyHudLayout(
            arenaMinimum,
            arenaMaximum,
            topOffset,
            activeCast is not null);
        var drawList = ImGui.GetWindowDrawList();
        var textColor = 0xffffffff;
        var mutedTextColor = ImGui.GetColorU32(new Vector4(0.78f, 0.82f, 0.9f, 1));
        var borderColor = ImGui.GetColorU32(new Vector4(0.55f, 0.61f, 0.7f, 0.9f));
        drawList.AddRectFilled(
            layout.PanelMinimum,
            layout.PanelMaximum,
            ImGui.GetColorU32(new Vector4(0.015f, 0.02f, 0.03f, 0.82f)));

        var hpLabel = FormatEnemyHudHpPercentage(actor);
        var hpLabelSize = ImGui.CalcTextSize(hpLabel);
        var hpLabelPosition = new Vector2(
            layout.HealthBarMaximum.X - hpLabelSize.X,
            layout.HeaderPosition.Y);
        drawList.PushClipRect(
            layout.HeaderPosition,
            new Vector2(hpLabelPosition.X - 6, layout.HeaderPosition.Y + EnemyHudTextHeight),
            true);
        drawList.AddText(layout.HeaderPosition, textColor, ActorLabel(actor));
        drawList.PopClipRect();
        drawList.AddText(hpLabelPosition, textColor, hpLabel);
        DrawHudProgressBar(
            drawList,
            layout.HealthBarMinimum,
            layout.HealthBarMaximum,
            ResolveHpFraction(actor),
            ImGui.GetColorU32(new Vector4(0.24f, 0.72f, 0.42f, 1)),
            borderColor);

        var mousePosition = ImGui.GetIO().MousePos;
        if (Contains(layout.HealthBarMinimum, layout.HealthBarMaximum, mousePosition))
        {
            ImGui.SetTooltip(FormatPartyHp(actor));
        }

        if (replay is null || activeCast is null)
        {
            return layout.NextTopOffset;
        }

        var actionName = this.gameDataCatalog.GetActionName(activeCast.ActionId!.Value);
        var hasProgress = TryResolveCastProgress(
            replay,
            actor.Actor.StableActorId,
            activeCast,
            timestampMilliseconds,
            out var current,
            out var total);
        var castTimeLabel = hasProgress
            ? FormatCastTimeLabel(current, total)
            : "—";
        var castTimeSize = ImGui.CalcTextSize(castTimeLabel);
        var castTimePosition = new Vector2(
            layout.CastBarMaximum.X - castTimeSize.X,
            layout.CastHeaderPosition.Y);
        drawList.PushClipRect(
            layout.CastHeaderPosition,
            new Vector2(castTimePosition.X - 6, layout.CastHeaderPosition.Y + EnemyHudTextHeight),
            true);
        drawList.AddText(layout.CastHeaderPosition, mutedTextColor, actionName);
        drawList.PopClipRect();
        drawList.AddText(castTimePosition, mutedTextColor, castTimeLabel);
        DrawHudProgressBar(
            drawList,
            layout.CastBarMinimum,
            layout.CastBarMaximum,
            hasProgress ? current / total : 0,
            ImGui.GetColorU32(new Vector4(0.93f, 0.68f, 0.23f, 1)),
            borderColor);
        return layout.NextTopOffset;
    }

    private static void DrawHudProgressBar(
        ImDrawListPtr drawList,
        Vector2 minimum,
        Vector2 maximum,
        float fraction,
        uint fillColor,
        uint borderColor)
    {
        var clampedFraction = Math.Clamp(fraction, 0, 1);
        drawList.AddRectFilled(
            minimum,
            maximum,
            ImGui.GetColorU32(new Vector4(0.08f, 0.1f, 0.14f, 0.95f)));
        if (clampedFraction > 0)
        {
            drawList.AddRectFilled(
                minimum,
                new Vector2(
                    minimum.X + ((maximum.X - minimum.X) * clampedFraction),
                    maximum.Y),
                fillColor);
        }

        drawList.AddRect(minimum, maximum, borderColor);
    }

    private static bool Contains(Vector2 minimum, Vector2 maximum, Vector2 point) =>
        point.X >= minimum.X
        && point.X <= maximum.X
        && point.Y >= minimum.Y
        && point.Y <= maximum.Y;

    private void DrawBottomTimeline(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        ImGui.Separator();
        if (ImGui.Button("<< 1s##ReplayBack"))
        {
            replay.Seek(replay.CurrentTimeMilliseconds - 1_000);
            replay.Pause();
        }

        ImGui.SameLine();
        if (ImGui.Button(replay.IsPlaying ? "Pause##ReplayPlay" : "Play##ReplayPlay"))
        {
            if (replay.IsPlaying)
            {
                replay.Pause();
            }
            else
            {
                replay.Play();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("1s >>##ReplayForward"))
        {
            replay.Seek(replay.CurrentTimeMilliseconds + 1_000);
            replay.Pause();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(86);
        if (ImGui.BeginCombo("##ReplaySpeed", $"{this.playbackSpeed:F1}x"))
        {
            DrawSpeedOption(ref this.playbackSpeed, 0.5f);
            DrawSpeedOption(ref this.playbackSpeed, 1);
            DrawSpeedOption(ref this.playbackSpeed, 1.5f);
            DrawSpeedOption(ref this.playbackSpeed, 2);
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(
            $"{FormatTimestamp(replay.CurrentTimeMilliseconds)} / {FormatTimestamp(replay.DurationMilliseconds)}");
        this.DrawTimelineControl(replay, presentation);
        DrawSectionHeader("DEATH QUICK JUMP");
        this.DrawDeathQuickJumps(replay, presentation);
    }

    private static void DrawSpeedOption(ref float current, float candidate)
    {
        if (ImGui.Selectable($"{candidate:F1}x", Math.Abs(current - candidate) < 0.01f))
        {
            current = candidate;
        }
    }

    private void DrawTimelineControl(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        var currentSeconds = replay.CurrentTimeMilliseconds / 1000f;
        var durationSeconds = replay.DurationMilliseconds / 1000f;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat(
                "##ReplayTimeline",
                ref currentSeconds,
                0,
                durationSeconds,
                string.Empty))
        {
            replay.Seek((long)Math.Round(currentSeconds * 1000));
        }

        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        var centerY = (minimum.Y + maximum.Y) * 0.5f;
        var mouse = ImGui.GetIO().MousePos;
        var drawList = ImGui.GetWindowDrawList();
        var deathColor = ImGui.GetColorU32(new Vector4(1, 0.2f, 0.25f, 1));
        for (var index = 0; index < presentation.Deaths.Length;)
        {
            var death = presentation.Deaths[index];
            var timestamp = death.Correlation.DeathTimestampMilliseconds;
            var groupCount = 1;
            while (index + groupCount < presentation.Deaths.Length
                && presentation.Deaths[index + groupCount].Correlation.DeathTimestampMilliseconds
                    == timestamp)
            {
                groupCount++;
            }

            var amount = replay.DurationMilliseconds == 0
                ? 0
                : timestamp / (float)replay.DurationMilliseconds;
            var x = minimum.X + ((maximum.X - minimum.X) * Math.Clamp(amount, 0, 1));
            var markerCenter = new Vector2(x, centerY);
            drawList.AddLine(
                new Vector2(x, minimum.Y - 3),
                new Vector2(x, maximum.Y + 3),
                deathColor,
                2);
            drawList.AddCircleFilled(markerCenter, groupCount > 1 ? 6 : 4, deathColor);
            if (groupCount > 1)
            {
                drawList.AddText(
                    markerCenter + new Vector2(5, -14),
                    0xffffffff,
                    groupCount.ToString(CultureInfo.InvariantCulture));
            }

            var hovered = Math.Abs(mouse.X - x) <= 7
                && mouse.Y >= minimum.Y - 8
                && mouse.Y <= maximum.Y + 8;
            if (hovered)
            {
                var job = JobIconResources.GetAbbreviation(death.Actor.ClassJobId) ?? "Player";
                ImGui.SetTooltip(
                    groupCount == 1
                        ? $"{FormatTimestamp(timestamp)}\n{job}"
                        : $"{FormatTimestamp(timestamp)}\n{groupCount} party deaths");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    this.SelectDeath(replay, death);
                }
            }

            index += groupCount;
        }
    }

    private void DrawDeathQuickJumps(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        if (presentation.Deaths.Length == 0)
        {
            ImGui.TextDisabled("No recorded party deaths.");
            return;
        }

        for (var index = 0; index < presentation.Deaths.Length; index++)
        {
            var death = presentation.Deaths[index];
            var job = JobIconResources.GetAbbreviation(death.Actor.ClassJobId) ?? "Player";
            if (ImGui.SmallButton(
                    $"{job}  {FormatTimestamp(death.Correlation.DeathTimestampMilliseconds)}" +
                    $"##ReplayDeathQuick{death.Correlation.DeathOriginalRecordedIndex}"))
            {
                this.SelectDeath(replay, death);
            }

            if ((index + 1) % 4 != 0 && index + 1 < presentation.Deaths.Length)
            {
                ImGui.SameLine();
            }
        }
    }

    private void SelectDeath(ReplaySession replay, in ReplayDeathItem death)
    {
        this.selectedPlayerStableActorId = death.Actor.StableActorId;
        replay.Seek(death.Correlation.DeathTimestampMilliseconds);
        replay.Pause();
    }

    internal static bool IsPlayerDeath(
        ReplaySession replay,
        in ReplayTimelineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(replay);
        return entry.ObservedEvent.Type == ObservedEventType.Death
            && entry.ObservedEvent.StableActorId is { } stableActorId
            && replay.TryGetActor(stableActorId, out var actor)
            && string.Equals(actor.ObjectKind, "Pc", StringComparison.Ordinal);
    }


    private void RequestRuntimeSource()
    {
        this.requestedDebriefReplay = null;
        this.requestedInitialSeekTimestamp = null;
        this.activeSuggestedReplayWindow = null;
        this.requestedSourceMode = ReplaySourceMode.RuntimeLastCompletedPull;
        this.ApplyRuntimeSource(this.captureService.GetReplaySourceSnapshot());
    }

    private void RefreshRuntimeSource()
    {
        if (this.requestedSourceMode == ReplaySourceMode.RuntimeLastCompletedPull)
        {
            this.ApplyRuntimeSource(this.captureService.GetReplaySourceSnapshot());
        }
    }

    private void ApplyRuntimeSource(ReplaySourceSnapshot snapshot)
    {
        if (this.requestedDebriefReplay is { } requestedDebrief
            && !IsDebriefReplayRequestCurrent(snapshot, requestedDebrief))
        {
            if (this.loadCoordinator.IsLoading)
            {
                this.loadCoordinator.Invalidate();
            }

            this.statusMessage =
                "Debrief 所屬 Pull 已不再是目前的 in-memory completed Pull。";
            return;
        }

        var decision = ResolveRuntimeSource(snapshot);
        if (decision.Kind is RuntimeReplaySourceDecisionKind.WaitForFinalization
            or RuntimeReplaySourceDecisionKind.Empty)
        {
            if (this.loadCoordinator.IsLoading)
            {
                this.loadCoordinator.Invalidate();
            }

            this.statusMessage = decision.Message;
            return;
        }

        var record = decision.Record
            ?? throw new InvalidOperationException("A Runtime Replay load decision requires a PullRecord.");
        if (this.loadCoordinator.PendingMode == ReplaySourceMode.RuntimeLastCompletedPull
            && this.loadCoordinator.PendingSourceGeneration == decision.SourceGeneration
            && this.loadCoordinator.PendingCaptureId == record.CaptureId)
        {
            return;
        }
        if (this.failedLoadSourceMode == ReplaySourceMode.RuntimeLastCompletedPull
            && this.failedLoadSourceGeneration == decision.SourceGeneration
            && this.failedLoadCaptureId == record.CaptureId)
        {
            this.statusMessage = this.failedLoadMessage
                ?? "Runtime Replay 載入失敗。";
            return;
        }

        if (this.activeSourceMode == ReplaySourceMode.RuntimeLastCompletedPull
            && this.activeRuntimeSourceGeneration == decision.SourceGeneration
            && this.session?.Record.CaptureId == record.CaptureId)
        {
            this.activeSourceDetail = decision.SourceDetail;
            this.statusMessage = decision.Message;
            this.ApplyRequestedInitialSeek();
            return;
        }

        this.statusMessage =
            decision.Kind == RuntimeReplaySourceDecisionKind.LoadPreviousAfterFailure
                ? "最近的 Pull 不可用；正在建立前一個有效 Runtime Replay…"
                : "正在建立最新的 Runtime Replay…";
        this.loadCoordinator.Start(
            record,
            ReplaySourceMode.RuntimeLastCompletedPull,
            decision.SourceGeneration,
            decision.SourceDetail
                ?? throw new InvalidOperationException("A Runtime Replay load decision requires source details."),
            decision.Message);
    }

    internal static bool IsDebriefReplayRequestCurrent(
        ReplaySourceSnapshot snapshot,
        DebriefReplayRequest request) =>
        snapshot.CompletedGeneration == request.SourceGeneration
        && snapshot.LastCompletedPull?.CaptureId == request.CaptureId
        && snapshot.LastCompletedDebrief?.CaptureId == request.CaptureId;

    private void ApplyRequestedInitialSeek()
    {
        if (this.requestedInitialSeekTimestamp is not { } timestamp
            || this.session is null)
        {
            return;
        }

        ApplyDebriefReplayRequest(this.session, timestamp);
        this.requestedInitialSeekTimestamp = null;
        this.statusMessage =
            $"{this.statusMessage} 已定位至 Debrief 建議起點 {FormatTimestamp(this.session.CurrentTimeMilliseconds)}，Replay 保持暫停。";
    }

    internal static void ApplyDebriefReplayRequest(
        ReplaySession session,
        long startTimestampMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Seek(startTimestampMilliseconds);
        session.Pause();
    }

    private void StartDeveloperFixtureLoad()
    {
        this.requestedSourceMode = ReplaySourceMode.DeveloperTestFixture;
        this.requestedDebriefReplay = null;
        this.requestedInitialSeekTimestamp = null;
        this.activeSuggestedReplayWindow = null;
        var path = this.fixturePath;
        var sourceGeneration = ++this.developerSourceGeneration;
        this.statusMessage = "正在背景載入 Developer/Test fixture…";
        this.loadCoordinator.Start(
            () => CaptureJson.Load(path),
            Guid.Empty,
            ReplaySourceMode.DeveloperTestFixture,
            sourceGeneration,
            $"Developer/Test JSON Fixture ({path})",
            $"已載入 Developer/Test JSON Fixture：{path}。");
    }

    internal static RuntimeReplaySourceDecision ResolveRuntimeSource(
        ReplaySourceSnapshot snapshot)
    {
        var finalizationCaptureId = snapshot.FinalizationCaptureId?.ToString()
            ?? "未知 Capture";
        if (snapshot.FinalizationState == ReplaySourceFinalizationState.Finalizing)
        {
            return new RuntimeReplaySourceDecision(
                RuntimeReplaySourceDecisionKind.WaitForFinalization,
                snapshot.FinalizationGeneration,
                null,
                null,
                $"Pull {finalizationCaptureId} 正在背景完成並驗證；完成前不會載入較舊的 Runtime Pull。");
        }

        if (snapshot.FinalizationState == ReplaySourceFinalizationState.Failed)
        {
            var error = string.IsNullOrWhiteSpace(snapshot.FinalizationError)
                ? "未知錯誤"
                : snapshot.FinalizationError;
            if (snapshot.LastCompletedPull is not { } previousRecord)
            {
                return new RuntimeReplaySourceDecision(
                    RuntimeReplaySourceDecisionKind.Empty,
                    snapshot.FinalizationGeneration,
                    null,
                    null,
                    $"最近的 Pull {finalizationCaptureId} 完成或驗證失敗：{error}。目前沒有可用的 in-memory completed Pull。");
            }

            return new RuntimeReplaySourceDecision(
                RuntimeReplaySourceDecisionKind.LoadPreviousAfterFailure,
                snapshot.FinalizationGeneration,
                previousRecord,
                $"Runtime previous valid Pull ({previousRecord.CaptureId}; latest unavailable {finalizationCaptureId})",
                $"最近的 Pull {finalizationCaptureId} 完成或驗證失敗：{error}。已保留並載入前一個有效 Runtime Pull {previousRecord.CaptureId}。");
        }

        if (snapshot.LastCompletedPull is not { } latestRecord)
        {
            return new RuntimeReplaySourceDecision(
                RuntimeReplaySourceDecisionKind.Empty,
                snapshot.FinalizationGeneration,
                null,
                null,
                "Runtime 目前沒有 in-memory completed Pull；不會自動從 disk 恢復。");
        }

        return new RuntimeReplaySourceDecision(
            RuntimeReplaySourceDecisionKind.LoadLatest,
            snapshot.FinalizationGeneration,
            latestRecord,
            $"Runtime LastCompletedPull ({latestRecord.CaptureId})",
            $"已載入最新的 Runtime LastCompletedPull {latestRecord.CaptureId}。");
    }

    internal static bool IsRuntimeLoadCurrent(
        RuntimeReplaySourceDecision decision,
        long sourceGeneration,
        Guid captureId) =>
        (decision.Kind is RuntimeReplaySourceDecisionKind.LoadLatest
            or RuntimeReplaySourceDecisionKind.LoadPreviousAfterFailure)
        && decision.SourceGeneration == sourceGeneration
        && decision.Record?.CaptureId == captureId;


    private static Vector2 ProjectToCanvas(
        ArenaPoint point,
        Vector2 origin,
        float size,
        ArenaViewport viewport) =>
        origin + (viewport.Project(point) * size);

    internal static float ResolveTargetCircleRadius(
        ArenaActorMarkerKind kind,
        float worldRadius,
        ArenaBounds worldBounds,
        float arenaSize,
        ArenaViewport viewport) =>
        kind == ArenaActorMarkerKind.Player
            ? PlayerTargetCircleHalfWidth * TargetCircleOuterRingRadiusRatio
            : ProjectWorldRadius(worldRadius, worldBounds, arenaSize, viewport);

    internal static TargetCircleVariant ResolveTargetCircleVariant(
        ArenaActorMarkerKind kind,
        bool recordedIsOmnidirectional,
        bool hasRecordedOmnidirectionality,
        bool baseIsOmnidirectional) =>
        kind == ArenaActorMarkerKind.BattleNpc
            && (hasRecordedOmnidirectionality
                ? recordedIsOmnidirectional
                : baseIsOmnidirectional)
            ? TargetCircleVariant.Omnidirectional
            : TargetCircleVariant.Directional;

    internal static float ProjectWorldRadius(
        float worldRadius,
        ArenaBounds worldBounds,
        float arenaSize,
        ArenaViewport viewport)
    {
        if (!float.IsFinite(worldRadius) || worldRadius <= 0)
        {
            return 0;
        }

        var pixelsPerWorldUnit = arenaSize * viewport.Zoom
            / MathF.Max(worldBounds.Width, worldBounds.Depth);
        return worldRadius * pixelsPerWorldUnit;
    }

    internal static (
        Vector2 TopLeft,
        Vector2 TopRight,
        Vector2 BottomRight,
        Vector2 BottomLeft) ResolveTargetCircleQuad(
            Vector2 position,
            ArenaVector facing,
            float radius,
            float textureAspectRatio,
            float outerRingRadiusRatio = TargetCircleOuterRingRadiusRatio)
    {
        if (!float.IsFinite(radius)
            || !float.IsFinite(textureAspectRatio)
            || !float.IsFinite(outerRingRadiusRatio)
            || radius <= 0
            || textureAspectRatio <= 0
            || outerRingRadiusRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radius),
                radius,
                "Target Circle radius, texture aspect ratio, and outer-ring ratio must be finite and positive.");
        }

        var up = Vector2.Normalize(new Vector2(facing.X, facing.Y));
        var right = new Vector2(-up.Y, up.X);
        var textureHalfWidth = radius / outerRingRadiusRatio;
        var horizontal = right * textureHalfWidth;
        var vertical = up * textureHalfWidth * textureAspectRatio;
        return (
            position - horizontal + vertical,
            position + horizontal + vertical,
            position + horizontal - vertical,
            position - horizontal - vertical);
    }

    internal static EnemyHudLayout ResolveEnemyHudLayout(
        Vector2 arenaMinimum,
        Vector2 arenaMaximum,
        float topOffset,
        bool hasActiveCast)
    {
        var arenaWidth = arenaMaximum.X - arenaMinimum.X;
        if (!float.IsFinite(arenaMinimum.X)
            || !float.IsFinite(arenaMinimum.Y)
            || !float.IsFinite(arenaMaximum.X)
            || !float.IsFinite(arenaMaximum.Y)
            || !float.IsFinite(topOffset)
            || topOffset < 0
            || arenaWidth <= EnemyHudHorizontalInset * 2
            || arenaMaximum.Y <= arenaMinimum.Y)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arenaMaximum),
                arenaMaximum,
                "Enemy HUD geometry must be finite and fit inside the Arena.");
        }

        var width = MathF.Min(
            EnemyHudWidth,
            arenaWidth - (EnemyHudHorizontalInset * 2));
        var headerPosition = arenaMinimum
            + new Vector2(EnemyHudHorizontalInset, topOffset);
        var healthBarMinimum = headerPosition
            + new Vector2(0, EnemyHudTextHeight + EnemyHudRowGap);
        var healthBarMaximum = healthBarMinimum
            + new Vector2(width, EnemyHudBarHeight);
        var castHeaderPosition = new Vector2(
            healthBarMinimum.X,
            healthBarMaximum.Y + EnemyHudRowGap);
        var castBarMinimum = castHeaderPosition
            + new Vector2(0, EnemyHudTextHeight + EnemyHudRowGap);
        var castBarMaximum = castBarMinimum
            + new Vector2(width, EnemyHudBarHeight);
        var contentBottom = hasActiveCast
            ? castBarMaximum.Y
            : healthBarMaximum.Y;
        return new EnemyHudLayout(
            headerPosition + new Vector2(-6, -4),
            new Vector2(healthBarMaximum.X + 6, contentBottom + 4),
            headerPosition,
            healthBarMinimum,
            healthBarMaximum,
            castHeaderPosition,
            castBarMinimum,
            castBarMaximum,
            (contentBottom - arenaMinimum.Y) + EnemyHudGroupGap);
    }

    internal static bool IsArenaBackgroundPoint(
        Vector2 point,
        Vector2 arenaMinimum,
        Vector2 arenaMaximum,
        float arenaSize,
        ArenaViewport viewport,
        ReadOnlySpan<ArenaActorMarker> actors,
        int enemyHudCount,
        float enemyHudNextTop)
    {
        if (!Contains(arenaMinimum, arenaMaximum, point))
        {
            return false;
        }

        if (enemyHudCount > 0)
        {
            var hudWidth = MathF.Min(
                EnemyHudWidth,
                (arenaMaximum.X - arenaMinimum.X) - (EnemyHudHorizontalInset * 2));
            var hudMinimum = arenaMinimum
                + new Vector2(
                    EnemyHudHorizontalInset - 6,
                    EnemyHudTopInset - 4);
            var hudMaximum = new Vector2(
                arenaMinimum.X + EnemyHudHorizontalInset + hudWidth + 6,
                arenaMinimum.Y
                    + enemyHudNextTop
                    - EnemyHudGroupGap
                    + 4);
            if (Contains(hudMinimum, hudMaximum, point))
            {
                return false;
            }
        }

        foreach (ref readonly var actor in actors)
        {
            var markerPosition = ProjectToCanvas(
                actor.Position,
                arenaMinimum,
                arenaSize,
                viewport);
            var markerExtent = actor.Kind == ArenaActorMarkerKind.Player
                ? PlayerIconHalfSize
                : 7;
            var hitRadius = markerExtent + 5;
            if (Vector2.DistanceSquared(point, markerPosition) <= hitRadius * hitRadius)
            {
                return false;
            }
        }

        return true;
    }

    internal static float ResolveHpFraction(in ArenaActorMarker marker) =>
        marker.MaxHp == 0
            ? 0
            : Math.Clamp(marker.CurrentHp / (float)marker.MaxHp, 0, 1);

    internal static bool ShouldDrawEnemyVitals(in ArenaActorMarker marker) =>
        marker.Kind == ArenaActorMarkerKind.BattleNpc && marker.IsTargetable;

    internal static int? ResolveActiveBossActorId(
        ReadOnlySpan<ArenaActorMarker> actors,
        ActorRecord? referenceBoss)
    {
        int? matchingActorId = null;
        uint matchingMaxHp = 0;
        int? fallbackActorId = null;
        uint fallbackMaxHp = 0;
        foreach (ref readonly var marker in actors)
        {
            if (!ShouldDrawEnemyVitals(marker) || marker.MaxHp == 0)
            {
                continue;
            }

            var actorId = marker.Actor.StableActorId;
            if (marker.MaxHp > fallbackMaxHp
                || (marker.MaxHp == fallbackMaxHp
                    && actorId < fallbackActorId.GetValueOrDefault(int.MaxValue)))
            {
                fallbackActorId = actorId;
                fallbackMaxHp = marker.MaxHp;
            }

            if (referenceBoss is null
                || !MatchesRecordedBossIdentity(marker.Actor, referenceBoss)
                || (marker.MaxHp < matchingMaxHp
                    || (marker.MaxHp == matchingMaxHp
                        && actorId >= matchingActorId.GetValueOrDefault(int.MaxValue))))
            {
                continue;
            }

            matchingActorId = actorId;
            matchingMaxHp = marker.MaxHp;
        }

        return matchingActorId ?? fallbackActorId;
    }

    private static bool MatchesRecordedBossIdentity(
        ActorRecord actor,
        ActorRecord referenceBoss) =>
        actor.StableActorId == referenceBoss.StableActorId
        || (referenceBoss.BaseId != 0 && actor.BaseId == referenceBoss.BaseId)
        || (!string.IsNullOrWhiteSpace(referenceBoss.Name)
            && string.Equals(
                actor.Name,
                referenceBoss.Name,
                StringComparison.Ordinal));

    internal static string FormatEnemyHudHpPercentage(in ArenaActorMarker marker)
    {
        if (marker.MaxHp == 0)
        {
            return "—";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{marker.CurrentHp * 100d / marker.MaxHp:F1}%");
    }

    internal static string FormatCastTimeLabel(float progress, float total)
    {
        if (!float.IsFinite(progress)
            || !float.IsFinite(total)
            || progress < 0
            || total <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(progress),
                progress,
                "Cast progress must be finite and non-negative, with a finite positive total.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Min(progress, total):F1} / {total:F1}s");
    }

    /// <summary>
    /// Resolves party health-bar geometry as fractions of the full bar width.
    /// Both fractions are measured from the left edge: health fills the bar and the
    /// barrier overlays the upper strip, so a barrier stays visible at full health.
    /// Each fraction is clamped to the bar width.
    /// </summary>
    internal static (float HpFraction, float BarrierFraction) ResolvePartyBarFractions(
        uint currentHp,
        uint maxHp,
        byte? barrierPercentage)
    {
        if (maxHp == 0)
        {
            return (0, 0);
        }

        var hpFraction = Math.Clamp(currentHp / (float)maxHp, 0, 1);
        var barrierFraction = barrierPercentage is { } percentage && percentage > 0
            ? Math.Clamp(percentage / 100f, 0, 1)
            : 0;
        return (hpFraction, barrierFraction);
    }

    internal static Vector4 ResolvePartyHpColor(bool isDead) =>
        isDead ? PartyDeadBarColor : PartyHpColor;

    internal static string FormatPartyHp(in ArenaActorMarker marker)
    {
        if (marker.MaxHp == 0)
        {
            return "—";
        }

        var state = marker.IsDead ? "DEAD · " : string.Empty;
        var percentage = marker.CurrentHp * 100d / marker.MaxHp;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{state}{marker.CurrentHp:N0} / {marker.MaxHp:N0} · {percentage:F1}%");
    }

    internal static string FormatPartyBarrierSuffix(in ArenaActorMarker marker)
    {
        if (marker.BarrierPercentage is not > 0)
        {
            return string.Empty;
        }

        return $"  ·  Barrier {FormatBarrierAmount(marker.MaxHp, marker.BarrierPercentage.Value)}";
    }

    /// <summary>
    /// Formats a recorded barrier. Stacked shields can exceed the actor's maximum health,
    /// so the percentage is reported as recorded rather than capped at 100.
    /// </summary>
    internal static string FormatBarrierAmount(uint maxHp, byte barrierPercentage)
    {

        if (maxHp == 0)
        {
            return $"{barrierPercentage}%";
        }

        var approximateAmount = (uint)(((ulong)maxHp * barrierPercentage) / 100);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"~{approximateAmount:N0}  ({barrierPercentage}%)");
    }

    internal static string ActorDisplayLabel(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.Equals(actor.ObjectKind, "Pc", StringComparison.Ordinal))
        {
            return JobIconResources.GetAbbreviation(actor.ClassJobId) is { } job
                ? $"{actor.Name} · {job}"
                : actor.Name;
        }

        return string.IsNullOrWhiteSpace(actor.Name)
            ? $"BattleNpc #{actor.StableActorId}"
            : actor.Name;
    }

    internal static bool TryFindActorMarker(
        ReadOnlySpan<ArenaActorMarker> actors,
        int stableActorId,
        out ArenaActorMarker result)
    {
        foreach (ref readonly var actor in actors)
        {
            if (actor.Actor.StableActorId == stableActorId)
            {
                result = actor;
                return true;
            }
        }

        result = default;
        return false;
    }


    internal static string ActorLabel(in ArenaActorMarker actor)
    {
        if (actor.Kind == ArenaActorMarkerKind.Player)
        {
            return JobIconResources.GetAbbreviation(actor.Actor.ClassJobId) ?? "PC";
        }

        return string.IsNullOrWhiteSpace(actor.Actor.Name)
            ? $"BattleNpc #{actor.Actor.StableActorId}"
            : actor.Actor.Name;
    }

    internal static float ResolveFocusedActorOpacity(
        int? selectedPlayerStableActorId,
        in ArenaActorMarker actor)
    {
        if (selectedPlayerStableActorId is null
            || actor.Actor.StableActorId == selectedPlayerStableActorId)
        {
            return 1;
        }

        return actor.Kind == ArenaActorMarkerKind.Player ? 0.2f : 0.55f;
    }

    internal static uint ResolveWaymarkIconId(WaymarkId id) => id switch
    {
        WaymarkId.A => 61241,
        WaymarkId.B => 61242,
        WaymarkId.C => 61243,
        WaymarkId.D => 61247,
        WaymarkId.One => 61244,
        WaymarkId.Two => 61245,
        WaymarkId.Three => 61246,
        WaymarkId.Four => 61248,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Waymark ID."),
    };

    internal static float ResolveWaymarkHalfSize(
        WaymarkId id,
        ArenaBounds worldBounds,
        float arenaSize,
        ArenaViewport viewport)
    {
        var worldHalfSize = id is WaymarkId.A or WaymarkId.B or WaymarkId.C or WaymarkId.D
            ? LetterWaymarkWorldRadius
            : NumberWaymarkWorldHalfSize;
        return ProjectWorldRadius(worldHalfSize, worldBounds, arenaSize, viewport);
    }

    internal static (Vector2 Minimum, Vector2 Maximum) ResolveCenteredWaymarkBounds(
        Vector2 position,
        float halfSize)
    {
        if (!float.IsFinite(position.X)
            || !float.IsFinite(position.Y)
            || !float.IsFinite(halfSize)
            || halfSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(halfSize),
                halfSize,
                "Waymark position and half-size must be finite, and half-size must be positive.");
        }

        var extent = new Vector2(halfSize);
        return (position - extent, position + extent);
    }

    private static uint WaymarkColor(WaymarkId id) => id switch
    {
        WaymarkId.A or WaymarkId.One => 0xff3f78ff,
        WaymarkId.B or WaymarkId.Two => 0xffff8f3f,
        WaymarkId.C or WaymarkId.Three => 0xff5ddd7a,
        _ => 0xffd464d9,
    };

    private static string WaymarkLabel(WaymarkId id) => id switch
    {
        WaymarkId.One => "1",
        WaymarkId.Two => "2",
        WaymarkId.Three => "3",
        WaymarkId.Four => "4",
        _ => id.ToString(),
    };

    private static string FormatTimestamp(long timestampMilliseconds) =>
        TimeSpan.FromMilliseconds(timestampMilliseconds).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
}
