using System.Text.Json;
using OpenClaw.Core.A2UI;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ComponentTypePolicyTests
{
    [Fact]
    public void Default_AllowsCanonicalElevenComponentTypes()
    {
        var policy = ComponentTypePolicy.Default;

        foreach (var type in ComponentTypePolicy.DefaultComponentTypes)
        {
            Assert.True(policy.IsAllowed(type), $"Default policy should allow '{type}'.");
        }
    }

    [Fact]
    public void Default_RejectsUnknownComponentType()
    {
        var policy = ComponentTypePolicy.Default;
        Assert.False(policy.IsAllowed("custom-widget"));
    }

    [Fact]
    public void FromConfig_EmptyAllowlist_FallsBackToDefaultDictionary()
    {
        var policy = ComponentTypePolicy.FromConfig(allowedComponentTypes: [], allowAny: false);

        Assert.False(policy.AllowsAny);
        Assert.True(policy.IsAllowed("button"));
        Assert.False(policy.IsAllowed("frobnicator"));
    }

    [Fact]
    public void FromConfig_NonEmptyAllowlist_OverridesDefaultDictionary()
    {
        var policy = ComponentTypePolicy.FromConfig(allowedComponentTypes: ["text"], allowAny: false);

        Assert.True(policy.IsAllowed("text"));
        // Default-only types must be rejected when an explicit allow-list is supplied.
        Assert.False(policy.IsAllowed("button"));
    }

    [Fact]
    public void FromConfig_AllowAny_PermitsArbitraryComponentTypes()
    {
        var policy = ComponentTypePolicy.FromConfig(allowedComponentTypes: ["text"], allowAny: true);

        Assert.True(policy.AllowsAny);
        Assert.True(policy.IsAllowed("anything-goes"));
    }

    [Fact]
    public void Accepts_NestedTreeRejectsForbiddenChildComponent()
    {
        var policy = ComponentTypePolicy.Default;
        using var doc = JsonDocument.Parse(
            """
            {
                "type": "card",
                "children": [
                    { "type": "text", "id": "a" },
                    { "type": "evil-script", "id": "b" }
                ]
            }
            """);

        Assert.False(policy.Accepts(doc.RootElement));
    }

    [Fact]
    public void Accepts_DeeplyNestedAllowedTreeIsAccepted()
    {
        var policy = ComponentTypePolicy.Default;
        using var doc = JsonDocument.Parse(
            """
            {
                "type": "card",
                "items": [
                    {
                        "type": "card",
                        "items": [
                            { "type": "text", "id": "leaf" }
                        ]
                    }
                ]
            }
            """);

        Assert.True(policy.Accepts(doc.RootElement));
    }

    [Fact]
    public void IsAllowed_RejectsTypeBeyondMaxLength()
    {
        var policy = ComponentTypePolicy.Default;
        var longType = new string('x', A2UIProtocolLimits.MaxComponentTypeLength + 1);

        Assert.False(policy.IsAllowed(longType));
    }
}
