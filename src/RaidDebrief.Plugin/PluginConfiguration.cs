using System;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace RaidDebrief.Plugin;

[Serializable]
internal sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool AutomaticCaptureEnabled { get; set; }

    [JsonProperty("ShowWipeReplayPrompt")]
    public bool ShowPostWipeDebrief { get; set; } = true;

    public bool CloseReplayOnCombatStart { get; set; } = true;

    public bool ShowHotEffects { get; set; }
}
