using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolDeclarationReducerContractTests
{
    [Fact]
    public void Request_DefaultsAreEmptyAndNotTurnRoutingProbe()
    {
        var request = new ToolDeclarationReductionRequest();

        Assert.Empty(request.RecentToolNames);
        Assert.Empty(request.RecentToolFailures);
        Assert.False(request.IsTurnRoutingProbe);
    }

    [Fact]
    public void Result_CarriesToolsAndDiagnostics()
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var tool = AIFunctionFactory.CreateDeclaration(
            "read_file",
            "Read a file",
            schema.RootElement.Clone(),
            returnJsonSchema: null);
        var diagnostics = new ToolDeclarationReductionDiagnostics
        {
            Enabled = true,
            Mode = "rule",
            CandidateCount = 5,
            SelectedCount = 1,
            MaxTools = 16,
            HardMaxTools = 24,
            PresetId = "coding",
            SelectedTools = ["read_file"]
        };

        var result = new ToolDeclarationReductionResult
        {
            Tools = [tool],
            Diagnostics = diagnostics
        };

        Assert.Equal("read_file", result.Tools[0].Name);
        Assert.Equal("rule", result.Diagnostics.Mode);
        Assert.Equal(["read_file"], result.Diagnostics.SelectedTools);
    }
}