using System;
using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace RaidDebrief.Plugin;

internal static class JobIconResources
{
    private static readonly uint[] SupportedIds =
    [
        19, // PLD
        20, // MNK
        21, // WAR
        22, // DRG
        23, // BRD
        24, // WHM
        25, // BLM
        27, // SMN
        28, // SCH
        30, // NIN
        31, // MCH
        32, // DRK
        33, // AST
        34, // SAM
        35, // RDM
        37, // GNB
        38, // DNC
        39, // RPR
        40, // SGE
        41, // VPR
        42, // PCT
    ];

    public static ReadOnlySpan<uint> SupportedClassJobIds => SupportedIds;

    public static ISharedImmediateTexture?[] LoadTextures(
        ITextureProvider textureProvider,
        Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(textureProvider);
        ArgumentNullException.ThrowIfNull(assembly);
        var icons = new ISharedImmediateTexture?[43];
        foreach (var classJobId in SupportedIds)
        {
            var resourceName = GetManifestResourceName(classJobId)
                ?? throw new InvalidOperationException(
                    $"Missing Job icon mapping for ClassJob {classJobId}.");
            icons[(int)classJobId] =
                textureProvider.GetFromManifestResource(assembly, resourceName);
        }

        return icons;
    }

    public static string? GetManifestResourceName(uint classJobId) => classJobId switch
    {
        19 => "RaidDebrief.JobIcons.PLD.png",
        20 => "RaidDebrief.JobIcons.MNK.png",
        21 => "RaidDebrief.JobIcons.WAR.png",
        22 => "RaidDebrief.JobIcons.DRG.png",
        23 => "RaidDebrief.JobIcons.BRD.png",
        24 => "RaidDebrief.JobIcons.WHm.png",
        25 => "RaidDebrief.JobIcons.BLM.png",
        27 => "RaidDebrief.JobIcons.SMN.png",
        28 => "RaidDebrief.JobIcons.SCH.png",
        30 => "RaidDebrief.JobIcons.NIN.png",
        31 => "RaidDebrief.JobIcons.MCH.png",
        32 => "RaidDebrief.JobIcons.DRK.png",
        33 => "RaidDebrief.JobIcons.AST.png",
        34 => "RaidDebrief.JobIcons.SAM.png",
        35 => "RaidDebrief.JobIcons.RDM.png",
        37 => "RaidDebrief.JobIcons.GNB.png",
        38 => "RaidDebrief.JobIcons.DNC.png",
        39 => "RaidDebrief.JobIcons.RPR.png",
        40 => "RaidDebrief.JobIcons.SGE.png",
        41 => "RaidDebrief.JobIcons.VPR.png",
        42 => "RaidDebrief.JobIcons.PCT.png",
        _ => null,
    };

    public static string? GetAbbreviation(uint classJobId) => classJobId switch
    {
        19 => "PLD",
        20 => "MNK",
        21 => "WAR",
        22 => "DRG",
        23 => "BRD",
        24 => "WHM",
        25 => "BLM",
        27 => "SMN",
        28 => "SCH",
        30 => "NIN",
        31 => "MCH",
        32 => "DRK",
        33 => "AST",
        34 => "SAM",
        35 => "RDM",
        37 => "GNB",
        38 => "DNC",
        39 => "RPR",
        40 => "SGE",
        41 => "VPR",
        42 => "PCT",
        _ => null,
    };
}
