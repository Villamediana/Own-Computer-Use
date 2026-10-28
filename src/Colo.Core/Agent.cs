using System.Text;
using System.Text.Json.Nodes;

namespace Colo.Core;

public enum AgentEventKind { Thinking, Action, Message, Done, Ignored, Error, Stopped }

public sealed record AgentEvent(AgentEventKind Kind, string Text);

/// <summary>
/// Bucle captura → modelo → acciones. Cada turno es independiente (sin estado en el servidor):
/// se manda lo que dijo el usuario, un registro corto de lo hecho y solo la captura actual,
/// así el tamaño de la petición no crece con los pasos.
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
    public int SettleMs { get; init; } = 350;
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
        string? final = null;
        var released = false;
        try
        {
            for (var step = 0; step < MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                var obs = await _pc.ObserveAsync(ct);
                DrainIncoming(heard, log);
                Emit(AgentEventKind.Thinking, step == 0 ? "Pensando…" : "Mirando la pantalla…");
                var resp = await _ai.RespondAsync(BuildRequest(heard, log, obs), ct);
                var (message, calls) = Parse(resp);

                if (message.Length > 0)
                {
                    log.Add("Tú: " + message);
                    if (calls.Count > 0) Emit(AgentEventKind.Message, message);
                }

                var finished = calls.Count == 0;
                var ignored = false;
                if (finished) final = message;

                foreach (var call in calls)
                {
                    if (call.Name == "done") { final = call.Str("message"); finished = true; break; }
                    if (call.Name == "ignore") { ignored = finished = true; break; }

                    var label = Describe(call, obs);
                    Emit(AgentEventKind.Action, label);
                    string result;
                    try { result = await _pc.ExecuteAsync(call, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { result = "error: " + ex.Message; }
                    log.Add($"{label} → {result}");
                    if (result.StartsWith("error", StringComparison.Ordinal)) break;
                }

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

                if (SettleMs > 0) await Task.Delay(SettleMs, ct);
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

    public JsonObject BuildRequest(IReadOnlyList<string> heard, IReadOnlyList<string> log, Observation obs)
    {
        var sb = new StringBuilder();
        string[] past;
        lock (_lock) past = [.. _past];
        if (past.Length > 0)
        {
            sb.AppendLine("Pedidos anteriores (solo contexto):");
            foreach (var p in past) sb.AppendLine("- " + p);
            sb.AppendLine();
        }
        sb.AppendLine("El usuario dijo:");
        foreach (var h in heard) sb.AppendLine($"«{h}»");
        if (log.Count > 0)
        {
            sb.AppendLine().AppendLine("Lo que ya hiciste en esta tarea:");
            foreach (var l in log) sb.AppendLine("- " + l);
        }
        sb.AppendLine().AppendLine($"Ventana activa: {obs.Window}");
        sb.AppendLine($"Captura de {obs.Width}x{obs.Height} px. Elementos marcados:");
        foreach (var m in obs.Marks)
            sb.AppendLine($"[{m.Id}] {m.Role}{(m.Name.Length > 0 ? " «" + m.Name + "»" : "")}");
        if (obs.Marks.Count == 0) sb.AppendLine("(ninguno; usa x,y)");

        var body = new JsonObject
        {
            ["model"] = _s.Model,
            ["instructions"] = Prompt.System,
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "input_text", ["text"] = sb.ToString() },
                    new JsonObject
                    {
                        ["type"] = "input_image",
                        ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(obs.Jpeg),
                        ["detail"] = "high",
                    }),
            }),
            ["tools"] = Prompt.Tools(),
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = true,
            ["store"] = false,
        };
        if (!string.IsNullOrEmpty(_s.ReasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = _s.ReasoningEffort };
        return body;
    }

    public static (string Message, List<ToolCall> Calls) Parse(JsonNode resp)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        foreach (var item in resp["output"]?.AsArray() ?? [])
        {
            switch (item?["type"]?.GetValue<string>())
            {
                case "function_call":
                    JsonObject args;
                    try { args = JsonNode.Parse(item["arguments"]?.GetValue<string>() ?? "{}") as JsonObject ?? []; }
                    catch { args = []; }
                    calls.Add(new ToolCall(item["name"]!.GetValue<string>(), args));
                    break;
                case "message":
                    foreach (var c in item["content"]?.AsArray() ?? [])
                        if (c?["type"]?.GetValue<string>() == "output_text")
                            text.Append(c["text"]?.GetValue<string>());
                    break;
            }
        }
        return (text.ToString().Trim(), calls);
    }

    public static string Describe(ToolCall c, Observation obs)
    {
        string Target()
        {
            if (c.Int("mark") is int id)
            {
                var m = obs.Marks.FirstOrDefault(x => x.Id == id);
                return m is null ? $" [{id}]" : $" [{id}] {(m.Name.Length > 0 ? "«" + Trunc(m.Name, 40) + "»" : m.Role)}";
            }
            return c.Int("x") is int x && c.Int("y") is int y ? $" ({x},{y})" : "";
        }
        return c.Name switch
        {
            "click" => (c.Bool("double") ? "Doble clic" : c.Str("button") == "right" ? "Clic derecho" : "Clic") + Target(),
            "type_text" => $"Escribe «{Trunc(c.Str("text"), 60)}»",
            "press_keys" => $"Teclas {c.Str("keys")}" + (c.Int("repeat") is > 1 and var n ? $" ×{n}" : ""),
            "scroll" => $"Desplaza {c.Str("direction")}" + Target(),
            "open" => $"Abre {c.Str("target")}",
            "screenshot" => c.Str("area") == "window" ? "Print de la ventana" : "Print de la pantalla",
            "find_files" => $"Busca archivos «{c.Str("query")}»",
            "wait" => "Espera",
            _ => c.Name,
        };
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
