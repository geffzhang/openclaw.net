using System.Text;
using Microsoft.Extensions.AI;

namespace OpenClaw.Agent.ToolDeclarations;

internal static class ToolDeclarationText
{
    public static string Build(AITool tool)
    {
        var builder = new StringBuilder();
        builder.Append(tool.Name);
        builder.Append(' ');
        builder.Append(tool.Description);
        var declaration = tool as AIFunctionDeclaration ?? tool.GetService<AIFunctionDeclaration>();
        if (declaration?.JsonSchema is not null)
        {
            builder.Append(' ');
            builder.Append(declaration.JsonSchema.ToString());
        }

        return builder.ToString();
    }
}