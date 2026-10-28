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

    public async Task<Observation> ObserveAsync(CancellationToken ct)
    {
        if (BeforeObserve is not null) await BeforeObserve();
        return new([1, 2, 3], 1366, 768, "Google Chrome", [new Mark(1, "campo de texto", "Buscar"), new Mark(2, "botón", "Enviar")]);
    }

    public Task<string> ExecuteAsync(ToolCall call, CancellationToken ct)
    {
        lock (Done) Done.Add($"{call.Name} {call.Args.ToJsonString()}");
        return Task.FromResult("ok");
    }
}

public class CoreTests(ITestOutputHelper output)
{
    static string Calls(params (string Name, string Args)[] calls)
    {
        var arr = new JsonArray();
        arr.Add(new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray() });
        foreach (var (n, a) in calls)
            arr.Add(new JsonObject { ["type"] = "function_call", ["name"] = n, ["arguments"] = a, ["call_id"] = "c" + n });
        return new JsonObject { ["output"] = arr }.ToJsonString();
    }

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

        await ai.RespondAsync(new JsonObject { ["model"] = "luna" }, default);
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
        Assert.StartsWith("data:image/jpeg;base64,", content[1]!["image_url"]!.GetValue<string>());
        Assert.Contains(first["tools"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "screenshot");

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
            if (JsonNode.Parse(body)!["input"]![0]!["content"]![0]!["text"]!.GetValue<string>().Contains("el usuario añadió")) return (HttpStatusCode.OK, Calls(("done", """{"message":"hecho"}""")));
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

    static string? ModelsDir()
    {
        var d = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.models"));
        return File.Exists(Path.Combine(d, "parakeet-v3", "encoder.int8.onnx")) ? d : null;
    }

    [Fact]
    public void Local_parakeet_transcribes_spanish_and_english()
    {
        var dir = ModelsDir();
        if (dir is null) { output.WriteLine("sin modelos en .models; omitido"); return; }
        var sw = Stopwatch.StartNew();
        using var stt = new LocalStt(new SpeechModels(dir));
        output.WriteLine($"carga: {sw.ElapsedMilliseconds} ms");
        foreach (var lang in new[] { "es", "en" })
        {
            var audio = Wav.Read(Path.Combine(dir, lang + ".wav"), out var rate);
            sw.Restart();
            var text = stt.Transcribe(audio, rate);
            output.WriteLine($"{lang} ({audio.Length / 16000.0:F1} s de audio, {sw.ElapsedMilliseconds} ms): {text}");
            Assert.True(text.Length > 10);
        }
    }

    [Fact]
    public void Listener_cuts_phrases_on_silence()
    {
        var dir = ModelsDir();
        if (dir is null) return;
        var speech = Wav.Read(Path.Combine(dir, "es.wav"), out _);
        using var l = new Listener(Path.Combine(dir, "silero_vad.onnx"), 6);
        var got = new List<float[]>();
        var flips = new List<bool>();
        l.Utterance += got.Add;
        l.SpeakingChanged += flips.Add;
        // frase · 1,5 s de silencio · frase · silencio, en trozos de 40 ms como llega del micrófono
        var silence = new float[24000];
        var stream = silence.Concat(speech).Concat(silence).Concat(speech).Concat(silence).ToArray();
        var pcm = stream.Select(f => (short)(f * 32767)).ToArray();
        for (var i = 0; i < pcm.Length; i += 640) l.Feed(pcm.AsSpan(i, Math.Min(640, pcm.Length - i)));
        output.WriteLine($"frases: {got.Count} ({string.Join(", ", got.Select(g => $"{g.Length / 16000.0:F1}s"))}); cambios: {flips.Count}");
        Assert.True(got.Count >= 2);
        Assert.Contains(true, flips);

        using var stt = new LocalStt(new SpeechModels(dir));
        output.WriteLine("1ª frase: " + stt.Transcribe(got[0]));
    }
}
