using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Colo.Core;

public enum McpKind { Command, Url }

public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public McpKind Kind { get; set; }
    /// <summary>Command: línea de comando completa, p. ej. «npx -y @modelcontextprotocol/server-filesystem C:\Users\yo».</summary>
    public string Command { get; set; } = "";
    /// <summary>Command: variables KEY=VALOR, una por línea.</summary>
    public string Env { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Url: cabeceras «Nombre: valor», una por línea.</summary>
    public string Headers { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Tools elegidas; null = todas.</summary>
    public List<string>? Tools { get; set; }
}

public sealed record McpTool(string Name, string Description, JsonObject InputSchema);

interface IMcpTransport : IAsyncDisposable
{
    Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct);
    Task NotifyAsync(string method, CancellationToken ct);
}

/// <summary>Cliente MCP mínimo: initialize, tools/list y tools/call, por stdio o HTTP (Streamable HTTP).</summary>
public sealed class McpClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    readonly IMcpTransport _t;

    McpClient(IMcpTransport t) => _t = t;

    public static async Task<McpClient> ConnectAsync(McpServerConfig c, CancellationToken ct, HttpClient? http = null)
    {
        IMcpTransport t = c.Kind == McpKind.Url ? new HttpTransport(c, http) : StdioTransport.Start(c);
        var client = new McpClient(t);
        try
        {
            await t.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "Colo", ["version"] = "1.1" },
            }, ct);
            await t.NotifyAsync("notifications/initialized", ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        do
        {
            var p = cursor is null ? null : new JsonObject { ["cursor"] = cursor };
            var r = await _t.RequestAsync("tools/list", p, ct);
            foreach (var t in r?["tools"]?.AsArray() ?? [])
            {
                var schema = t?["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" };
                tools.Add(new McpTool(t!["name"]!.GetValue<string>(), t["description"]?.GetValue<string>() ?? "",
                    (JsonObject)schema.DeepClone()));
            }
            cursor = r?["nextCursor"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(cursor) && tools.Count < 1000);
        return tools;
    }

    /// <summary>Devuelve el texto del resultado; si el servidor marca isError, empieza por «error:».</summary>
    public async Task<string> CallToolAsync(string name, JsonObject args, CancellationToken ct)
    {
        var r = await _t.RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }, ct);
        var sb = new StringBuilder();
        foreach (var c in r?["content"]?.AsArray() ?? [])
        {
            switch (c?["type"]?.GetValue<string>())
            {
                case "text": sb.AppendLine(c["text"]?.GetValue<string>()); break;
                case "image": sb.AppendLine("[imagen]"); break;
                case "resource": sb.AppendLine(c["resource"]?["text"]?.GetValue<string>() ?? "[recurso]"); break;
                default: sb.AppendLine(c?.ToJsonString()); break;
            }
        }
        if (sb.Length == 0 && r?["structuredContent"] is JsonNode sc) sb.Append(sc.ToJsonString());
        var text = sb.ToString().Trim();
        return r?["isError"]?.GetValue<bool>() == true ? "error: " + text : "ok: " + text;
    }

    public ValueTask DisposeAsync() => _t.DisposeAsync();

    // ───────────────────────── stdio

    sealed class StdioTransport : IMcpTransport
    {
        readonly Process _p;
        readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode>> _pending = new();
        readonly SemaphoreSlim _write = new(1, 1);
        readonly StringBuilder _stderr = new();
        long _id;

        StdioTransport(Process p) => _p = p;

        public static StdioTransport Start(McpServerConfig c)
        {
            if (string.IsNullOrWhiteSpace(c.Command)) throw new InvalidOperationException("Falta el comando.");
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{c.Command.Trim()}\"")
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", c.Command.Trim() } };
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.StandardInputEncoding = new UTF8Encoding(false);
            foreach (var (k, v) in Pairs(c.Env, '=')) psi.Environment[k] = v;
            var p = Process.Start(psi) ?? throw new InvalidOperationException("No arrancó el proceso.");
            var t = new StdioTransport(p);
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (t._stderr)
                {
                    t._stderr.AppendLine(e.Data);
                    if (t._stderr.Length > 4000) t._stderr.Remove(0, t._stderr.Length - 4000);
                }
            };
            p.BeginErrorReadLine();
            _ = Task.Run(t.ReadLoop);
            return t;
        }

        async Task ReadLoop()
        {
            try
            {
                string? line;
                while ((line = await _p.StandardOutput.ReadLineAsync()) is not null)
                {
                    if (line.Length == 0 || line[0] != '{') continue;
                    JsonNode? msg;
                    try { msg = JsonNode.Parse(line); } catch { continue; }
                    if (msg is null) continue;
                    if (msg["method"] is not null)
                    {
                        // petición del servidor: solo ping tiene respuesta obligatoria
                        if (msg["id"] is JsonNode rid)
                            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = rid.DeepClone(), ["result"] = new JsonObject() });
                        continue;
                    }
                    if (msg["id"] is JsonValue idv && idv.TryGetValue<long>(out var id) && _pending.TryRemove(id, out var tcs))
                        tcs.TrySetResult(msg);
                }
            }
            catch { }
            var err = Stderr();
            foreach (var kv in _pending)
                kv.Value.TrySetException(new InvalidOperationException("El servidor MCP se cerró." + (err.Length > 0 ? " " + err : "")));
        }

        string Stderr()
        {
            lock (_stderr)
            {
                var s = _stderr.ToString().Trim();
                return s.Length > 400 ? s[^400..] : s;
            }
        }

        async Task Send(JsonObject msg)
        {
            await _write.WaitAsync();
            try
            {
                await _p.StandardInput.WriteLineAsync(msg.ToJsonString());
                await _p.StandardInput.FlushAsync();
            }
            finally { _write.Release(); }
        }

        public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (_p.HasExited) throw new InvalidOperationException("El servidor MCP no está corriendo. " + Stderr());
            var id = Interlocked.Increment(ref _id);
            var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null) msg["params"] = parameters;
            await Send(msg);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(method == "initialize" ? 60 : 120));
            using (timeout.Token.Register(() => tcs.TrySetCanceled()))
            {
                JsonNode reply;
                try { reply = await tcs.Task; }
                finally { _pending.TryRemove(id, out _); }
                return Result(reply);
            }
        }

        public Task NotifyAsync(string method, CancellationToken ct) =>
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

        public ValueTask DisposeAsync()
        {
            try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
            _p.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // ───────────────────────── HTTP

    sealed class HttpTransport(McpServerConfig c, HttpClient? http) : IMcpTransport
    {
        readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        string? _session;
        long _id;

        HttpRequestMessage Req(JsonObject msg)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, c.Url.Trim())
            {
                Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            r.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
            if (_session is not null) r.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
            foreach (var (k, v) in Pairs(c.Headers, ':')) r.Headers.TryAddWithoutValidation(k, v);
            return r;
        }

        public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _id);
            var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null) msg["params"] = parameters;
            using var resp = await _http.SendAsync(Req(msg), HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _session = sid.FirstOrDefault() ?? _session;
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
            }
            if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
                var data = new StringBuilder();
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal)) { data.Append(line[5..].TrimStart()); continue; }
                    if (line.Length > 0 || data.Length == 0) continue;
                    var node = TryParse(data.ToString());
                    data.Clear();
                    if (node?["id"] is JsonValue v && v.TryGetValue<long>(out var got) && got == id) return Result(node);
                }
                var last = TryParse(data.ToString());
                if (last?["id"] is JsonValue lv && lv.TryGetValue<long>(out var lid) && lid == id) return Result(last);
                throw new InvalidOperationException("El servidor MCP no respondió.");
            }
            var json = TryParse(await resp.Content.ReadAsStringAsync(ct)) ?? throw new InvalidOperationException("Respuesta MCP vacía.");
            return Result(json);
        }

        public async Task NotifyAsync(string method, CancellationToken ct)
        {
            using var resp = await _http.SendAsync(Req(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }), ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static JsonNode? TryParse(string s)
    {
        try { return JsonNode.Parse(s); } catch { return null; }
    }

    static JsonNode? Result(JsonNode reply)
    {
        if (reply["error"] is JsonNode e)
            throw new InvalidOperationException(e["message"]?.GetValue<string>() ?? e.ToJsonString());
        return reply["result"];
    }

    static IEnumerable<(string, string)> Pairs(string text, char sep)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var i = line.IndexOf(sep);
            if (i <= 0) continue;
            yield return (line[..i].Trim(), line[(i + 1)..].Trim());
        }
    }
}

/// <summary>Servidores MCP activos y sus tools, expuestas al modelo como mcp_servidor_tool.</summary>
public sealed class McpHub : IToolHost, IAsyncDisposable
{
    readonly List<McpClient> _clients = [];
    readonly Dictionary<string, (McpClient Client, string Tool)> _map = [];
    readonly List<JsonObject> _defs = [];

    public IReadOnlyList<JsonObject> Definitions => _defs;
    public List<string> Problems { get; } = [];

    public static async Task<McpHub> StartAsync(IEnumerable<McpServerConfig> servers, CancellationToken ct = default)
    {
        var hub = new McpHub();
        var active = servers.Where(s => s.Enabled).ToList();
        var results = await Task.WhenAll(active.Select(async s =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                var client = await McpClient.ConnectAsync(s, cts.Token);
                var tools = await client.ListToolsAsync(cts.Token);
                return (s, client, tools, error: (string?)null);
            }
            catch (Exception ex)
            {
                return (s, client: (McpClient?)null, tools: new List<McpTool>(), error: ex.Message);
            }
        }));
        foreach (var (s, client, tools, error) in results)
        {
            if (client is null) { hub.Problems.Add($"{s.Name}: {error}"); continue; }
            hub._clients.Add(client);
            foreach (var t in tools)
            {
                if (s.Tools is not null && !s.Tools.Contains(t.Name)) continue;
                var name = hub.Unique(ToolName(s.Name, t.Name));
                hub._map[name] = (client, t.Name);
                var schema = (JsonObject)t.InputSchema.DeepClone();
                schema["type"] = "object";
                if (schema["properties"] is null) schema["properties"] = new JsonObject();
                var desc = $"[{s.Name}] {t.Description}".Trim();
                hub._defs.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = name,
                    ["description"] = desc.Length > 400 ? desc[..400] + "…" : desc,
                    ["parameters"] = schema,
                    ["strict"] = false,
                });
            }
        }
        return hub;
    }

    public static string ToolName(string server, string tool)
    {
        static string Clean(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "_").Trim('_');
        var n = $"mcp_{Clean(server)}_{Clean(tool)}";
        return n.Length > 64 ? n[..64] : n;
    }

    string Unique(string name)
    {
        var n = name;
        for (var i = 2; _map.ContainsKey(n); i++) n = (name.Length > 60 ? name[..60] : name) + "_" + i;
        return n;
    }

    public bool Handles(string name) => _map.ContainsKey(name);

    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        if (!_map.TryGetValue(name, out var t)) return $"error: no existe {name}";
        var r = await t.Client.CallToolAsync(t.Tool, args, ct);
        return r.Length > 8000 ? r[..8000] + "…" : r;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        _clients.Clear();
    }
}
