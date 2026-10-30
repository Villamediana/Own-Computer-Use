using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Colo.Core;

/// <summary>
/// Conversación en vivo con GPT-Live (/openai/v1/live/sessions, WebSocket). GPT-Live habla y escucha; lo que
/// haya que hacer en el PC lo delega en el cliente (session.delegation.created) y Colo le devuelve el
/// resultado con session.commentary.append para que lo diga. Audio PCM16 mono 24 kHz en los dos sentidos.
/// </summary>
public sealed class LiveCall : IAsyncDisposable
{
    public const int Rate = 24000;

    /// <summary>
    /// Fijas: van una vez al abrir la llamada. Cortas a propósito; las reglas finas son del agente que actúa.
    /// </summary>
    public const string Instructions = """
Eres Colo, la voz de un asistente que maneja el PC con Windows de alguien con un bebé en brazos. Te habla
manteniendo un atajo apretado, en español o portugués. Tú no tocas el PC: todo lo que haya que hacer, mirar o
leer en él (abrir, escribir, dictar, buscar, leer la pantalla o un correo…) lo delegas con lo que pidió.
Mientras se hace, como mucho una frase muy corta. Cuando llegue el resultado, dilo en una frase natural.
Si pide algo más o corrige, delega otra vez. Charla o dudas generales: responde tú, breve.
Responde en el idioma en que te habló.
""";

    readonly ClientWebSocket _ws = new();
    readonly Channel<string> _out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    readonly StringBuilder _heard = new();
    readonly object _lock = new();
    readonly Upsampler _up = new();
    Task? _loops;

    /// <summary>Lo que transcribe de ti, a trozos.</summary>
    public event Action<string>? Heard;
    /// <summary>Lo que dice, a trozos.</summary>
    public event Action<string>? Said;
    /// <summary>Audio para el altavoz (PCM16 mono 24 kHz).</summary>
    public event Action<byte[]>? Audio;
    /// <summary>Una tarea para Colo: id de la delegación y lo que dijiste desde la anterior.</summary>
    public event Action<string, string>? Delegated;
    /// <summary>Para el registro.</summary>
    public event Action<string>? Trace;
    /// <summary>Se cerró (razón o error).</summary>
    public event Action<string>? Closed;

    public string SessionId { get; private set; } = "";
    public bool Open => _ws.State == WebSocketState.Open;

    /// <summary>wss://…/openai/v1/live/sessions en Foundry; wss://api.openai.com/v1/live/sessions en OpenAI.</summary>
    public static Uri Url(Settings connection)
    {
        var root = AiClient.Root(connection);
        var http = connection.Provider == Provider.OpenAI ? root + "/live/sessions" : root + "/openai/v1/live/sessions";
        return new Uri("ws" + http[4..]);
    }

    public static JsonObject StartEvent(Settings s) => new()
    {
        ["type"] = "session.start",
        ["session"] = new JsonObject
        {
            ["model"] = s.LiveModel.Trim(),
            ["instructions"] = Instructions,
            ["audio"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = Rate },
                ["output"] = new JsonObject { ["voice"] = string.IsNullOrWhiteSpace(s.LiveVoice) ? "marin" : s.LiveVoice.Trim() },
            },
            ["delegation"] = new JsonObject { ["type"] = "client" },
        },
    };

    public async Task StartAsync(Settings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.LiveModel)) throw new AiException("Falta el modelo de GPT-Live.");
        var c = AiClient.VoiceConnection(s);
        if (string.IsNullOrWhiteSpace(c.ApiKey)) throw new AiException("Falta la API key de la voz.");
        if (c.Provider == Provider.OpenAI) _ws.Options.SetRequestHeader("Authorization", "Bearer " + c.ApiKey.Trim());
        else _ws.Options.SetRequestHeader("api-key", c.ApiKey.Trim());
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        link.CancelAfter(TimeSpan.FromSeconds(15));
        try { await _ws.ConnectAsync(Url(c), link.Token); }
        catch (WebSocketException ex) { throw new AiException("GPT-Live no conectó: " + ex.Message); }
        await SendAsync(StartEvent(s).ToJsonString(), link.Token);
        var first = JsonNode.Parse(await ReceiveAsync(link.Token) ?? "{}");
        if (first?["type"]?.GetValue<string>() != "session.started")
            throw new AiException("GPT-Live: " + (first?["error"]?["message"]?.GetValue<string>() ?? first?.ToJsonString() ?? "sin respuesta"));
        SessionId = first["session"]?["id"]?.GetValue<string>() ?? "";
        _loops = Task.WhenAll(Task.Run(SendLoopAsync), Task.Run(ReceiveLoopAsync));
    }

    /// <summary>Audio del micrófono (16 kHz): se pasa a 24 kHz y se manda.</summary>
    public void Append(ReadOnlySpan<short> pcm16k)
    {
        if (pcm16k.Length == 0) return;
        byte[] bytes;
        lock (_lock) bytes = _up.Push(pcm16k);
        if (bytes.Length == 0) return;
        Post(new JsonObject { ["type"] = "session.input_audio.append", ["audio"] = Convert.ToBase64String(bytes) });
    }

    public void Mute() => Post(new JsonObject { ["type"] = "session.input_audio.mute" });
    public void Unmute() => Post(new JsonObject { ["type"] = "session.input_audio.unmute" });

    /// <summary>Resultado de una tarea, para que lo diga en voz alta.</summary>
    public void Commentary(string delegationId, string text) => Post(new JsonObject
    {
        ["type"] = "session.commentary.append",
        ["delegation_id"] = delegationId,
        ["content"] = Trim(text),
    });

    /// <summary>Contexto silencioso (no se dice).</summary>
    public void Thinking(string delegationId, string text) => Post(new JsonObject
    {
        ["type"] = "session.thinking.append",
        ["delegation_id"] = delegationId,
        ["content"] = Trim(text),
    });

    // El límite es de 500 tokens: con 1200 caracteres sobra.
    static string Trim(string t) => t.Length > 1200 ? t[..1200] + "…" : t;

    void Post(JsonObject ev) => _out.Writer.TryWrite(ev.ToJsonString());

    async Task SendLoopAsync()
    {
        try
        {
            await foreach (var json in _out.Reader.ReadAllAsync(_cts.Token)) await SendAsync(json, _cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
    }

    Task SendAsync(string json, CancellationToken ct) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    async Task ReceiveLoopAsync()
    {
        var reason = "connection_lost";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var text = await ReceiveAsync(_cts.Token);
                if (text is null) break;
                JsonNode? ev;
                try { ev = JsonNode.Parse(text); } catch { continue; }
                switch (ev?["type"]?.GetValue<string>())
                {
                    case "session.output_audio.delta":
                        if (ev["delta"]?.GetValue<string>() is { Length: > 0 } a) Audio?.Invoke(Convert.FromBase64String(a));
                        break;
                    case "session.input_transcript.delta":
                        var heard = ev["delta"]?.GetValue<string>() ?? "";
                        lock (_lock) _heard.Append(heard);
                        Heard?.Invoke(heard);
                        break;
                    case "session.output_transcript.delta":
                        Said?.Invoke(ev["delta"]?.GetValue<string>() ?? "");
                        break;
                    case "session.delegation.created":
                        var id = ev["delegation"]?["id"]?.GetValue<string>() ?? "";
                        _ = DelegateAsync(id);
                        break;
                    case "session.closed":
                        reason = ev["reason"]?.GetValue<string>() ?? "closed";
                        Trace?.Invoke($"live: cerrada ({reason}) · {ev["usage"]?["seconds"]} s de voz");
                        _cts.Cancel();
                        break;
                    case "error":
                        Trace?.Invoke("live: error · " + (ev["error"]?["message"]?.GetValue<string>() ?? text));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        Closed?.Invoke(reason);
    }

    async Task DelegateAsync(string id)
    {
        // La transcripción de lo último que dijo puede llegar un pelo después del aviso.
        await Task.Delay(250);
        string request;
        lock (_lock)
        {
            request = _heard.ToString().Trim();
            _heard.Clear();
        }
        Delegated?.Invoke(id, request);
    }

    /// <summary>Cierre ordenado: session.close y se espera session.closed (máx. 5 s).</summary>
    public async Task CloseAsync()
    {
        if (_ws.State != WebSocketState.Open) return;
        Post(new JsonObject { ["type"] = "session.close" });
        if (_loops is not null) await Task.WhenAny(_loops, Task.Delay(5000));
    }

    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync(); } catch { }
        _cts.Cancel();
        _out.Writer.TryComplete();
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
    }

    /// <summary>16 kHz → 24 kHz lineal, continuo entre bloques.</summary>
    public sealed class Upsampler
    {
        short _prev;
        double _t;
        bool _started;

        public byte[] Push(ReadOnlySpan<short> x)
        {
            var output = new List<short>(x.Length * 3 / 2 + 2);
            // buf = [anterior] + x; posiciones de salida cada 2/3 de muestra de entrada.
            var buf = new short[x.Length + 1];
            buf[0] = _prev;
            x.CopyTo(buf.AsSpan(1));
            short At(int i) => buf[i];
            var len = buf.Length;
            var t = _started ? _t : 1;
            for (; t < len - 1; t += 2.0 / 3)
            {
                var k = (int)t;
                var f = t - k;
                output.Add((short)Math.Round(At(k) * (1 - f) + At(k + 1) * f));
            }
            _t = t - (len - 1);
            _prev = x[^1];
            _started = true;
            var bytes = new byte[output.Count * 2];
            Buffer.BlockCopy(output.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}
