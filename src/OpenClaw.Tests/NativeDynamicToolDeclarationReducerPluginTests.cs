using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using Xunit;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Tests;

public sealed class NativeDynamicToolDeclarationReducerPluginTests
{
    [Fact]
    public void PluginContext_CanRegisterToolDeclarationReducer()
    {
        var context = NativeDynamicPluginHost.CreateTestRegistrationContext("semantic-reducer", NullLogger.Instance);
        var reducer = new PassThroughReducer();

        context.RegisterToolDeclarationReducer(reducer);

        Assert.Same(reducer, Assert.Single(context.ToolDeclarationReducers));
    }

    [Fact]
    public void SelectToolDeclarationReducer_SemanticMode_PrefersPluginReducer()
    {
        var ruleReducer = new PassThroughReducer();
        var semanticReducer = new PassThroughReducer();

        var selected = RuntimeInitializationExtensions.SelectToolDeclarationReducer(
            new ToolDeclarationReductionConfig
            {
                Enabled = true,
                Mode = "semantic",
                FallbackToRuleWhenSemanticUnavailable = true
            },
            ruleReducer,
            [semanticReducer],
            NullLogger.Instance);

        Assert.Same(semanticReducer, selected);
    }

    [Fact]
    public void SelectToolDeclarationReducer_WhenSemanticPluginMissingAndFallbackDisabled_ReturnsNull()
    {
        var selected = RuntimeInitializationExtensions.SelectToolDeclarationReducer(
            new ToolDeclarationReductionConfig
            {
                Enabled = true,
                Mode = "semantic",
                FallbackToRuleWhenSemanticUnavailable = false
            },
            new PassThroughReducer(),
            [],
            NullLogger.Instance);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectToolDeclarationReducer_RuleMode_ReturnsRuleReducer()
    {
        var ruleReducer = new PassThroughReducer();

        var selected = RuntimeInitializationExtensions.SelectToolDeclarationReducer(
            new ToolDeclarationReductionConfig
            {
                Enabled = true,
                Mode = "rule"
            },
            ruleReducer,
            [],
            NullLogger.Instance);

        Assert.Same(ruleReducer, selected);
    }

    private sealed class PassThroughReducer : IToolDeclarationReducer
    {
        public ValueTask<ToolDeclarationReductionResult> ReduceAsync(ToolDeclarationReductionContext context, CancellationToken ct)
            => ValueTask.FromResult(new ToolDeclarationReductionResult
            {
                Tools = context.CandidateTools,
                Diagnostics = new ToolDeclarationReductionDiagnostics
                {
                    Enabled = true,
                    Mode = "test",
                    CandidateCount = context.CandidateTools.Count,
                    SelectedCount = context.CandidateTools.Count,
                    MaxTools = context.Config.MaxTools,
                    HardMaxTools = context.Config.HardMaxTools,
                    SelectedTools = context.CandidateTools.Select(static tool => tool.Name).ToArray()
                }
            });
    }
}