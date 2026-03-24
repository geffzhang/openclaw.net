using OpenClaw.Core.Models;

namespace OpenClaw.Core.Security;

public readonly record struct ToolAuthorizationDecision(bool Allowed, string? FailureReason)
{
    public static ToolAuthorizationDecision Allow() => new(true, null);

    public static ToolAuthorizationDecision Deny(string reason) => new(false, reason);
}

public static class ToolAuthorizationPolicyEvaluator
{
    public static ToolAuthorizationDecision Evaluate(ToolAuthorizationConfig config, Session? session, string toolName)
    {
        if (!config.Enabled)
            return ToolAuthorizationDecision.Allow();

        var authContext = session?.AuthContext;
        if (authContext is null || !authContext.IsAuthenticated)
            return ToolAuthorizationDecision.Allow();

        var matchingRules = config.Rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Tool) && GlobMatcher.IsMatch(rule.Tool, toolName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingRules.Length == 0)
        {
            return IsDenyByDefault(config)
                ? ToolAuthorizationDecision.Deny($"Tool '{toolName}' is not allowed for the current JWT identity.")
                : ToolAuthorizationDecision.Allow();
        }

        foreach (var rule in matchingRules)
        {
            if (MatchesRule(rule, authContext))
                return ToolAuthorizationDecision.Allow();
        }

        return ToolAuthorizationDecision.Deny($"Tool '{toolName}' requires one of the configured JWT scopes or roles.");
    }

    public static bool IsDenyByDefault(ToolAuthorizationConfig config)
        => string.Equals(config.DefaultPolicy, "deny", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesRule(ToolAuthorizationRule rule, SessionAuthContext authContext)
    {
        var scopeMatch = rule.AllowedScopes.Length == 0 || rule.AllowedScopes.Any(scope => authContext.Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase));
        var roleMatch = rule.AllowedRoles.Length == 0 || rule.AllowedRoles.Any(role => authContext.Roles.Contains(role, StringComparer.OrdinalIgnoreCase));
        return scopeMatch && roleMatch;
    }
}
