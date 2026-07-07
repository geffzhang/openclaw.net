using System.Text.Json;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolDeclarationReductionConfigTests
{
    [Fact]
    public void Defaults_AreBackwardCompatibleAndRuleReady()
    {
        var config = new GatewayConfig();

        Assert.False(config.Tooling.DeclarationReduction.Enabled);
        Assert.Equal("rule", config.Tooling.DeclarationReduction.Mode);
        Assert.Equal(16, config.Tooling.DeclarationReduction.MaxTools);
        Assert.Equal(4, config.Tooling.DeclarationReduction.MinTools);
        Assert.Equal(24, config.Tooling.DeclarationReduction.HardMaxTools);
        Assert.Equal(0.10, config.Tooling.DeclarationReduction.MinScore);
        Assert.True(config.Tooling.DeclarationReduction.FallbackToPresetOnEmpty);
        Assert.True(config.Tooling.DeclarationReduction.FallbackToRuleWhenSemanticUnavailable);
        Assert.False(config.Tooling.DeclarationReduction.EnablePromptDistillation);
        Assert.Empty(config.Tooling.DeclarationReduction.AlwaysIncludeTools);
        Assert.Empty(config.Tooling.DeclarationReduction.NeverAutoIncludeTools);
    }

    [Fact]
    public void GatewayConfigJson_RoundTripsDeclarationReduction()
    {
        var config = new GatewayConfig();
        config.Tooling.DeclarationReduction.Enabled = true;
        config.Tooling.DeclarationReduction.MaxTools = 12;
        config.Tooling.DeclarationReduction.AlwaysIncludeTools = ["read_file"];

        var json = JsonSerializer.Serialize(config, CoreJsonContext.Default.GatewayConfig);
        var roundTripped = JsonSerializer.Deserialize(json, CoreJsonContext.Default.GatewayConfig)!;

        Assert.True(roundTripped.Tooling.DeclarationReduction.Enabled);
        Assert.Equal(12, roundTripped.Tooling.DeclarationReduction.MaxTools);
        Assert.Equal(["read_file"], roundTripped.Tooling.DeclarationReduction.AlwaysIncludeTools);
    }
}