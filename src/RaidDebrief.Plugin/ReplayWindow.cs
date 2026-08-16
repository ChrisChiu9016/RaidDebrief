using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;
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
    Enemy,
}

internal enum PartyHpTextMode
{
    Full,
    CurrentOnly,
    Hidden,
}

internal readonly record struct ActiveMitigationStatus(
    uint StatusId,
    int ActiveEventIndex,
    MitigationTargetKind TargetKind,
    int TargetActorId);


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
    float NextTopOffset,
    float TextHeight,
    bool IsCompact);

internal readonly record struct EnemyHudMetrics(
    float Width,
    float TextHeight,
    float BarHeight,
    float RowGap,
    float GroupGap,
    float HorizontalInset,
    float TopInset,
    bool IsCompact);

internal readonly record struct ArenaCanvasLayout(
    float Size,
    Vector2 Offset);


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
    internal const float EnemyHudCompactArenaWidthThreshold = 520;
    internal const float EnemyHudCompactWidth = 220;
    internal const float EnemyHudCompactTextHeight = 18;
    internal const float EnemyHudCompactBarHeight = 8;
    internal const float EnemyHudCompactRowGap = 3;
    internal const float EnemyHudCompactGroupGap = 8;
    internal const float EnemyHudCompactHorizontalInset = 8;
    internal const float EnemyHudCompactTopInset = 24;
    internal const float ArenaActorLabelOverlapDistance = 34;
    private const long CastCompletionToleranceMilliseconds = 150;
    private const long DeathContextWindowMilliseconds = 8_000;
    internal const long DeathQuickJumpClusterWindowMilliseconds = 5_000;
    internal const float ReplayWindowReferenceWidth = 1_600;
    internal const float ReplayWindowReferenceHeight = 1_080;
    internal const float ReplayWindowMinimumWidth = 1_200;
    internal const float ReplayWindowMinimumHeight = 840;
    internal const float ReplayLeftPanelWidth = 320;
    internal const float ReplayRightPanelWidth = 440;
    private const float ReplayPanelGap = 8;
    private const float ReplayPanelPadding = 8;
    internal const float ContextStatThreeColumnMinimumWidth = 390;
    private const float DeathQuickJumpSingleCardWidth = 200;
    private const float DeathQuickJumpClusterCardWidth = 300;
    private const float DeathQuickJumpCardHeight = 54;
    private const float DeathQuickJumpCardGap = 6;
    private const float DeathQuickJumpCardPadding = 8;
    private const float DeathQuickJumpJobIconSize = 28;
    private const float DeathQuickJumpCardRounding = 4;
    private static readonly Vector4 DeathQuickJumpCardColor = new(0.08f, 0.10f, 0.14f, 0.98f);
    private static readonly Vector4 DeathQuickJumpCardHoverColor = new(0.11f, 0.14f, 0.18f, 1);
    private static readonly Vector4 DeathQuickJumpCardSelectedColor = new(0.14f, 0.07f, 0.09f, 1);
    private static readonly Vector4 DeathQuickJumpCardBorderColor = new(0.36f, 0.41f, 0.49f, 1);
    private static readonly Vector4 DeathQuickJumpCardSelectedBorderColor = new(0.76f, 0.31f, 0.35f, 1);
    private static readonly Vector4 DeathQuickJumpCardAccentColor = new(0.88f, 0.35f, 0.38f, 1);
    internal const float ReplayWindowBackgroundAlpha = 0.98f;
    private const float PanelRounding = 4;
    private static readonly Vector4 PanelBackgroundColor = new(0.06f, 0.08f, 0.12f, 0.98f);
    private static readonly Vector4 PanelBorderColor = new(0.34f, 0.39f, 0.47f, 1);
    private static readonly Vector4 SubpanelBackgroundColor = new(0.08f, 0.10f, 0.14f, 0.98f);
    private static readonly Vector4 SectionHeaderColor = new(0.78f, 0.81f, 0.87f, 1f);
    private static readonly Vector4 SecondaryTextColor = new(0.66f, 0.70f, 0.78f, 1);
    private static readonly Vector4 DeathColor = new(0.94f, 0.40f, 0.42f, 1);
    private static readonly Vector4 AliveColor = new(0.34f, 0.82f, 0.58f, 1);
    private static readonly Vector4 ConfidenceColor = new(0.88f, 0.72f, 0.36f, 1);
    private static readonly Vector4 FocusAccentColor = new(0.20f, 0.90f, 1f, 1f);
    private static readonly Vector4 FocusRowBackgroundColor = new(0.14f, 0.26f, 0.35f, 1f);
    private static readonly Vector4 FocusRowHoverColor = new(0.11f, 0.17f, 0.23f, 1f);
    private const float FocusAccentBarWidth = 3;
    private const float TimelineTrackHeight = 20;
    private const float TimelineTrackRounding = 3;
    private const float TimelineMarkerHitRadius = 7;
    private static readonly Vector4 TimelineTrackColor = new(0.05f, 0.06f, 0.09f, 1f);
    private static readonly Vector4 TimelineProgressColor = new(0.22f, 0.55f, 0.82f, 1f);
    private static readonly Vector4 TimelinePlayheadColor = new(0.96f, 0.98f, 1f, 1f);
    private static readonly Vector4 TimelineDeathMarkerColor = new(1f, 0.28f, 0.30f, 1f);
    private static readonly Vector4 ArenaDeathCrossColor = new(1f, 0.28f, 0.30f, 1f);
    private static readonly Vector4 ArenaOutlineColor = new(0.02f, 0.02f, 0.04f, 0.88f);
    private static readonly Vector4 ArenaDeadIconTint = new(0.58f, 0.48f, 0.50f, 1f);
    private const float PartyJobTextScale = 1.15f;
    private const float PartyValueTextScale = 1.05f;
    internal const float KillingBlowActionTextScale = 1.6f;
    internal const float KillingBlowAmountTextScale = 1.8f;
    private const float KillingBlowSourceTextScale = 0.9f;
    private const float KillingBlowCardPadding = 10;
    private const float KillingBlowCardRounding = 4;
    private static readonly Vector4 KillingBlowCardColor = new(0.17f, 0.07f, 0.09f, 0.96f);
    private static readonly Vector4 KillingBlowCardBorderColor = new(0.45f, 0.20f, 0.23f, 1);
    private static readonly Vector4 KillingBlowAmountColor = new(1f, 0.42f, 0.40f, 1f);
    private static readonly Vector4 KillingBlowHitRowColor = new(0.24f, 0.08f, 0.10f, 0.92f);
    private const float ContextHeaderHeight = 48;
    private const float ContextHeaderIconSize = 28;
    internal const float ContextStateTextScale = 1.2f;
    private const float ContextStatCardHeight = 60;
    private const float ContextVitalsHeight = 46;
    internal const float ContextBodyTextScale = 1.1f;
    internal const float ContextMetricTextScale = 1.3f;
    private const float ContextJobTextScale = 1.08f;
    internal const float ContextVitalsIconScale = 0.9f;
    internal const float ContextVitalsTextScale = 1.15f;

    private readonly record struct ContextStatCell(
        string Label,
        string Value,
        Vector4 Color,
        float ValueScale = ContextMetricTextScale);
    internal const float ContextHealthChangeRowHeight = 30;
    internal const int MaximumDetailedHealthChanges = 10;
    internal const ImGuiWindowFlags HealthChangeDetailsWindowFlags =
        ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoSavedSettings;
    private const float ContextEmptyStateHeight = 64;
    private const float PartyIconSize = 24;
    private const float PartyJobTextOffset = 31;
    private const float PartyHpTextOffset = 80;
    private const float PartyRowBarHeight = 9;
    private const float PartyBarrierBarHeight = 6;
    private const float PartyBarrierOverhang = 3;
    private const float PartyRowBarGap = 8;
    private const float PartyRowBottomPadding = 16;
    private const float PartyBarRounding = 2;
    private const float PartyDeadRowAlpha = 0.72f;
    private static readonly Vector4 PartyBarTrackColor = new(0.07f, 0.09f, 0.12f, 1);
    private static readonly Vector4 PartyBarrierColor = new(0.96f, 0.86f, 0.24f, 1f);
    private static readonly Vector4 PartyHpColor = new(0.23f, 0.72f, 0.47f, 1f);
    private static readonly Vector4 PartyDeadTextColor = new(0.90f, 0.35f, 0.36f, 1f);
    private static readonly Vector4 PartyDeadBarColor = new(0.42f, 0.16f, 0.16f, 1f);
    private const float MitigationIconSize = 30;
    private const float MitigationTileWidth = 38;
    private const float MitigationTileGap = 6;
    private const int MaximumDisplayedMitigations = 64;
    private static readonly string HeartIcon = FontAwesomeIcon.Heart.ToIconString();
    private static readonly string ShieldIcon = FontAwesomeIcon.ShieldAlt.ToIconString();
    private static readonly string SkullIcon = FontAwesomeIcon.Skull.ToIconString();
    private static readonly string EmptyStateIcon = FontAwesomeIcon.TimesCircle.ToIconString();
    private const float PartyHpTextGap = 10;
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
    private readonly Action<bool> savePostWipeDebriefSetting;
    private readonly Action<bool> saveCloseReplayOnCombatStartSetting;
    private readonly Action<bool> saveDeveloperModeSetting;
    private readonly Action drawDeveloperOptions;
    private readonly Action<bool> setDeveloperOptionsVisible;
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
    private string statusMessage = "尚未載入 Replay；請先完成一個 Pull。";
    private double elapsedRemainderMilliseconds;
    private float playbackSpeed = 1;
    private ArenaViewport arenaViewport = ArenaViewport.Fit;
    private ArenaViewport minimumArenaViewport = ArenaViewport.Fit;
    private bool disposed;
    private bool closeReplayOnCombatStart = true;
    private bool showPostWipeDebrief;
    private bool developerModeEnabled;
    private bool showHotEffects;
    private int? selectedPlayerStableActorId;
    private bool healthChangeDetailsVisible;
    private bool timelineMarkerCapture;
    private bool developerOptionsVisible;
    private bool selectReplayTabOnNextDraw = true;
    private bool focusWindowOnNextDraw;

    public ReplayWindow(
        CaptureService captureService,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        BattleNpcOmnidirectionalityCatalog omnidirectionalityCatalog,
        bool showHotEffects,
        Action<bool> saveShowHotEffectsSetting,
        bool showPostWipeDebrief,
        Action<bool> savePostWipeDebriefSetting,
        bool closeReplayOnCombatStart,
        Action<bool> saveCloseReplayOnCombatStartSetting,
        bool developerModeEnabled,
        Action<bool> saveDeveloperModeSetting,
        Action drawDeveloperOptions,
        Action<bool> setDeveloperOptionsVisible,
        string pluginConfigDirectory,
        string pluginAssemblyPath)
        : base(
            "Raid Debrief##RaidDebriefReplay",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.captureService = captureService;
        this.omnidirectionalityCatalog = omnidirectionalityCatalog
            ?? throw new ArgumentNullException(nameof(omnidirectionalityCatalog));
        this.showHotEffects = showHotEffects;
        this.saveShowHotEffectsSetting = saveShowHotEffectsSetting
            ?? throw new ArgumentNullException(nameof(saveShowHotEffectsSetting));
        this.showPostWipeDebrief = showPostWipeDebrief;
        this.savePostWipeDebriefSetting = savePostWipeDebriefSetting
            ?? throw new ArgumentNullException(nameof(savePostWipeDebriefSetting));
        this.closeReplayOnCombatStart = closeReplayOnCombatStart;
        this.saveCloseReplayOnCombatStartSetting = saveCloseReplayOnCombatStartSetting
            ?? throw new ArgumentNullException(nameof(saveCloseReplayOnCombatStartSetting));
        this.developerModeEnabled = developerModeEnabled;
        this.saveDeveloperModeSetting = saveDeveloperModeSetting
            ?? throw new ArgumentNullException(nameof(saveDeveloperModeSetting));
        this.drawDeveloperOptions = drawDeveloperOptions
            ?? throw new ArgumentNullException(nameof(drawDeveloperOptions));
        this.setDeveloperOptionsVisible = setDeveloperOptionsVisible
            ?? throw new ArgumentNullException(nameof(setDeveloperOptionsVisible));
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
        this.Size = new Vector2(ReplayWindowReferenceWidth, ReplayWindowReferenceHeight);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = null;
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
        this.selectReplayTabOnNextDraw = true;
        this.focusWindowOnNextDraw = true;
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
        this.selectReplayTabOnNextDraw = true;
        this.focusWindowOnNextDraw = true;
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
        this.SetDeveloperOptionsVisible(false);
        this.statusMessage =
            "戰鬥開始；Replay 已暫停並自動關閉。脫離戰鬥後不會自動重開。";
        Plugin.Log.Information("Replay paused and hidden because InCombat=true.");
    }

    private void SuspendReplayWork()
    {
        this.session?.Pause();
        this.requestedSourceMode = null;
        this.requestedDebriefReplay = null;
        this.healthChangeDetailsVisible = false;
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
        this.SetDeveloperOptionsVisible(false);
        this.requestedSourceMode = null;
        this.elapsedRemainderMilliseconds = 0;
        this.loadCoordinator.Dispose();
    }



    public override void PreDraw()
    {
        var uiScale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var minimum = new Vector2(
            ReplayWindowMinimumWidth * uiScale,
            ResolveReplayMinimumWindowHeight(
                ImGui.GetTextLineHeight(),
                ImGui.GetFrameHeight(),
                style.ItemSpacing.Y,
                style.WindowPadding.Y,
                style.ScrollbarSize,
                uiScale));
        ImGui.SetNextWindowSizeConstraints(minimum, Vector2.PositiveInfinity);
        if (this.focusWindowOnNextDraw)
        {
            ImGui.SetNextWindowFocus();
            this.focusWindowOnNextDraw = false;
        }
    }
    public override void OnOpen()
    {
        this.selectReplayTabOnNextDraw = true;
        if (this.requestedSourceMode is null)
        {
            this.RequestRuntimeSource();
        }

        base.OnOpen();
    }

    public override void OnClose()
    {
        this.SuspendReplayWork();
        this.SetDeveloperOptionsVisible(false);
        base.OnClose();
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
            this.SetDeveloperOptionsVisible(false);
            return;
        }

        this.RefreshRuntimeSource();
        this.CompletePendingLoad();

        var developerTabVisible = false;
        if (ImGui.BeginTabBar("##RaidDebriefMainTabs"))
        {
            var replayTabFlags = this.selectReplayTabOnNextDraw
                ? ImGuiTabItemFlags.SetSelected
                : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("Replay", replayTabFlags))
            {
                this.DrawReplayTab(framePolicy.ShouldAdvance);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("設定"))
            {
                this.DrawSettingsTab();
                ImGui.EndTabItem();
            }

            if (this.developerModeEnabled && ImGui.BeginTabItem("開發者"))
            {
                developerTabVisible = true;
                this.DrawDeveloperTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
            this.selectReplayTabOnNextDraw = false;
        }

        this.SetDeveloperOptionsVisible(developerTabVisible);
    }

    private void DrawReplayTab(bool shouldAdvance)
    {
        if (shouldAdvance)
        {
            this.AdvancePlayback();
        }

        this.DrawReplayStatus();
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

    private void DrawSettingsTab()
    {
        if (ImGui.BeginChild("##RaidDebriefSettings"))
        {
            ImGui.TextUnformatted("一般");
            ImGui.Separator();

            var status = this.captureService.Status;
            var automaticCaptureEnabled = status.AutomaticCaptureEnabled;
            if (!status.IsRecording && !status.IsBusy)
            {
                if (ImGui.Checkbox("自動 Pull 擷取", ref automaticCaptureEnabled))
                {
                    this.captureService.SetAutomaticCaptureEnabled(automaticCaptureEnabled);
                }
            }
            else
            {
                ImGui.TextUnformatted(
                    $"自動 Pull 擷取：{(automaticCaptureEnabled ? "已啟用" : "已停用")}（擷取或背景工作期間不可切換）");
            }

            if (ImGui.Checkbox(
                    "Wipe 後顯示 Debrief 摘要",
                    ref this.showPostWipeDebrief))
            {
                this.savePostWipeDebriefSetting(this.showPostWipeDebrief);
            }

            if (ImGui.Checkbox(
                    "進入戰鬥時自動關閉 Replay 視窗",
                    ref this.closeReplayOnCombatStart))
            {
                this.saveCloseReplayOnCombatStartSetting(this.closeReplayOnCombatStart);
            }
            if (!this.closeReplayOnCombatStart)
            {
                ImGui.TextDisabled("停用時視窗會保持開啟，但戰鬥期間 Replay 仍會暫停。");
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("進階");
            ImGui.Separator();
            if (ImGui.Checkbox("開發人員", ref this.developerModeEnabled))
            {
                this.saveDeveloperModeSetting(this.developerModeEnabled);
                if (!this.developerModeEnabled)
                {
                    this.SetDeveloperOptionsVisible(false);
                }
            }

            ImGui.TextDisabled("啟用後顯示開發者分頁、即時診斷與手動 Capture 載入工具。");
        }

        ImGui.EndChild();
    }

    private void DrawDeveloperTab()
    {
        if (ImGui.BeginChild("##RaidDebriefDeveloperOptions"))
        {
            this.DrawDeveloperSourceControls();
            this.drawDeveloperOptions();
        }

        ImGui.EndChild();
    }

    private void SetDeveloperOptionsVisible(bool visible)
    {
        if (this.developerOptionsVisible == visible)
        {
            return;
        }

        this.developerOptionsVisible = visible;
        this.setDeveloperOptionsVisible(visible);
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
        this.healthChangeDetailsVisible = false;
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

    private void DrawReplayStatus()
    {
        var snapshot = this.captureService.GetReplaySourceSnapshot();
        if (this.session is not null)
        {
            return;
        }

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

    private void DrawDeveloperSourceControls()
    {
        ImGui.TextUnformatted("Replay 資料來源");
        ImGui.Separator();
        ImGui.TextWrapped(
            this.activeSourceMode is null
                ? "目前來源：尚未載入"
                : $"目前來源：{this.activeSourceDetail}");
        if (ImGui.Button("重新載入目前 Runtime Pull"))
        {
            this.RequestRuntimeSource();
        }

        ImGui.TextDisabled("手動 .json／.json.gz 匯入僅供開發測試，不是正式 Replay 資料來源。");
        ImGui.SetNextItemWidth(-100);
        ImGui.InputText("##ReplayFixturePath", ref this.fixturePath, 1_024);
        ImGui.SameLine();
        if (ImGui.Button("載入 Capture"))
        {
            this.StartDeveloperFixtureLoad();
        }

        ImGui.TextWrapped(this.statusMessage);
        ImGui.Spacing();
    }

    private void DrawReplayLayout(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        var available = ImGui.GetContentRegionAvail();
        var uiScale = Math.Max(0, ImGuiHelpers.GlobalScale);
        var panelGap = ReplayPanelGap * uiScale;
        var panelPadding = ReplayPanelPadding * uiScale;
        var layoutStartY = ImGui.GetCursorPosY();
        var bottomHeight = ResolveReplayBottomPanelHeight(
            ImGui.GetFrameHeight(),
            ImGui.GetTextLineHeight(),
            ImGui.GetStyle().ItemSpacing.Y,
            panelPadding,
            ImGui.GetStyle().ScrollbarSize);
        var mainHeight = Math.Max(0, available.Y - bottomHeight - panelGap);
        var widths = ResolveReplayColumnWidths(available.X, uiScale);

        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Vector2(panelPadding));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, PanelRounding);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackgroundColor);
        ImGui.PushStyleColor(ImGuiCol.Border, PanelBorderColor);

        if (ImGui.BeginChild(
                "##ReplayLeftPanel",
                new Vector2(widths.X, mainHeight),
                true,
                ImGuiWindowFlags.NoScrollbar))
        {
            this.DrawLeftPanel(replay, presentation);
        }

        ImGui.EndChild();
        ImGui.SameLine(0, panelGap);
        if (ImGui.BeginChild(
                "##ReplayCenterPanel",
                new Vector2(widths.Y, mainHeight),
                true,
                ImGuiWindowFlags.NoScrollbar))
        {
            this.DrawArena(replay.Scene);
        }

        ImGui.EndChild();
        ImGui.SameLine(0, panelGap);
        if (ImGui.BeginChild(
                "##ReplayContextPanel",
                new Vector2(widths.Z, mainHeight),
                true,
                ImGuiWindowFlags.NoScrollbar))
        {
            this.DrawContextPanel(replay, presentation);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);

        ImGui.SetCursorPosY(layoutStartY + mainHeight + panelGap);
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Vector2(panelPadding));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, PanelRounding);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackgroundColor);
        ImGui.PushStyleColor(ImGuiCol.Border, PanelBorderColor);
        if (ImGui.BeginChild(
                "##ReplayBottomPanel",
                new Vector2(0, bottomHeight),
                true))
        {
            this.DrawBottomTimeline(replay, presentation);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    /// <summary>
    /// Resolves the three replay column widths. Both side panels are fixed so
    /// their cards never change width with the window; the Arena absorbs all
    /// remaining width.
    /// </summary>
    internal static Vector3 ResolveReplayColumnWidths(
        float availableWidth,
        float uiScale = 1)
    {
        uiScale = Math.Max(0, uiScale);
        var contentWidth = Math.Max(0, availableWidth - (ReplayPanelGap * uiScale * 2));
        var left = ReplayLeftPanelWidth * uiScale;
        var right = ReplayRightPanelWidth * uiScale;
        var sides = left + right;
        if (sides > contentWidth && sides > 0)
        {
            // Degenerate surface: keep both side panels proportional instead of
            // letting the Arena column go negative.
            var factor = contentWidth / sides;
            left *= factor;
            right *= factor;
        }

        return new Vector3(left, contentWidth - left - right, right);
    }

    internal static float ResolveReplayBottomPanelHeight(
        float frameHeight,
        float textLineHeight,
        float itemSpacingY,
        float windowPaddingY,
        float scrollbarSize) =>
        (windowPaddingY * 2)
        + MathF.Max(frameHeight, TimelineTrackHeight)
        + textLineHeight
        + DeathQuickJumpCardHeight
        + scrollbarSize
        + (itemSpacingY * 3)
        + 1;

    /// <summary>
    /// Scales a reference pixel length by the Dalamud UI scale so framed context
    /// boxes grow with the font instead of clipping enlarged text.
    /// </summary>
    internal static float ResolveScaledLength(float referenceLength, float uiScale) =>
        referenceLength * (float.IsFinite(uiScale) && uiScale > 0 ? uiScale : 1);

    private static float Scaled(float referenceLength) =>
        ResolveScaledLength(referenceLength, ImGuiHelpers.GlobalScale);

    /// <summary>
    /// Resolves the minimum window height that lets the tallest context column —
    /// the Death context — render completely, so the side panels never need a
    /// scrollbar at the minimum size. Bounded content only; an unusually deep
    /// mitigation grid still scrolls by wheel.
    /// </summary>
    internal static float ResolveReplayMinimumWindowHeight(
        float textLineHeight,
        float frameHeight,
        float itemSpacingY,
        float windowPaddingY,
        float scrollbarSize,
        float uiScale)
    {
        var panelPaddingY = ResolveScaledLength(ReplayPanelPadding, uiScale);
        var killingBlowCardHeight =
            (ResolveScaledLength(KillingBlowCardPadding, uiScale) * 2)
            + (textLineHeight
                * (KillingBlowActionTextScale
                    + KillingBlowAmountTextScale
                    + KillingBlowSourceTextScale));
        var contextHeight = (panelPaddingY * 2)
            // Identity card: header plus the vitality row.
            + ResolveScaledLength(ContextHeaderHeight, uiScale)
            + itemSpacingY
            + ResolveScaledLength(ContextVitalsHeight, uiScale)
            + itemSpacingY
            // Killing blow heading and card.
            + textLineHeight + itemSpacingY
            + killingBlowCardHeight
            + itemSpacingY
            // 命中當下 heading and its impact card row.
            + textLineHeight + itemSpacingY
            + ResolveScaledLength(ContextStatCardHeight, uiScale)
            + itemSpacingY
            // 血量變動紀錄 heading row, table header, and its five body rows.
            + frameHeight + itemSpacingY
            + textLineHeight + itemSpacingY
            + (ResolveScaledLength(ContextHealthChangeRowHeight, uiScale)
                * ReplayHealthChangeIndex.MaximumVisibleChanges)
            + itemSpacingY
            // 生效中的減傷 heading row and its framed container.
            + frameHeight + itemSpacingY
            + ResolveScaledLength(ContextEmptyStateHeight, uiScale)
            // Each explicit Spacing() between the five context blocks also pays
            // ImGui's automatic inter-widget ItemSpacing.
            + (itemSpacingY * 5);
        var chrome = (windowPaddingY * 2)
            // Title bar, tab bar, and the capture-limitation status line.
            + frameHeight
            + frameHeight + itemSpacingY
            + textLineHeight + itemSpacingY
            + ResolveScaledLength(ReplayPanelGap, uiScale);
        var required = chrome
            + contextHeight
            + ResolveReplayBottomPanelHeight(
                frameHeight,
                textLineHeight,
                itemSpacingY,
                panelPaddingY,
                scrollbarSize);
        return MathF.Max(ResolveScaledLength(ReplayWindowMinimumHeight, uiScale), required);
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
        DrawSectionHeader($"PARTY ({presentation.PartyActors.Length})");
        this.DrawPartyList(replay, presentation);
    }

    private static void DrawPullSummary(DebriefSummary summary)
    {
        DrawSectionHeader("PULL SUMMARY");
        var pullNumber = summary.PullNumber is { } number
            ? $"Pull #{number}"
            : "Pull —";
        DrawLabelValueRow("PULL", pullNumber);
        DrawLabelValueRow(
            "PULL DURATION",
            FormatTimestamp(summary.DurationMilliseconds));
        DrawLabelValueRow(
            "FINAL BOSS HP",
            summary.BossHpAtEnd is { } hp ? $"{hp.Percentage:F1}%" : "—");
    }

    private static void DrawLabelValueRow(string label, string value)
    {
        ImGui.TextColored(SecondaryTextColor, label);
        ImGui.SameLine();
        var valueWidth = ImGui.CalcTextSize(value).X;
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right - valueWidth));
        ImGui.TextUnformatted(value);
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
            var isSelected = this.selectedPlayerStableActorId == actor.StableActorId;
            ImGui.PushStyleColor(ImGuiCol.Header, FocusRowBackgroundColor);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, FocusRowHoverColor);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, FocusRowBackgroundColor);
            var activated = ImGui.Selectable(
                $"##ReplayParty{actor.StableActorId}",
                isSelected,
                ImGuiSelectableFlags.None,
                new Vector2(0, rowHeight));
            ImGui.PopStyleColor(3);
            if (activated)
            {
                this.selectedPlayerStableActorId = actor.StableActorId;
                isSelected = true;
            }

            // The accent bar shares FocusAccentColor with the arena selection ring so
            // the same focused player reads identically across both panels.
            if (isSelected)
            {
                var rowMinimum = ImGui.GetItemRectMin();
                var rowMaximum = ImGui.GetItemRectMax();
                ImGui.GetWindowDrawList().AddRectFilled(
                    rowMinimum,
                    new Vector2(rowMinimum.X + FocusAccentBarWidth, rowMaximum.Y),
                    ImGui.GetColorU32(FocusAccentColor),
                    PanelRounding,
                    ImDrawFlags.RoundCornersLeft);
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
        if (isDead)
        {
            var jobWidth = ImGui.CalcTextSize(job).X * PartyJobTextScale;
            drawList.AddText(
                font,
                valueFontSize,
                new Vector2(
                    origin.X + PartyJobTextOffset + jobWidth + 7,
                    origin.Y + valueOffsetY),
                ImGui.GetColorU32(PartyDeadTextColor),
                "DEAD");
        }

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

        var hpText = string.Create(
            CultureInfo.InvariantCulture,
            $"{marker.CurrentHp:N0} / {marker.MaxHp:N0}");
        var percentage = marker.CurrentHp * 100d / marker.MaxHp;
        var percentText = string.Create(CultureInfo.InvariantCulture, $"{percentage:F1}%");
        var percentWidth = ImGui.CalcTextSize(percentText).X * PartyValueTextScale;
        var hpWidth = ImGui.CalcTextSize(hpText).X * PartyValueTextScale;
        var percentX = origin.X + width - percentWidth;
        var minimumHpX = origin.X + (isDead ? 112 : PartyHpTextOffset);
        var availableHpWidth = Math.Max(0, percentX - minimumHpX - PartyHpTextGap);
        var hpMode = PartyHpTextMode.Full;
        if (hpWidth > availableHpWidth)
        {
            var compactHpText = marker.CurrentHp.ToString("N0", CultureInfo.InvariantCulture);
            var compactHpWidth =
                ImGui.CalcTextSize(compactHpText).X * PartyValueTextScale;
            hpMode = ResolvePartyHpTextMode(
                availableHpWidth,
                hpWidth,
                compactHpWidth);
            if (hpMode == PartyHpTextMode.CurrentOnly)
            {
                hpText = compactHpText;
                hpWidth = compactHpWidth;
            }
        }

        if (hpMode != PartyHpTextMode.Hidden)
        {
            drawList.AddText(
                font,
                valueFontSize,
                new Vector2(percentX - hpWidth - PartyHpTextGap, origin.Y + valueOffsetY),
                isDead ? dimColor : textColor,
                hpText);
        }
        drawList.AddText(
            font,
            valueFontSize,
            new Vector2(percentX, origin.Y + valueOffsetY),
            isDead ? ImGui.GetColorU32(PartyDeadTextColor) : dimColor,
            percentText);
    }

    internal static PartyHpTextMode ResolvePartyHpTextMode(
        float availableWidth,
        float fullTextWidth,
        float compactTextWidth)
    {
        availableWidth = Math.Max(0, availableWidth);
        if (fullTextWidth <= availableWidth)
        {
            return PartyHpTextMode.Full;
        }

        return compactTextWidth <= availableWidth
            ? PartyHpTextMode.CurrentOnly
            : PartyHpTextMode.Hidden;
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
            this.DrawDeathDetails(replay, presentation, death);
            return;
        }

        if (focusedActorId is { } selectedActorId
            && replay.TryGetActor(selectedActorId, out var actor))
        {
            this.DrawActorSnapshot(replay, presentation, actor);
            return;
        }

        DrawSectionHeader("內容");
        ImGui.TextWrapped(
            "拖曳時間軸至死亡事件，或選取隊員以固定顯示其記錄狀態。");
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
        ReplayPresentationModel presentation,
        ActorRecord actor)
    {
        var stateAvailable = TryFindActorMarker(
            replay.Scene.Actors,
            actor.StableActorId,
            out var marker);
        var isDead = stateAvailable && marker.IsDead;
        this.DrawContextIdentityCard(
            actor,
            isDead ? "死亡" : "存活",
            isDead ? DeathColor : AliveColor,
            replay.CurrentTimeMilliseconds,
            stateAvailable,
            marker,
            isDead);
        ImGui.Spacing();

        if (stateAvailable)
        {
            var barrier = marker.BarrierPercentage is { } barrierPercentage
                ? FormatBarrierAmount(marker.MaxHp, barrierPercentage)
                : "—  未記錄";
            DrawContextStatRow(
                "##ReplayActorBarrier",
                new("護盾", barrier, Vector4.One));
        }
        else
        {
            DrawContextEmptyState(
                "##ReplayActorUnavailable",
                "此回放時間點沒有記錄到該角色。");
        }

        ImGui.Spacing();
        this.DrawHealthChangeSection(
            presentation.HealthChanges.GetRecentChanges(
                actor.StableActorId,
                replay.CurrentTimeMilliseconds),
            presentation.HealthChanges.GetChangesInWindow(
                actor.StableActorId,
                replay.CurrentTimeMilliseconds),
            replay.CurrentTimeMilliseconds,
            candidate: null,
            "##ReplayActorHealthChanges");

        ImGui.Spacing();
        this.DrawActiveMitigations(
            replay,
            actor.StableActorId,
            replay.CurrentTimeMilliseconds);
    }

    private void DrawDeathDetails(
        ReplaySession replay,
        ReplayPresentationModel presentation,
        in ReplayDeathItem death)
    {
        var correlation = death.Correlation;
        var stateAvailable = TryFindActorMarker(
            replay.Scene.Actors,
            death.Actor.StableActorId,
            out var marker);
        this.DrawContextIdentityCard(
            death.Actor,
            "死亡",
            DeathColor,
            correlation.DeathTimestampMilliseconds,
            stateAvailable,
            marker,
            forceDead: true);
        ImGui.Spacing();
        DrawKillingBlowHeading(correlation);

        if (correlation.KillingBlowCandidate is { } candidate)
        {
            this.DrawKillingBlowCard(replay, candidate);
        }
        else
        {
            DrawContextEmptyState(
                "##ReplayKillingBlowUnavailable",
                "沒有記錄到可解析目標的承受傷害。");
        }

        ImGui.Spacing();
        DrawSectionHeader("命中當下");
        DrawDeathImpactGrid(correlation);
        ImGui.Spacing();
        this.DrawHealthChangeSection(
            presentation.HealthChanges.GetRecentChanges(
                death.Actor.StableActorId,
                correlation.DeathTimestampMilliseconds),
            presentation.HealthChanges.GetChangesInWindow(
                death.Actor.StableActorId,
                correlation.DeathTimestampMilliseconds),
            correlation.DeathTimestampMilliseconds,
            correlation.KillingBlowCandidate,
            "##ReplayDeathHealthChanges");
        ImGui.Spacing();
        this.DrawActiveMitigations(
            replay,
            death.Actor.StableActorId,
            ResolveDeathMitigationAnchorTimestamp(correlation),
            correlation.DeathOriginalRecordedIndex);
    }

    private void DrawContextIdentityCard(
        ActorRecord actor,
        string state,
        Vector4 stateColor,
        long timestampMilliseconds,
        bool stateAvailable,
        in ArenaActorMarker marker,
        bool forceDead)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var itemSpacingY = ImGui.GetStyle().ItemSpacing.Y;
        var maximum = origin + new Vector2(
            width,
            Scaled(ContextHeaderHeight) + itemSpacingY + Scaled(ContextVitalsHeight));
        drawList.AddRectFilled(
            origin,
            maximum,
            ImGui.GetColorU32(SubpanelBackgroundColor),
            PanelRounding);
        drawList.AddRect(
            origin,
            maximum,
            ImGui.GetColorU32(PanelBorderColor),
            PanelRounding);
        var dividerY = origin.Y + Scaled(ContextHeaderHeight) + (itemSpacingY * 0.5f);
        drawList.AddLine(
            new Vector2(origin.X, dividerY),
            new Vector2(maximum.X, dividerY),
            ImGui.GetColorU32(PanelBorderColor));

        this.DrawContextHeader(actor, state, stateColor, timestampMilliseconds);
        DrawContextVitals(stateAvailable, marker, forceDead);
    }

    private void DrawContextHeader(
        ActorRecord actor,
        string state,
        Vector4 stateColor,
        long timestampMilliseconds)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var headerHeight = Scaled(ContextHeaderHeight);
        var iconSize = Scaled(ContextHeaderIconSize);
        var maximum = origin + new Vector2(width, headerHeight);
        ImGui.Dummy(new Vector2(width, headerHeight));

        var iconOrigin = new Vector2(
            origin.X + ReplayPanelPadding,
            origin.Y + ((headerHeight - iconSize) * 0.5f));
        if (actor.ClassJobId < this.jobIcons.Length
            && this.jobIcons[(int)actor.ClassJobId] is { } icon
            && icon.TryGetWrap(out var texture, out _)
            && texture is not null)
        {
            drawList.AddImage(
                texture.Handle,
                iconOrigin,
                iconOrigin + new Vector2(iconSize));
        }

        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var stateFontSize = fontSize * ContextStateTextScale;
        var job = JobIconResources.GetAbbreviation(actor.ClassJobId) ?? "Player";
        var textY = origin.Y + ((headerHeight - ImGui.GetTextLineHeight()) * 0.5f);
        var stateTextY = origin.Y + ((headerHeight - stateFontSize) * 0.5f);
        var jobX = iconOrigin.X + iconSize + Scaled(10);
        drawList.AddText(
            font,
            fontSize * ContextJobTextScale,
            new Vector2(jobX, textY - 1),
            ImGui.GetColorU32(Vector4.One),
            job);
        var jobWidth = ImGui.CalcTextSize(job).X * ContextJobTextScale;
        var stateX = jobX + jobWidth + Scaled(16);
        drawList.AddLine(
            new Vector2(stateX - 8, origin.Y + 11),
            new Vector2(stateX - 8, maximum.Y - 11),
            ImGui.GetColorU32(PanelBorderColor));
        drawList.AddText(
            font,
            stateFontSize,
            new Vector2(stateX, stateTextY),
            ImGui.GetColorU32(stateColor),
            state);

        var timestamp = FormatTimestamp(timestampMilliseconds);
        var timestampFontSize = fontSize * ContextBodyTextScale;
        var timestampWidth = ImGui.CalcTextSize(timestamp).X * ContextBodyTextScale;
        drawList.AddText(
            font,
            timestampFontSize,
            new Vector2(
                maximum.X - ReplayPanelPadding - timestampWidth,
                origin.Y
                    + ((headerHeight
                        - (ImGui.GetTextLineHeight() * ContextBodyTextScale)) * 0.5f)),
            ImGui.GetColorU32(stateColor),
            timestamp);
    }

    private static void DrawContextVitals(
        bool stateAvailable,
        in ArenaActorMarker marker,
        bool forceDead)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var contentOrigin = origin + new Vector2(ReplayPanelPadding, 0);
        var contentWidth = Math.Max(0, width - (ReplayPanelPadding * 2));
        var isDead = forceDead || (stateAvailable && marker.IsDead);
        var barWidth = contentWidth;
        ImGui.Dummy(new Vector2(width, Scaled(ContextVitalsHeight)));

        var font = ImGui.GetFont();
        var metricFontSize = ImGui.GetFontSize() * ContextVitalsTextScale;
        var iconFontSize = ImGui.GetFontSize() * ContextVitalsIconScale;
        drawList.AddText(
            UiBuilder.IconFont,
            iconFontSize,
            contentOrigin + new Vector2(0, 2),
            ImGui.GetColorU32(isDead ? DeathColor : AliveColor),
            HeartIcon);
        var hpText = stateAvailable && marker.MaxHp > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{marker.CurrentHp:N0} / {marker.MaxHp:N0}")
            : "—";
        drawList.AddText(
            font,
            metricFontSize,
            contentOrigin + new Vector2(iconFontSize + Scaled(8), 0),
            ImGui.GetColorU32(Vector4.One),
            hpText);
        var percentage = stateAvailable && marker.MaxHp > 0
            ? marker.CurrentHp * 100d / marker.MaxHp
            : 0;
        var percentText = stateAvailable && marker.MaxHp > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{percentage:F1}%")
            : "—";
        var percentWidth = ImGui.CalcTextSize(percentText).X * ContextVitalsTextScale;
        drawList.AddText(
            font,
            metricFontSize,
            new Vector2(contentOrigin.X + contentWidth - percentWidth, contentOrigin.Y),
            ImGui.GetColorU32(isDead ? DeathColor : SecondaryTextColor),
            percentText);

        var barMinimum = contentOrigin + new Vector2(0, Scaled(28));
        var barMaximum = barMinimum + new Vector2(barWidth, Scaled(9));
        drawList.AddRectFilled(
            barMinimum,
            barMaximum,
            ImGui.GetColorU32(PartyBarTrackColor),
            PartyBarRounding);
        if (stateAvailable && marker.MaxHp > 0 && marker.CurrentHp > 0)
        {
            var fraction = Math.Clamp(marker.CurrentHp / (float)marker.MaxHp, 0, 1);
            drawList.AddRectFilled(
                barMinimum,
                new Vector2(barMinimum.X + (barWidth * fraction), barMaximum.Y),
                ImGui.GetColorU32(PartyHpColor),
                PartyBarRounding);
        }

    }

    private static void DrawKillingBlowHeading(in DeathEventCorrelation correlation)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(DeathColor, ShieldIcon);
        ImGui.PopFont();
        ImGui.SameLine();
        var heading = FormatKillingBlowHeading(correlation.Confidence);
        ImGui.TextColored(SectionHeaderColor, heading);
        var hovered = ImGui.IsItemHovered();
        var confidence = correlation.Confidence switch
        {
            CorrelationConfidence.High => "信心：高",
            CorrelationConfidence.Medium => "信心：中",
            CorrelationConfidence.Low => "信心：低",
            _ => "無法判定",
        };
        ImGui.SameLine();
        var confidenceWidth = ImGui.CalcTextSize(confidence).X;
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right - confidenceWidth));
        ImGui.TextColored(ConfidenceColor, confidence);
        if (hovered || ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"依據記錄的 HP、技能效果與死亡狀態轉換推算。\n" +
                $"證據：{correlation.Evidence}\n" +
                $"限制：{correlation.Limitations}\n" +
                "FFXIV 未提供直接的致死一擊欄位。");
        }
    }

    internal static string FormatKillingBlowHeading(CorrelationConfidence confidence) =>
        confidence switch
        {
            CorrelationConfidence.High => "已關聯的致死傷害",
            CorrelationConfidence.Medium => "推測致死傷害",
            CorrelationConfidence.Low => "可能的最後一擊",
            _ => "致死傷害",
        };

    private static void DrawDeathImpactGrid(in DeathEventCorrelation correlation)
    {
        var hp = correlation.EstimatedHpBeforeHit is { } hpBefore
            ? hpBefore.ToString("N0", CultureInfo.InvariantCulture)
            : "—";
        var barrier = correlation.Barrier.Disposition switch
        {
            BarrierDisposition.NotRecorded => "—  未記錄",
            BarrierDisposition.None => "—",
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"~{correlation.Barrier.AmountAtDeath:N0} ({correlation.Barrier.PercentageAtDeath}%)"),
        };
        var overkill = correlation.EstimatedOverkill is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"~{value:N0}")
            : "—";
        var gridHovered = DrawContextStatGrid(
            new("命中前 HP", hp, Vector4.One),
            new("護盾", barrier, Vector4.One),
            new("預估溢出傷害", overkill, DeathColor));

        if (correlation.EstimatedEffectivePoolBeforeHit is { } pool
            && correlation.Barrier.StoodAgainstTheKillingBlow
            && gridHovered)
        {
            ImGui.SetTooltip(
                $"命中前有效承傷量：{pool:N0}\n" +
                "死亡前最後一筆狀態仍記錄有護盾。");
        }
    }

    private static bool DrawContextStatGrid(
        ContextStatCell first,
        ContextStatCell second,
        ContextStatCell? third = null)
    {
        var columnCount = ResolveContextStatColumnCount(
            ImGui.GetContentRegionAvail().X,
            third.HasValue ? 3 : 2,
            ImGuiHelpers.GlobalScale);
        if (third.HasValue && columnCount == 2)
        {
            var hovered = DrawContextStatRow(
                "##ReplayContextStatGridTop",
                first,
                second);
            ImGui.Spacing();

            // The lone reflowed card keeps the same width as the two above it, so
            // card proportions stay stable when the window ratio changes.
            return DrawContextStatRow(
                "##ReplayContextStatGridBottom",
                third.Value,
                reservedColumnCount: 2) || hovered;
        }

        return DrawContextStatRow(
            "##ReplayContextStatGrid",
            first,
            second,
            third);
    }

    internal static int ResolveContextStatColumnCount(
        float availableWidth,
        int cellCount,
        float uiScale = 1)
    {
        if (cellCount is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(cellCount));
        }

        return cellCount == 3
            && availableWidth < ContextStatThreeColumnMinimumWidth * Math.Max(0, uiScale)
                ? 2
                : cellCount;
    }

    private static bool DrawContextStatRow(
        string tableId,
        ContextStatCell first,
        ContextStatCell? second = null,
        ContextStatCell? third = null,
        int? reservedColumnCount = null)
    {
        var count = third.HasValue ? 3 : second.HasValue ? 2 : 1;
        var columnCount = Math.Max(count, reservedColumnCount ?? count);
        ImGui.PushID(tableId);
        if (!ImGui.BeginTable(
                "##Grid",
                columnCount,
                ImGuiTableFlags.SizingStretchSame,
                new Vector2(0, Scaled(ContextStatCardHeight))))
        {
            ImGui.PopID();
            return false;
        }

        var hovered = false;
        ImGui.TableNextRow();
        for (var index = 0; index < count; index++)
        {
            var cell = index switch
            {
                0 => first,
                1 => second!.Value,
                _ => third!.Value,
            };
            var id = index switch
            {
                0 => "##Stat0",
                1 => "##Stat1",
                _ => "##Stat2",
            };
            ImGui.TableSetColumnIndex(index);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, SubpanelBackgroundColor);
            ImGui.PushStyleColor(ImGuiCol.Border, PanelBorderColor);
            if (ImGui.BeginChild(
                    id,
                    new Vector2(0, Scaled(ContextStatCardHeight)),
                    true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.TextColored(SecondaryTextColor, cell.Label);
                DrawContextStatValue(cell);
            }

            ImGui.EndChild();
            hovered |= ImGui.IsItemHovered();
            ImGui.PopStyleColor(2);
        }

        ImGui.EndTable();
        ImGui.PopID();
        return hovered;
    }

    private static void DrawContextStatValue(in ContextStatCell cell)
    {
        if (cell.ValueScale <= 1)
        {
            ImGui.TextColored(cell.Color, cell.Value);
            return;
        }

        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * cell.ValueScale;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight() * cell.ValueScale));
        ImGui.GetWindowDrawList().AddText(
            font,
            fontSize,
            origin,
            ImGui.GetColorU32(cell.Color),
            cell.Value);
    }


    internal static Vector4 ResolveHealthChangeColor(ActionEffectKind kind) =>
        kind switch
        {
            ActionEffectKind.Damage => DeathColor,
            ActionEffectKind.Heal => AliveColor,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private void DrawHealthChangeSection(
        ReadOnlySpan<ReplayHealthChange> recentChanges,
        ReadOnlySpan<ReplayHealthChange> allChanges,
        long anchorTimestampMilliseconds,
        CorrelatedDamageEvent? candidate,
        string tableId)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, SectionHeaderColor);
        ImGui.TextUnformatted("血量變動紀錄");
        ImGui.PopStyleColor();

        var buttonWidth = ImGui.CalcTextSize("詳細記錄").X
            + (ImGui.GetStyle().FramePadding.X * 2);
        var buttonX = ImGui.GetCursorPosX()
            + ImGui.GetContentRegionAvail().X
            - buttonWidth;
        ImGui.SameLine(buttonX);
        if (ImGui.Button($"詳細記錄##{tableId}DetailButton"))
        {
            this.healthChangeDetailsVisible =
                ResolveHealthChangeDetailsVisibility(
                    this.healthChangeDetailsVisible,
                    openRequested: true,
                    closeRequested: false);
        }

        ImGui.Spacing();
        this.DrawHealthChanges(
            recentChanges,
            anchorTimestampMilliseconds,
            candidate,
            tableId,
            ReplayHealthChangeIndex.MaximumVisibleChanges,
            scroll: false);

        if (!this.healthChangeDetailsVisible)
        {
            return;
        }

        ImGui.SetNextWindowSizeConstraints(
            new Vector2(390, 0),
            new Vector2(480, float.MaxValue));
        if (ImGui.Begin(
                "十秒內血量變動##ReplayHealthChangeDetails",
                HealthChangeDetailsWindowFlags))
        {
            this.DrawHealthChanges(
                allChanges,
                anchorTimestampMilliseconds,
                candidate,
                "##ReplayHealthChangeDetailsTable",
                MaximumDetailedHealthChanges,
                scroll: true);
            ImGui.Spacing();
            if (ImGui.Button("關閉##ReplayHealthChangeDetailsClose"))
            {
                this.healthChangeDetailsVisible =
                    ResolveHealthChangeDetailsVisibility(
                        this.healthChangeDetailsVisible,
                        openRequested: false,
                        closeRequested: true);
            }
        }

        ImGui.End();
    }

    internal static bool ResolveHealthChangeDetailsVisibility(
        bool currentlyVisible,
        bool openRequested,
        bool closeRequested) =>
        !closeRequested && (currentlyVisible || openRequested);

    internal static int ResolveHealthChangeRenderedRowCount(
        int changeCount,
        int visibleRowCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(changeCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(visibleRowCapacity, 0);
        return Math.Max(changeCount, visibleRowCapacity);
    }

    /// <summary>
    /// Vertically centers a health-change row's text inside its reserved row
    /// height, measured from the cell's content origin after cell padding.
    /// </summary>
    internal static float ResolveHealthChangeRowTextOffsetY(
        float rowHeight,
        float textLineHeight,
        float cellPaddingY) =>
        Math.Max(0, (rowHeight - (cellPaddingY * 2) - textLineHeight) * 0.5f);

    private void DrawHealthChanges(
        ReadOnlySpan<ReplayHealthChange> changes,
        long anchorTimestampMilliseconds,
        CorrelatedDamageEvent? candidate,
        string tableId,
        int visibleRowCapacity,
        bool scroll)
    {
        var tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.SizingStretchProp;
        var tableSize = Vector2.Zero;
        if (scroll)
        {
            tableFlags |= ImGuiTableFlags.ScrollY;
            tableSize.Y = ImGui.GetTextLineHeightWithSpacing()
                + (Scaled(ContextHealthChangeRowHeight) * visibleRowCapacity)
                + 2;
        }

        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, PanelBorderColor);
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, PanelBorderColor);
        if (ImGui.BeginTable(tableId, 4, tableFlags, tableSize))
        {
            ImGui.TableSetupColumn("時間", ImGuiTableColumnFlags.WidthFixed, Scaled(54));
            ImGui.TableSetupColumn("招式", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(
                "承受傷害",
                ImGuiTableColumnFlags.WidthFixed,
                Scaled(84));
            ImGui.TableSetupColumn(
                "受治療量",
                ImGuiTableColumnFlags.WidthFixed,
                Scaled(84));
            if (scroll)
            {
                ImGui.TableSetupScrollFreeze(0, 1);
            }

            ImGui.TableHeadersRow();
            var rowHeight = Scaled(ContextHealthChangeRowHeight);
            var rowTextOffsetY = ResolveHealthChangeRowTextOffsetY(
                rowHeight,
                ImGui.GetTextLineHeight(),
                ImGui.GetStyle().CellPadding.Y);
            var renderedRowCount = ResolveHealthChangeRenderedRowCount(
                changes.Length,
                visibleRowCapacity);
            for (var rowIndex = 0; rowIndex < renderedRowCount; rowIndex++)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                if (rowIndex >= changes.Length)
                {
                    continue;
                }

                ref readonly var change = ref changes[rowIndex];
                var isDamage = change.Kind == ActionEffectKind.Damage;
                var rowColor = ResolveHealthChangeColor(change.Kind);
                var isCandidate = change.IsDamageCandidate(candidate);
                if (isCandidate)
                {
                    ImGui.TableSetBgColor(
                        ImGuiTableBgTarget.RowBg0,
                        ImGui.GetColorU32(KillingBlowHitRowColor));
                }

                ImGui.TableSetColumnIndex(0);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + rowTextOffsetY);
                var offset =
                    (change.TimestampMilliseconds - anchorTimestampMilliseconds) / 1000f;
                ImGui.TextColored(rowColor, $"{offset,5:F1}s");

                ImGui.TableSetColumnIndex(1);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + rowTextOffsetY);
                if (isCandidate)
                {
                    ImGui.TextColored(DeathColor, "◆");
                    ImGui.SameLine(0, 4);
                }

                ImGui.TextColored(
                    rowColor,
                    this.gameDataCatalog.GetActionName(change.ActionId));
                if (isCandidate && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("此筆為上方顯示的致死候選傷害。");
                }

                ImGui.TableSetColumnIndex(isDamage ? 2 : 3);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + rowTextOffsetY);
                ImGui.TextColored(
                    rowColor,
                    change.Amount.ToString("N0", CultureInfo.InvariantCulture));
            }

            ImGui.EndTable();
        }

        ImGui.PopStyleColor(2);
    }

    private static void DrawContextEmptyState(
        string id,
        string message,
        float height = ContextEmptyStateHeight)
    {
        var scaledHeight = Scaled(height);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, SubpanelBackgroundColor);
        ImGui.PushStyleColor(ImGuiCol.Border, PanelBorderColor);
        if (ImGui.BeginChild(
                id,
                new Vector2(0, scaledHeight),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var messageWidth = ImGui.CalcTextSize(message).X;
            var iconWidth = ImGui.GetFontSize();
            var totalWidth = iconWidth + 10 + messageWidth;
            ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetContentRegionAvail().X - totalWidth) * 0.5f));
            ImGui.SetCursorPosY(
                Math.Max(0, (scaledHeight - ImGui.GetTextLineHeight()) * 0.5f - 4));
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(SecondaryTextColor, EmptyStateIcon);
            ImGui.PopFont();
            ImGui.SameLine(0, 10);
            ImGui.TextColored(SecondaryTextColor, message);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
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
                ? $"來源：{ActorDisplayLabel(sourceActor)}"
                : "未解析到來源角色";
        textY += amountHeight;
        drawList.AddText(
            font,
            baseFontSize * KillingBlowSourceTextScale,
            new Vector2(textX, textY),
            ImGui.GetColorU32(SecondaryTextColor),
            source);
    }

    /// <summary>
    /// Resolves the timestamp whose recorded statuses represent the mitigation the
    /// actor carried into the killing blow. The Action Effect timestamp is stamped
    /// inside the client hook, so it avoids the 10 Hz HP／status sampling lag that
    /// separates the recorded Death transition from the hit itself. Without a
    /// resolved candidate the Death transition remains the only recorded anchor.
    /// </summary>
    internal static long ResolveDeathMitigationAnchorTimestamp(
        in DeathEventCorrelation correlation) =>
        correlation.KillingBlowCandidate?.TimestampMilliseconds
        ?? correlation.DeathTimestampMilliseconds;

    /// <param name="exclusiveRecordedIndexLimit">
    /// Recorded event index at or after which status transitions are ignored. The
    /// Death transition and the status removals it causes share one sample
    /// timestamp, and capture appends Death first, so passing the Death index
    /// keeps death-stripped statuses from reading as expired.
    /// </param>
    private void DrawActiveMitigations(
        ReplaySession replay,
        int actorId,
        long timestampMilliseconds,
        int? exclusiveRecordedIndexLimit = null)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, SectionHeaderColor);
        ImGui.TextUnformatted("生效中的減傷");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        const string hotLabel = "HoT##ReplayShowHotEffects";
        var hotWidth = ImGui.GetFrameHeight() + ImGui.CalcTextSize("HoT").X;
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right - hotWidth));
        if (ImGui.Checkbox(hotLabel, ref this.showHotEffects))
        {
            this.saveShowHotEffectsSetting(this.showHotEffects);
        }
        ImGui.Spacing();

        Span<ActiveMitigationStatus> activeStatuses =
            stackalloc ActiveMitigationStatus[MaximumDisplayedMitigations];
        var events = replay.Timeline.Events;
        var activeCount = CollectActiveMitigations(
            events,
            actorId,
            replay.Scene.Actors,
            timestampMilliseconds,
            this.gameDataCatalog.StatusEffects,
            this.showHotEffects,
            activeStatuses,
            exclusiveRecordedIndexLimit);
        if (activeCount == 0)
        {
            DrawContextEmptyState(
                "##ReplayMitigationUnavailable",
                this.showHotEffects
                    ? "沒有記錄到生效中的減傷或 HoT。"
                    : "沒有記錄到生效中的減傷。");
            return;
        }

        // The tile grid reuses the empty state's framed subpanel so the section
        // keeps one container whether or not anything is active.
        var style = ImGui.GetStyle();
        var tileHeight = Scaled(MitigationIconSize)
            + (ImGui.GetTextLineHeight() * ContextBodyTextScale)
            + Scaled(2);
        var columns = ResolveMitigationGridColumnCount(
            ImGui.GetContentRegionAvail().X - (style.WindowPadding.X * 2) - 2,
            ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, SubpanelBackgroundColor);
        ImGui.PushStyleColor(ImGuiCol.Border, PanelBorderColor);
        if (ImGui.BeginChild(
                "##ReplayMitigationGrid",
                new Vector2(
                    0,
                    ResolveMitigationGridHeight(
                        activeCount,
                        columns,
                        tileHeight,
                        style.ItemSpacing.Y,
                        style.WindowPadding.Y)),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            for (var index = 0; index < activeCount; index++)
            {
                if (index > 0 && index % columns != 0)
                {
                    ImGui.SameLine(0, Scaled(MitigationTileGap));
                }

                var activeStatus = activeStatuses[index];
                float? remaining = null;
                if (TryResolveStatusRemainingSeconds(
                        events,
                        activeStatus.ActiveEventIndex,
                        timestampMilliseconds,
                        out var remainingSeconds,
                        exclusiveRecordedIndexLimit))
                {
                    remaining = remainingSeconds;
                }

                var statusParam = this.gameDataCatalog.HasStacks(activeStatus.StatusId)
                    ? events[activeStatus.ActiveEventIndex].ObservedEvent.StatusParam ?? 0
                    : (ushort)0;
                this.DrawMitigationStatusTile(
                    replay,
                    activeStatus.StatusId,
                    statusParam,
                    remaining,
                    activeStatus.TargetKind,
                    activeStatus.TargetActorId);
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    internal static int ResolveMitigationGridColumnCount(
        float availableWidth,
        float uiScale)
    {
        var tileWidth = ResolveScaledLength(MitigationTileWidth, uiScale);
        var tileGap = ResolveScaledLength(MitigationTileGap, uiScale);
        return Math.Max(
            1,
            (int)MathF.Floor(
                (Math.Max(0, availableWidth) + tileGap) / (tileWidth + tileGap)));
    }

    internal static float ResolveMitigationGridHeight(
        int tileCount,
        int columns,
        float tileHeight,
        float rowSpacing,
        float verticalPadding)
    {
        if (tileCount <= 0)
        {
            return 0;
        }

        var rows = ((tileCount - 1) / Math.Max(1, columns)) + 1;
        return (verticalPadding * 2)
            + (rows * tileHeight)
            + ((rows - 1) * rowSpacing);
    }

    internal static int CollectActiveMitigations(
        ReadOnlySpan<ReplayTimelineEntry> events,
        int playerActorId,
        ReadOnlySpan<ArenaActorMarker> actors,
        long timestampMilliseconds,
        ReplayStatusEffectDatabase statusEffects,
        bool showHealingOverTime,
        Span<ActiveMitigationStatus> destination,
        int? exclusiveRecordedIndexLimit = null)
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
            destination,
            exclusiveRecordedIndexLimit);
        foreach (ref readonly var marker in actors)
        {
            if (count == destination.Length)
            {
                break;
            }
            if (marker.Actor.StableActorId == playerActorId
                || !ShouldDrawEnemyVitals(marker))
            {
                continue;
            }

            count += CollectActiveMitigationsForActor(
                events,
                marker.Actor.StableActorId,
                timestampMilliseconds,
                MitigationTargetKind.Enemy,
                statusEffects,
                showHealingOverTime,
                destination[count..],
                exclusiveRecordedIndexLimit);
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
        Span<ActiveMitigationStatus> destination,
        int? exclusiveRecordedIndexLimit)
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
                || IsAtOrAfterRecordedIndexLimit(entry, exclusiveRecordedIndexLimit)
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
                targetKind,
                actorId);
            if (activeCount == destination.Length)
            {
                break;
            }
        }

        return activeCount;
    }
    private void DrawMitigationStatusTile(
        ReplaySession replay,
        uint statusId,
        ushort statusParam,
        float? remainingSeconds,
        MitigationTargetKind targetKind,
        int targetActorId)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight() * ContextBodyTextScale;
        var iconSize = Scaled(MitigationIconSize);
        var tileWidth = Scaled(MitigationTileWidth);
        var tileHeight = iconSize + lineHeight + Scaled(2);
        ImGui.Dummy(new Vector2(tileWidth, tileHeight));

        var iconOrigin = origin + new Vector2((tileWidth - iconSize) * 0.5f, 0);
        var iconMaximum = iconOrigin + new Vector2(iconSize);
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

        if (targetKind == MitigationTargetKind.Enemy)
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
        var durationFontSize = ImGui.GetFontSize() * ContextBodyTextScale;
        var durationWidth = ImGui.CalcTextSize(durationText).X * ContextBodyTextScale;
        drawList.AddText(
            ImGui.GetFont(),
            durationFontSize,
            new Vector2(
                origin.X + ((tileWidth - durationWidth) * 0.5f),
                iconMaximum.Y + Scaled(2)),
            ImGui.GetColorU32(new Vector4(0.72f, 0.76f, 0.84f, 1)),
            durationText);

        if (ImGui.IsItemHovered())
        {
            var target = targetKind == MitigationTargetKind.Enemy
                ? replay.TryGetActor(targetActorId, out var targetActor)
                    ? $"Enemy debuff · {ActorDisplayLabel(targetActor)}"
                    : $"Enemy debuff · Actor {targetActorId}"
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

    /// <summary>
    /// True when a recorded event is the Death transition or anything capture
    /// appended after it in the same sample, such as the status removals death
    /// causes. Recorded indices, not timestamps, separate them because both share
    /// the sample timestamp.
    /// </summary>
    internal static bool IsAtOrAfterRecordedIndexLimit(
        in ReplayTimelineEntry entry,
        int? exclusiveRecordedIndexLimit) =>
        exclusiveRecordedIndexLimit is { } limit
        && entry.OriginalRecordedIndex >= limit;


    /// <summary>
    /// Resolves a status countdown at <paramref name="timestampMilliseconds"/>.
    /// <paramref name="exclusiveRecordedIndexLimit"/> also hides the loss that the
    /// Death transition caused, so a legacy capture without recorded remaining time
    /// reports the duration as unavailable instead of deriving a near-zero countdown
    /// from a removal that only happened because the actor died.
    /// </summary>
    internal static bool TryResolveStatusRemainingSeconds(
        ReadOnlySpan<ReplayTimelineEntry> events,
        int activeEventIndex,
        long timestampMilliseconds,
        out float remainingSeconds,
        int? exclusiveRecordedIndexLimit = null)
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
            if (IsAtOrAfterRecordedIndexLimit(candidate, exclusiveRecordedIndexLimit)
                || candidateEvent.StableActorId != actorId
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
        var canvasLayout = ResolveArenaCanvasLayout(available);
        var canvasSize = canvasLayout.Size;
        var canvasRegionOrigin = ImGui.GetCursorScreenPos();
        var origin = canvasRegionOrigin + canvasLayout.Offset;
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##ReplayArena", new Vector2(canvasSize, canvasSize));

        var cursorAfterCanvas = canvasRegionOrigin
            + new Vector2(0, Math.Max(available.Y, canvasSize));
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

        var outside = ImGui.GetColorU32(new Vector4(0.010f, 0.014f, 0.022f, 1));
        var fieldColor = ImGui.GetColorU32(new Vector4(0.19f, 0.30f, 0.43f, 1));
        var border = ImGui.GetColorU32(new Vector4(0.72f, 0.78f, 0.88f, 1));
        drawList.AddRectFilled(origin, maximum, outside);
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
        var enemyHudTop = ResolveEnemyHudMetrics(arenaSize).TopInset;
        var hoveredActorStableId = isArenaHovered
            ? ResolveHoveredArenaActorStableId(
                scene.Actors,
                io.MousePos,
                arenaOrigin,
                arenaSize,
                this.arenaViewport)
            : null;
        for (var drawPriority = 0; drawPriority <= 2; drawPriority++)
        {
            foreach (ref readonly var actor in scene.Actors)
            {
                if (ResolveArenaActorDrawPriority(
                        actor.Actor.StableActorId,
                        this.selectedPlayerStableActorId,
                        hoveredActorStableId) != drawPriority)
                {
                    continue;
                }

                var position = ProjectToCanvas(
                    actor.Position,
                    arenaOrigin,
                    arenaSize,
                    this.arenaViewport);
                var opacity = actor.Actor.StableActorId == hoveredActorStableId
                    ? 1
                    : ResolveFocusedActorOpacity(
                        this.selectedPlayerStableActorId,
                        actor);
                var markerExtent = ResolveArenaActorMarkerExtent(actor);
                var iconDrawn = false;
                var classJobId = actor.Actor.ClassJobId;
                if (actor.Kind == ArenaActorMarkerKind.Player
                    && classJobId < this.jobIcons.Length
                    && this.jobIcons[(int)classJobId] is { } icon
                    && icon.TryGetWrap(out var texture, out _)
                    && texture is not null)
                {
                    var iconOffset = new Vector2(markerExtent);
                    var iconTint = actor.IsDead
                        ? ArenaDeadIconTint with { W = opacity }
                        : new Vector4(1, 1, 1, opacity);
                    drawList.AddImage(
                        texture.Handle,
                        position - iconOffset,
                        position + iconOffset,
                        Vector2.Zero,
                        Vector2.One,
                        ImGui.GetColorU32(iconTint));
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

                var isFocused =
                    actor.Actor.StableActorId == this.selectedPlayerStableActorId;
                if (isFocused)
                {
                    drawList.AddCircle(
                        position,
                        markerExtent + 5,
                        ImGui.GetColorU32(FocusAccentColor),
                        0,
                        3);
                }

                if (actor.IsDead)
                {
                    // Bright arena floors wash out a plain red cross, so the marker is
                    // stroked over a dark backing pass and reaches past the job icon.
                    var deadExtent = markerExtent + 4;
                    if (!isFocused)
                    {
                        drawList.AddCircle(
                            position,
                            deadExtent,
                            ImGui.GetColorU32(ArenaDeathCrossColor with { W = opacity }),
                            0,
                            2);
                    }

                    DrawArenaDeathCross(
                        drawList,
                        position,
                        deadExtent,
                        ImGui.GetColorU32(
                            ArenaOutlineColor with { W = ArenaOutlineColor.W * opacity }),
                        6);
                    DrawArenaDeathCross(
                        drawList,
                        position,
                        deadExtent,
                        ImGui.GetColorU32(ArenaDeathCrossColor with { W = opacity }),
                        3);
                }
            }
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

        for (var actorIndex = 0; actorIndex < scene.Actors.Length; actorIndex++)
        {
            ref readonly var actor = ref scene.Actors[actorIndex];
            if (ResolveArenaActorDrawPriority(
                    actor.Actor.StableActorId,
                    this.selectedPlayerStableActorId,
                    hoveredActorStableId) != 0
                || !ShouldDrawArenaActorLabel(
                    scene.Actors,
                    actorIndex,
                    arenaOrigin,
                    arenaSize,
                    this.arenaViewport,
                    this.selectedPlayerStableActorId,
                    hoveredActorStableId))
            {
                continue;
            }

            DrawArenaActorLabel(
                drawList,
                actor,
                ProjectToCanvas(
                    actor.Position,
                    arenaOrigin,
                    arenaSize,
                    this.arenaViewport),
                ResolveFocusedActorOpacity(this.selectedPlayerStableActorId, actor),
                emphasized: false);
        }

        if (hoveredActorStableId is { } hoveredId
            && hoveredId != this.selectedPlayerStableActorId
            && TryFindActorMarker(scene.Actors, hoveredId, out var hoveredActor))
        {
            DrawArenaActorLabel(
                drawList,
                hoveredActor,
                ProjectToCanvas(
                    hoveredActor.Position,
                    arenaOrigin,
                    arenaSize,
                    this.arenaViewport),
                1,
                emphasized: true);
        }

        if (this.selectedPlayerStableActorId is { } selectedId
            && TryFindActorMarker(scene.Actors, selectedId, out var selectedActor))
        {
            DrawArenaActorLabel(
                drawList,
                selectedActor,
                ProjectToCanvas(
                    selectedActor.Position,
                    arenaOrigin,
                    arenaSize,
                    this.arenaViewport),
                1,
                emphasized: true);
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
            new Vector2(hpLabelPosition.X - 6, layout.HeaderPosition.Y + layout.TextHeight),
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
            new Vector2(castTimePosition.X - 6, layout.CastHeaderPosition.Y + layout.TextHeight),
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

    internal static string ResolvePlaybackButtonLabel(bool isPlaying) =>
        isPlaying ? "Pause##ReplayPlay" : "Play##ReplayPlay";

    private void DrawBottomTimeline(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, PanelRounding);
        if (ImGui.Button("« 1s##ReplayBack"))
        {
            replay.Seek(replay.CurrentTimeMilliseconds - 1_000);
            replay.Pause();
        }

        ImGui.SameLine();
        if (ImGui.Button(ResolvePlaybackButtonLabel(replay.IsPlaying)))
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
        if (ImGui.Button("1s »##ReplayForward"))
        {
            replay.Seek(replay.CurrentTimeMilliseconds + 1_000);
            replay.Pause();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(76);
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
        ImGui.SameLine(0, 14);
        this.DrawTimelineControl(replay, presentation);
        ImGui.PopStyleVar();

        ImGui.Separator();
        ImGui.PushStyleColor(ImGuiCol.Text, SectionHeaderColor);
        ImGui.TextUnformatted("死亡快速跳轉");
        ImGui.PopStyleColor();
        const string guidance = "點選群組，以選擇特定玩家與時間點。";
        var guidanceWidth = ImGui.CalcTextSize(guidance).X;
        ImGui.SameLine();
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right - guidanceWidth));
        ImGui.TextColored(SecondaryTextColor, guidance);
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
        var duration = replay.DurationMilliseconds;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var height = MathF.Max(ImGui.GetFrameHeight(), TimelineTrackHeight);
        ImGui.InvisibleButton("##ReplayTimeline", new Vector2(width, height));
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        var trackWidth = maximum.X - minimum.X;
        var centerY = (minimum.Y + maximum.Y) * 0.5f;
        var mouse = ImGui.GetIO().MousePos;

        // Death markers win the click; scrubbing only takes over when the press did
        // not start on one, so a drag that crosses a marker keeps seeking.
        var hoveredDeath = -1;
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var nearest = TimelineMarkerHitRadius;
            for (var index = 0; index < presentation.Deaths.Length; index++)
            {
                var markerX = ResolveTimelineMarkerX(
                    presentation.Deaths[index].Correlation.DeathTimestampMilliseconds,
                    duration,
                    minimum.X,
                    trackWidth);
                var distance = Math.Abs(mouse.X - markerX);
                if (distance <= nearest)
                {
                    nearest = distance;
                    hoveredDeath = index;
                }
            }
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            this.timelineMarkerCapture = false;
        }

        if (hoveredDeath >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            this.timelineMarkerCapture = true;
            this.SelectDeath(replay, presentation.Deaths[hoveredDeath]);
        }
        else if (ImGui.IsItemActive() && !this.timelineMarkerCapture && trackWidth > 0)
        {
            replay.Seek(
                (long)Math.Round(
                    Math.Clamp((mouse.X - minimum.X) / trackWidth, 0, 1) * duration));
        }

        var drawList = ImGui.GetWindowDrawList();
        var outlineColor = ImGui.GetColorU32(ArenaOutlineColor);
        var deathColor = ImGui.GetColorU32(TimelineDeathMarkerColor);
        var playheadColor = ImGui.GetColorU32(TimelinePlayheadColor);
        var playheadX = ResolveTimelineMarkerX(
            replay.CurrentTimeMilliseconds,
            duration,
            minimum.X,
            trackWidth);
        drawList.AddRectFilled(
            minimum,
            maximum,
            ImGui.GetColorU32(TimelineTrackColor),
            TimelineTrackRounding);
        if (playheadX > minimum.X)
        {
            drawList.AddRectFilled(
                minimum,
                new Vector2(playheadX, maximum.Y),
                ImGui.GetColorU32(TimelineProgressColor),
                TimelineTrackRounding,
                ImDrawFlags.RoundCornersLeft);
        }

        drawList.AddRect(
            minimum,
            maximum,
            ImGui.GetColorU32(PanelBorderColor),
            TimelineTrackRounding);

        var markerTop = minimum.Y - 4;
        var markerBottom = maximum.Y + 4;
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

            var x = ResolveTimelineMarkerX(timestamp, duration, minimum.X, trackWidth);
            var markerCenter = new Vector2(x, centerY);
            drawList.AddRectFilled(
                new Vector2(x - 3, markerTop),
                new Vector2(x + 3, markerBottom),
                outlineColor,
                2);
            drawList.AddRectFilled(
                new Vector2(x - 1.5f, markerTop),
                new Vector2(x + 1.5f, markerBottom),
                deathColor,
                1);
            var radius = groupCount > 1 ? 8f : 5.5f;
            drawList.AddCircleFilled(markerCenter, radius + 1.5f, outlineColor);
            drawList.AddCircleFilled(markerCenter, radius, deathColor);
            if (replay.CurrentTimeMilliseconds == timestamp)
            {
                drawList.AddCircle(markerCenter, radius + 3.5f, playheadColor, 0, 2);
            }

            if (groupCount > 1)
            {
                var count = groupCount.ToString(CultureInfo.InvariantCulture);
                var countSize = ImGui.CalcTextSize(count);
                drawList.AddText(
                    markerCenter - (countSize * 0.5f),
                    ImGui.GetColorU32(new Vector4(0.12f, 0.03f, 0.04f, 1)),
                    count);
            }

            if (hoveredDeath >= index && hoveredDeath < index + groupCount)
            {
                var job = JobIconResources.GetAbbreviation(death.Actor.ClassJobId) ?? "Player";
                ImGui.SetTooltip(
                    groupCount == 1
                        ? $"{FormatTimestamp(timestamp)}\n{job}"
                        : $"{FormatTimestamp(timestamp)}\n{groupCount} party deaths");
            }

            index += groupCount;
        }

        var playheadTop = minimum.Y - 6;
        var playheadBottom = maximum.Y + 6;
        drawList.AddRectFilled(
            new Vector2(playheadX - 3, playheadTop),
            new Vector2(playheadX + 3, playheadBottom),
            outlineColor,
            2);
        drawList.AddRectFilled(
            new Vector2(playheadX - 1.5f, playheadTop),
            new Vector2(playheadX + 1.5f, playheadBottom),
            playheadColor,
            1);
        drawList.AddTriangleFilled(
            new Vector2(playheadX - 6, playheadTop),
            new Vector2(playheadX + 6, playheadTop),
            new Vector2(playheadX, playheadTop + 7),
            playheadColor);
    }

    private void DrawDeathQuickJumps(
        ReplaySession replay,
        ReplayPresentationModel presentation)
    {
        if (presentation.Deaths.Length == 0)
        {
            ImGui.TextDisabled("目前沒有隊伍成員的死亡紀錄。");
            return;
        }

        var viewportHeight = Math.Max(
            DeathQuickJumpCardHeight + ImGui.GetStyle().ScrollbarSize,
            ImGui.GetContentRegionAvail().Y);
        if (ImGui.BeginChild(
                "##ReplayDeathQuickJumpScroller",
                new Vector2(0, viewportHeight),
                false,
                ImGuiWindowFlags.HorizontalScrollbar))
        {
            for (var clusterStart = 0; clusterStart < presentation.Deaths.Length;)
            {
                var clusterEnd = ResolveDeathQuickJumpClusterEnd(
                    presentation.Deaths,
                    clusterStart);
                this.DrawDeathQuickJumpClusterCard(
                    replay,
                    presentation.Deaths,
                    clusterStart,
                    clusterEnd);
                clusterStart = clusterEnd;
                if (clusterStart < presentation.Deaths.Length)
                {
                    ImGui.SameLine(0, DeathQuickJumpCardGap);
                }
            }
        }

        ImGui.EndChild();
    }

    internal static int ResolveDeathQuickJumpClusterEnd(
        ReadOnlySpan<ReplayDeathItem> deaths,
        int clusterStart)
    {
        if ((uint)clusterStart >= (uint)deaths.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(clusterStart));
        }

        var firstTimestamp = deaths[clusterStart].Correlation.DeathTimestampMilliseconds;
        var clusterEnd = clusterStart + 1;
        while (clusterEnd < deaths.Length
               && IsWithinDeathCluster(
                   firstTimestamp,
                   deaths[clusterEnd].Correlation.DeathTimestampMilliseconds))
        {
            clusterEnd++;
        }

        return clusterEnd;
    }

    /// <summary>
    /// One shared grouping rule for every death cluster presentation. Callers keep
    /// their own typed loop so no surface has to materialize a timestamp collection
    /// per frame.
    /// </summary>
    internal static bool IsWithinDeathCluster(
        long firstTimestampMilliseconds,
        long candidateTimestampMilliseconds) =>
        candidateTimestampMilliseconds - firstTimestampMilliseconds
            is >= 0 and <= DeathQuickJumpClusterWindowMilliseconds;

    private void DrawDeathQuickJumpClusterCard(
        ReplaySession replay,
        ReplayDeathItem[] deaths,
        int clusterStart,
        int clusterEnd)
    {
        var count = clusterEnd - clusterStart;
        if (count == 1)
        {
            this.DrawDeathQuickJumpSingleCard(replay, deaths[clusterStart]);
            return;
        }

        var firstDeath = deaths[clusterStart];
        var firstTimestamp = firstDeath.Correlation.DeathTimestampMilliseconds;
        var lastTimestamp = deaths[clusterEnd - 1].Correlation.DeathTimestampMilliseconds;
        var selected = false;
        for (var index = clusterStart; index < clusterEnd; index++)
        {
            var death = deaths[index];
            if (this.selectedPlayerStableActorId == death.Actor.StableActorId
                && replay.CurrentTimeMilliseconds
                    == death.Correlation.DeathTimestampMilliseconds)
            {
                selected = true;
                break;
            }
        }

        var popupId =
            $"##ReplayDeathClusterPopup{firstDeath.Correlation.DeathOriginalRecordedIndex}";
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(
            $"##ReplayDeathClusterCard{firstDeath.Correlation.DeathOriginalRecordedIndex}",
            new Vector2(DeathQuickJumpClusterCardWidth, DeathQuickJumpCardHeight));
        var hovered = ImGui.IsItemHovered();
        var clicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var maximum = origin
            + new Vector2(DeathQuickJumpClusterCardWidth, DeathQuickJumpCardHeight);
        var drawList = ImGui.GetWindowDrawList();
        var backgroundColor = selected
            ? DeathQuickJumpCardSelectedColor
            : hovered
                ? DeathQuickJumpCardHoverColor
                : DeathQuickJumpCardColor;
        drawList.AddRectFilled(
            origin,
            maximum,
            ImGui.GetColorU32(backgroundColor),
            DeathQuickJumpCardRounding);
        drawList.AddRect(
            origin,
            maximum,
            ImGui.GetColorU32(
                selected
                    ? DeathQuickJumpCardSelectedBorderColor
                    : DeathQuickJumpCardBorderColor),
            DeathQuickJumpCardRounding,
            ImDrawFlags.None,
            selected ? 2 : 1);
        drawList.AddRectFilled(
            origin,
            new Vector2(origin.X + 3, maximum.Y),
            ImGui.GetColorU32(DeathQuickJumpCardAccentColor),
            DeathQuickJumpCardRounding,
            ImDrawFlags.RoundCornersLeft);

        var lineHeight = ImGui.GetTextLineHeight();
        var iconCenter = origin + new Vector2(26, DeathQuickJumpCardHeight * 0.5f);
        drawList.AddCircleFilled(
            iconCenter,
            15,
            ImGui.GetColorU32(new Vector4(0.23f, 0.09f, 0.11f, 1)));
        var skullSize = ImGui.GetFontSize() * 0.9f;
        drawList.AddText(
            UiBuilder.IconFont,
            skullSize,
            iconCenter - new Vector2(skullSize * 0.42f, skullSize * 0.5f),
            ImGui.GetColorU32(DeathQuickJumpCardAccentColor),
            SkullIcon);
        var textOrigin = origin + new Vector2(50, DeathQuickJumpCardPadding - 1);
        var title = FormatDeathQuickJumpClusterTitle(count);
        drawList.AddText(
            textOrigin,
            ImGui.GetColorU32(count == 8 ? DeathColor : Vector4.One),
            title);

        var timestampText =
            $"{FormatTimestamp(firstTimestamp)}–{FormatTimestamp(lastTimestamp)}";
        drawList.AddText(
            textOrigin + new Vector2(0, lineHeight),
            ImGui.GetColorU32(SecondaryTextColor),
            timestampText);
        var badgeRadius = 12f;
        var badgeCenter = new Vector2(maximum.X - badgeRadius - 9, iconCenter.Y);
        drawList.AddCircleFilled(
            badgeCenter,
            badgeRadius,
            ImGui.GetColorU32(new Vector4(0.17f, 0.20f, 0.25f, 1)));
        var countText = count.ToString(CultureInfo.InvariantCulture);
        var countSize = ImGui.CalcTextSize(countText);
        drawList.AddText(
            badgeCenter - (countSize * 0.5f),
            ImGui.GetColorU32(Vector4.One),
            countText);

        if (hovered)
        {
            ImGui.SetTooltip(
                $"{timestampText}\nClick to choose one of {count} player deaths.");
        }

        if (clicked)
        {
            ImGui.OpenPopup(popupId);
        }



        ImGui.SetNextWindowSizeConstraints(
            new Vector2(250, 0),
            new Vector2(360, float.MaxValue));
        if (ImGui.BeginPopup(popupId))
        {
            ImGui.TextUnformatted(title);
            ImGui.TextDisabled(timestampText);
            ImGui.Separator();
            for (var index = clusterStart; index < clusterEnd; index++)
            {
                this.DrawDeathQuickJumpMenuItem(replay, deaths[index]);
            }

            ImGui.EndPopup();
        }
    }

    internal static string FormatDeathQuickJumpClusterTitle(int count)
    {
        if (count < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return count == 8 ? "WIPED" : $"{count} DEATHS";
    }

    private void DrawDeathQuickJumpSingleCard(
        ReplaySession replay,
        in ReplayDeathItem death)
    {
        var timestamp = death.Correlation.DeathTimestampMilliseconds;
        var selected = this.selectedPlayerStableActorId == death.Actor.StableActorId
            && replay.CurrentTimeMilliseconds == timestamp;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(
            $"##ReplayDeathQuick{death.Correlation.DeathOriginalRecordedIndex}",
            new Vector2(DeathQuickJumpSingleCardWidth, DeathQuickJumpCardHeight));
        var hovered = ImGui.IsItemHovered();
        var clicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var maximum = origin
            + new Vector2(DeathQuickJumpSingleCardWidth, DeathQuickJumpCardHeight);
        var drawList = ImGui.GetWindowDrawList();
        var backgroundColor = selected
            ? DeathQuickJumpCardSelectedColor
            : hovered
                ? DeathQuickJumpCardHoverColor
                : DeathQuickJumpCardColor;
        drawList.AddRectFilled(
            origin,
            maximum,
            ImGui.GetColorU32(backgroundColor),
            DeathQuickJumpCardRounding);
        drawList.AddRect(
            origin,
            maximum,
            ImGui.GetColorU32(
                selected
                    ? DeathQuickJumpCardSelectedBorderColor
                    : DeathQuickJumpCardBorderColor),
            DeathQuickJumpCardRounding,
            ImDrawFlags.None,
            selected ? 2 : 1);
        drawList.AddRectFilled(
            origin,
            new Vector2(origin.X + 3, maximum.Y),
            ImGui.GetColorU32(DeathQuickJumpCardAccentColor),
            DeathQuickJumpCardRounding,
            ImDrawFlags.RoundCornersLeft);

        var contentHeight = Math.Max(
            DeathQuickJumpJobIconSize,
            ImGui.GetTextLineHeight() * 2);
        var contentOrigin = new Vector2(
            origin.X + DeathQuickJumpCardPadding,
            origin.Y + ((DeathQuickJumpCardHeight - contentHeight) * 0.5f));
        var iconOrigin = new Vector2(
            contentOrigin.X,
            contentOrigin.Y + ((contentHeight - DeathQuickJumpJobIconSize) * 0.5f));
        if (death.Actor.ClassJobId < this.jobIcons.Length
            && this.jobIcons[(int)death.Actor.ClassJobId] is { } icon
            && icon.TryGetWrap(out var texture, out _)
            && texture is not null)
        {
            drawList.AddImage(
                texture.Handle,
                iconOrigin,
                iconOrigin + new Vector2(DeathQuickJumpJobIconSize));
        }

        var textX = contentOrigin.X + DeathQuickJumpJobIconSize + 7;
        var job = JobIconResources.GetAbbreviation(death.Actor.ClassJobId) ?? "Player";
        drawList.AddText(
            new Vector2(textX, contentOrigin.Y - 1),
            ImGui.GetColorU32(new Vector4(0.96f, 0.96f, 0.98f, 1)),
            job);
        drawList.AddText(
            new Vector2(textX, contentOrigin.Y + ImGui.GetTextLineHeight()),
            ImGui.GetColorU32(new Vector4(0.67f, 0.69f, 0.76f, 1)),
            FormatTimestamp(timestamp));

        if (hovered)
        {
            ImGui.SetTooltip($"{job}\n{FormatTimestamp(timestamp)}");
        }

        if (clicked)
        {
            this.SelectDeath(replay, death);
        }
    }


    private void DrawDeathQuickJumpMenuItem(
        ReplaySession replay,
        in ReplayDeathItem death)
    {
        var timestamp = death.Correlation.DeathTimestampMilliseconds;
        var selected = this.selectedPlayerStableActorId == death.Actor.StableActorId
            && replay.CurrentTimeMilliseconds == timestamp;
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        if (ImGui.Selectable(
                $"##ReplayDeathClusterItem{death.Correlation.DeathOriginalRecordedIndex}",
                selected,
                ImGuiSelectableFlags.None,
                new Vector2(0, 32)))
        {
            this.SelectDeath(replay, death);
            ImGui.CloseCurrentPopup();
        }

        var iconOrigin = origin + new Vector2(5, 4);
        if (death.Actor.ClassJobId < this.jobIcons.Length
            && this.jobIcons[(int)death.Actor.ClassJobId] is { } icon
            && icon.TryGetWrap(out var texture, out _)
            && texture is not null)
        {
            ImGui.GetWindowDrawList().AddImage(
                texture.Handle,
                iconOrigin,
                iconOrigin + new Vector2(DeathQuickJumpJobIconSize));
        }

        var job = JobIconResources.GetAbbreviation(death.Actor.ClassJobId) ?? "Player";
        var textY = origin.Y + ((32 - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.GetWindowDrawList().AddText(
            new Vector2(iconOrigin.X + DeathQuickJumpJobIconSize + 7, textY),
            ImGui.GetColorU32(new Vector4(0.96f, 0.96f, 0.98f, 1)),
            job);
        var timestampText = FormatTimestamp(timestamp);
        var timestampWidth = ImGui.CalcTextSize(timestampText).X;
        ImGui.GetWindowDrawList().AddText(
            new Vector2(origin.X + width - timestampWidth - 7, textY),
            ImGui.GetColorU32(new Vector4(0.72f, 0.74f, 0.80f, 1)),
            timestampText);
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
            $"Developer/Test Capture ({path})",
            $"已載入 Developer/Test Capture：{path}。");
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
                "目前沒有已擷取的紀錄。");
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

    internal static ArenaCanvasLayout ResolveArenaCanvasLayout(Vector2 available)
    {
        if (!float.IsFinite(available.X) || !float.IsFinite(available.Y))
        {
            throw new ArgumentOutOfRangeException(
                nameof(available),
                available,
                "Arena canvas availability must be finite.");
        }

        var width = Math.Max(0, available.X);
        var height = Math.Max(0, available.Y);
        var constrainedHeight = height > 100 ? height : width;
        var size = MathF.Max(280, MathF.Min(width, constrainedHeight));
        return new ArenaCanvasLayout(
            size,
            new Vector2(
                Math.Max(0, (width - size) * 0.5f),
                Math.Max(0, (height - size) * 0.5f)));
    }

    internal static float ResolveArenaActorMarkerExtent(
        in ArenaActorMarker actor) =>
        actor.Kind == ArenaActorMarkerKind.Player ? PlayerIconHalfSize : 7;

    private static void DrawArenaDeathCross(
        ImDrawListPtr drawList,
        Vector2 position,
        float extent,
        uint color,
        float thickness)
    {
        drawList.AddLine(
            position + new Vector2(-extent, -extent),
            position + new Vector2(extent, extent),
            color,
            thickness);
        drawList.AddLine(
            position + new Vector2(-extent, extent),
            position + new Vector2(extent, -extent),
            color,
            thickness);
    }

    internal static float ResolveTimelineMarkerX(
        long timestampMilliseconds,
        long durationMilliseconds,
        float originX,
        float trackWidth)
    {
        if (durationMilliseconds <= 0 || trackWidth <= 0)
        {
            return originX;
        }

        var amount = Math.Clamp(
            timestampMilliseconds / (float)durationMilliseconds,
            0,
            1);
        return originX + (trackWidth * amount);
    }

    internal static int ResolveArenaActorDrawPriority(
        int actorStableId,
        int? selectedActorStableId,
        int? hoveredActorStableId)
    {
        if (actorStableId == selectedActorStableId)
        {
            return 2;
        }

        return actorStableId == hoveredActorStableId ? 1 : 0;
    }

    internal static int? ResolveHoveredArenaActorStableId(
        ReadOnlySpan<ArenaActorMarker> actors,
        Vector2 point,
        Vector2 arenaMinimum,
        float arenaSize,
        ArenaViewport viewport)
    {
        int? resolved = null;
        var nearestDistanceSquared = float.MaxValue;
        foreach (ref readonly var actor in actors)
        {
            var markerPosition = ProjectToCanvas(
                actor.Position,
                arenaMinimum,
                arenaSize,
                viewport);
            var hitRadius = ResolveArenaActorMarkerExtent(actor) + 5;
            var distanceSquared = Vector2.DistanceSquared(point, markerPosition);
            if (distanceSquared <= hitRadius * hitRadius
                && distanceSquared < nearestDistanceSquared)
            {
                resolved = actor.Actor.StableActorId;
                nearestDistanceSquared = distanceSquared;
            }
        }

        return resolved;
    }

    internal static bool ShouldDrawArenaActorLabel(
        ReadOnlySpan<ArenaActorMarker> actors,
        int actorIndex,
        Vector2 arenaMinimum,
        float arenaSize,
        ArenaViewport viewport,
        int? selectedActorStableId,
        int? hoveredActorStableId)
    {
        if ((uint)actorIndex >= (uint)actors.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(actorIndex));
        }

        ref readonly var actor = ref actors[actorIndex];
        if (ResolveArenaActorDrawPriority(
                actor.Actor.StableActorId,
                selectedActorStableId,
                hoveredActorStableId) > 0)
        {
            return true;
        }

        var markerPosition = ProjectToCanvas(
            actor.Position,
            arenaMinimum,
            arenaSize,
            viewport);
        var overlapDistanceSquared =
            ArenaActorLabelOverlapDistance * ArenaActorLabelOverlapDistance;
        for (var otherIndex = 0; otherIndex < actors.Length; otherIndex++)
        {
            if (otherIndex == actorIndex)
            {
                continue;
            }

            var otherPosition = ProjectToCanvas(
                actors[otherIndex].Position,
                arenaMinimum,
                arenaSize,
                viewport);
            if (Vector2.DistanceSquared(markerPosition, otherPosition)
                < overlapDistanceSquared)
            {
                return false;
            }
        }

        return true;
    }

    private static void DrawArenaActorLabel(
        ImDrawListPtr drawList,
        in ArenaActorMarker actor,
        Vector2 markerPosition,
        float opacity,
        bool emphasized)
    {
        var labelPosition = markerPosition
            + new Vector2(ResolveArenaActorMarkerExtent(actor) + 3, -8);
        var label = ActorLabel(actor);
        var alpha = emphasized ? 1 : Math.Max(0.35f, opacity);
        drawList.AddText(
            labelPosition + Vector2.One,
            ImGui.GetColorU32(new Vector4(0, 0, 0, alpha * 0.9f)),
            label);
        drawList.AddText(
            labelPosition,
            ImGui.GetColorU32(new Vector4(1, 1, 1, alpha)),
            label);
    }

    internal static EnemyHudMetrics ResolveEnemyHudMetrics(float arenaWidth)
    {
        if (!float.IsFinite(arenaWidth) || arenaWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arenaWidth));
        }

        var compact = arenaWidth < EnemyHudCompactArenaWidthThreshold;
        return compact
            ? new EnemyHudMetrics(
                EnemyHudCompactWidth,
                EnemyHudCompactTextHeight,
                EnemyHudCompactBarHeight,
                EnemyHudCompactRowGap,
                EnemyHudCompactGroupGap,
                EnemyHudCompactHorizontalInset,
                EnemyHudCompactTopInset,
                true)
            : new EnemyHudMetrics(
                EnemyHudWidth,
                EnemyHudTextHeight,
                EnemyHudBarHeight,
                EnemyHudRowGap,
                EnemyHudGroupGap,
                EnemyHudHorizontalInset,
                EnemyHudTopInset,
                false);
    }

    internal static EnemyHudLayout ResolveEnemyHudLayout(
        Vector2 arenaMinimum,
        Vector2 arenaMaximum,
        float topOffset,
        bool hasActiveCast)
    {
        var arenaWidth = arenaMaximum.X - arenaMinimum.X;
        var metrics = ResolveEnemyHudMetrics(arenaWidth);
        if (!float.IsFinite(arenaMinimum.X)
            || !float.IsFinite(arenaMinimum.Y)
            || !float.IsFinite(arenaMaximum.X)
            || !float.IsFinite(arenaMaximum.Y)
            || !float.IsFinite(topOffset)
            || topOffset < 0
            || arenaWidth <= metrics.HorizontalInset * 2
            || arenaMaximum.Y <= arenaMinimum.Y)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arenaMaximum),
                arenaMaximum,
                "Enemy HUD geometry must be finite and fit inside the Arena.");
        }

        var width = MathF.Min(
            metrics.Width,
            arenaWidth - (metrics.HorizontalInset * 2));
        var headerPosition = arenaMinimum
            + new Vector2(metrics.HorizontalInset, topOffset);
        var healthBarMinimum = headerPosition
            + new Vector2(0, metrics.TextHeight + metrics.RowGap);
        var healthBarMaximum = healthBarMinimum
            + new Vector2(width, metrics.BarHeight);
        var castHeaderPosition = new Vector2(
            healthBarMinimum.X,
            healthBarMaximum.Y + metrics.RowGap);
        var castBarMinimum = castHeaderPosition
            + new Vector2(0, metrics.TextHeight + metrics.RowGap);
        var castBarMaximum = castBarMinimum
            + new Vector2(width, metrics.BarHeight);
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
            (contentBottom - arenaMinimum.Y) + metrics.GroupGap,
            metrics.TextHeight,
            metrics.IsCompact);
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
            var arenaWidth = arenaMaximum.X - arenaMinimum.X;
            var metrics = ResolveEnemyHudMetrics(arenaWidth);
            var hudWidth = MathF.Min(
                metrics.Width,
                arenaWidth - (metrics.HorizontalInset * 2));
            var hudMinimum = arenaMinimum
                + new Vector2(
                    metrics.HorizontalInset - 6,
                    metrics.TopInset - 4);
            var hudMaximum = new Vector2(
                arenaMinimum.X + metrics.HorizontalInset + hudWidth + 6,
                arenaMinimum.Y
                    + enemyHudNextTop
                    - metrics.GroupGap
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
            var markerExtent = ResolveArenaActorMarkerExtent(actor);
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
