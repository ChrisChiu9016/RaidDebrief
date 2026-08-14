using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RaidDebrief.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly string[] CommandNames = ["/rdebrief", "/rd"];
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
    private readonly CaptureService captureService;
    private readonly ActionEffectReader actionEffectReader;
    private readonly LiveDataProbe liveDataProbe;
    private readonly ProbeWindow probeWindow;
    private readonly ReplayWindow replayWindow;
    private readonly DebriefSummaryWindow debriefSummaryWindow;

    public Plugin()
    {
        this.configuration = PluginInterface.GetPluginConfig() as PluginConfiguration
            ?? new PluginConfiguration();
        this.waymarkReader = new WaymarkReader(Log);
        this.targetMarkerReader = new TargetMarkerReader(Log);
        this.omnidirectionalityCatalog = new BattleNpcOmnidirectionalityCatalog(DataManager, Log);
        this.captureActionNameResolver = new CaptureActionNameResolver(DataManager, GameGui, Log);
        this.captureService = new CaptureService(
            PluginInterface.GetPluginConfigDirectory(),
            DutyState,
            Log,
            this.waymarkReader,
            this.targetMarkerReader,
            this.configuration.AutomaticCaptureEnabled,
            this.SaveAutomaticCaptureSetting,
            resolveActionName: this.captureActionNameResolver.TryResolve);
        this.actionEffectReader = new ActionEffectReader(
            GameInteropProvider,
            Log,
            this.captureService);
        this.liveDataProbe = new LiveDataProbe(
            Framework,
            Condition,
            ClientState,
            PartyList,
            ObjectTable,
            Log,
            this.captureService,
            this.omnidirectionalityCatalog);
        this.replayWindow = new ReplayWindow(
            this.captureService,
            TextureProvider,
            DataManager,
            this.omnidirectionalityCatalog,
            this.configuration.ShowHotEffects,
            this.SaveShowHotEffectsSetting,
            PluginInterface.GetPluginConfigDirectory(),
            PluginInterface.AssemblyLocation.FullName);
        this.debriefSummaryWindow =
            new DebriefSummaryWindow(this.OpenDebriefReplay, TextureProvider);
        this.probeWindow = new ProbeWindow(
            this.liveDataProbe,
            this.captureService,
            this.actionEffectReader,
            this.OpenReplayUi,
            this.configuration.ShowPostWipeDebrief,
            this.SavePostWipeDebriefSetting,
            this.configuration.CloseReplayOnCombatStart,
            this.SaveCloseReplayOnCombatStartSetting);
        this.windowSystem.AddWindow(this.probeWindow);
        this.windowSystem.AddWindow(this.replayWindow);
        this.windowSystem.AddWindow(this.debriefSummaryWindow);

        foreach (var commandName in CommandNames)
        {
            CommandManager.AddHandler(commandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "開啟 Capture 視窗；使用 /rdebrief replay 或 /rd replay 開啟上一個 Pull 的 Replay。",
            });
        }

        PluginInterface.UiBuilder.Draw += this.DrawUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleMainUi;

        Log.Information("Raid Debrief in-game Replay and Debrief windows loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= this.DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleMainUi;
        foreach (var commandName in CommandNames)
        {
            CommandManager.RemoveHandler(commandName);
        }

        this.windowSystem.RemoveAllWindows();
        this.replayWindow.Dispose();
        this.probeWindow.Dispose();
        this.liveDataProbe.Dispose();
        this.actionEffectReader.Dispose();
        this.captureService.Dispose();
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


    private void OnCommand(string command, string arguments)
    {
        if (string.Equals(arguments.Trim(), "replay", StringComparison.OrdinalIgnoreCase))
        {
            this.OpenReplayUi();
            return;
        }

        this.ToggleMainUi();
    }

    private void ToggleMainUi() => this.probeWindow.Toggle();

    private void OpenReplayUi() =>
        this.replayWindow.OpenRuntime(this.liveDataProbe.InCombat);

    private void OpenDebriefReplay(DebriefReplayRequest request) =>
        this.replayWindow.OpenDebriefReplay(request, this.liveDataProbe.InCombat);
}
