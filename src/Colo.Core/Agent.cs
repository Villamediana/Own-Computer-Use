using System.Text;
using System.Text.Json.Nodes;

namespace Colo.Core;

public enum AgentEventKind { Thinking, Action, Message, Done, Ignored, Error, Stopped, Listening }

public sealed record AgentEvent(AgentEventKind Kind, string Text);

/// <summary>
/// Bucle pantalla → modelo → acciones. Cada turno es independiente (sin estado en el servidor):
/// lo que dijo el usuario, un registro corto de lo hecho y la pantalla actual (lista de elementos y,
/// si hace falta, la captura), así la petición no crece con los pasos.
/// </summary>
public sealed class Agent
{
    readonly AiClient _ai;
    readonly Settings _s;
    readonly IComputer _pc;
    readonly object _lock = new();
    readonly Queue<string> _incoming = new();
    readonly List<string> _past = [];
    bool _running;
    CancellationTokenSource? _cts;
    // El paso en curso: si dice algo nuevo a mitad, se corta para que lo lea ya (no sigue con lo de antes).
    CancellationTokenSource? _step;

    /// <summary>Tope de pasos por orden: corta una tarea que se enreda antes de gastar demasiado.</summary>
    public int MaxSteps { get; init; } = 25;
    public int SettleMs { get; init; } = 250;
    /// <summary>La misma acción idéntica seguida: a la tercera se avisa al modelo, a la cuarta se para.</summary>
    public int MaxRepeats { get; init; } = 3;
    /// <summary>Herramientas de servidores MCP (se cambia al guardar Ajustes).</summary>
    public IToolHost? Extra { get; set; }
    /// <summary>Lo que sabe del usuario entre días (null = sin memoria).</summary>
    public Memory? Memory { get; set; }
    public event Action<AgentEvent>? Event;

    public Agent(AiClient ai, Settings settings, IComputer pc)
    {
        _ai = ai;
        _s = settings;
        _pc = pc;
    }

    /// <summary>Tras este tiempo sin pedidos, los anteriores dejan de contar: el contexto se limpia solo.</summary>
    public TimeSpan ContextTtl { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Cuánto espera a que siga hablando cuando el modelo ve la orden a medias (listen).</summary>
    public TimeSpan ListenFor { get; init; } = TimeSpan.FromSeconds(7);
    DateTime _lastTask = DateTime.MinValue;

    public bool Busy { get { lock (_lock) return _running; } }

    /// <summary>Lo que dijo el usuario: arranca una tarea o, si hay una en marcha, se suma a ella.</summary>
    public Task Hear(string text)
    {
        lock (_lock)
        {
            if (_running)
            {
                _incoming.Enqueue(text);
                try { _step?.Cancel(); } catch (ObjectDisposedException) { }
                return Task.CompletedTask;
            }
            _running = true;
            if (DateTime.Now - _lastTask > ContextTtl) _past.Clear();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            return Task.Run(() => RunAsync(text, ct));
        }
    }

    public void Stop()
    {
        lock (_lock) _cts?.Cancel();
    }

    async Task RunAsync(string first, CancellationToken ct)
    {
        var heard = new List<string> { first };
        var log = new List<string>();
        var fresh = new List<string>();
        string? final = null;
        var released = false;
        var look = false;
        // Lo hecho en esta tarea, para que el próximo pedido («ahí no», «en la barra») sepa a qué se refiere.
        var did = new List<string>();
        string? lastCall = null;
        var waits = 0;
        var repeats = 0;
        var quietListens = 0;
        try
        {
            for (var step = 0; step < MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                var obs = await _pc.ObserveAsync(_s.Vision == Vision.Always || look, ct);
                look = false;
                DrainIncoming(heard, log);
                Emit(AgentEventKind.Thinking, step == 0 ? "Pensando…" : "Mirando la pantalla…");
                var body = BuildRequest(heard, log, obs, fresh);
                var sentFresh = fresh.ToList();
                fresh.Clear();

                var message = new StringBuilder();
                var anyCall = false;
                var acted = false;
                var finished = false;
                var ignored = false;
                var listening = false;
                var stopped = false;
                var closed = false;
                string? closing = null;
                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_lock)
                {
                    _step = stepCts;
                    if (_incoming.Count > 0) stepCts.Cancel();
                }
                var stepCt = stepCts.Token;
                // Cada herramienta se ejecuta en cuanto llega del stream, sin esperar al resto.
                try
                {
                    await foreach (var item in _ai.StreamAsync(body, stepCt))
                    {
                        var type = item["type"]?.GetValue<string>();
                        if (type == "message")
                        {
                            foreach (var c in item["content"]?.AsArray() ?? [])
                                if (c?["type"]?.GetValue<string>() == "output_text")
                                    message.Append(c["text"]?.GetValue<string>());
                            continue;
                        }
                        if (type != "function_call") continue;
                        // Tras done/ignore/look no se ejecuta nada más, pero se lee hasta el final del stream:
                        // ahí llega el uso de tokens del paso.
                        if (closed) continue;
                        anyCall = true;
                        var call = ParseCall(item);
                        // Un done en una acción que no fue la última no cierra nada: venían más pasos
                        // (recordar algo después no cuenta como paso).
                        if (call.Name is not ("remember" or "forget")) closing = null;
                        if (call.Name == "done") { final = call.Str("message"); finished = closed = true; continue; }
                        if (call.Name == "stop") { stopped = finished = closed = true; continue; }
                        if (call.Name == "ignore") { ignored = finished = closed = true; continue; }
                        if (call.Name == "listen" && !acted) { listening = closed = true; continue; }
                        if (call.Name == "look")
                        {
                            look = closed = true;
                            log.Add("look → la captura va en este turno");
                            continue;
                        }

                        var label = Describe(call, obs);
                        // Un clic que funcionó pero no cambia nada visible hacía repetirlo hasta agotar los pasos.
                        // Bajar, esperar o pasar página varias veces seguidas sí es normal.
                        var signature = call.Name is "scroll" or "wait" or "press_keys" ? null : call.Name + call.Args.ToJsonString();
                        repeats = signature is not null && signature == lastCall ? repeats + 1 : 1;
                        lastCall = signature;
                        if (repeats > MaxRepeats)
                        {
                            Remember(heard, did, "se repetía sin avanzar");
                            Emit(AgentEventKind.Error, $"Me detuve: repetía «{label}».");
                            return;
                        }
                        Emit(AgentEventKind.Action, label);
                        var remembers = call.Name is "remember" or "forget";
                        // Recordar no toca la pantalla: no hace falta esperar a que se asiente.
                        if (!remembers) acted = true;
                        string result;
                        try
                        {
                            result = remembers
                                ? Memory is { } mem
                                    ? call.Name == "remember" ? mem.Remember(call.Str("fact")) : mem.Forget(call.Str("fact"))
                                    : "error: memoria apagada"
                                : Extra is { } host && host.Handles(call.Name)
                                    ? await host.CallAsync(call.Name, call.Args, stepCt)
                                    : await _pc.ExecuteAsync(call, stepCt);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { result = "error: " + ex.Message; }
                        // El resultado largo viaja entero solo en el turno siguiente; en el registro queda corto.
                        if (result.Length > 300) fresh.Add($"{label}:\n{result}");
                        log.Add($"{label} → {Trunc(result, 300)}");
                        did.Add(result.StartsWith("error", StringComparison.Ordinal) ? $"{label} (falló)" : label);
                        if (repeats == MaxRepeats)
                            log.Add($"Ya hiciste «{label}» {repeats} veces seguidas. Si el pedido está cumplido, done; si no tuvo efecto, prueba de otra forma.");
                        if (result.StartsWith("error", StringComparison.Ordinal)) closed = true;
                        // La acción traía la frase final: si es la última de la respuesta, hecho sin otro turno.
                        else closing = Tools.Finishing.Contains(call.Name) && call.Str("done").Trim() is { Length: > 0 } f ? f : null;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Habló a mitad del paso: lo que iba a hacer se deja y se vuelve a mirar con lo nuevo.
                    log.Add("[te interrumpió: lee lo nuevo que dijo antes de seguir]");
                    step--;
                    continue;
                }
                catch (AiException ex) when (AiClient.IsRateLimit(ex.Message) && !anyCall && message.Length == 0 && waits < 4)
                {
                    // Límite por minuto del proveedor antes de que llegue nada: se espera y se repite el mismo paso.
                    waits++;
                    fresh.AddRange(sentFresh);
                    Emit(AgentEventKind.Thinking, "Un momento…");
                    await Task.Delay(AiClient.RetryDelay(ex.Message, waits), ct);
                    step--;
                    continue;
                }
                waits = 0;
                if (listening && !finished)
                {
                    // Habló a medias (pensando, «escribe algo como…»): se espera lo que sigue sin gastar pasos y
                    // se junta todo. Si no dice nada más, el modelo decide con lo que hay; a la segunda, se deja.
                    Emit(AgentEventKind.Listening, "");
                    step--;
                    if (await WaitForMoreAsync(ct)) continue;
                    if (++quietListens >= 2) { ignored = finished = true; }
                    else
                    {
                        log.Add($"listen → esperaste {ListenFor.TotalSeconds:0} s y no dijo nada más: actúa con lo que hay o pregunta con done");
                        continue;
                    }
                }
                if (closing is not null && !finished)
                {
                    final = closing;
                    finished = true;
                }

                var said = message.ToString().Trim();
                if (said.Length > 0)
                {
                    log.Add("Tú: " + said);
                    if (!finished && anyCall) Emit(AgentEventKind.Message, said);
                }
                if (!anyCall) { finished = true; final = said; }

                if (finished)
                {
                    lock (_lock)
                    {
                        // Si habló mientras el modelo cerraba la tarea, se sigue con lo nuevo.
                        if (_incoming.Count > 0) { final = null; continue; }
                        _running = false;
                        released = true;
                    }
                    if (ignored)
                    {
                        Emit(AgentEventKind.Ignored, "");
                        return;
                    }
                    if (stopped)
                    {
                        Remember(heard, did, "el usuario la detuvo");
                        Emit(AgentEventKind.Stopped, "Detenido.");
                        return;
                    }
                    Remember(heard, did, final);
                    Emit(AgentEventKind.Done, string.IsNullOrWhiteSpace(final) ? "Listo." : final!);
                    return;
                }

                // Solo se espera a que la pantalla se asiente si algo cambió en este paso.
                if (SettleMs > 0 && acted) await Task.Delay(SettleMs, ct);
                if (log.Count > 40) log.RemoveRange(0, log.Count - 40);
            }
            Remember(heard, did, "se cortó por demasiados pasos");
            Emit(AgentEventKind.Error, "Me detuve: demasiados pasos.");
        }
        catch (OperationCanceledException)
        {
            Remember(heard, did, "el usuario la detuvo");
            Emit(AgentEventKind.Stopped, "Detenido.");
        }
        catch (Exception ex)
        {
            Emit(AgentEventKind.Error, ex.Message);
        }
        finally
        {
            lock (_lock) _step = null;
            if (!released)
                lock (_lock)
                {
                    _running = false;
                    _incoming.Clear();
                }
        }
    }

    async Task<bool> WaitForMoreAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow + ListenFor;
        while (DateTime.UtcNow < until)
        {
            lock (_lock) if (_incoming.Count > 0) return true;
            await Task.Delay(80, ct);
        }
        lock (_lock) return _incoming.Count > 0;
    }

    void DrainIncoming(List<string> heard, List<string> log)
    {
        lock (_lock)
        {
            while (_incoming.Count > 0)
            {
                var t = _incoming.Dequeue();
                heard.Add(t);
                log.Add($"[el usuario añadió: «{t}»]");
            }
        }
    }

    void Remember(List<string> heard, List<string> did, string? outcome)
    {
        var steps = did.Count == 0 ? "" : " → hiciste: " + string.Join("; ", did.TakeLast(6));
        lock (_lock)
        {
            _past.Add($"«{string.Join(" / ", heard)}»{steps} → {(string.IsNullOrWhiteSpace(outcome) ? "hecho" : Expressive.Clean(outcome))}");
            if (_past.Count > 8) _past.RemoveAt(0);
            _lastTask = DateTime.Now;
        }
    }

    void Emit(AgentEventKind kind, string text) => Event?.Invoke(new AgentEvent(kind, text));

    public JsonObject BuildRequest(IReadOnlyList<string> heard, IReadOnlyList<string> log, Observation obs,
        IReadOnlyList<string>? fresh = null)
    {
        var sb = new StringBuilder();
        var facts = Memory is { } mem && !(_s.DisabledTools?.Contains("memory") ?? false) ? mem.Facts : [];
        if (facts.Count > 0)
        {
            sb.AppendLine("Lo que sabes del usuario:");
            foreach (var f in facts) sb.AppendLine("- " + f);
            sb.AppendLine();
        }
        string[] past;
        lock (_lock) past = [.. _past];
        if (past.Length > 0)
        {
            sb.AppendLine("Pedidos anteriores (contexto):");
            foreach (var p in past) sb.AppendLine("- " + p);
            sb.AppendLine();
        }
        sb.AppendLine("El usuario dijo:");
        foreach (var h in heard) sb.AppendLine($"«{h}»");
        if (log.Count > 0)
        {
            sb.AppendLine().AppendLine("Ya hiciste:");
            foreach (var l in log) sb.AppendLine("- " + l);
        }
        if (fresh is { Count: > 0 })
        {
            sb.AppendLine().AppendLine("Resultado completo de lo último:");
            foreach (var f in fresh) sb.AppendLine(f);
        }
        var withImage = obs.Jpeg.Length > 0;
        sb.AppendLine().AppendLine($"Ventana activa: {obs.Window}");
        if (obs.Others is { Count: > 0 } others) sb.AppendLine("Otras ventanas: " + string.Join(" · ", others));
        sb.AppendLine(withImage
            ? $"Captura {obs.Width}x{obs.Height} adjunta. Elementos:"
            : "Sin captura en este turno (look si la necesitas). Elementos:");
        foreach (var m in obs.Marks)
        {
            sb.Append('[').Append(m.Id).Append("] ").Append(m.Role);
            if (m.Name.Length > 0) sb.Append(" «").Append(m.Name).Append('»');
            if (m.Value.Length > 0) sb.Append(" =").Append(m.Value);
            if (m.State.Length > 0) sb.Append(" (").Append(m.State).Append(')');
            sb.AppendLine();
        }
        if (obs.Marks.Count == 0) sb.AppendLine(withImage ? "(ninguno; usa x,y)" : "(ninguno; llama look)");

        var content = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = sb.ToString() });
        if (withImage)
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(obs.Jpeg),
                ["detail"] = "high",
            });

        // Instrucciones y tools van primero y no cambian entre pasos: el proveedor las cachea.
        var tools = Tools.Build(_s.DisabledTools);
        if (Extra is { } host)
            foreach (var d in host.Definitions) tools.Add(d.DeepClone());

        var body = new JsonObject
        {
            ["model"] = _s.Model,
            ["instructions"] = SpeaksExpressively() ? Tools.System + Expressive.Note : Tools.System,
            ["tools"] = tools,
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = true,
            ["store"] = false,
            // Todo lo fijo (instrucciones, tools) va delante y lo que cambia al final: con la misma clave el
            // proveedor reutiliza el prefijo cacheado entre pasos y pedidos.
            ["prompt_cache_key"] = "colo",
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };
        if (!string.IsNullOrEmpty(_s.ReasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = _s.ReasoningEffort };
        return body;
    }

    /// <summary>La respuesta la dirá una voz que interpreta etiquetas de emoción.</summary>
    bool SpeaksExpressively() => _s.SpeakReplies && _s.ReplyVoice == ReplyVoice.ElevenLabs
        && !string.IsNullOrWhiteSpace(_s.ElevenLabsKey) && ElevenLabsTts.ActsTags(_s.ElevenTtsModel);

    static ToolCall ParseCall(JsonNode item)
    {
        JsonObject args;
        try { args = JsonNode.Parse(item["arguments"]?.GetValue<string>() ?? "{}") as JsonObject ?? []; }
        catch { args = []; }
        return new ToolCall(item["name"]!.GetValue<string>(), args);
    }

    public static string Describe(ToolCall c, Observation obs)
    {
        string Target()
        {
            if (c.Mark() is int id)
            {
                var m = obs.Marks.FirstOrDefault(x => x.Id == id);
                return m is null ? $" [{id}]" : $" [{id}] {(m.Name.Length > 0 ? "«" + Trunc(m.Name, 40) + "»" : m.Role)}";
            }
            return c.Int("x") is int x && c.Int("y") is int y ? $" ({x},{y})" : "";
        }
        return c.Name switch
        {
            "click" => (c.Bool("double") ? "Doble clic" : c.Str("button") == "right" ? "Clic derecho" : "Clic") + Target(),
            "type_text" => $"Escribe «{Trunc(c.Str("text"), 60)}»" + (c.Mark() is not null ? " en" + Target() : "")
                + (c.Bool("submit") ? " ⏎" : ""),
            "dictate" => $"Dicta «{Trunc(c.Str("text"), 60)}»",
            "edit_text" => $"Cambia «{Trunc(c.Str("find"), 30)}» por «{Trunc(c.Str("replace"), 30)}»",
            "press_keys" => $"Teclas {c.Str("keys")}" + (c.Int("repeat") is > 1 and var n ? $" ×{n}" : ""),
            "scroll" => c.Str("to") switch
            {
                "top" => "Al principio",
                "bottom" => "Al final",
                _ => $"Desplaza {c.Str("direction")}" + Target(),
            },
            "drag" => "Arrastra",
            "browser" => c.Str("action") switch
            {
                "go" => $"Va a {c.Str("url")}",
                "new_tab" => "Pestaña nueva" + (c.Str("url").Length > 0 ? " " + c.Str("url") : ""),
                "search" => $"Busca «{c.Str("text")}»",
                "find" => $"Busca en la página «{c.Str("text")}»",
                "back" => "Atrás",
                "forward" => "Adelante",
                "reload" => "Recarga",
                "close_tab" => "Cierra la pestaña",
                "next_tab" or "prev_tab" or "tab" => "Cambia de pestaña",
                var a => a,
            },
            "open" => $"Abre {c.Str("target")}",
            "remember" => $"Recuerda «{Trunc(c.Str("fact"), 60)}»",
            "forget" => $"Olvida «{Trunc(c.Str("fact"), 60)}»",
            "window" => c.Str("action") switch
            {
                "list" => "Mira las ventanas",
                "focus" => $"Cambia a {c.Str("name")}",
                "minimize" => "Minimiza",
                "maximize" => "Maximiza",
                "close" => "Cierra la ventana",
                var a => a,
            },
            "read_text" => "Lee la pantalla",
            "clipboard" => c.Str("action") == "set" ? "Copia al portapapeles" : "Lee el portapapeles",
            "screenshot" => c.Str("area") == "window" ? "Print de la ventana" : "Print de la pantalla",
            "find_files" => $"Busca archivos «{c.Str("query")}»",
            "wait" => "Espera",
            var n when n.StartsWith("mcp_", StringComparison.Ordinal) => n[4..].Replace('_', ' '),
            _ => c.Name,
        };
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
