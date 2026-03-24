# Keycloak JWT tool RBAC

This guide shows how to run OpenClaw.NET behind Keycloak with:

- gateway JWT bearer validation
- tool-level RBAC based on JWT scopes and roles
- a public-bind configuration that stays compatible with the existing ASP.NET Core JWT path

## What this enables

When `OpenClaw:Security:Jwt:Enabled=true`, the gateway validates bearer tokens issued by your identity provider.

When `OpenClaw:Security:ToolAuthorization:Enabled=true`, OpenClaw.NET can also restrict individual tools based on the authenticated caller's JWT claims.

Current behavior:

- tool RBAC is enforced for sessions that carry an authenticated JWT caller context
- if a matching tool rule is not satisfied, the tool call is denied before execution
- non-JWT channels keep their current behavior unless you explicitly route them through JWT-authenticated entry points

## OpenClaw gateway config template

Use this as a starting point for `appsettings.json` or an environment-specific overlay:

```json
{
  "OpenClaw": {
    "BindAddress": "0.0.0.0",
    "Port": 18789,
    "Security": {
      "AllowQueryStringToken": false,
      "Jwt": {
        "Enabled": true,
        "Authority": "https://keycloak.example.com/realms/openclaw",
        "Audience": "openclaw-gateway",
        "ValidIssuer": "https://keycloak.example.com/realms/openclaw",
        "RequireHttpsMetadata": true,
        "SigningKeyRef": null
      },
      "ToolAuthorization": {
        "Enabled": true,
        "DefaultPolicy": "deny",
        "ScopeClaimTypes": [ "scope", "scp" ],
        "RoleClaimTypes": [ "role", "roles", "realm_access", "resource_access" ],
        "Rules": [
          {
            "Tool": "shell",
            "AllowedScopes": [ "tools.shell" ],
            "AllowedRoles": [ "openclaw-shell" ]
          },
          {
            "Tool": "write_*",
            "AllowedScopes": [ "tools.write" ],
            "AllowedRoles": [ "openclaw-editor" ]
          },
          {
            "Tool": "browser*",
            "AllowedScopes": [ "tools.browser" ],
            "AllowedRoles": [ "openclaw-browser" ]
          },
          {
            "Tool": "read_*",
            "AllowedScopes": [ "tools.read" ],
            "AllowedRoles": [ "openclaw-reader" ]
          }
        ]
      }
    },
    "Tooling": {
      "RequireToolApproval": true,
      "ApprovalRequiredTools": [ "shell", "write_file" ]
    }
  }
}
```

## Recommended Keycloak realm layout

### Realm

- Realm name: `openclaw`
- Realm URL: `https://keycloak.example.com/realms/openclaw`

### Client for the gateway audience

Create a client named `openclaw-gateway`:

- Client authentication: `Off` for browser/public PKCE clients, `On` for confidential machine clients
- Authorization: optional
- Standard flow: `Enabled`
- Direct access grants: `Disabled` unless you explicitly need them
- Valid redirect URIs: your app-specific frontends
- Web origins: explicit frontend origins only

Use the client ID as the JWT `aud` value consumed by OpenClaw.NET.

### Realm roles

Create realm roles that represent coarse permissions:

- `openclaw-reader`
- `openclaw-editor`
- `openclaw-browser`
- `openclaw-shell`
- `openclaw-admin`

Keycloak emits realm roles under `realm_access.roles`. OpenClaw.NET extracts those values when `RoleClaimTypes` includes `realm_access`.

### Optional client roles

If you prefer client roles, define them under a client such as `openclaw-web` or `openclaw-automation`.

Keycloak emits client roles under `resource_access.<client>.roles`. OpenClaw.NET extracts those values when `RoleClaimTypes` includes `resource_access`.

### Optional scopes

If you prefer scopes for tool control, add audience/scopes such as:

- `tools.read`
- `tools.write`
- `tools.browser`
- `tools.shell`

OpenClaw.NET reads `scope` and `scp` by default. A space-delimited `scope` claim works as-is.

## Keycloak mapper guidance

Typical mapper choices:

1. **Audience mapper**
   - ensure `openclaw-gateway` appears in `aud`
2. **Realm roles mapper**
   - keep realm roles in the token
3. **Client roles mapper**
   - keep client roles in the token when you use client-role-based RBAC
4. **Scope mapper**
   - include your tool scopes in `scope`

If you use custom claim names, update:

- `OpenClaw:Security:ToolAuthorization:ScopeClaimTypes`
- `OpenClaw:Security:ToolAuthorization:RoleClaimTypes`

## RBAC rule semantics

Rules are matched by `Tool` using OpenClaw's glob matcher.

Examples:

- `shell` matches only `shell`
- `write_*` matches `write_file`
- `browser*` matches browser-related tools
- `*` matches every tool

Evaluation model:

1. if tool RBAC is disabled, the request proceeds
2. if the session has no authenticated JWT context, the request keeps existing behavior
3. matching rules are collected for the tool
4. if any matching rule is satisfied, the tool is allowed
5. if rules match but none are satisfied, the tool is denied
6. if no rules match, `DefaultPolicy` decides the result (`allow` or `deny`)

Each rule is an AND across configured dimensions:

- `AllowedScopes` means at least one configured scope must be present
- `AllowedRoles` means at least one configured role must be present
- when both are configured, the token must satisfy both the scope side and the role side

## Example policies

### Read-only chat users

```json
{
  "Tool": "read_*",
  "AllowedScopes": [ "tools.read" ],
  "AllowedRoles": [ "openclaw-reader" ]
}
```

### Editors can write but not shell

```json
{
  "Tool": "write_*",
  "AllowedRoles": [ "openclaw-editor" ]
}
```

### Shell requires both a scope and a privileged role

```json
{
  "Tool": "shell",
  "AllowedScopes": [ "tools.shell" ],
  "AllowedRoles": [ "openclaw-shell" ]
}
```

## Machine-to-machine clients

For automation, use Keycloak client credentials with a confidential client.

Recommended pattern:

1. create a confidential client such as `openclaw-automation`
2. assign service account roles or scopes required for the exact tools it may invoke
3. request a short-lived access token from Keycloak
4. call OpenClaw.NET with `Authorization: Bearer <token>`

## Deployment checklist

- enable HTTPS for Keycloak and the reverse proxy in front of OpenClaw.NET
- keep `RequireHttpsMetadata=true` outside local development
- set `DefaultPolicy=deny` for public or semi-public deployments
- keep `RequireToolApproval=true` for dangerous tools even when RBAC is enabled
- restrict `AllowedReadRoots` and `AllowedWriteRoots`
- disable local shell on public bind unless you intentionally support it
- test at least one allow case and one deny case for every privileged tool family
- verify the actual token contents with Keycloak before assuming a role or scope is present

## Troubleshooting

### Token authenticates but tools are denied

Check:

- `aud` matches `OpenClaw:Security:Jwt:Audience`
- the token contains the expected `scope`, `realm_access`, or `resource_access` claims
- the tool name matches the configured `Tool` glob
- `DefaultPolicy` is not unexpectedly set to `deny`

### Roles are missing from OpenClaw decisions

Check Keycloak mappers and ensure roles are present in the access token, not only the ID token.

### NativeAOT note

This RBAC path is configuration-driven and avoids adding reflection-heavy custom authorization logic to the tool execution path. The gateway still relies on ASP.NET Core JWT bearer validation, so production builds should continue to validate the exact publish profile you ship.
