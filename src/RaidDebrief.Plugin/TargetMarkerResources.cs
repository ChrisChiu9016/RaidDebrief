using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal static class TargetMarkerResources
{
    private const string ResourcePrefix = "RaidDebrief.TargetMarkers.";

    public static string GetManifestResourceName(TargetMarkerId id)
    {
        var fileName = id == TargetMarkerId.Plus ? "plus" : id.ToString();
        return $"{ResourcePrefix}{fileName}.png";
    }
}
