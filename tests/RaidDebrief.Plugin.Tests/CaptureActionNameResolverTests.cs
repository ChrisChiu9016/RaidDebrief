using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class CaptureActionNameResolverTests
{
    [Fact]
    public void StaticExcelNameWinsOverUiObservation()
    {
        var result = CaptureActionNameResolver.Resolve(
            1_234,
            "Static Action",
            wasResolvedAtStartup: true,
            clientRsvName: null,
            "UI Action",
            "English");

        var actionName = Assert.IsType<RecordedActionName>(result);
        Assert.Equal("Static Action", actionName.Name);
        Assert.Equal(ActionNameSource.StaticExcel, actionName.Source);
    }

    [Fact]
    public void NewlyResolvedLuminaNameIsRecordedAsRuntimeRsv()
    {
        var result = CaptureActionNameResolver.Resolve(
            49_890,
            "Runtime Boss Cast",
            wasResolvedAtStartup: false,
            clientRsvName: null,
            "UI Action",
            "Japanese");

        var actionName = Assert.IsType<RecordedActionName>(result);
        Assert.Equal("Runtime Boss Cast", actionName.Name);
        Assert.Equal("Japanese", actionName.Language);
        Assert.Equal(ActionNameSource.RuntimeRsv, actionName.Source);
    }
    [Fact]
    public void ClientRsvResolutionWinsOverEnemyCastBar()
    {
        var result = CaptureActionNameResolver.Resolve(
            49_890,
            "_rsv_49890_-1_1_0_0",
            wasResolvedAtStartup: false,
            clientRsvName: "Client-resolved Boss Cast",
            "Observed Boss Cast",
            "English");

        var actionName = Assert.IsType<RecordedActionName>(result);
        Assert.Equal("Client-resolved Boss Cast", actionName.Name);
        Assert.Equal(ActionNameSource.RuntimeRsv, actionName.Source);
    }


    [Fact]
    public void EnemyCastBarIsFallbackForReservedLuminaName()
    {
        var result = CaptureActionNameResolver.Resolve(
            49_890,
            "_rsv_49890_-1_1_0_0",
            wasResolvedAtStartup: false,
            clientRsvName: null,
            "Observed Boss Cast",
            "English");

        var actionName = Assert.IsType<RecordedActionName>(result);
        Assert.Equal("Observed Boss Cast", actionName.Name);
        Assert.Equal(ActionNameSource.UiObserved, actionName.Source);
    }

    [Fact]
    public void UnresolvedSourcesProduceNoSnapshot()
    {
        Assert.Null(
            CaptureActionNameResolver.Resolve(
                49_890,
                "_rsv_49890_-1_1_0_0",
                wasResolvedAtStartup: false,
                clientRsvName: null,
                " ",
                "English"));
    }
}
