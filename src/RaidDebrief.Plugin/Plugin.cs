using System;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RaidDebrief.Plugin;

internal sealed class PluginCommandRouter
{
    private readonly Action openReplay;

    public PluginCommandRouter(Action openReplay)
    {
        this.openReplay = openReplay ?? throw new ArgumentNullException(nameof(openReplay));
    }

    public void Execute(string arguments) =>
        this.openReplay();
}


public sealed class Plugin : IDalamudPlugin
{
    private static readonly string[] CommandNames = ["/rdebrief", "/rdb"];
    internal static ReadOnlySpan<string> RegisteredCommandNames => CommandNames;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;
    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static ICondition Condition { get; private set; } = null!;

    [PluginService]
    internal static IPartyList PartyList { get; private set; } = null!;

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static IDutyState DutyState { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService]
    internal static IGameGui GameGui { get; private set; } = null!;


    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;


    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("RaidDebrief");
    private readonly PluginConfiguration configuration;
    private readonly WaymarkReader waymarkReader;
    private readonly TargetMarkerReader targetMarkerReader;
    private readonly BattleNpcOmnidirectionalityCatalog omnidirectionalityCatalog;
    private readonly CaptureActionNameResolver captureActionNameResolver;
    private readonly DutyRunTracker dutyRunTracker;
    private readonly PullHistoryStore pullHistoryStore;
    private readonly CaptureService captureService;
    private readonly ActionEffectReader actionEffectReader;
    private readonly LiveDataProbe liveDataProbe;
    private readonly ProbeWindow probeWindow;
    private readonly ReplayWindow replayWindow;
    private readonly HistoryWindow historyWindow;
    private readonly DebriefSummaryWindow debriefSummaryWindow;
    private readonly PluginCommandRouter commandRouter;
    private readonly bool[] registeredCommands = new bool[CommandNames.Length];

    public Plugin()
    {
        this.configuration = PluginInterface.GetPluginConfig() as PluginConfiguration
            ?? new PluginConfiguration();
        var pluginConfigurationDirectory = PluginInterface.GetPluginConfigDirectory();
        this.dutyRunTracker = new DutyRunTracker();
        this.pullHistoryStore = new PullHistoryStore(pluginConfigurationDirectory, Log);
        this.waymarkReader = new WaymarkReader(Log);
        this.targetMarkerReader = new TargetMarkerReader(Log);
        this.omnidirectionalityCatalog = new BattleNpcOmnidirectionalityCatalog(DataManager, Log);
        this.captureActionNameResolver = new CaptureActionNameResolver(DataManager, GameGui, Log);
        this.captureService = new CaptureService(
            pluginConfigurationDirectory,
            DutyState,
            Log,
            this.waymarkReader,
            this.targetMarkerReader,
            this.configuration.AutomaticCaptureEnabled,
            this.SaveAutomaticCaptureSetting,
            resolveActionName: this.captureActionNameResolver.TryResolve,
            beginDutyPull: this.dutyRunTracker.BeginPull,
            archiveAutomaticPull: record =>
            {
                this.pullHistoryStore.TryEnqueue(record);
            });
        this.actionEffectReader = new ActionEffectReader(
            GameInteropProvider,
            Log,
            this.captureService);
        ClientState.ZoneInit += this.OnZoneInit;
        this.liveDataProbe = new LiveDataProbe(
            Framework,
            Condition,
            ClientState,
            DutyState,
            PartyList,
            ObjectTable,
            Log,
            this.captureService,
            this.dutyRunTracker,
            this.omnidirectionalityCatalog);
        this.probeWindow = new ProbeWindow(
            this.liveDataProbe,
            this.captureService,
            this.actionEffectReader,
            this.OpenReplayUi);
        this.replayWindow = new ReplayWindow(
            this.captureService,
            this.pullHistoryStore,
            this.OpenHistoryUi,
            TextureProvider,
            DataManager,
            this.omnidirectionalityCatalog,
            this.configuration.ShowHotEffects,
            this.SaveShowHotEffectsSetting,
            this.configuration.ShowPostWipeDebrief,
            this.SavePostWipeDebriefSetting,
            this.configuration.CloseReplayOnCombatStart,
            this.SaveCloseReplayOnCombatStartSetting,
            this.configuration.DeveloperModeEnabled,
            this.SaveDeveloperModeSetting,
            this.probeWindow.DrawEmbedded,
            this.probeWindow.SetEmbeddedVisible,
            PluginInterface.GetPluginConfigDirectory(),
            PluginInterface.AssemblyLocation.FullName);
        this.historyWindow = new HistoryWindow(
            this.pullHistoryStore,
            this.replayWindow.OpenHistoryEntry,
            this.replayWindow.GetHistoryReplayState);
        this.debriefSummaryWindow =
            new DebriefSummaryWindow(this.OpenDebriefReplay, TextureProvider);
        this.commandRouter = new PluginCommandRouter(this.OpenReplayUi);
        this.windowSystem.AddWindow(this.replayWindow);
        this.windowSystem.AddWindow(this.historyWindow);
        this.windowSystem.AddWindow(this.debriefSummaryWindow);

        for (var index = 0; index < CommandNames.Length; index++)
        {
            var commandName = CommandNames[index];
            var registered = CommandManager.AddHandler(commandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "開啟 Replay 主畫面並切回 Replay 分頁。",
            });
            this.registeredCommands[index] = registered;
            if (registered)
            {
                Log.Information("Raid Debrief registered command {CommandName}.", commandName);
            }
            else
            {
                Log.Warning(
                    "Raid Debrief could not register command {CommandName}; another handler may already own it.",
                    commandName);
            }
        }

        PluginInterface.UiBuilder.Draw += this.DrawUi;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenReplayUi;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenReplayUi;

        Log.Information("Raid Debrief in-game Replay and Debrief windows loaded.");
    }

    public void Dispose()
    {
        ClientState.ZoneInit -= this.OnZoneInit;
        PluginInterface.UiBuilder.Draw -= this.DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenReplayUi;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenReplayUi;
        for (var index = 0; index < CommandNames.Length; index++)
        {
            if (this.registeredCommands[index])
            {
                CommandManager.RemoveHandler(CommandNames[index]);
            }
        }

        this.windowSystem.RemoveAllWindows();
        this.replayWindow.Dispose();
        this.probeWindow.Dispose();
        this.liveDataProbe.Dispose();
        this.actionEffectReader.Dispose();
        this.captureService.Dispose();
        this.pullHistoryStore.Dispose();
    }

    private void SaveAutomaticCaptureSetting(bool enabled)
    {
        this.configuration.AutomaticCaptureEnabled = enabled;
        PluginInterface.SavePluginConfig(this.configuration);
    }

    private void SavePostWipeDebriefSetting(bool enabled)
    {
        this.configuration.ShowPostWipeDebrief = enabled;
        PluginInterface.SavePluginConfig(this.configuration);
    }

    private void SaveCloseReplayOnCombatStartSetting(bool enabled)
    {
        this.configuration.CloseReplayOnCombatStart = enabled;
        PluginInterface.SavePluginConfig(this.configuration);
    }

    private void SaveShowHotEffectsSetting(bool enabled)
    {
        this.configuration.ShowHotEffects = enabled;
        PluginInterface.SavePluginConfig(this.configuration);
    }

    private void SaveDeveloperModeSetting(bool enabled)
    {
        this.configuration.DeveloperModeEnabled = enabled;
        PluginInterface.SavePluginConfig(this.configuration);
    }

    private void OnZoneInit(ZoneInitEventArgs args)
    {
        var contentFinderConditionId = args.ContentFinderCondition.RowId;
        var dutyName = contentFinderConditionId != 0 && args.ContentFinderCondition.IsValid
            ? args.ContentFinderCondition.Value.Name.ToString()
            : null;
        this.dutyRunTracker.ObserveZoneInitialized(
            args.TerritoryType.RowId,
            contentFinderConditionId,
            dutyName);
    }
    private void DrawUi()
    {
        var inCombat = this.liveDataProbe.InCombat;
        this.replayWindow.UpdateUiState(
            inCombat,
            this.configuration.CloseReplayOnCombatStart);
        this.debriefSummaryWindow.Update(
            this.captureService.GetReplaySourceSnapshot(),
            inCombat,
            this.configuration.ShowPostWipeDebrief);
        this.windowSystem.Draw();
    }


    private void OnCommand(string command, string arguments) =>
        this.commandRouter.Execute(arguments);

    private void OpenReplayUi() =>
        this.replayWindow.OpenRuntime(this.liveDataProbe.InCombat);

    private void OpenHistoryUi() =>
        this.historyWindow.IsOpen = true;

    private void OpenDebriefReplay(DebriefReplayRequest request) =>
        this.replayWindow.OpenDebriefReplay(request, this.liveDataProbe.InCombat);
}
