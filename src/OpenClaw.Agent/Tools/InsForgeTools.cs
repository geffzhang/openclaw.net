using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

public abstract class InsForgeToolBase : ITool, IDisposable
{
    private readonly bool _ownsHttpClient;
    protected readonly InsForgeConfig Config;
    protected readonly HttpClient Http;

    protected InsForgeToolBase(InsForgeConfig config, HttpClient? httpClient = null)
    {
        Config = config;
        Http = httpClient ?? HttpClientFactory.Create();
        _ownsHttpClient = httpClient is null;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string ParameterSchema { get; }
    public abstract ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct);

    protected Uri? BuildUri(string path)
    {
        if (string.IsNullOrWhiteSpace(Config.Endpoint) ||
            !Uri.TryCreate(Config.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return new Uri(endpoint, path.StartsWith('/') ? path : "/" + path);
    }

    protected async Task<string> SendJsonAsync(HttpMethod method, Uri uri, JsonElement body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        var apiKey = SecretResolver.Resolve(Config.ApiKeyRef);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Config.TimeoutSeconds)));

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
            return $"Error: InsForge HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

        return responseText;
    }

    protected static bool TryParseArguments(string argumentsJson, out JsonDocument document, out string error)
    {
        document = null!;
        error = "";
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return true;
        }
        catch (JsonException ex)
        {
            error = "Error: Invalid JSON arguments — " + ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            Http.Dispose();
    }
}

public sealed class InsForgeQueryComponentTool : InsForgeToolBase
{
    public InsForgeQueryComponentTool(InsForgeConfig config, HttpClient? httpClient = null)
        : base(config, httpClient)
    {
    }

    public override string Name => "insforge_query_component";

    public override string Description =>
        "Query InsForge for an approved A2UI component template using semantic context.";

    public override string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Semantic UI need or business context" },
            "limit": { "type": "integer", "description": "Maximum templates to return" },
            "session_id": { "type": "string", "description": "Optional OpenClaw session id" }
          },
          "required": ["query"]
        }
        """;

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!TryParseArguments(argumentsJson, out var args, out var error))
            return error;

        using (args)
        {
            if (!args.RootElement.TryGetProperty("query", out var query) ||
                query.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(query.GetString()))
            {
                return "Error: 'query' is required.";
            }

            var uri = BuildUri(Config.ComponentQueryPath);
            return uri is null
                ? "Error: InsForge.Endpoint must be an absolute http(s) URL."
                : await SendJsonAsync(HttpMethod.Post, uri, args.RootElement, ct);
        }
    }
}

public sealed class InsForgeUpdateDataModelTool : InsForgeToolBase
{
    public InsForgeUpdateDataModelTool(InsForgeConfig config, HttpClient? httpClient = null)
        : base(config, httpClient)
    {
    }

    public override string Name => "insforge_update_datamodel";

    public override string Description =>
        "Write an A2UI session data-model update to InsForge JSONB storage.";

    public override string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "session_id": { "type": "string", "description": "OpenClaw or A2UI session id" },
            "path": { "type": "string", "description": "RFC 6901 JSON Pointer path to update" },
            "value": { "description": "JSON value to store at the path" }
          },
          "required": ["session_id", "path", "value"]
        }
        """;

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!TryParseArguments(argumentsJson, out var args, out var error))
            return error;

        using (args)
        {
            if (!args.RootElement.TryGetProperty("session_id", out var sessionId) ||
                sessionId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(sessionId.GetString()))
            {
                return "Error: 'session_id' is required.";
            }

            if (!args.RootElement.TryGetProperty("path", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String ||
                !A2UIProtocol.IsSafeJsonPointer(pathElement.GetString()))
            {
                return "Error: 'path' must be a safe RFC 6901 JSON Pointer.";
            }

            if (!args.RootElement.TryGetProperty("value", out _))
                return "Error: 'value' is required.";

            var uri = BuildUri(Config.DataModelPath);
            return uri is null
                ? "Error: InsForge.Endpoint must be an absolute http(s) URL."
                : await SendJsonAsync(HttpMethod.Post, uri, args.RootElement, ct);
        }
    }
}

public sealed class InsForgeCallEdgeA2UITool : InsForgeToolBase
{
    public InsForgeCallEdgeA2UITool(InsForgeConfig config, HttpClient? httpClient = null)
        : base(config, httpClient)
    {
    }

    public override string Name => "insforge_call_edge_a2ui";

    public override string Description =>
        "Call an InsForge Edge Function that returns A2UI protocol JSON for direct client streaming.";

    public override string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "function": { "type": "string", "description": "Edge Function name under the configured base path" },
            "payload": { "description": "JSON payload for the Edge Function" }
          },
          "required": ["function"]
        }
        """;

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!TryParseArguments(argumentsJson, out var args, out var error))
            return error;

        using (args)
        {
            if (!args.RootElement.TryGetProperty("function", out var functionElement) ||
                functionElement.ValueKind != JsonValueKind.String ||
                !IsSafeFunctionName(functionElement.GetString()))
            {
                return "Error: 'function' must contain only letters, digits, underscores, or dashes.";
            }

            var functionName = functionElement.GetString()!;
            var functionPath = Config.EdgeFunctionBasePath.TrimEnd('/') + "/" + functionName;
            var uri = BuildUri(functionPath);
            if (uri is null)
                return "Error: InsForge.Endpoint must be an absolute http(s) URL.";

            var body = args.RootElement.TryGetProperty("payload", out var payload)
                ? payload
                : args.RootElement;

            return await SendJsonAsync(HttpMethod.Post, uri, body, ct);
        }
    }

    private static bool IsSafeFunctionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            return false;

        return name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
    }
}
