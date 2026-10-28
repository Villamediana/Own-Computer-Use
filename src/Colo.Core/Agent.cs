using System.Text;
using System.Text.Json.Nodes;

namespace Colo.Core;

public enum AgentEventKind { Thinking, Action, Message, Done, Ignored, Error, Stopped }

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

    public int MaxSteps { get; init; } = 40;
    public int SettleMs { get; init; } = 250;
    /// <summary>La misma acción idéntica seguida: a la tercera se avisa al modelo, a la cuarta se para.</summary>
    public int MaxRepeats { get; init; } = 3;
    /// <summary>Herramientas de servidores MCP (se cambia al guardar Ajustes).</summary>
    public IToolHost? Extra { get; set; }
    public event Action<AgentEvent>? Event;

    public Agent(AiClient ai, Settings settings, IComputer pc)
    {
        _ai = ai;
        _s = settings;
        _pc = pc;
    }

    public bool Busy { get { lock (_lock) return _running; } }

    /// <summary>Lo que dijo el usuario: arranca una tarea o, si hay una en marcha, se suma a ella.</summary>
    public Task Hear(string text)
    {
        lock (_lock)
        {
            if (_running)
            {
                _incoming.Enqueue(text);
                return Task.CompletedTask;
            }
            _running = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            return Task.Run(() => RunAsync(text, ct));
        }
    }

    public void Stop()
    {
        lock (_lock) _cts?.Cancel();
    }

    public void ForgetContext()
    {
        lock (_lock) _past.Clear();
    }

    async Task RunAsync(string first, CancellationToken ct)
    {
        var heard = new List<string> { first };
        var log = new List<string>();
        var fresh = new List<string>();
        string? final = null;
        var released = false;
        var look = false;
        string? lastCall = null;
        var repeats = 0;
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
                fresh.Clear();

                var message = new StringBuilder();
                var anyCall = false;
                var acted = false;
                var finished = false;
                var ignored = false;
                // Cada herramienta se ejecuta en cuanto llega del stream, sin esperar al resto.
                await foreach (var item in _ai.StreamAsync(body, ct))
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
                    anyCall = true;
                    var call = ParseCall(item);
                    if (call.Name == "done") { final = call.Str("message"); finished = true; break; }
                    if (call.Name == "ignore") { ignored = finished = true; break; }
                    if (call.Name == "look")
                    {
                        look = true;
                        log.Add("look → la captura va en este turno");
                        break;
                    }

                    var label = Describe(call, obs);
                    // Un clic que funcionó pero no cambia nada visible hacía repetirlo hasta agotar los pasos.
                    // Bajar, esperar o pasar página varias veces seguidas sí es normal.
                    var signature = call.Name is "scroll" or "wait" or "press_keys" ? null : call.Name + call.Args.ToJsonString();
                    repeats = signature is not null && signature == lastCall ? repeats + 1 : 1;
                    lastCall = signature;
                    if (repeats > MaxRepeats)
                    {
                        Remember(heard, "se repetía sin avanzar");
                        Emit(AgentEventKind.Error, $"Me detuve: repetía «{label}».");
                        return;
                    }
                    Emit(AgentEventKind.Action, label);
                    acted = true;
                    string result;
                    try
                    {
                        result = Extra is { } host && host.Handles(call.Name)
                            ? await host.CallAsync(call.Name, call.Args, ct)
                            : await _pc.ExecuteAsync(call, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { result = "error: " + ex.Message; }
                    // El resultado largo viaja entero solo en el turno siguiente; en el registro queda corto.
                    if (result.Length > 300) fresh.Add($"{label}:\n{result}");
                    log.Add($"{label} → {Trunc(result, 300)}");
                    if (repeats == MaxRepeats)
                        log.Add($"Ya hiciste «{label}» {repeats} veces seguidas. Si el pedido está cumplido, done; si no tuvo efecto, prueba de otra forma.");
                    if (result.StartsWith("error", StringComparison.Ordinal)) break;
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
                    Remember(heard, final);
                    Emit(AgentEventKind.Done, string.IsNullOrWhiteSpace(final) ? "Listo." : final!);
                    return;
                }

                // Solo se espera a que la pantalla se asiente si algo cambió en este paso.
                if (SettleMs > 0 && acted) await Task.Delay(SettleMs, ct);
                if (log.Count > 40) log.RemoveRange(0, log.Count - 40);
            }
            Remember(heard, "se cortó por demasiados pasos");
            Emit(AgentEventKind.Error, "Me detuve: demasiados pasos.");
        }
        catch (OperationCanceledException)
        {
            Remember(heard, "el usuario la detuvo");
            Emit(AgentEventKind.Stopped, "Detenido.");
        }
        catch (Exception ex)
        {
            Emit(AgentEventKind.Error, ex.Message);
        }
        finally
        {
            if (!released)
                lock (_lock)
                {
                    _running = false;
                    _incoming.Clear();
                }
        }
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

    void Remember(List<string> heard, string? outcome)
    {
        lock (_lock)
        {
            _past.Add($"«{string.Join(" / ", heard)}» → {(string.IsNullOrWhiteSpace(outcome) ? "hecho" : outcome)}");
            if (_past.Count > 6) _past.RemoveAt(0);
        }
    }

    void Emit(AgentEventKind kind, string text) => Event?.Invoke(new AgentEvent(kind, text));

    public JsonObject BuildRequest(IReadOnlyList<string> heard, IReadOnlyList<string> log, Observation obs,
        IReadOnlyList<string>? fresh = null)
    {
        var sb = new StringBuilder();
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
        sb.AppendLine(withImage
            ? $"Captura {obs.Width}x{obs.Height} adjunta. Elementos:"
            : "Sin captura en este turno (look si la necesitas). Elementos:");
        foreach (var m in obs.Marks)
        {
            sb.Append('[').Append(m.Id).Append("] ").Append(m.Role);
            if (m.Name.Length > 0) sb.Append(" «").Append(m.Name).Append('»');
            if (m.Value.Length > 0) sb.Append(" =").Append(m.Value);
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
            ["instructions"] = Tools.System,
            ["tools"] = tools,
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = true,
            ["store"] = false,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };
        if (!string.IsNullOrEmpty(_s.ReasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = _s.ReasoningEffort };
        return body;
    }

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
