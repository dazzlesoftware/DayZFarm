using System.Net.Http.Json;
using GameFarm.Core.Plugins;
using GameFarm.Shared;

namespace GameFarm.Controller.Services;

/// <summary>Talks to one guest agent over HTTP using its per-agent bearer token.</summary>
public sealed class AgentHttpClient : IAgentClient
{
    private readonly HttpClient _http;

    public AgentHttpClient(HttpClient http)
    {
        _http = http;
    }

    public static void ApplyToken(HttpRequestMessage request, string token) =>
        request.Headers.Add(AgentAuthConstants.TokenHeaderName, token);

    public async Task<AgentStatusResponse?> GetStatusAsync(Uri baseAddress, string token, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, AgentApiRoutes.Status));
        ApplyToken(request, token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AgentStatusResponse>(cancellationToken: ct);
    }

    public Task<AgentCommandResult> PostAsync(Uri baseAddress, string route, string token, CancellationToken ct = default) =>
        SendAsync<object?>(baseAddress, route, token, null, ct);

    public Task<AgentCommandResult> PostAsync<TBody>(Uri baseAddress, string route, string token, TBody body, CancellationToken ct = default) =>
        SendAsync(baseAddress, route, token, body, ct);

    private async Task<AgentCommandResult> SendAsync<TBody>(Uri baseAddress, string route, string token, TBody? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, route));
        ApplyToken(request, token);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? AgentCommandResult.Ok(text)
                : AgentCommandResult.Fail($"Agent returned {(int)response.StatusCode}: {text}");
        }
        catch (Exception ex)
        {
            return AgentCommandResult.Fail($"Agent unreachable: {ex.Message}");
        }
    }

    public async Task<LogsResponse?> GetLogsAsync(Uri baseAddress, string token, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, AgentApiRoutes.Logs));
        ApplyToken(request, token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<LogsResponse>(cancellationToken: ct);
    }

    public async Task<ModsResponse?> GetModsAsync(Uri baseAddress, string token, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, AgentApiRoutes.Mods));
        ApplyToken(request, token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<ModsResponse>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<PluginSummary>?> GetAgentPluginsAsync(Uri baseAddress, string token, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, AgentApiRoutes.Plugins));
        ApplyToken(request, token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<List<PluginSummary>>(cancellationToken: ct);
    }

    public async Task<PluginRunResult?> RunAgentPluginAsync(Uri baseAddress, string token, string pluginName, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, AgentApiRoutes.PluginRun(pluginName)));
        ApplyToken(request, token);
        using var response = await _http.SendAsync(request, ct);
        return await response.Content.ReadFromJsonAsync<PluginRunResult>(cancellationToken: ct);
    }

    // If a package is large enough that the transfer genuinely takes longer than this HttpClient's
    // configured Timeout (45s as of Program.cs), bump that timeout -- there's nothing package-size-
    // aware here, it shares the same HttpClient/timeout as every other (much smaller) agent call.
    public async Task<AgentCommandResult> PushUpdatePackageAsync(Uri baseAddress, string token, byte[] zipBytes, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, AgentApiRoutes.UpdatePackage));
        ApplyToken(request, token);
        request.Content = new ByteArrayContent(zipBytes);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? AgentCommandResult.Ok(text)
                : AgentCommandResult.Fail($"Agent returned {(int)response.StatusCode}: {text}");
        }
        catch (Exception ex)
        {
            return AgentCommandResult.Fail($"Agent unreachable: {ex.Message}");
        }
    }
}
