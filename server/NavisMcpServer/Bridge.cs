using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NavisMcpServer;

/// <summary>
/// HTTP client of the MCP Bridge plugin inside Navisworks (http://localhost:PORT/). The plugin
/// answers 200 with the result object, or 500 with {error, detail, path}. Environment:
///   NAVIS_MCP_PORT (8765), NAVIS_MCP_TIMEOUT (seconds, 30), NAVIS_MCP_LONG_TIMEOUT (seconds, 3600)
/// for routes that open, refresh or save models, run clash tests or write reports.
/// </summary>
internal sealed class Bridge
{
    private readonly HttpClient _http;
    private readonly int _port;
    private readonly double _timeout;
    private readonly double _longTimeout;
    private volatile bool _verified;

    public Bridge()
    {
        _port = int.TryParse(Environment.GetEnvironmentVariable("NAVIS_MCP_PORT"), out int p) ? p : 8765;
        _timeout = Seconds("NAVIS_MCP_TIMEOUT", 30);
        _longTimeout = Math.Max(_timeout, Seconds("NAVIS_MCP_LONG_TIMEOUT", 3600));
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    private static double Seconds(string variable, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0 ? s : fallback;

    public string NotRunning =>
        $"The Navisworks MCP bridge is not reachable at http://localhost:{_port}. Start Navisworks Manage/Simulate " +
        "2026-2027 and click 'MCP Bridge' on the 'AI companion' ribbon tab - the bridge does not start on its own.";

    /// <summary>GET /health: plugin version, port, number of routes.</summary>
    public async Task<JsonObject> HealthAsync(CancellationToken ct)
    {
        var health = await SendAsync(HttpMethod.Get, "health", null, _timeout, ct);
        Verify(health);
        health.Remove("ok");
        return health;
    }

    /// <summary>POST /route; throws ToolException with the plugin's error.</summary>
    public async Task<JsonObject> CallAsync(string route, JsonObject args, bool isLong, CancellationToken ct)
    {
        if (!_verified) await HealthAsync(ct);
        return await SendAsync(HttpMethod.Post, route.TrimStart('/'), args, isLong ? _longTimeout : _timeout, ct);
    }

    // Some other program could be listening on the port. Plugins before 2.3 send no "product"
    // but always "routes", so accept that too.
    private void Verify(JsonObject health)
    {
        string? product = health["product"]?.ToString();
        bool ours = product == "Navisworks" || (product == null && health["routes"] != null);
        if (!ours)
            throw new ToolException($"Port {_port} is served by {product ?? "an unknown program"}, not by the Navisworks " +
                "bridge. Set NAVIS_MCP_PORT and the plugin's Port to a free port.");
        _verified = true;
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string route, JsonObject? body, double seconds, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, route);
        // The plugin's HttpListener needs a Content-Length on POST, so always send a body.
        if (method == HttpMethod.Post || body != null)
        {
            req.Content = new StringContent((body ?? []).ToJsonString(), Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await _http.SendAsync(req, cts.Token);
            text = await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (HttpRequestException ex) { _verified = false; throw new ToolException($"{NotRunning} ({ex.Message})"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ToolException($"Navisworks did not answer within {seconds:0} s. It may be busy with a long " +
                "operation or waiting for a dialog to be closed.");
        }

        JsonObject result;
        try
        {
            result = JsonNode.Parse(text) as JsonObject ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            throw new ToolException($"Port {_port} answered with something that is not the Navisworks bridge " +
                $"(HTTP {(int)resp.StatusCode}).");
        }
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            string detail = result["detail"]?.ToString() ?? result["error"]?.ToString() ?? $"HTTP {(int)resp.StatusCode}";
            throw new ToolException(detail);
        }
        return result;
    }
}

/// <summary>An error to show to the model as the tool result (isError: true).</summary>
internal sealed class ToolException(string message) : Exception(message);
