using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;

namespace Colo.Core;

// ───────────────────────── atajo de grabar (un solo atajo, por toques)

public enum RecState { Idle, Recording, Paused }
public enum RecAction { None, Start, Pause, Resume, Stop }

/// <summary>
/// Un toque: parado → graba; grabando → pausa; en pausa → sigue. Dos toques seguidos (grabando o en pausa) → para y
/// guarda. El primer toque actúa al momento (no espera a ver si viene otro); si llega el segundo, para.
/// </summary>
public sealed class RecordKey
{
    public static readonly TimeSpan Double = TimeSpan.FromMilliseconds(450);
    DateTime _last = DateTime.MinValue;
    RecAction _lastAction;

    public RecAction Tap(RecState state, DateTime now)
    {
        var quick = now - _last <= Double;
        _last = now;
        RecAction a;
        // Doble toque justo al empezar: se ignora el segundo (no se graba medio segundo para tirarlo).
        if (quick && _lastAction == RecAction.Start) a = RecAction.None;
        else if (quick && _lastAction is RecAction.Pause or RecAction.Resume && state != RecState.Idle) a = RecAction.Stop;
        else a = state switch
        {
            RecState.Idle => RecAction.Start,
            RecState.Recording => RecAction.Pause,
            _ => RecAction.Resume,
        };
        if (a != RecAction.None) _lastAction = a;
        if (a == RecAction.Stop) _last = DateTime.MinValue;
        return a;
    }
}

// ───────────────────────── audio

/// <summary>Pasa un canal a 16 kHz promediando (filtro de caja): suficiente para voz y sin dependencias.</summary>
public sealed class Downsampler(int sourceRate, int targetRate = 16000)
{
    readonly double _step = (double)sourceRate / targetRate;
    double _phase, _acc;
    int _n;

    public void Feed(ReadOnlySpan<float> x, List<float> output)
    {
        foreach (var v in x)
        {
            _acc += v;
            _n++;
            _phase += 1;
            if (_phase >= _step)
            {
                output.Add((float)(_acc / _n));
                _acc = 0;
                _n = 0;
                _phase -= _step;
            }
        }
    }

    /// <summary>Lo que llega de WASAPI (float o PCM 16, n canales) en mono.</summary>
    public static float[] Mono(byte[] buffer, int bytes, WaveFormat f)
    {
        var ch = Math.Max(1, f.Channels);
        if (f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32))
        {
            var frames = bytes / 4 / ch;
            var r = new float[frames];
            for (var i = 0; i < frames; i++)
            {
                float s = 0;
                for (var c = 0; c < ch; c++) s += BitConverter.ToSingle(buffer, (i * ch + c) * 4);
                r[i] = s / ch;
            }
            return r;
        }
        if (f.BitsPerSample == 16)
        {
            var frames = bytes / 2 / ch;
            var r = new float[frames];
            for (var i = 0; i < frames; i++)
            {
                float s = 0;
                for (var c = 0; c < ch; c++) s += BitConverter.ToInt16(buffer, (i * ch + c) * 2) / 32768f;
                r[i] = s / ch;
            }
            return r;
        }
        if (f.BitsPerSample == 24)
        {
            var frames = bytes / 3 / ch;
            var r = new float[frames];
            for (var i = 0; i < frames; i++)
            {
                float s = 0;
                for (var c = 0; c < ch; c++)
                {
                    var o = (i * ch + c) * 3;
                    s += ((buffer[o] << 8 | buffer[o + 1] << 16 | buffer[o + 2] << 24) >> 8) / 8388608f;
                }
                r[i] = s / ch;
            }
            return r;
        }
        return [];
    }
}

/// <summary>Lecturas del WAV de la reunión (16 kHz estéreo: izquierda = micrófono, derecha = sonido del PC).</summary>
public static class MeetingAudio
{
    public const int Rate = 16000, Frame = 1600; // 100 ms

    /// <summary>
    /// Un WAV que no se cerró bien (proceso matado) dice en la cabecera menos datos de los que tiene: se corrige con
    /// el tamaño real. Devuelve los bytes de audio.
    /// </summary>
    public static long RepairHeader(string wav)
    {
        using var f = new FileStream(wav, FileMode.Open, FileAccess.ReadWrite);
        using var r = new BinaryReader(f);
        using var w = new BinaryWriter(f);
        string Id() => Encoding.ASCII.GetString(r.ReadBytes(4));
        if (f.Length < 44 || Id() != "RIFF") return 0;
        f.Position = 12;
        while (f.Position + 8 <= f.Length)
        {
            var id = Id();
            var len = r.ReadInt32();
            if (id == "data")
            {
                var real = f.Length - f.Position;
                real -= real % 4;
                if (len != real)
                {
                    f.Position -= 4;
                    w.Write((int)real);
                    f.Position = 4;
                    w.Write((int)(f.Length - 8));
                }
                return real;
            }
            f.Position += len + (len & 1);
        }
        return 0;
    }

    /// <summary>Volumen (RMS) de cada canal cada 100 ms, leyendo el archivo por partes.</summary>
    public static (float[] Mic, float[] Pc) Envelope(string wav)
    {
        using var r = new WaveFileReader(wav);
        var frames = (int)(r.SampleCount / Frame) + 1;
        var mic = new float[frames];
        var pc = new float[frames];
        var buf = new byte[Frame * 4];
        for (var f = 0; f < frames; f++)
        {
            var n = r.Read(buf, 0, buf.Length) / 4;
            if (n <= 0) break;
            double l = 0, rr = 0;
            for (var i = 0; i < n; i++)
            {
                var a = BitConverter.ToInt16(buf, i * 4) / 32768.0;
                var b = BitConverter.ToInt16(buf, i * 4 + 2) / 32768.0;
                l += a * a;
                rr += b * b;
            }
            mic[f] = (float)Math.Sqrt(l / n);
            pc[f] = (float)Math.Sqrt(rr / n);
        }
        return (mic, pc);
    }

    /// <summary>Quién sonaba más en ese tramo: «mic» (el usuario) o «pc» (los demás).</summary>
    public static string Channel((float[] Mic, float[] Pc) env, double start, double end)
    {
        var a = Math.Clamp((int)(start * 10), 0, env.Mic.Length - 1);
        var b = Math.Clamp((int)Math.Ceiling(end * 10), a + 1, env.Mic.Length);
        double m = 0, p = 0;
        for (var i = a; i < b; i++) { m += env.Mic[i]; p += env.Pc[i]; }
        return m >= p ? "mic" : "pc";
    }

    /// <summary>El audio en trozos mono (mezcla de los dos canales) de seconds segundos, para transcribir por partes.</summary>
    public static IEnumerable<(double At, float[] Samples)> MonoChunks(string wav, int seconds)
    {
        using var r = new WaveFileReader(wav);
        var buf = new byte[Rate * 4 * seconds];
        for (var at = 0.0; ; at += seconds)
        {
            var n = r.Read(buf, 0, buf.Length) / 4;
            if (n <= 0) yield break;
            var s = new float[n];
            for (var i = 0; i < n; i++) s[i] = (BitConverter.ToInt16(buf, i * 4) + BitConverter.ToInt16(buf, i * 4 + 2)) / 65536f;
            yield return (at, s);
        }
    }
}

// ───────────────────────── trabajos

/// <summary>
/// Una reunión grabada y su camino: recorded → encoded (audio comprimido) → transcribed → done. Si un paso falla
/// queda con Error y se reintenta al arrancar (hasta 3 veces); el audio no se borra hasta que todo sale bien.
/// </summary>
public sealed record MeetingJob(string Id, string Dir, DateTime Started, double Seconds, string Status = "recorded",
    string Error = "", int Attempts = 0, string Title = "", string Project = "", string Summary = "",
    List<string>? Decisions = null);

public sealed class Meetings
{
    readonly string _path;
    readonly object _lock = new();
    List<MeetingJob> _jobs = [];

    public Meetings(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _jobs = JsonSerializer.Deserialize<List<MeetingJob>>(File.ReadAllText(path)) ?? []; }
        catch { _jobs = []; }
    }

    public IReadOnlyList<MeetingJob> All { get { lock (_lock) return [.. _jobs]; } }

    public MeetingJob Add(string dir, DateTime started, double seconds)
    {
        var job = new MeetingJob(started.ToString("yyyyMMdd-HHmmss"), dir, started, seconds);
        lock (_lock) { _jobs.Add(job); Save(); }
        return job;
    }

    public MeetingJob Update(MeetingJob job)
    {
        lock (_lock)
        {
            var i = _jobs.FindIndex(j => j.Id == job.Id);
            if (i >= 0) _jobs[i] = job; else _jobs.Add(job);
            Save();
        }
        return job;
    }

    /// <summary>
    /// Carpetas con audio.wav que no están en la lista (Colo cerrado a la fuerza grabando): se arregla la cabecera del
    /// WAV y se añaden. Devuelve las recuperadas.
    /// </summary>
    public List<string> Recover(string root)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var wav = Path.Combine(dir, "audio.wav");
            if (!File.Exists(wav) || File.Exists(Path.Combine(dir, "ata.md"))) continue;
            if (All.Any(j => string.Equals(Path.GetFullPath(j.Dir), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))) continue;
            var seconds = MeetingAudio.RepairHeader(wav) / (MeetingAudio.Rate * 4.0);
            if (seconds < 3) continue;
            Add(dir, File.GetCreationTime(wav), seconds);
            found.Add(dir);
        }
        return found;
    }

    /// <summary>Lo que falta procesar (los fallidos, hasta 3 intentos).</summary>
    public IReadOnlyList<MeetingJob> Pending()
    {
        lock (_lock) return _jobs.Where(j => j.Status != "done" && j.Attempts < 3 && Directory.Exists(j.Dir)).ToList();
    }

    public string List(DateTime now)
    {
        var all = All.OrderByDescending(j => j.Started).Take(12).ToList();
        if (all.Count == 0) return "ok: no hay reuniones grabadas";
        return "ok:\n" + string.Join("\n", all.Select(j =>
            $"{j.Started:dd/MM HH:mm} · {Minutes(j.Seconds)} · {(j.Title.Length > 0 ? j.Title : "(sin título)")}"
            + (j.Project.Length > 0 ? $" [{j.Project}]" : "") + " · " + StatusText(j) + $" · {j.Dir}"));
    }

    public static string StatusText(MeetingJob j) => j.Status switch
    {
        "done" => "con acta",
        _ when j.Error.Length > 0 => $"falló: {j.Error}" + (j.Attempts >= 3 ? " (meeting retry para intentarlo otra vez)" : ""),
        "transcribed" => "haciendo el acta",
        _ => "transcribiendo",
    };

    static string Minutes(double s) => s < 3600 ? $"{(int)(s / 60)} min" : $"{(int)(s / 3600)} h {(int)(s % 3600 / 60):00} min";

    /// <summary>Para agenda search: actas (título, resumen, decisiones, proyecto) y, si no, la transcripción.</summary>
    public IEnumerable<string> Search(string query)
    {
        var words = Extract.Fold(query).Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToArray();
        if (words.Length == 0) yield break;
        var n = 0;
        foreach (var j in All.Where(j => j.Status == "done").OrderByDescending(j => j.Started))
        {
            if (n >= 6) yield break;
            var meta = Extract.Fold($"{j.Title} {j.Project} {j.Summary} {string.Join(" ", j.Decisions ?? [])}");
            var head = $"Reunión {j.Started:dd/MM/yyyy HH:mm} «{j.Title}»{(j.Project.Length > 0 ? $" [{j.Project}]" : "")}";
            if (words.All(meta.Contains))
            {
                n++;
                yield return $"{head}: {j.Summary}" + (j.Decisions is { Count: > 0 } d ? " Decisiones: " + string.Join("; ", d) : "")
                    + $" (acta en {Path.Combine(j.Dir, "ata.md")})";
                continue;
            }
            var t = Path.Combine(j.Dir, "transcricao.md");
            if (!File.Exists(t)) continue;
            var text = File.ReadAllText(t);
            var fold = Extract.Fold(text);
            if (!words.All(fold.Contains)) continue;
            n++;
            var at = fold.IndexOf(words[0], StringComparison.Ordinal);
            var from = Math.Max(0, at - 120);
            yield return $"{head}, en la transcripción: …{text.Substring(from, Math.Min(280, text.Length - from)).Replace('\n', ' ')}…";
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_jobs));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }
}

// ───────────────────────── transcripción y acta

/// <summary>Una frase de la transcripción: cuándo, quién (hablante del diarizador) y por qué canal sonó.</summary>
public sealed record Utterance(double Start, double End, string Speaker, string Channel, string Text);

public static class MeetingText
{
    /// <summary>Palabras de Scribe (con speaker_id y tiempos) → frases por hablante.</summary>
    public static List<Utterance> FromScribe(JsonNode scribe, (float[] Mic, float[] Pc)? env)
    {
        var list = new List<Utterance>();
        var names = new Dictionary<string, string>();
        string? speaker = null;
        double start = 0, end = 0;
        var sb = new StringBuilder();
        void Flush()
        {
            var text = sb.ToString().Trim();
            if (speaker is not null && text.Length > 0)
                list.Add(new Utterance(start, end, names[speaker], env is { } e ? MeetingAudio.Channel(e, start, end) : "", text));
            sb.Clear();
        }
        foreach (var w in scribe["words"] as JsonArray ?? [])
        {
            var type = w?["type"]?.GetValue<string>() ?? "word";
            var text = w?["text"]?.GetValue<string>() ?? "";
            if (type == "audio_event") continue;
            if (type == "spacing") { if (sb.Length > 0) sb.Append(text); continue; }
            var id = w?["speaker_id"]?.GetValue<string>() ?? "speaker_0";
            if (!names.ContainsKey(id)) names[id] = $"P{names.Count + 1}";
            var s = w?["start"]?.GetValue<double>() ?? end;
            if (id != speaker) { Flush(); speaker = id; start = s; }
            sb.Append(text);
            end = w?["end"]?.GetValue<double>() ?? s;
        }
        Flush();
        // Sin palabras (respuesta sin tiempos): el texto entero.
        if (list.Count == 0 && scribe["text"]?.GetValue<string>() is { Length: > 0 } all) list.Add(new Utterance(0, 0, "", "", all.Trim()));
        return list;
    }

    public static string Markdown(IEnumerable<Utterance> utterances, DateTime started, double seconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Transcrição · {started:dd/MM/yyyy HH:mm} · {TimeSpan.FromSeconds(seconds):h\\:mm\\:ss}");
        sb.AppendLine();
        sb.AppendLine("mic = microfone (você) · pc = som do computador (os outros) · P1, P2… = vozes diferentes");
        sb.AppendLine();
        foreach (var u in utterances)
        {
            var who = string.Join(" · ", new[] { u.Speaker, u.Channel }.Where(x => x.Length > 0));
            sb.AppendLine($"[{TimeSpan.FromSeconds(u.Start):h\\:mm\\:ss}]{(who.Length > 0 ? " " + who + ":" : "")} {u.Text}");
        }
        return sb.ToString();
    }

    public const string MinutesPrompt = """
Eres quien toma el acta de una reunión del usuario. Recibes la transcripción: «mic» es el usuario (su micrófono),
«pc» son los demás (sonido del ordenador); P1, P2… son voces distintas. También recibes sus proyectos.
Devuelve SOLO un objeto JSON, en el idioma de la reunión:
{"title": "título corto", "summary": "3 a 6 frases: de qué se habló y qué quedó",
 "project": "nombre del proyecto: uno de la lista si es ese (escrito igual); uno nuevo si es claramente otro; \"\" si no hay",
 "project_status": "cómo va el proyecto en pocas palabras, solo si se dijo",
 "decisions": ["…"],
 "my_tasks": [{"text": "lo que tiene que hacer el usuario", "due": "yyyy-MM-dd HH:mm o \"\""}],
 "others_tasks": [{"text": "…", "who": "nombre o «P2» si no se dijo", "due": ""}],
 "reminders": [{"text": "…", "remind": "yyyy-MM-dd HH:mm"}]}
Solo lo que se dijo; nada inventado. Fechas relativas («el viernes») a partir de la fecha de la reunión. Tareas
concretas, una frase cada una, sin repetir.
""";

    /// <summary>La llamada al modelo para el acta.</summary>
    public static JsonObject MinutesRequest(Settings s, string transcript, MeetingJob job, Agenda agenda)
    {
        var ctx = new StringBuilder();
        ctx.AppendLine($"Fecha de la reunión: {job.Started.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.GetCultureInfo("es"))} ({(int)(job.Seconds / 60)} min)");
        var projects = agenda.Projects;
        if (projects.Count > 0)
        {
            ctx.AppendLine("Proyectos:");
            foreach (var p in projects)
            {
                var open = agenda.Items.Where(i => !i.Done && i.Project == p.Name).Take(8).Select(i => i.Text).ToList();
                ctx.AppendLine($"- {p.Name}{(p.Status.Length > 0 ? " · " + p.Status : "")}{(p.Summary.Length > 0 ? ": " + p.Summary : "")}"
                    + (open.Count > 0 ? " · abiertas: " + string.Join("; ", open) : ""));
            }
        }
        ctx.AppendLine().AppendLine(transcript);
        var body = new JsonObject
        {
            ["model"] = s.Model,
            ["instructions"] = MinutesPrompt,
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = ctx.ToString() }),
            }),
            ["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_object" } },
            ["store"] = false,
        };
        if (!string.IsNullOrEmpty(s.ReasoningEffort)) body["reasoning"] = new JsonObject { ["effort"] = s.ReasoningEffort };
        return body;
    }

    /// <summary>El JSON del acta (tolera texto alrededor).</summary>
    public static JsonObject? ParseMinutes(string text)
    {
        var a = text.IndexOf('{');
        var b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try { return JsonNode.Parse(text[a..(b + 1)]) as JsonObject; } catch (JsonException) { return null; }
    }

    static string S(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
    static IEnumerable<JsonNode> Arr(JsonNode? n, string key) => (n?[key] as JsonArray ?? []).Where(x => x is not null)!;

    /// <summary>Lleva el acta a la agenda (proyecto, tareas suyas y de otros, recordatorios) y devuelve el .md.</summary>
    public static (string Markdown, int Tasks) Apply(JsonObject m, MeetingJob job, Agenda agenda, DateTime now)
    {
        var title = S(m, "title");
        var project = S(m, "project");
        if (project.Length > 0) agenda.Project(project, S(m, "project_status"), "", "", now);
        var source = $"reunión {job.Started:dd/MM HH:mm}" + (title.Length > 0 ? $" «{title}»" : "");
        var tasks = 0;
        var md = new StringBuilder();
        md.AppendLine($"# {(title.Length > 0 ? title : "Reunião")}");
        md.AppendLine($"{job.Started:dd/MM/yyyy HH:mm} · {(int)(job.Seconds / 60)} min" + (project.Length > 0 ? $" · {project}" : ""));
        md.AppendLine().AppendLine(S(m, "summary"));
        void Section(string head, IEnumerable<string> lines)
        {
            var l = lines.Where(x => x.Length > 0).ToList();
            if (l.Count == 0) return;
            md.AppendLine().AppendLine($"## {head}");
            foreach (var x in l) md.AppendLine("- " + x);
        }
        Section("Decisões", Arr(m, "decisions").Select(d => d is JsonValue v && v.TryGetValue<string>(out var s) ? s : ""));
        var mine = new List<string>();
        foreach (var t in Arr(m, "my_tasks"))
        {
            var text = S(t, "text");
            if (text.Length == 0) continue;
            agenda.Add(text, project, "", Agenda.Date(S(t, "due")), null, "", "", source, now);
            mine.Add(text + (S(t, "due").Length > 0 ? $" (até {S(t, "due")})" : ""));
            tasks++;
        }
        Section("Minhas tarefas", mine);
        var others = new List<string>();
        foreach (var t in Arr(m, "others_tasks"))
        {
            var text = S(t, "text");
            if (text.Length == 0) continue;
            agenda.Add(text, project, "", Agenda.Date(S(t, "due")), null, "", S(t, "who"), source, now);
            others.Add($"{S(t, "who")}: {text}" + (S(t, "due").Length > 0 ? $" (até {S(t, "due")})" : ""));
            tasks++;
        }
        Section("Tarefas dos outros", others);
        var reminders = new List<string>();
        foreach (var t in Arr(m, "reminders"))
        {
            var text = S(t, "text");
            if (text.Length == 0) continue;
            agenda.Add(text, project, "", null, Agenda.Date(S(t, "remind")), "", "", source, now);
            reminders.Add(text + (S(t, "remind").Length > 0 ? $" ({S(t, "remind")})" : ""));
            tasks++;
        }
        Section("Lembretes", reminders);
        md.AppendLine().AppendLine("[Transcrição completa](transcricao.md)");
        return (md.ToString(), tasks);
    }
}

/// <summary>
/// Procesa las reuniones en segundo plano, una a una, sin tocar el turno de voz: comprime el audio, transcribe
/// (ElevenLabs Scribe con hablantes; si no, el modelo de transcripción por API o Whisper local, por trozos) y hace el
/// acta con el modelo, que va a la agenda. Cada paso queda guardado: si Colo se cierra, sigue al volver.
/// </summary>
public sealed class MeetingWork(Meetings meetings, Settings settings, AiClient ai, Agenda agenda)
{
    readonly SemaphoreSlim _one = new(1, 1);
    static readonly Lazy<HttpClient> LongHttp = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(60) });

    /// <summary>Comprime audio.wav → audio.m4a (en Windows con Media Foundation). null = no se comprime.</summary>
    public Func<string, string, bool>? Encode { get; set; }
    /// <summary>Whisper local para el último recurso (muestras 16 kHz mono → texto). null = no hay.</summary>
    public Func<float[], string>? LocalTranscribe { get; set; }
    /// <summary>Avisos para la onda («Acta lista…»). El bool dice si es un error.</summary>
    public event Action<string, bool>? Notice;

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!await _one.WaitAsync(0, ct)) return;
        try
        {
            // Una a una hasta que no quede nada nuevo (también lo que se grabó mientras procesaba otra).
            var seen = new HashSet<string>();
            while (meetings.Pending().FirstOrDefault(j => seen.Add(j.Id)) is { } job)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessAsync(job, ct);
            }
        }
        finally { _one.Release(); }
    }

    async Task ProcessAsync(MeetingJob job, CancellationToken ct)
    {
        var wav = Path.Combine(job.Dir, "audio.wav");
        var m4a = Path.Combine(job.Dir, "audio.m4a");
        try
        {
            if (job.Status == "recorded")
            {
                if (Encode is { } enc && File.Exists(wav) && !File.Exists(m4a))
                    try { enc(wav, m4a); } catch { try { File.Delete(m4a); } catch { } }
                job = meetings.Update(job with { Status = "encoded", Error = "" });
            }
            if (job.Status == "encoded")
            {
                var md = await TranscribeAsync(job, wav, m4a, ct);
                await File.WriteAllTextAsync(Path.Combine(job.Dir, "transcricao.md"), md, ct);
                job = meetings.Update(job with { Status = "transcribed", Error = "" });
            }
            if (job.Status == "transcribed")
            {
                var transcript = await File.ReadAllTextAsync(Path.Combine(job.Dir, "transcricao.md"), ct);
                // En streaming: una reunión larga tarda en resumirse y la llamada normal se cortaría a los 90 s.
                var text = new StringBuilder();
                await foreach (var item in ai.StreamAsync(MeetingText.MinutesRequest(settings, transcript, job, agenda), ct))
                    if (item["type"]?.GetValue<string>() == "message")
                        foreach (var c in item["content"] as JsonArray ?? [])
                            text.Append(c?["text"]?.GetValue<string>());
                var minutes = MeetingText.ParseMinutes(text.ToString()) ?? throw new AiException("el acta no vino en JSON");
                var (mdText, tasks) = MeetingText.Apply(minutes, job, agenda, DateTime.Now);
                await File.WriteAllTextAsync(Path.Combine(job.Dir, "ata.md"), mdText, ct);
                job = meetings.Update(job with
                {
                    Status = "done", Error = "",
                    Title = minutes["title"]?.ToString() ?? "",
                    Project = minutes["project"]?.ToString() ?? "",
                    Summary = minutes["summary"]?.ToString() ?? "",
                    Decisions = (minutes["decisions"] as JsonArray ?? []).Select(d => d?.ToString() ?? "").Where(d => d.Length > 0).ToList(),
                });
                // Con la versión comprimida guardada, el WAV grande sobra.
                if (File.Exists(m4a) && File.Exists(wav)) try { File.Delete(wav); } catch { }
                Notice?.Invoke($"Acta lista: «{job.Title}»" + (tasks > 0 ? $" · {tasks} cosas a la agenda" : "")
                    + (job.Project.Length > 0 ? $" ({job.Project})" : ""), false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            meetings.Update(job with { Error = ex.Message.Length > 160 ? ex.Message[..160] : ex.Message, Attempts = job.Attempts + 1 });
            Notice?.Invoke("La reunión no se pudo procesar: " + ex.Message, true);
        }
    }

    async Task<string> TranscribeAsync(MeetingJob job, string wav, string m4a, CancellationToken ct)
    {
        (float[], float[])? env = File.Exists(wav) ? MeetingAudio.Envelope(wav) : null;
        if (!string.IsNullOrWhiteSpace(settings.ElevenLabsKey))
        {
            var file = File.Exists(m4a) ? m4a : wav;
            var scribe = await ScribeAsync(file, ct);
            return MeetingText.Markdown(MeetingText.FromScribe(scribe, env), job.Started, job.Seconds);
        }
        if (!File.Exists(wav)) throw new AiException("falta audio.wav para transcribir por partes");
        var parts = new List<Utterance>();
        if (!string.IsNullOrWhiteSpace(settings.TranscribeModel))
        {
            foreach (var (at, samples) in MeetingAudio.MonoChunks(wav, 300))
                parts.Add(new Utterance(at, at + samples.Length / 16000.0, "", "", await ai.TranscribeAsync(Wav.Encode(samples), "", ct)));
        }
        else if (LocalTranscribe is { } local)
        {
            foreach (var (at, samples) in MeetingAudio.MonoChunks(wav, 30))
                parts.Add(new Utterance(at, at + samples.Length / 16000.0, "", "", await Task.Run(() => local(samples), ct)));
        }
        else throw new AiException("no hay con qué transcribir: pon la key de ElevenLabs o un modelo de voz en Ajustes");
        return MeetingText.Markdown(parts.Where(p => p.Text.Length > 0), job.Started, job.Seconds);
    }

    async Task<JsonNode> ScribeAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ElevenLabsStt.Url);
        req.Headers.Add("xi-api-key", settings.ElevenLabsKey.Trim());
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(File.OpenRead(path));
        file.Headers.ContentType = new MediaTypeHeaderValue(path.EndsWith(".m4a") ? "audio/mp4" : "audio/wav");
        form.Add(file, "file", Path.GetFileName(path));
        form.Add(new StringContent(string.IsNullOrWhiteSpace(settings.ElevenLabsModel) ? ElevenLabsStt.Models[0] : settings.ElevenLabsModel.Trim()), "model_id");
        form.Add(new StringContent("true"), "diarize");
        form.Add(new StringContent("word"), "timestamps_granularity");
        form.Add(new StringContent("false"), "tag_audio_events");
        req.Content = form;
        using var resp = await LongHttp.Value.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new AiException($"ElevenLabs {(int)resp.StatusCode}: {(body.Length > 160 ? body[..160] : body)}");
        return JsonNode.Parse(body) ?? throw new AiException("ElevenLabs devolvió vacío");
    }
}
