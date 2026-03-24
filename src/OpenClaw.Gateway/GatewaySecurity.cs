using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal static class GatewaySecurity
{
    public static bool IsLoopbackBind(string bindAddress)
    {
        if (IPAddress.TryParse(bindAddress, out var ip))
            return IPAddress.IsLoopback(ip);

        return string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    public static string? GetBearerToken(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var auth))
            return null;

        var value = auth.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = value[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public static string? GetToken(HttpContext ctx, bool allowQueryStringToken)
    {
        var token = GetBearerToken(ctx);
        if (!string.IsNullOrEmpty(token))
            return token;

        if (!allowQueryStringToken)
            return null;

        return ctx.Request.Query["token"].FirstOrDefault();
    }

    public static bool IsJwtAuthenticationEnabled(SecurityConfig security)
        => security.Jwt.Enabled;

    public static bool IsAuthenticated(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true;

    public static string? GetAuthenticatedSubject(ClaimsPrincipal? principal)
    {
        if (!IsAuthenticated(principal))
            return null;

        return principal!.FindFirst("sub")?.Value
            ?? principal.FindFirst("client_id")?.Value
            ?? principal.Identity?.Name;
    }

    public static string? ResolveJwtSigningKey(JwtSecurityConfig config)
        => SecretResolver.Resolve(config.SigningKeyRef);

    public static SessionAuthContext? CreateSessionAuthContext(ClaimsPrincipal? principal, SecurityConfig security)
    {
        if (!IsAuthenticated(principal))
            return null;

        var subject = GetAuthenticatedSubject(principal);
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        var toolAuth = security.ToolAuthorization;
        var scopes = ExtractClaimValues(principal!, toolAuth.ScopeClaimTypes);
        var roles = ExtractClaimValues(principal!, toolAuth.RoleClaimTypes);

        return new SessionAuthContext
        {
            Subject = subject,
            DisplayName = principal!.Identity?.Name,
            IsAuthenticated = true,
            AuthMode = "bearer",
            Scopes = scopes,
            Roles = roles
        };
    }

    public static bool IsTokenValid(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided))
            return false;

        // FixedTimeEquals handles different-length spans in constant time (returns false).
        // An explicit length check would leak timing info about the expected token length.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }

    public static string ComputeHmacSha256Hex(string secret, string payload)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hashBytes = HMACSHA256.HashData(secretBytes, payloadBytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    public static bool IsHmacSha256SignatureValid(string secret, ReadOnlySpan<byte> payload, string? providedSignature)
    {
        if (string.IsNullOrWhiteSpace(providedSignature))
            return false;

        var signature = providedSignature.Trim();
        const string prefix = "sha256=";
        if (signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            signature = signature[prefix.Length..].Trim();

        Span<byte> providedHash = stackalloc byte[32];
        if (!TryDecodeHex(signature, providedHash))
            return false;

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var expectedHash = HMACSHA256.HashData(secretBytes, payload);
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }

    public static bool IsHmacSha256SignatureValid(string secret, string payload, string? providedSignature)
        => IsHmacSha256SignatureValid(secret, Encoding.UTF8.GetBytes(payload), providedSignature);

    private static bool TryDecodeHex(string hex, Span<byte> destination)
    {
        if (hex.Length != destination.Length * 2)
            return false;

        for (var i = 0; i < destination.Length; i++)
        {
            var hi = DecodeHexNibble(hex[i * 2]);
            var lo = DecodeHexNibble(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0)
                return false;
            destination[i] = (byte)((hi << 4) | lo);
        }

        return true;
    }

    private static int DecodeHexNibble(char c)
    {
        if (c is >= '0' and <= '9')
            return c - '0';
        if (c is >= 'a' and <= 'f')
            return c - 'a' + 10;
        if (c is >= 'A' and <= 'F')
            return c - 'A' + 10;
        return -1;
    }

    private static string[] ExtractClaimValues(ClaimsPrincipal principal, string[] claimTypes)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claimType in claimTypes)
        {
            foreach (var claim in principal.Claims.Where(claim => string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase)))
                AddClaimValue(values, claim.Type, claim.Value);
        }

        return values.ToArray();
    }

    private static void AddClaimValue(HashSet<string> values, string claimType, string claimValue)
    {
        if (string.IsNullOrWhiteSpace(claimValue))
            return;

        if (TryAddKeycloakRoles(values, claimType, claimValue))
            return;

        if (claimValue.StartsWith("[", StringComparison.Ordinal) && TryAddJsonArrayValues(values, claimValue))
            return;

        foreach (var part in claimValue.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            values.Add(part);
    }

    private static bool TryAddKeycloakRoles(HashSet<string> values, string claimType, string claimValue)
    {
        if (!claimValue.StartsWith("{", StringComparison.Ordinal))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(claimValue);
            if (string.Equals(claimType, "realm_access", StringComparison.OrdinalIgnoreCase))
            {
                AddJsonArrayProperty(values, doc.RootElement, "roles");
                return true;
            }

            if (string.Equals(claimType, "resource_access", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var client in doc.RootElement.EnumerateObject())
                    AddJsonArrayProperty(values, client.Value, "roles");
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool TryAddJsonArrayValues(HashSet<string> values, string claimValue)
    {
        try
        {
            using var doc = JsonDocument.Parse(claimValue);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    values.Add(item.GetString()!);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddJsonArrayProperty(HashSet<string> values, JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var roles) || roles.ValueKind != JsonValueKind.Array)
            return;

        foreach (var role in roles.EnumerateArray())
        {
            if (role.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(role.GetString()))
                values.Add(role.GetString()!);
        }
    }
}
