using OpenClaw.PluginKit;

namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic;

public sealed class SemanticToolDeclarationReductionPlugin : INativeDynamicPlugin
{
    public void Register(INativeDynamicPluginContext context)
    {
        context.RegisterToolDeclarationReducer(new SemanticToolDeclarationReducer(context.Logger));
    }
}