using OpenClaw.Core.A2UI;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Plan step 6 — coverage for the unified OpenClaw:A2UI overlay onto legacy keys.
/// </summary>
public sealed class A2UIConfigMigrationTests
{
    [Fact]
    public void Overlay_WhenUnifiedEnabledSet_OverridesBothLegacyKeys()
    {
        var config = new GatewayConfig
        {
            Canvas = new CanvasConfig { Enabled = true },
            Channels = new ChannelsConfig { A2UI = new A2UIChannelConfig { Enabled = false } },
            A2UI = new A2UIConfig { Enabled = false }
        };

        var legacy = A2UIConfigMigration.ApplyOverlay(config);

        Assert.False(config.Canvas.Enabled);
        Assert.False(config.Channels.A2UI.Enabled);
        Assert.Empty(legacy);
    }

    [Fact]
    public void Overlay_WhenUnifiedUnset_KeepsLegacyAndReportsDeprecation()
    {
        // Channels.A2UI.Enabled defaults to false; flipping it to true forces the legacy path.
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig { A2UI = new A2UIChannelConfig { Enabled = true } }
        };

        var legacy = A2UIConfigMigration.ApplyOverlay(config);

        Assert.True(config.Channels.A2UI.Enabled);
        Assert.Contains("OpenClaw:Channels:A2UI:Enabled", legacy);
    }

    [Fact]
    public void Overlay_ConnectionFieldsApplyToChannelsA2UI()
    {
        var config = new GatewayConfig
        {
            A2UI = new A2UIConfig
            {
                Connection = new A2UIConnectionConfig
                {
                    MaxConnections = 7,
                    MaxConnectionsPerIp = 3,
                    MessagesPerMinutePerConnection = 42,
                    ReceiveTimeoutSeconds = 99,
                    MaxMessageBytes = 4096
                }
            }
        };

        A2UIConfigMigration.ApplyOverlay(config);

        Assert.Equal(7, config.Channels.A2UI.MaxConnections);
        Assert.Equal(3, config.Channels.A2UI.MaxConnectionsPerIp);
        Assert.Equal(42, config.Channels.A2UI.MessagesPerMinutePerConnection);
        Assert.Equal(99, config.Channels.A2UI.ReceiveTimeoutSeconds);
        Assert.Equal(4096, config.Channels.A2UI.MaxMessageBytes);
    }

    [Fact]
    public void Overlay_FrameFieldsApplyToCanvasAndChannels()
    {
        var config = new GatewayConfig
        {
            A2UI = new A2UIConfig
            {
                Frames = new A2UIFramesConfig
                {
                    MaxFramesPerPush = 200,
                    MaxBytes = 1_000_000,
                    MaxInstructionBytes = 64_000
                }
            }
        };

        A2UIConfigMigration.ApplyOverlay(config);

        Assert.Equal(200, config.Canvas.MaxFramesPerPush);
        Assert.Equal(1_000_000, config.Canvas.MaxCommandBytes);
        Assert.Equal(64_000, config.Channels.A2UI.MaxInstructionBytes);
    }

    [Fact]
    public void Overlay_ComponentFieldsApplyToChannelsA2UI()
    {
        var config = new GatewayConfig
        {
            A2UI = new A2UIConfig
            {
                Components = new A2UIComponentsConfig
                {
                    AllowedTypes = ["text", "card"],
                    AllowAny = true
                }
            }
        };

        A2UIConfigMigration.ApplyOverlay(config);

        Assert.Equal(new[] { "text", "card" }, config.Channels.A2UI.AllowedComponentTypes);
        Assert.True(config.Channels.A2UI.AllowAnyComponentType);
    }

    [Fact]
    public void Overlay_InsForgeOverridesLegacyBlock()
    {
        var config = new GatewayConfig
        {
            InsForge = new InsForgeConfig { Enabled = true, Endpoint = "old" },
            A2UI = new A2UIConfig
            {
                InsForge = new InsForgeConfig { Enabled = true, Endpoint = "new" }
            }
        };

        A2UIConfigMigration.ApplyOverlay(config);

        Assert.Equal("new", config.InsForge.Endpoint);
    }

    [Fact]
    public void Overlay_PublicBindGateUnification()
    {
        var config = new GatewayConfig
        {
            Canvas = new CanvasConfig { AllowOnPublicBind = false },
            A2UI = new A2UIConfig { AllowOnPublicBind = true }
        };

        A2UIConfigMigration.ApplyOverlay(config);

        Assert.True(config.Canvas.AllowOnPublicBind);
    }
}
