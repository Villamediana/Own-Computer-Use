using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Colo.Core;
using Xunit;
using Xunit.Abstractions;

namespace Colo.Tests;

sealed class FakeHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> reply) : HttpMessageHandler
{
    public List<(HttpRequestMessage Req, string Body)> Seen { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
        lock (Seen) Seen.Add((r, body));
        var (code, json) = reply(r, body);
        return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

sealed class FakeComputer : IComputer
{
    public List<string> Done { get; } = [];
    public Func<Task>? BeforeObserve;
    public List<bool> Images { get; } = [];

    public async Task<Observation> ObserveAsync(bool withImage, CancellationToken ct)
    {
        if (BeforeObserve is not null) await BeforeObserve();
        lock (Images) Images.Add(withImage);
        return new(withImage ? [1, 2, 3] : [], 1366, 768, "Google Chrome",
            [new Mark(1, "campo", "Buscar", "gatos"), new Mark(2, "botón", "Enviar")]);
    }

    public Task<string> ExecuteAsync(ToolCall call, CancellationToken ct)
    {
        lock (Done) Done.Add($"{call.Name} {call.Args.ToJsonString()}");
        return Task.FromResult("ok");
    }
}

public class CoreTests(ITestOutputHelper output)
{
    /// <summary>Respuesta en streaming (SSE) de la Responses API con esas llamadas.</summary>
    static string Calls(params (string Name, string Args)[] calls)
    {
        var sb = new StringBuilder();
        void Ev(JsonObject e) => sb.Append("event: ").Append(e["type"]!.GetValue<string>()).Append("\ndata: ").Append(e.ToJsonString()).Append("\n\n");
        Ev(new JsonObject { ["type"] = "response.created" });
        Ev(new JsonObject { ["type"] = "response.output_item.done", ["item"] = new JsonObject { ["type"] = "reasoning" } });
        foreach (var (n, a) in calls)
            Ev(new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["item"] = new JsonObject { ["type"] = "function_call", ["name"] = n, ["arguments"] = a, ["call_id"] = "c" + n },
            });
        Ev(new JsonObject { ["type"] = "response.completed" });
        return sb.ToString();
    }

    static string UserText(string body) =>
        JsonNode.Parse(body)!["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();

    static Settings Azure() => new()
    {
        Provider = Provider.Azure,
        Endpoint = "https://latam.openai.azure.com/openai/deployments/x?api-version=1",
        ApiKey = "k",
        Model = "gpt-5.6-luna",
        TranscribeModel = "gpt-4o-mini-transcribe",
    };

    [Fact]
    public async Task Azure_lists_deployments_and_uses_v1_responses()
    {
        var h = new FakeHandler((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/openai/deployments" => (HttpStatusCode.OK, """{"data":[{"id":"luna","model":"gpt-5.6"},{"id":"voz","model":"gpt-4o-mini-transcribe"}]}"""),
            _ => (HttpStatusCode.OK, Calls(("done", """{"message":"ok"}"""))),
        });
        var s = Azure();
        var ai = new AiClient(s, new HttpClient(h));
        var models = await ai.ListModelsAsync();
        Assert.Equal(["luna", "voz"], models.Select(m => m.Id));
        Assert.Equal("luna  ·  gpt-5.6", models[0].ToString());
        Assert.Equal("https://latam.openai.azure.com/openai/deployments?api-version=2022-12-01", h.Seen[0].Req.RequestUri!.ToString());
        Assert.Equal("k", h.Seen[0].Req.Headers.GetValues("api-key").Single());

        await foreach (var _ in ai.StreamAsync(new JsonObject { ["model"] = "luna" }, default)) { }
        Assert.Equal("https://latam.openai.azure.com/openai/v1/responses", h.Seen[1].Req.RequestUri!.ToString());
    }

    [Fact]
    public async Task Azure_list_falls_to_second_api_version_and_reports_error_text()
    {
        var h = new FakeHandler((r, _) => r.RequestUri!.Query.Contains("2022")
            ? (HttpStatusCode.NotFound, """{"error":{"message":"api version retired"}}""")
            : (HttpStatusCode.OK, """{"data":[{"id":"a"}]}"""));
        var models = await new AiClient(Azure(), new HttpClient(h)).ListModelsAsync();
        Assert.Single(models);

        var bad = new FakeHandler((_, _) => (HttpStatusCode.Unauthorized, """{"error":{"message":"Access denied due to invalid subscription key"}}"""));
        var ex = await Assert.ThrowsAsync<AiException>(() => new AiClient(Azure(), new HttpClient(bad)).ListModelsAsync());
        Assert.Contains("invalid subscription key", ex.Message);
    }

    [Fact]
    public async Task OpenAI_uses_bearer_and_models_endpoint()
    {
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, """{"data":[{"id":"gpt-5.5"},{"id":"gpt-4o-mini-transcribe"}]}"""));
        var s = new Settings { Provider = Provider.OpenAI, ApiKey = "sk-x" };
        var models = await new AiClient(s, new HttpClient(h)).ListModelsAsync();
        Assert.Equal(2, models.Count);
        Assert.Equal("https://api.openai.com/v1/models", h.Seen[0].Req.RequestUri!.ToString());
        Assert.Equal("Bearer sk-x", h.Seen[0].Req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Api_transcription_paths()
    {
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, """{"text":" abre chrome "}"""));
        var wav = Wav.Encode(new float[1600]);
        Assert.Equal("abre chrome", await new AiClient(Azure(), new HttpClient(h)).TranscribeAsync(wav, default));
        Assert.Equal("https://latam.openai.azure.com/openai/deployments/gpt-4o-mini-transcribe/audio/transcriptions?api-version=2025-03-01-preview",
            h.Seen[0].Req.RequestUri!.ToString());
        Assert.Contains("name=model", h.Seen[0].Body);

        var o = new Settings { Provider = Provider.OpenAI, ApiKey = "sk", TranscribeModel = "gpt-4o-mini-transcribe" };
        await new AiClient(o, new HttpClient(h)).TranscribeAsync(wav, default);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", h.Seen[1].Req.RequestUri!.ToString());
    }

    [Fact]
    public async Task Agent_runs_batch_of_actions_then_done()
    {
        var step = 0;
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, Interlocked.Increment(ref step) == 1
            ? Calls(("click", """{"mark":1}"""), ("type_text", """{"text":"bebés"}"""), ("press_keys", """{"keys":"enter"}"""))
            : Calls(("done", """{"message":"Listo, busqué."}"""))));
        var s = Azure();
        s.Vision = Vision.Always;
        var pc = new FakeComputer();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, pc) { SettleMs = 0 };
        var events = new List<AgentEvent>();
        agent.Event += e => { lock (events) events.Add(e); };

        await agent.Hear("busca bebés");

        Assert.Equal(3, pc.Done.Count);
        Assert.StartsWith("click", pc.Done[0]);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Action && e.Text == "Clic [1] «Buscar»");
        Assert.Equal(AgentEventKind.Done, events[^1].Kind);
        Assert.Equal("Listo, busqué.", events[^1].Text);
        Assert.False(agent.Busy);

        var first = JsonNode.Parse(h.Seen[0].Body)!;
        Assert.Equal("gpt-5.6-luna", first["model"]!.GetValue<string>());
        Assert.Equal("low", first["reasoning"]!["effort"]!.GetValue<string>());
        Assert.False(first["store"]!.GetValue<bool>());
        var content = first["input"]![0]!["content"]!.AsArray();
        Assert.Contains("«busca bebés»", content[0]!["text"]!.GetValue<string>());
        Assert.Contains("[2] botón «Enviar»", content[0]!["text"]!.GetValue<string>());
        Assert.Contains("[1] campo «Buscar» =gatos", content[0]!["text"]!.GetValue<string>());
        Assert.True(first["stream"]!.GetValue<bool>());
        Assert.StartsWith("data:image/jpeg;base64,", content[1]!["image_url"]!.GetValue<string>());
        Assert.Contains(first["tools"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "screenshot");
        Assert.Contains(first["tools"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "browser");

        // El segundo turno lleva lo hecho, no la conversación entera.
        var second = JsonNode.Parse(h.Seen[1].Body)!["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Escribe «bebés» → ok", second);
        Assert.Single(JsonNode.Parse(h.Seen[1].Body)!["input"]!.AsArray());
    }

    [Fact]
    public async Task Words_spoken_mid_task_join_the_running_task()
    {
        var step = 0;
        var h = new FakeHandler((_, body) =>
        {
            var n = Interlocked.Increment(ref step);
            if (n == 1) return (HttpStatusCode.OK, Calls(("open", """{"target":"chrome"}""")));
            if (UserText(body).Contains("el usuario añadió")) return (HttpStatusCode.OK, Calls(("done", """{"message":"hecho"}""")));
            return (HttpStatusCode.OK, Calls(("wait", """{"seconds":0.1}""")));
        });
        var s = Azure();
        var pc = new FakeComputer();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, pc) { SettleMs = 0 };
        var gate = new TaskCompletionSource();
        var observed = 0;
        pc.BeforeObserve = async () => { if (Interlocked.Increment(ref observed) == 2) await gate.Task; };

        var run = agent.Hear("abre chrome");
        await Task.Delay(100);
        Assert.True(agent.Busy);
        await agent.Hear("y entra en youtube");   // va a la cola de la tarea en curso
        gate.SetResult();
        await run;

        Assert.Contains(h.Seen, x => x.Body.Contains("y entra en youtube"));
        Assert.False(agent.Busy);
        Assert.Equal(2, step);
    }

    [Fact]
    public async Task Ignore_ends_quietly_and_stop_cancels()
    {
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, Calls(("ignore", "{}"))));
        var s = Azure();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, new FakeComputer()) { SettleMs = 0 };
        var events = new List<AgentEvent>();
        agent.Event += e => { lock (events) events.Add(e); };
        await agent.Hear("(llanto)");
        Assert.Equal(AgentEventKind.Ignored, events[^1].Kind);

        var slow = new FakeHandler((_, _) => (HttpStatusCode.OK, Calls(("wait", """{"seconds":1}"""))));
        var pc = new FakeComputer();
        var a2 = new Agent(new AiClient(s, new HttpClient(slow)), s, pc) { SettleMs = 50 };
        var ev2 = new List<AgentEvent>();
        a2.Event += e => { lock (ev2) ev2.Add(e); };
        var run = a2.Hear("espera");
        await Task.Delay(150);
        a2.Stop();
        await run;
        Assert.Equal(AgentEventKind.Stopped, ev2[^1].Kind);
        Assert.False(a2.Busy);
    }

    [Fact]
    public void No_reasoning_field_when_disabled()
    {
        var s = Azure();
        s.ReasoningEffort = "";
        var agent = new Agent(new AiClient(s), s, new FakeComputer());
        var body = agent.BuildRequest(["hola"], [], new([1], 10, 10, "x", []));
        Assert.Null(body["reasoning"]);
        Assert.Contains("(ninguno; usa x,y)", body.ToJsonString());

        s.DisabledTools = ["browser", "mouse"];
        var names = agent.BuildRequest(["hola"], [], new([], 0, 0, "x", []))["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("browser", names);
        Assert.DoesNotContain("click", names);
        Assert.Contains("type_text", names);
        Assert.Contains("look", names);
    }

    [Fact]
    public async Task Auto_vision_sends_image_only_after_look_and_full_results_once()
    {
        var step = 0;
        var long_text = new string('x', 900);
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, Interlocked.Increment(ref step) switch
        {
            1 => Calls(("read_text", """{"what":"window"}"""), ("look", "{}"), ("click", """{"mark":2}""")),
            2 => Calls(("click", """{"mark":2}""")),
            _ => Calls(("done", """{"message":"ok"}""")),
        }));
        var s = Azure();
        var pc = new FakeComputer();
        var reader = new LongResultComputer(pc, long_text);
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, reader) { SettleMs = 0 };
        await agent.Hear("lee el correo");

        Assert.Equal([false, true, false], pc.Images);
        // la acción después de look no se ejecuta: hace falta ver primero
        Assert.Equal(["read_text", "click"], reader.Calls);
        var t2 = UserText(h.Seen[1].Body);
        Assert.Contains(long_text, t2);
        Assert.Equal(2, JsonNode.Parse(h.Seen[1].Body)!["input"]![0]!["content"]!.AsArray().Count);
        var t3 = UserText(h.Seen[2].Body);
        Assert.DoesNotContain(long_text, t3);
        Assert.Contains("Sin captura en este turno", t3);
    }

    sealed class LongResultComputer(FakeComputer inner, string text) : IComputer
    {
        public List<string> Calls { get; } = [];
        public Task<Observation> ObserveAsync(bool withImage, CancellationToken ct) => inner.ObserveAsync(withImage, ct);
        public Task<string> ExecuteAsync(ToolCall call, CancellationToken ct)
        {
            Calls.Add(call.Name);
            return Task.FromResult(call.Name == "read_text" ? "ok:\n" + text : "ok");
        }
    }

    [Fact]
    public async Task Stream_error_event_surfaces_message()
    {
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK,
            "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"quota exceeded\"}}}\n\n"));
        var s = Azure();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, new FakeComputer()) { SettleMs = 0 };
        var events = new List<AgentEvent>();
        agent.Event += e => { lock (events) events.Add(e); };
        await agent.Hear("hola");
        Assert.Equal(AgentEventKind.Error, events[^1].Kind);
        Assert.Contains("quota exceeded", events[^1].Text);
    }

    [Fact]
    public async Task Mcp_stdio_server_tools_reach_the_model_and_run()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "mcp_echo.py");
        var cfg = new McpServerConfig { Name = "Eco Server", Kind = McpKind.Command, Command = $"python3 \"{script}\"", Tools = ["echo"] };
        await using var hub = await McpHub.StartAsync([cfg]);
        Assert.Empty(hub.Problems);
        Assert.Equal(["mcp_eco_server_echo"], hub.Definitions.Select(d => d["name"]!.GetValue<string>()));
        Assert.Equal("ok: eco: hola", await hub.CallAsync("mcp_eco_server_echo", new JsonObject { ["text"] = "hola" }, default));

        var step = 0;
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, Interlocked.Increment(ref step) == 1
            ? Calls(("mcp_eco_server_echo", """{"text":"bebé"}"""))
            : Calls(("done", """{"message":"listo"}"""))));
        var s = Azure();
        var pc = new FakeComputer();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, pc) { SettleMs = 0, Extra = hub };
        await agent.Hear("usa el eco");
        Assert.Contains(JsonNode.Parse(h.Seen[0].Body)!["tools"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "mcp_eco_server_echo");
        Assert.Contains("eco: bebé", UserText(h.Seen[1].Body));
        Assert.Empty(pc.Done);

        await using var all = await McpHub.StartAsync([new McpServerConfig { Name = "e", Command = $"python3 \"{script}\"" }]);
        Assert.Equal(2, all.Definitions.Count);
        Assert.Equal("ok: 5", await all.CallAsync("mcp_e_add", new JsonObject { ["a"] = 2, ["b"] = 3 }, default));

        await using var broken = await McpHub.StartAsync([new McpServerConfig { Name = "x", Command = "no-existe-este-comando-mcp" }]);
        Assert.Single(broken.Problems);
    }

    [Fact]
    public async Task Mcp_http_handles_json_and_sse_and_session()
    {
        var h = new FakeHandler((r, body) =>
        {
            var msg = JsonNode.Parse(body)!;
            var id = msg["id"]?.GetValue<long>();
            return msg["method"]!.GetValue<string>() switch
            {
                "initialize" => (HttpStatusCode.OK, $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"protocolVersion":"2025-06-18"}}"""),
                "notifications/initialized" => (HttpStatusCode.Accepted, ""),
                "tools/list" => (HttpStatusCode.OK, $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"tools":[{"name":"crm.find","description":"Busca","inputSchema":{"type":"object"}}]}}"""),
                _ => (HttpStatusCode.OK, $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"content":[{"type":"text","text":"3 clientes"}]}}"""),
            };
        });
        var cfg = new McpServerConfig { Name = "crm", Kind = McpKind.Url, Url = "https://mcp.example/mcp", Headers = "Authorization: Bearer abc" };
        await using var client = await McpClient.ConnectAsync(cfg, default, new HttpClient(h));
        var tools = await client.ListToolsAsync(default);
        Assert.Equal("crm.find", tools.Single().Name);
        Assert.Equal("ok: 3 clientes", await client.CallToolAsync("crm.find", [], default));
        Assert.Equal("Bearer abc", h.Seen[0].Req.Headers.GetValues("Authorization").Single());
        Assert.Equal("mcp_crm_crm_find", McpHub.ToolName("crm", "crm.find"));

        var sse = new FakeHandler((_, body) =>
        {
            var id = JsonNode.Parse(body)!["id"]?.GetValue<long>();
            return (HttpStatusCode.OK, $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{}}""");
        });
        await using var c2 = await McpClient.ConnectAsync(cfg, default, new HttpClient(new SseWrap(sse)));
        Assert.Empty(await c2.ListToolsAsync(default));
    }

    /// <summary>Envuelve la respuesta JSON como text/event-stream y añade Mcp-Session-Id.</summary>
    sealed class SseWrap(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var resp = await base.SendAsync(r, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            resp.Content = new StringContent($"event: message\ndata: {json}\n\n", Encoding.UTF8, "text/event-stream");
            resp.Headers.Add("Mcp-Session-Id", "s1");
            return resp;
        }
    }

    /// <summary>Stream que manda un evento y después se queda callado.</summary>
    sealed class SilentStream : Stream
    {
        bool _sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent)
            {
                _sent = true;
                var b = Encoding.UTF8.GetBytes("data: {\"type\":\"response.created\"}\n\n");
                b.CopyTo(buffer);
                return b.Length;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().Result;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) { }
    }

    sealed class SilentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) });
    }

    [Fact]
    public async Task Silent_stream_ends_with_error_instead_of_hanging()
    {
        var s = Azure();
        var ai = new AiClient(s, new HttpClient(new SilentHandler())) { StreamIdle = TimeSpan.FromMilliseconds(300) };
        var agent = new Agent(ai, s, new FakeComputer()) { SettleMs = 0 };
        var events = new List<AgentEvent>();
        agent.Event += e => { lock (events) events.Add(e); };
        var run = agent.Hear("hola");
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(5000)));
        Assert.Equal(AgentEventKind.Error, events[^1].Kind);
        Assert.Contains("dejó de responder", events[^1].Text);
    }

    [Fact]
    public void Settings_json_matches_what_the_settings_page_sends()
    {
        var opts = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var fromPage = """
        {"Provider":"OpenAI","Endpoint":"","ApiKey":"sk","Model":"gpt-4.1","ReasoningEffort":"none","SpeechEngine":"Api",
         "TranscribeModel":"whisper","MicDevice":1,"Sensitivity":7,"SpeakReplies":true,"StartWithWindows":false,"Vision":"Always",
         "DisabledTools":["browser"],"WidgetX":5,
         "McpServers":[{"Name":"Archivos","Kind":"Command","Command":"npx x","Env":"A=1","Url":"","Headers":"","Enabled":true,"Tools":["read_file"]},
                       {"Name":"CRM","Kind":"Url","Command":"","Env":"","Url":"https://x/mcp","Headers":"","Enabled":false,"Tools":null}]}
        """;
        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(fromPage, opts)!;
        Assert.Equal(Provider.OpenAI, s.Provider);
        Assert.Equal(SpeechEngine.Api, s.SpeechEngine);
        Assert.Equal(Vision.Always, s.Vision);
        Assert.Equal(McpKind.Url, s.McpServers[1].Kind);
        Assert.Null(s.McpServers[1].Tools);
        Assert.Equal(["read_file"], s.McpServers[0].Tools!);
        var back = System.Text.Json.JsonSerializer.Serialize(s, opts);
        Assert.Contains("\"Provider\":\"OpenAI\"", back);
        Assert.Contains("\"Kind\":\"Command\"", back);
    }

    [Fact]
    public async Task ElevenLabs_scribe_request_and_errors()
    {
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, """{"language_code":"spa","text":" abre chrome ","words":[]}"""));
        var s = new Settings { SpeechEngine = SpeechEngine.ElevenLabs, ElevenLabsKey = "xi-123" };
        var text = await new ElevenLabsStt(s, new HttpClient(h)).TranscribeAsync(Wav.Encode(new float[1600]), default);
        Assert.Equal("abre chrome", text);
        var (req, body) = h.Seen[0];
        Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", req.RequestUri!.ToString());
        Assert.Equal("xi-123", req.Headers.GetValues("xi-api-key").Single());
        Assert.Contains("name=model_id", body);
        Assert.Contains("scribe_v2", body);
        Assert.Contains("name=file", body);

        var bad = new FakeHandler((_, _) => (HttpStatusCode.Unauthorized, """{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}"""));
        var ex = await Assert.ThrowsAsync<AiException>(() => new ElevenLabsStt(s, new HttpClient(bad)).TranscribeAsync([0], default));
        Assert.Contains("Invalid API key", ex.Message);
    }

    /// <summary>Remuestreo lineal simple solo para fabricar entradas de prueba.</summary>
    static float[] Resample(float[] x, int from, int to)
    {
        var n = (int)((long)x.Length * to / from);
        var y = new float[n];
        for (var i = 0; i < n; i++)
        {
            var p = (double)i * from / to;
            var k = (int)p;
            var t = (float)(p - k);
            y[i] = k + 1 < x.Length ? x[k] * (1 - t) + x[k + 1] * t : x[^1];
        }
        return y;
    }

    [Theory]
    [InlineData(48000, 2, true)]    // headset USB típico por WASAPI: 48 kHz estéreo float
    [InlineData(44100, 2, false)]   // 44,1 kHz estéreo PCM 16
    [InlineData(48000, 1, true)]
    [InlineData(16000, 1, false)]
    public void Mic_formats_are_converted_and_speech_is_detected(int rate, int channels, bool isFloat)
    {
        var dir = ModelsDir();
        if (dir is null) return;
        var speech = Wav.Read(Path.Combine(dir, "es.wav"), out _).Select(f => f * 0.15f).ToArray();
        var signal = new float[16000].Concat(speech).Concat(new float[32000]).ToArray();
        var native = Resample(signal, 16000, rate);

        var format = isFloat
            ? (NAudio.Wave.WaveFormat)new NAudio.Wave.WaveFormatExtensible(rate, 32, channels)
            : new NAudio.Wave.WaveFormat(rate, 16, channels);
        var conv = new AudioConverter(format);
        using var l = new Listener(7);
        var phrases = new List<float[]>();
        l.Utterance += phrases.Add;

        // Bloques de 10 ms, como los manda WASAPI.
        var frames = rate / 100;
        var bytesPerSample = isFloat ? 4 : 2;
        var block = new byte[frames * channels * bytesPerSample];
        for (var i = 0; i < native.Length; i += frames)
        {
            var n = Math.Min(frames, native.Length - i);
            var b = 0;
            for (var k = 0; k < n; k++)
                for (var c = 0; c < channels; c++)
                {
                    if (isFloat) { BitConverter.TryWriteBytes(block.AsSpan(b), native[i + k]); b += 4; }
                    else { BitConverter.TryWriteBytes(block.AsSpan(b), (short)(native[i + k] * 32767)); b += 2; }
                }
            conv.Push(block, b, pcm => l.Feed(pcm));
        }
        Assert.Single(phrases);
        using var stt = Whisper(dir);
        var text = stt.Transcribe(phrases[0]);
        output.WriteLine($"{conv.Describe()}: «{text}»");
        Assert.Contains("país", text.ToLowerInvariant());
    }

    [Fact]
    public async Task Tokens_are_counted_even_on_the_final_step()
    {
        // usage llega en response.completed, después del done: hay que leer el stream hasta el final.
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, Calls(("done", """{"message":"[cheerfully] Listo"}""")).Replace(
            "\"type\":\"response.completed\"", "\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1800,\"input_tokens_details\":{\"cached_tokens\":1600},\"output_tokens\":30}}")));
        var s = Azure();
        var ai = new AiClient(s, new HttpClient(h));
        var used = new List<Usage>();
        ai.Used += u => used.Add(u);
        var agent = new Agent(ai, s, new FakeComputer()) { SettleMs = 0 };
        var events = new List<AgentEvent>();
        agent.Event += e => { lock (events) events.Add(e); };
        await agent.Hear("abre una pestaña");
        Assert.Equal(new Usage(1800, 1600, 30), Assert.Single(used));

        var path = Path.Combine(Path.GetTempPath(), $"colo-usage-{Guid.NewGuid():N}.json");
        var meter = new TokenMeter(path);
        meter.Add(used[0]);
        meter.Add(new Usage(1000, 0, 70));
        Assert.Equal(2900, new TokenMeter(path).AllTokens);
        Assert.Equal(2900, new TokenMeter(path).TodayTokens);
        Assert.Equal("2,9 mil", TokenMeter.Short(2900).Replace('.', ','));
        File.Delete(path);
    }

    [Fact]
    public void Emotion_tags_never_reach_the_screen()
    {
        Assert.Equal("¡Listo! Pestaña nueva abierta.", Expressive.Clean("[cheerfully] ¡Listo! [warmly] Pestaña nueva abierta."));
        Assert.Equal("Vale, ya está.", Expressive.Clean("Vale [laughs], ya está."));
        Assert.True(ElevenLabsTts.ActsTags("eleven_v4"));
        Assert.False(ElevenLabsTts.ActsTags("eleven_multilingual_v2"));
    }

    [Fact]
    public async Task Context_is_forgotten_by_itself_after_a_while()
    {
        var bodies = new List<string>();
        var h = new FakeHandler((_, body) => { lock (bodies) bodies.Add(body); return (HttpStatusCode.OK, Calls(("done", """{"message":"ok"}"""))); });
        var s = Azure();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, new FakeComputer()) { SettleMs = 0, ContextTtl = TimeSpan.FromMilliseconds(300) };
        await agent.Hear("escribe fresh");
        await agent.Hear("no, en la barra");
        Assert.Contains("escribe fresh", bodies[1]);
        await Task.Delay(400);
        await agent.Hear("abre el bloc");
        Assert.DoesNotContain("escribe fresh", bodies[2]);
    }

    [Fact]
    public async Task Action_with_done_finishes_in_one_call_only_when_last()
    {
        var step = 0;
        // 1ª orden: una acción con done → termina sin segundo turno.
        // 2ª orden: done en la primera de dos acciones → no cierra; hace falta otro turno.
        var h = new FakeHandler((_, body) => (HttpStatusCode.OK, Interlocked.Increment(ref step) switch
        {
            1 => Calls(("browser", """{"action":"new_tab","done":"Pestaña nueva abierta."}""")),
            2 => Calls(("browser", """{"action":"new_tab","done":"Abierta"}"""), ("browser", """{"action":"search","text":"clima"}""")),
            _ => Calls(("done", """{"message":"Buscado."}""")),
        }));
        var s = Azure();
        var pc = new FakeComputer();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, pc) { SettleMs = 0 };
        var events = new List<AgentEvent>();
        agent.Event += e => { lock (events) events.Add(e); };
        await agent.Hear("abre una pestaña");
        Assert.Equal(1, step);
        Assert.Equal("Pestaña nueva abierta.", events[^1].Text);
        await agent.Hear("abre una pestaña y busca clima");
        Assert.Equal(3, step);
        Assert.Equal("Buscado.", events[^1].Text);
        Assert.Equal(3, pc.Done.Count);
    }

    [Fact]
    public async Task Memory_is_kept_between_days_and_sent_with_each_request()
    {
        var path = Path.Combine(Path.GetTempPath(), $"colo-mem-{Guid.NewGuid():N}.json");
        var bodies = new List<string>();
        var step = 0;
        var h = new FakeHandler((_, body) =>
        {
            lock (bodies) bodies.Add(body);
            return (HttpStatusCode.OK, Interlocked.Increment(ref step) == 1
                ? Calls(("remember", """{"fact":"«El backlog» es la pestaña P&I · Backlog"}"""), ("done", """{"message":"Anotado."}"""))
                : Calls(("done", """{"message":"ok"}""")));
        });
        var s = Azure();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, new FakeComputer()) { SettleMs = 0, Memory = new Memory(path) };
        await agent.Hear("el backlog es la pestaña P&I");
        var tomorrow = new Agent(new AiClient(s, new HttpClient(h)), s, new FakeComputer()) { SettleMs = 0, Memory = new Memory(path) };
        await tomorrow.Hear("abre el backlog");
        Assert.Contains("Lo que sabes del usuario", bodies[1]);
        Assert.Contains("es la pesta", bodies[1]);
        var mem = new Memory(path);
        Assert.Equal("ok: ya lo sabía", mem.Remember("«el backlog» es la pestaña p&i · backlog"));
        Assert.Equal("ok: olvidado", mem.Forget("backlog"));
        Assert.Empty(new Memory(path).Facts);
        File.Delete(path);
    }

    [Fact]
    public void Screen_text_shows_focus_and_other_windows()
    {
        var s = Azure();
        var agent = new Agent(new AiClient(s), s, new FakeComputer());
        var obs = new Observation([], 0, 0, "P&I · Backlog - Comet [comet]",
            [new Mark(1, "pestaña", "P&I · Backlog", "", "activa"), new Mark(2, "campo", "Barra de endereço", "x", "foco")],
            ["Bandeja de entrada - Outlook [olk]"]);
        var text = agent.BuildRequest(["hola"], [], obs)["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Otras ventanas: Bandeja de entrada - Outlook [olk]", text);
        Assert.Contains("[1] pestaña «P&I · Backlog» (activa)", text);
        Assert.Contains("=x (foco)", text);
        var tools = Tools.Build(null).Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("edit_text", tools);
        Assert.Contains("remember", tools);
    }

    static short[] Pcm(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "wake", name + ".pcm"));
        var pcm = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
        return pcm;
    }

    static List<float> WakeScores(string name)
    {
        using var model = WakeModel.Bundled();
        var audio = Pcm(name);
        var scores = new List<float>();
        for (var i = 0; i + WakeModel.Chunk <= audio.Length; i += WakeModel.Chunk)
            scores.Add(model.Score(audio.AsSpan(i, WakeModel.Chunk)));
        return scores;
    }

    [Fact]
    public void Wake_word_scores_match_the_phone_chunk_by_chunk()
    {
        // Las mismas notas que el bucle de referencia de la Luna (oww_stream.py) con la misma persona real.
        foreach (var name in new[] { "luna", "lua" })
        {
            var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "wake", name + ".scores"))
                .Where(l => l.Trim().Length > 0).Select(l => float.Parse(l, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var actual = WakeScores(name);
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++) Assert.True(Math.Abs(expected[i] - actual[i]) < 1e-3, $"{name}[{i}]: {expected[i]} vs {actual[i]}");
        }
        Assert.Contains(WakeScores("luna"), s => s >= WakeWord.Cutoff);
        Assert.DoesNotContain(WakeScores("lua"), s => s >= WakeWord.Cutoff);
    }

    [Fact]
    public async Task Wake_word_fires_once_from_the_microphone_stream()
    {
        using var wake = new WakeWord(WakeModel.Bundled());
        var woke = 0;
        wake.Woke += _ => Interlocked.Increment(ref woke);
        // Medio segundo de ruido suave (para el calentamiento) y luego «luna», en bloques de 40 ms como el micrófono.
        var rnd = new Random(3);
        var audio = Enumerable.Range(0, 8000).Select(_ => (short)rnd.Next(-30, 30)).Concat(Pcm("luna")).ToArray();
        for (var i = 0; i < audio.Length; i += 640) wake.Feed(audio.AsSpan(i, Math.Min(640, audio.Length - i)));
        await Task.Delay(1500);
        Assert.Equal(1, woke);
        var lua = 0;
        using var other = new WakeWord(WakeModel.Bundled());
        other.Woke += _ => Interlocked.Increment(ref lua);
        var neg = Enumerable.Range(0, 8000).Select(_ => (short)rnd.Next(-30, 30)).Concat(Pcm("lua")).ToArray();
        for (var i = 0; i < neg.Length; i += 640) other.Feed(neg.AsSpan(i, Math.Min(640, neg.Length - i)));
        await Task.Delay(1500);
        Assert.Equal(0, lua);
    }

    [Fact]
    public async Task Rate_limit_is_retried()
    {
        // Visto en Windows real: Foundry contestó 429 («exceeded token rate limit») y la orden se perdía.
        var step = 0;
        var h = new FakeHandler((_, _) => Interlocked.Increment(ref step) == 1
            ? ((HttpStatusCode)429, """{"error":{"message":"exceeded token rate limit"}}""")
            : (HttpStatusCode.OK, Calls(("done", """{"message":"ok"}"""))));
        var s = Azure();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, new FakeComputer()) { SettleMs = 0 };
        var events = new List<AgentEvent>();
        agent.Event += e => { lock (events) events.Add(e); };
        await agent.Hear("abre una pestaña");
        Assert.Equal(AgentEventKind.Done, events[^1].Kind);
        Assert.Equal(2, step);

        // Azure también lo manda dentro del stream, con HTTP 200.
        step = 0;
        var inStream = new FakeHandler((_, _) => (HttpStatusCode.OK, Interlocked.Increment(ref step) == 1
            ? "event: error\ndata: {\"type\":\"error\",\"message\":\"Your requests to gpt-5.6-luna have exceeded token rate limit.\"}\n\n"
            : Calls(("done", """{"message":"ok"}"""))));
        var a2 = new Agent(new AiClient(s, new HttpClient(inStream)), s, new FakeComputer()) { SettleMs = 0 };
        var ev2 = new List<AgentEvent>();
        a2.Event += e => { lock (ev2) ev2.Add(e); };
        await a2.Hear("abre una pestaña");
        Assert.Equal(AgentEventKind.Done, ev2[^1].Kind);
        Assert.Equal(2, step);
    }

    [Fact]
    public async Task Same_click_over_and_over_warns_then_stops()
    {
        // Visto en Windows real: el clic funcionó, la página no cambió a la vista y el modelo clicaba sin fin.
        var bodies = new List<string>();
        var h = new FakeHandler((_, body) => { lock (bodies) bodies.Add(body); return (HttpStatusCode.OK, Calls(("click", """{"x":94,"y":285}"""))); });
        var s = Azure();
        var pc = new FakeComputer();
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, pc) { SettleMs = 0 };
        var events = new List<AgentEvent>();
        agent.Event += e => { lock (events) events.Add(e); };
        await agent.Hear("pulsa aceptar");
        Assert.Equal(3, pc.Done.Count);
        Assert.Contains("3 veces seguidas", bodies[3]);
        Assert.Equal(AgentEventKind.Error, events[^1].Kind);
        Assert.False(agent.Busy);
    }

    [Fact]
    public void Tools_are_not_strict_and_mark_zero_means_no_mark()
    {
        // La Responses API hace estrictas las funciones si no se dice nada: el modelo manda mark 0 para clicar x,y.
        Assert.All(Tools.Build(null), t => Assert.False(t!["strict"]!.GetValue<bool>()));
        var call = new ToolCall("click", (JsonObject)JsonNode.Parse("""{"mark":0,"x":300,"y":200}""")!);
        Assert.Null(call.Mark());
        Assert.Equal(300, call.Int("x"));
        Assert.Equal(7, new ToolCall("click", (JsonObject)JsonNode.Parse("""{"mark":7}""")!).Mark());
    }

    [Fact]
    public void Keys_parse()
    {
        Assert.Equal([(ushort)0x11, (ushort)0x4C], Keys.Parse("ctrl+l").Select(k => k.Vk));
        Assert.Equal([(ushort)0x11, (ushort)0x10, (ushort)0x54], Keys.Parse("Ctrl + Shift + T").Select(k => k.Vk));
        Assert.True(Keys.Parse("win").Single().Extended);
        Assert.Equal((ushort)0x73, Keys.Parse("alt+f4")[1].Vk);
        Assert.Equal((ushort)0xBB, Keys.Parse("ctrl++")[1].Vk);
        Assert.Throws<ArgumentException>(() => Keys.Parse("ctrl+banana"));
    }

    /// <summary>Carpeta .models del repo: whisper/ggml-base-q5_1.bin y es.wav (audio real en español).</summary>
    static string? ModelsDir()
    {
        var d = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.models"));
        return File.Exists(Path.Combine(d, "whisper", "ggml-base-q5_1.bin")) && File.Exists(Path.Combine(d, "es.wav")) ? d : null;
    }

    static LocalStt Whisper(string dir) => new(Path.Combine(dir, "whisper", "ggml-base-q5_1.bin")) { Language = "es" };

    [Fact]
    public void Whisper_transcribes_real_spanish_fast()
    {
        var dir = ModelsDir();
        if (dir is null) { output.WriteLine("sin modelos; omitido"); return; }
        using var stt = Whisper(dir);
        var audio = Wav.Read(Path.Combine(dir, "es.wav"), out var rate);
        stt.Transcribe(audio, rate);
        var sw = Stopwatch.StartNew();
        var text = stt.Transcribe(audio, rate);
        output.WriteLine($"{audio.Length / (double)rate:0.0} s en {sw.ElapsedMilliseconds} ms: {text}");
        Assert.Contains("país", text.ToLowerInvariant());
        Assert.Equal("", stt.Transcribe(new float[16000]));
        stt.Dispose();
        Assert.Equal("", stt.Transcribe(audio, rate));
    }

    [Fact]
    public void Listener_cuts_phrases_on_silence_and_ignores_quiet_noise()
    {
        var dir = ModelsDir();
        if (dir is null) return;
        var speech = Wav.Read(Path.Combine(dir, "es.wav"), out _);
        var rnd = new Random(3);
        var noise = Enumerable.Range(0, 24000).Select(_ => (float)(rnd.NextDouble() * 2 - 1) * 0.003f).ToArray();
        var stream = noise.Concat(speech).Concat(noise).Concat(speech).Concat(noise).Concat(noise).ToArray();
        var pcm = stream.Select(f => (short)(f * 32767)).ToArray();
        using var l = new Listener(6);
        var got = new List<float[]>();
        var flips = new List<bool>();
        l.Utterance += got.Add;
        l.SpeakingChanged += flips.Add;
        for (var i = 0; i < pcm.Length; i += 640) l.Feed(pcm.AsSpan(i, Math.Min(640, pcm.Length - i)));
        output.WriteLine($"frases: {string.Join(", ", got.Select(g => $"{g.Length / 16000.0:0.0}s"))}");
        Assert.Equal(2, got.Count);
        Assert.Equal([true, false, true, false], flips);
        using var stt = Whisper(dir);
        Assert.Contains("país", stt.Transcribe(got[1]).ToLowerInvariant());

        l.Dispose();
        l.Feed(pcm.AsSpan(0, 640));   // después de liberar no hace nada ni revienta
        l.Reset();
    }

    [Fact]
    public void Motion_SalidaYLlegadaSuaves_ConArcoLeve()
    {
        // Arranca y frena con velocidad cero y nunca se pasa del final.
        Assert.Equal(0, Motion.Ease(0));
        Assert.Equal(1, Motion.Ease(1), 9);
        Assert.True(Motion.Ease(0.01) < 0.0001);
        Assert.True(1 - Motion.Ease(0.99) < 0.0001);
        for (var t = 0.0; t <= 1; t += 0.01) Assert.InRange(Motion.Ease(t + 0.01) - Motion.Ease(t), 0, 0.02);

        // Más lejos tarda más, con tope; quieto no tarda nada.
        Assert.Equal(0, Motion.Duration(1));
        Assert.True(Motion.Duration(100) < Motion.Duration(800));
        Assert.InRange(Motion.Duration(5000), 0, 420);

        // Termina exacto donde debe; en el medio se separa un poco de la recta, sin pasarse de 36 px.
        var end = Motion.At(10, 20, 1210, 620, 1);
        Assert.Equal(1210, end.X, 6);
        Assert.Equal(620, end.Y, 6);
        var mid = Motion.At(0, 0, 1000, 0, 0.5);
        Assert.InRange(Math.Abs(mid.Y), 5, 36);
        Assert.Equal(0, Motion.At(0, 0, 1000, 0, 0.5, curved: false).Y);
    }
}
