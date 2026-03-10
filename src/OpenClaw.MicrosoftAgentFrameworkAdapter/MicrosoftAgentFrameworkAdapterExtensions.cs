using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public static class MicrosoftAgentFrameworkAdapterExtensions
{
    /// <summary>
    /// Registers Microsoft Agent Framework interop options.
    /// This does not automatically add tools to OpenClaw; hosts decide which tools to expose.
    /// </summary>
    public static IServiceCollection AddMicrosoftAgentFrameworkInterop(
        this IServiceCollection services,
        Action<MicrosoftAgentFrameworkInteropOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        return services;
    }

    /// <summary>
    /// Creates the MAF entrypoint tool.
    /// </summary>
    public static MicrosoftAgentFrameworkEntrypointTool CreateEntrypointTool(
        IMicrosoftAgentFrameworkRunner runner,
        MicrosoftAgentFrameworkInteropOptions? options = null)
        => new(runner, options);

    /// <summary>
    /// Binds interop options from configuration and creates the MAF entrypoint tool.
    /// </summary>
    public static MicrosoftAgentFrameworkEntrypointTool CreateEntrypointToolFromConfig(
        IConfiguration config,
        IMicrosoftAgentFrameworkRunner runner,
        string sectionName = "OpenClaw:MicrosoftAgentFramework")
    {
        var options = config.GetSection(sectionName).Get<MicrosoftAgentFrameworkInteropOptions>() ?? new MicrosoftAgentFrameworkInteropOptions();
        return new MicrosoftAgentFrameworkEntrypointTool(runner, options);
    }
}
