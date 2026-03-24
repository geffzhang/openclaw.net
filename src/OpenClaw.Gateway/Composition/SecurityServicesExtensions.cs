using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Composition;

internal static class SecurityServicesExtensions
{
    public static IServiceCollection AddOpenClawSecurityServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        ConfigureAuthentication(services, startup);

        services.AddSingleton<ToolApprovalService>();
        services.AddSingleton(sp =>
            new PairingManager(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PairingManager>>()));
        services.AddSingleton(sp => new BrowserSessionAuthService(startup.Config));
        services.AddSingleton(sp =>
            new AdminSettingsService(
                startup.Config,
                AdminSettingsService.CreateSnapshot(startup.Config),
                AdminSettingsService.GetSettingsPath(startup.Config),
                sp.GetRequiredService<ILogger<AdminSettingsService>>()));
        services.AddSingleton(sp =>
            new ApprovalAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ApprovalAuditStore>>()));
        services.AddSingleton(sp =>
            new RuntimeEventStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<RuntimeEventStore>>()));
        services.AddSingleton(sp =>
            new OperatorAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<OperatorAuditStore>>()));
        services.AddSingleton(sp =>
            new ToolApprovalGrantStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ToolApprovalGrantStore>>()));
        services.AddSingleton(sp =>
            new WebhookDeliveryStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<WebhookDeliveryStore>>()));
        services.AddSingleton(sp =>
            new PluginHealthService(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PluginHealthService>>()));
        services.AddSingleton(sp =>
            new ContractStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ContractStore>>()));
        services.AddSingleton(sp =>
            new ContractGovernanceService(
                startup,
                sp.GetRequiredService<ContractStore>(),
                sp.GetRequiredService<RuntimeEventStore>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.ProviderUsageTracker>(),
                sp.GetRequiredService<ILogger<ContractGovernanceService>>()));

        return services;
    }

    private static void ConfigureAuthentication(IServiceCollection services, GatewayStartupContext startup)
    {
        var jwt = startup.Config.Security.Jwt;
        if (GatewaySecurity.IsJwtAuthenticationEnabled(startup.Config.Security))
        {
            var auth = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
            auth.AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = jwt.RequireHttpsMetadata;
                options.SaveToken = false;

                if (!string.IsNullOrWhiteSpace(jwt.Authority))
                    options.Authority = jwt.Authority;

                if (!string.IsNullOrWhiteSpace(jwt.MetadataAddress))
                    options.MetadataAddress = jwt.MetadataAddress;

                if (!string.IsNullOrWhiteSpace(jwt.Audience))
                    options.Audience = jwt.Audience;

                options.TokenValidationParameters = CreateTokenValidationParameters(jwt);
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (string.IsNullOrWhiteSpace(context.Token))
                        {
                            context.Token = GatewaySecurity.GetToken(
                                context.HttpContext,
                                startup.Config.Security.AllowQueryStringToken);
                        }

                        return Task.CompletedTask;
                    }
                };
            });
        }
        else
        {
            services.AddAuthentication();
        }

        services.AddAuthorization();
    }

    private static TokenValidationParameters CreateTokenValidationParameters(JwtSecurityConfig jwt)
    {
        var validIssuers = Merge(jwt.ValidIssuer, jwt.ValidIssuers);
        var validAudiences = Merge(jwt.Audience, jwt.ValidAudiences);
        var signingKey = GatewaySecurity.ResolveJwtSigningKey(jwt);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = validIssuers.Length > 0 ||
                !string.IsNullOrWhiteSpace(jwt.Authority) ||
                !string.IsNullOrWhiteSpace(jwt.MetadataAddress),
            ValidateAudience = validAudiences.Length > 0,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "sub"
        };

        if (validIssuers.Length > 0)
        {
            parameters.ValidIssuers = validIssuers;
            parameters.ValidIssuer = validIssuers[0];
        }

        if (validAudiences.Length > 0)
        {
            parameters.ValidAudiences = validAudiences;
            parameters.ValidAudience = validAudiences[0];
        }

        if (!string.IsNullOrWhiteSpace(signingKey))
            parameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));

        return parameters;
    }

    private static string[] Merge(string? primary, string[] additional)
    {
        var values = new List<string>(additional.Length + 1);
        if (!string.IsNullOrWhiteSpace(primary))
            values.Add(primary);

        foreach (var value in additional)
        {
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }

        return values.Distinct(StringComparer.Ordinal).ToArray();
    }
}
