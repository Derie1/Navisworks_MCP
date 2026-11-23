using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace NavisMcpServer;

/// <summary>
/// Minimal MCP server over stdio: newline-delimited JSON-RPC 2.0 (initialize, ping, tools/list,
/// tools/call). Tool definitions come from the embedded tools.json; its "bridge" table maps each
/// tool onto a plugin route (plus argument renames and whether the call may take long).
/// stdout carries protocol messages only; diagnostics go to stderr.
/// </summary>
internal sealed class McpServer(string version, Bridge bridge)
{
    // Newest first; a client asking for one of these gets it back, any other gets the first.
    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Stream _out = Stream.Null;
    private JsonArray? _tools;
    private Dictionary<string, Route> _routes = [];
    private string _instructions = "";

    private sealed record Route(string Path, Dictionary<string, string> Rename, bool Long);

    public async Task<int> RunAsync(Stream input, Stream output)
    {
        _out = output;
        LoadTools();
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var running = new List<Task>();
        while (await reader.ReadLineAsync() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch (Exception) { await SendAsync(Error(null, -32700, "parse error")); continue; }

            // Requests run concurrently, so a slow Navisworks call does not block ping or other calls.
            if (msg is JsonArray batch)
                foreach (var m in batch) running.Add(HandleAsync(m as JsonObject));
            else
                running.Add(HandleAsync(msg as JsonObject));
            running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);
        _shutdown.Cancel();
        return 0;
    }

    private void LoadTools()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tools.json")
                      ?? throw new InvalidOperationException("tools.json is not embedded");
        var doc = JsonNode.Parse(s)!.AsObject();
        _tools = doc["tools"]!.AsArray();
        _instructions = doc["instructions"]?.ToString() ?? "";
        foreach (var (name, node) in doc["bridge"]!.AsObject())
        {
            var rename = new Dictionary<string, string>();
            if (node!["rename"] is JsonObject r)
                foreach (var (from, to) in r) rename[from] = to!.ToString();
            _routes[name] = new Route(node["route"]!.ToString(), rename, node["long"]?.GetValue<bool>() == true);
        }
        var missing = _tools.Select(t => t!["name"]!.ToString()).Where(n => !_routes.ContainsKey(n)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("tools without a route: " + string.Join(", ", missing));
    }

    private async Task HandleAsync(JsonObject? msg)
    {
        if (msg == null) { await SendAsync(Error(null, -32600, "invalid request")); return; }
        var id = msg["id"]?.DeepClone();
        string? method = msg["method"]?.ToString();
        if (method == null) return;            // a response from the client: we send no requests
        if (id == null) return;                // notification (initialized, cancelled, ...)

        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(msg["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = _tools!.DeepClone() },
                "tools/call" => await CallToolAsync(msg["params"] as JsonObject),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new RpcException(-32601, $"method not found: {method}"),
            };
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (RpcException ex) { await SendAsync(Error(id, ex.Code, ex.Message)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"navis_mcp_server: {method} failed: {ex}");
            await SendAsync(Error(id, -32603, ex.Message));
        }
    }

    private JsonObject Initialize(JsonObject? p)
    {
        string requested = p?["protocolVersion"]?.ToString() ?? ProtocolVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "navisworks", ["title"] = "Navisworks MCP Bridge", ["version"] = version },
            ["instructions"] = _instructions,
        };
    }

    private async Task<JsonNode> CallToolAsync(JsonObject? p)
    {
        string name = p?["name"]?.ToString() ?? throw new RpcException(-32602, "tools/call needs a tool name");
        if (!_routes.TryGetValue(name, out var route)) throw new RpcException(-32602, $"unknown tool: {name}");

        // Tool argument names are what the model sees; a few plugin routes use shorter names.
        var args = new JsonObject();
        if (p["arguments"] is JsonObject given)
            foreach (var (key, value) in given)
                if (value != null) args[route.Rename.GetValueOrDefault(key, key)] = value.DeepClone();

        try
        {
            JsonObject result;
            if (name == "health_check")
            {
                result = await bridge.HealthAsync(_shutdown.Token);
                result["server_version"] = version;
            }
            else result = await bridge.CallAsync(route.Path, args, route.Long, _shutdown.Token);

            // capture_view / zoom_to_clash: the picture goes to the model as MCP image content.
            if (result["image_png_base64"] is JsonValue data)
            {
                result.Remove("image_png_base64");
                return new JsonObject
                {
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = result.ToJsonString(Json.Indented) },
                        new JsonObject { ["type"] = "image", ["data"] = data.ToString(), ["mimeType"] = "image/png" }),
                    ["isError"] = false,
                };
            }
            return ToolResult(result.ToJsonString(Json.Indented), isError: false);
        }
        catch (ToolException ex)
        {
            return ToolResult(ex.Message, isError: true);
        }
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private async Task SendAsync(JsonObject message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Json.Options) + "\n");
        await _writeLock.WaitAsync();
        try
        {
            await _out.WriteAsync(bytes);
            await _out.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

internal static class Json
{
    /// <summary>Readable output: Cyrillic and other non-ASCII text is not escaped as \uXXXX.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly System.Text.Json.JsonSerializerOptions Indented = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
}
