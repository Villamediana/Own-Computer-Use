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

/// <summary>El WAV de la reunión (16 kHz estéreo: izquierda = micrófono, derecha = sonido del PC).</summary>
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
        var (start, _) = Layout(f);
        if (start < 0) return 0;
        var real = f.Length - start;
        real -= real % 4;
        using var w = new BinaryWriter(f);
        f.Position = start - 4;
        w.Write((int)real);
        f.Position = 4;
        w.Write((int)(f.Length - 8));
        return real;
    }

    /// <summary>Dónde empiezan los datos y cuántos pares de muestras hay de verdad (aunque la cabecera vaya atrasada).</summary>
    static (long Start, long Frames) Layout(FileStream f)
    {
        var r = new BinaryReader(f);
        string Id() => Encoding.ASCII.GetString(r.ReadBytes(4));
        f.Position = 0;
        if (f.Length < 44 || Id() != "RIFF") return (-1, 0);
        f.Position = 12;
        while (f.Position + 8 <= f.Length)
        {
            var id = Id();
            var len = r.ReadInt32();
            if (id == "data") return (f.Position, (f.Length - f.Position) / 4);
            f.Position += len + (len & 1);
        }
        return (-1, 0);
    }

    /// <summary>Pares de muestras que ya hay en el archivo (se puede mirar mientras se graba).</summary>
    public static long Frames(string wav)
    {
        using var f = new FileStream(wav, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Math.Max(0, Layout(f).Frames);
    }

    /// <summary>Un tramo de los dos canales, desde from (en muestras), mientras se graba también.</summary>
    public static (float[] Mic, float[] Pc) Read(string wav, long from, long count)
    {
        using var f = new FileStream(wav, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var (start, frames) = Layout(f);
        count = Math.Max(0, Math.Min(count, frames - from));
        var bytes = new byte[count * 4];
        f.Position = start + from * 4;
        var got = 0;
        while (got < bytes.Length && f.Read(bytes, got, bytes.Length - got) is var n and > 0) got += n;
        var frames2 = got / 4;
        var mic = new float[frames2];
        var pc = new float[frames2];
        for (var i = 0; i < frames2; i++)
        {
            mic[i] = BitConverter.ToInt16(bytes, i * 4) / 32768f;
            pc[i] = BitConverter.ToInt16(bytes, i * 4 + 2) / 32768f;
        }
        return (mic, pc);
    }

    public static float[] Mix(float[] mic, float[] pc)
    {
        var m = new float[mic.Length];
        for (var i = 0; i < m.Length; i++) m[i] = (mic[i] + pc[i]) / 2;
        return m;
    }

    /// <summary>
    /// Dónde cortar un trozo: el medio segundo más callado de los últimos window segundos antes de at (no se corta
    /// una palabra por la mitad). Muestras desde el inicio del tramo.
    /// </summary>
    public static int QuietCut(float[] mono, int at, int window)
    {
        var half = Rate / 2;
        var best = at;
        var bestE = double.MaxValue;
        for (var s = Math.Max(0, at - window); s + half <= Math.Min(at, mono.Length); s += Frame)
        {
            double e = 0;
            for (var i = s; i < s + half; i++) e += mono[i] * mono[i];
            if (e < bestE) { bestE = e; best = s + half / 2; }
        }
        return Math.Clamp(best, Rate, mono.Length);
    }

    /// <summary>Qué parte de la energía de ese tramo vino del micrófono (0–1).</summary>
    public static double MicShare(float[] mic, float[] pc, int from, int to)
    {
        double m = 0, p = 0;
        for (var i = Math.Max(0, from); i < Math.Min(to, mic.Length); i++) { m += mic[i] * mic[i]; p += pc[i] * pc[i]; }
        return m + p == 0 ? 0.5 : m / (m + p);
    }

    /// <summary>
    /// Sin el reconocedor de voces: turnos por canal (0 = micrófono, 1 = sonido del PC), cada 100 ms el que suena
    /// más; tramos de menos de medio segundo se juntan con el vecino.
    /// </summary>
    public static List<Turn> ChannelTurns(float[] mic, float[] pc)
    {
        var turns = new List<Turn>();
        for (var f = 0; f + Frame <= mic.Length; f += Frame)
        {
            double m = 0, p = 0;
            for (var i = f; i < f + Frame; i++) { m += mic[i] * mic[i]; p += pc[i] * pc[i]; }
            m = Math.Sqrt(m / Frame); p = Math.Sqrt(p / Frame);
            if (Math.Max(m, p) < 0.01) continue;
            var who = m >= p ? 0 : 1;
            double s = f / (double)Rate, e = (f + Frame) / (double)Rate;
            if (turns.Count > 0 && turns[^1].Speaker == who && s - turns[^1].End < 0.6) turns[^1] = turns[^1] with { End = e };
            else turns.Add(new Turn(s, e, who));
        }
        return turns.Where(t => t.End - t.Start >= 0.5).ToList();
    }

    /// <summary>WAV mono de 16 kHz (trocito para oír a alguien, o para transcribir).</summary>
    public static byte[] Wav(float[] mono, double from, double to)
    {
        var a = Math.Clamp((int)(from * Rate), 0, mono.Length);
        var b = Math.Clamp((int)(to * Rate), a, mono.Length);
        return Core.Wav.Encode(mono.AsSpan(a, b - a));
    }
}

// ───────────────────────── trabajos

/// <summary>
/// Una reunión grabada y su camino: recorded (con la transcripción avanzando mientras se graba) → transcribed →
/// done. Si un paso falla queda con Error y se reintenta al arrancar (hasta 3 veces); el audio no se borra hasta que
/// todo sale bien. Lo del acta (título, resumen, pendientes, tareas…) queda aquí para consultarlo.
/// </summary>
public sealed record MeetingJob(string Id, string Dir, DateTime Started, double Seconds, string Status = "recorded",
    string Error = "", int Attempts = 0, string Title = "", string Project = "", string Summary = "",
    List<string>? Decisions = null, List<string>? Pending = null, List<string>? Tasks = null,
    List<string>? FollowUps = null, List<string>? Participants = null);

public sealed class Meetings
{
    readonly string _path;
    readonly object _lock = new();
    List<MeetingJob> _jobs = [];

    /// <summary>Cambió algo (Ajustes se refresca).</summary>
    public event Action? Changed;

    public Meetings(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _jobs = JsonSerializer.Deserialize<List<MeetingJob>>(File.ReadAllText(path)) ?? []; }
        catch { _jobs = []; }
    }

    public IReadOnlyList<MeetingJob> All { get { lock (_lock) return [.. _jobs]; } }

    public MeetingJob? Find(string id) { lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id); }

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

    /// <summary>Borra la reunión entera (su carpeta con audio, transcripción y acta). Las tareas de la agenda quedan.</summary>
    public bool Delete(string id)
    {
        MeetingJob? job;
        lock (_lock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null) return false;
            _jobs.Remove(job);
            Save();
        }
        try { if (Directory.Exists(job.Dir)) Directory.Delete(job.Dir, true); } catch { }
        return true;
    }

    /// <summary>
    /// Carpetas con audio.wav que no están en la lista (Colo cerrado a la fuerza grabando): se arregla la cabecera del
    /// WAV y se añaden. Devuelve las recuperadas.
    /// </summary>
    public List<string> Recover(string root, string? recording = null)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var wav = Path.Combine(dir, "audio.wav");
            if (!File.Exists(wav) || File.Exists(Path.Combine(dir, "ata.md"))) continue;
            // La que se está grabando ahora no es una perdida.
            if (recording is { Length: > 0 } && Same(dir, recording)) continue;
            if (All.Any(j => Same(j.Dir, dir))) continue;
            try
            {
                var seconds = MeetingAudio.RepairHeader(wav) / (MeetingAudio.Rate * 4.0);
                if (seconds < 3) continue;
                Add(dir, File.GetCreationTime(wav), seconds);
                found.Add(dir);
            }
            catch (IOException) { } // abierto por otro proceso: se mira la próxima vez
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
            + (j.Project.Length > 0 ? $" [{j.Project}]" : "") + " · " + StatusText(j)));
    }

    public static string StatusText(MeetingJob j) => j.Status switch
    {
        "done" => "con acta",
        _ when j.Error.Length > 0 => $"falló: {j.Error}" + (j.Attempts >= 3 ? " (meeting retry para intentarlo otra vez)" : ""),
        "transcribed" => "haciendo el acta",
        _ => "transcribiendo",
    };

    public static string Minutes(double s) => s < 3600 ? $"{(int)(s / 60)} min" : $"{(int)(s / 3600)} h {(int)(s % 3600 / 60):00} min";

    static string[] Words(string q) => Extract.Fold(q).Split([' ', ',', '.', '?', '¿'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToArray();

    static string Meta(MeetingJob j) => Extract.Fold(string.Join(" ", new[] { j.Title, j.Project, j.Summary, j.Started.ToString("dd/MM/yyyy") }
        .Concat(j.Decisions ?? []).Concat(j.Pending ?? []).Concat(j.Tasks ?? []).Concat(j.FollowUps ?? []).Concat(j.Participants ?? [])));

    /// <summary>
    /// Para agenda search: lo del acta (título, resumen, decisiones, pendientes, quién quedó con qué) y, si no, la
    /// transcripción.
    /// </summary>
    public IEnumerable<string> Search(string query)
    {
        var words = Words(query);
        if (words.Length == 0) yield break;
        var n = 0;
        foreach (var j in All.Where(j => j.Status == "done").OrderByDescending(j => j.Started))
        {
            if (n >= 6) yield break;
            var head = $"Reunión {j.Started:dd/MM/yyyy HH:mm} «{j.Title}»{(j.Project.Length > 0 ? $" [{j.Project}]" : "")}";
            if (words.All(Meta(j).Contains))
            {
                n++;
                yield return $"{head}: {j.Summary}"
                    + (j.Participants is { Count: > 0 } who ? " Participantes: " + string.Join(", ", who) + "." : "")
                    + (j.Decisions is { Count: > 0 } d ? " Decisiones: " + string.Join("; ", d) + "." : "")
                    + (j.Pending is { Count: > 0 } p ? " Pendiente: " + string.Join("; ", p) + "." : "")
                    + (j.Tasks is { Count: > 0 } t ? " Tareas: " + string.Join("; ", t) + "." : "")
                    + (j.FollowUps is { Count: > 0 } f ? " Seguimiento: " + string.Join("; ", f) + "." : "")
                    + " (más: meeting read)";
                continue;
            }
            var path = Path.Combine(j.Dir, "transcricao.md");
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            var fold = Extract.Fold(text);
            if (!words.All(fold.Contains)) continue;
            n++;
            var at = fold.IndexOf(words[0], StringComparison.Ordinal);
            var from = Math.Clamp(at - 120, 0, text.Length);
            yield return $"{head}, en la transcripción: …{text.Substring(from, Math.Min(280, text.Length - from)).Replace('\n', ' ')}…";
        }
    }

    /// <summary>meeting read: el acta entera de la reunión que encaja (por título, proyecto, fecha o gente); vacío = la última.</summary>
    public string Read(string query)
    {
        var words = Words(query);
        var job = All.Where(j => j.Status == "done").OrderByDescending(j => j.Started)
            .FirstOrDefault(j => words.Length == 0 || words.All(Meta(j).Contains));
        if (job is null) return "error: no encuentro esa reunión (mira meeting list)";
        var ata = Path.Combine(job.Dir, "ata.md");
        var text = File.Exists(ata) ? File.ReadAllText(ata) : job.Summary;
        return "ok:\n" + (text.Length > 8000 ? text[..8000] + "\n…" : text);
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
        Changed?.Invoke();
    }
}

// ───────────────────────── transcripción con voces

/// <summary>Una voz de la reunión: S1, S2… con el nombre que le dio el usuario (o reconocido por la huella).</summary>
public sealed class MeetingSpeaker
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>El nombre vino de reconocer la voz (no lo escribió el usuario en esta reunión).</summary>
    public bool Auto { get; set; }
    public float[]? Voice { get; set; }
    public double Seconds { get; set; }
    public double MicSeconds { get; set; }
    /// <summary>Duración del trocito guardado en clips/Id.wav (el turno más largo, hasta 12 s).</summary>
    public double ClipSeconds { get; set; }
}

public sealed record TranscriptLine(double Start, double End, string Speaker, string Text);

/// <summary>
/// La transcripción que va creciendo mientras se graba (transcricao.json): hasta dónde va, las frases con su voz,
/// las voces y, en reuniones largas, notas por bloque de 20 min (para que el acta no tenga que leerlo todo).
/// </summary>
public sealed class MeetingTranscript
{
    public const string FileName = "transcricao.json";
    /// <summary>«voices» (por la voz) o «channels» (sin reconocedor: micrófono y computador).</summary>
    public string Mode { get; set; } = "";
    public double Covered { get; set; }
    public List<TranscriptLine> Lines { get; set; } = [];
    public List<MeetingSpeaker> Speakers { get; set; } = [];
    public List<string> Notes { get; set; } = [];

    public static MeetingTranscript Load(string dir)
    {
        var path = Path.Combine(dir, FileName);
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<MeetingTranscript>(File.ReadAllText(path)) ?? new(); }
        catch { }
        return new();
    }

    public void Save(string dir, DateTime started)
    {
        var path = Path.Combine(dir, FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, overwrite: true);
        File.WriteAllText(Path.Combine(dir, "transcricao.md"), Markdown(started));
    }

    public string Label(string id) => Speakers.FirstOrDefault(s => s.Id == id) is { Name.Length: > 0 } s ? s.Name : id;

    /// <summary>
    /// La voz de ese audio en la reunión: la más parecida si pasa el umbral (y su huella se afina con lo nuevo);
    /// si no, una nueva (S1, S2…), con nombre si es alguien que el usuario ya nombró otra vez.
    /// </summary>
    public MeetingSpeaker Assign(float[]? voice, double seconds, People? people)
    {
        MeetingSpeaker? best = null;
        var score = -1.0;
        if (voice is { Length: > 0 })
            foreach (var s in Speakers.Where(s => s.Voice is not null))
                if (VoiceMath.Cos(s.Voice, voice) is var c && c > score) { score = c; best = s; }
        if (best is not null && score >= VoiceMath.Same)
        {
            best.Voice = VoiceMath.Mix(best.Voice, best.Seconds, voice!, seconds);
            if (best.Name.Length == 0 && people?.Match(best.Voice) is { } known) { best.Name = known; best.Auto = true; }
            return best;
        }
        // Sin huella (tramo demasiado corto): la última voz que habló.
        if (voice is not { Length: > 0 } && Speakers.Count > 0) return Speakers[^1];
        var n = new MeetingSpeaker { Id = $"S{Speakers.Count + 1}", Voice = voice is { Length: > 0 } ? VoiceMath.Mix(null, 0, voice, 1) : null };
        if (people?.Match(n.Voice) is { } name) { n.Name = name; n.Auto = true; }
        Speakers.Add(n);
        return n;
    }

    /// <summary>
    /// El usuario dice quién es esa voz. Si otra voz de la reunión ya tiene ese nombre, son la misma persona: se
    /// juntan. La huella se guarda en People para reconocerla en las próximas.
    /// </summary>
    public void Rename(string id, string name, People? people, DateTime now, string? dir = null)
    {
        name = name.Trim();
        var s = Speakers.FirstOrDefault(x => x.Id == id);
        if (s is null) return;
        s.Name = name;
        s.Auto = false;
        var twin = name.Length == 0 ? null : Speakers.FirstOrDefault(x => x != s && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (twin is not null)
        {
            // Queda la que habló primero; la otra pasa a ella.
            var (keep, gone) = Speakers.IndexOf(twin) < Speakers.IndexOf(s) ? (twin, s) : (s, twin);
            keep.Voice = gone.Voice is null ? keep.Voice : VoiceMath.Mix(keep.Voice, keep.Seconds, gone.Voice, gone.Seconds);
            keep.Seconds += gone.Seconds;
            keep.MicSeconds += gone.MicSeconds;
            keep.Name = name;
            keep.Auto = false;
            if (dir is not null && gone.ClipSeconds > keep.ClipSeconds)
                try { File.Move(Path.Combine(dir, "clips", gone.Id + ".wav"), Path.Combine(dir, "clips", keep.Id + ".wav"), true); keep.ClipSeconds = gone.ClipSeconds; } catch { }
            Lines = Lines.Select(l => l.Speaker == gone.Id ? l with { Speaker = keep.Id } : l).ToList();
            Speakers.Remove(gone);
            s = keep;
        }
        if (name.Length > 0) people?.Learn(name, s.Voice, now);
    }

    static string Clock(double t) => TimeSpan.FromSeconds(t).ToString(t >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    /// <summary>
    /// Para el modelo, lo más corto posible: frases seguidas de la misma voz juntas, la hora solo al cambiar de voz,
    /// la voz por su id (S1…; los nombres van en la leyenda, así nombrar a alguien no cambia esto).
    /// </summary>
    public string Compact(double from = 0, double to = double.MaxValue)
    {
        var sb = new StringBuilder();
        string? last = null;
        foreach (var l in Lines.Where(l => l.Start >= from && l.Start < to))
        {
            if (l.Speaker != last) { sb.Append(sb.Length > 0 ? "\n" : "").Append($"[{Clock(l.Start)}] {l.Speaker}: "); last = l.Speaker; }
            else sb.Append(' ');
            sb.Append(l.Text);
        }
        return sb.ToString();
    }

    /// <summary>Quién es cada voz: nombre, cuánto habló y cuánto por el micrófono (el del usuario).</summary>
    public string Legend()
    {
        if (Mode == "channels") return "S1 = micrófono del usuario (él y quien esté a su lado) · S2 = sonido del ordenador (los demás)";
        return string.Join("\n", Speakers.Where(s => s.Seconds >= 2).Select(s =>
            $"{s.Id} = {(s.Name.Length > 0 ? s.Name + (s.Auto ? " (reconocido por la voz)" : "") : "sin nombre")} · habla {Math.Max(1, (int)Math.Round(s.Seconds / 60))} min"
            + $" · {(int)Math.Round(100 * s.MicSeconds / Math.Max(1, s.Seconds))}% por el micrófono"));
    }

    public string Markdown(DateTime started)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Transcrição · {started:dd/MM/yyyy HH:mm} · {TimeSpan.FromSeconds(Covered):h\\:mm\\:ss}");
        sb.AppendLine();
        string? last = null;
        foreach (var l in Lines)
        {
            if (l.Speaker != last) { sb.AppendLine().Append($"[{Clock(l.Start)}] **{Label(l.Speaker)}:** "); last = l.Speaker; }
            else sb.Append(' ');
            sb.Append(l.Text);
        }
        return sb.AppendLine().ToString();
    }
}

// ───────────────────────── acta

public static class MeetingText
{
    /// <summary>Hasta aquí el acta lee la transcripción entera (≈15 mil tokens, más de una hora de reunión).</summary>
    public const int FullLimit = 60_000;
    /// <summary>Bloques de las reuniones largas: se resumen mientras se graba.</summary>
    public const double Block = 1200;

    public const string MinutesPrompt = """
Eres quien toma el acta de una reunión del usuario. Recibes quién es cada voz (S1, S2… con su nombre si se sabe y
cuánto habló por el micrófono, que es el del usuario: quien habla casi todo por el micrófono suele ser él, salvo que
casi todos lo hagan, que es una reunión presencial), sus proyectos y la transcripción (o, en reuniones largas,
notas por bloque y el final transcrito).
Devuelve SOLO un objeto JSON, en el idioma de la reunión:
{"title": "nombre corto y concreto de la reunión según lo que se habló",
 "summary": "3 a 6 frases: de qué se habló y qué quedó",
 "participants": ["nombres, o S3 si no se sabe"],
 "project": "nombre del proyecto: uno de la lista si es ese (escrito igual); uno nuevo si es claramente otro; \"\" si no hay",
 "project_status": "cómo va el proyecto en pocas palabras, solo si se dijo",
 "decisions": ["…"],
 "pending": [{"text": "lo que quedó abierto o sin respuesta", "who": "quién lo tiene, o \"\""}],
 "my_tasks": [{"text": "lo que tiene que hacer el usuario", "due": "yyyy-MM-dd HH:mm o \"\""}],
 "others_tasks": [{"text": "…", "who": "nombre, o S2 si no se sabe", "due": ""}],
 "follow_ups": [{"text": "algo a revisar o retomar más adelante", "who": "", "when": "yyyy-MM-dd HH:mm o \"\""}],
 "reminders": [{"text": "…", "remind": "yyyy-MM-dd HH:mm"}]}
Usa los nombres cuando los haya. Solo lo que se dijo; nada inventado. Fechas relativas («el viernes») a partir de
la fecha de la reunión. Tareas concretas, una frase cada una, sin repetir.
""";

    public const string NotesPrompt = """
Resume este bloque de una reunión para quien hará el acta después: temas, decisiones, lo pendiente, quién quedó
con qué (con su S1, S2…) y fechas dichas. Viñetas cortas, nada inventado, en el idioma de la reunión.
""";

    static JsonObject Call(Settings s, string instructions, string text, string? effort, bool json)
    {
        var body = new JsonObject
        {
            ["model"] = s.Model,
            ["instructions"] = instructions,
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
            }),
            ["store"] = false,
            ["prompt_cache_key"] = "colo-reunion",
        };
        if (json) body["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_object" } };
        if (effort is not null && !string.IsNullOrEmpty(s.ReasoningEffort)) body["reasoning"] = new JsonObject { ["effort"] = effort };
        return body;
    }

    /// <summary>Notas de un bloque (barato: sin razonamiento).</summary>
    public static JsonObject NotesRequest(Settings s, string block) => Call(s, NotesPrompt, block, "none", false);

    /// <summary>
    /// La llamada del acta, económica: la transcripción compacta entera si cabe; si no, las notas de los bloques ya
    /// resumidos y solo el final transcrito.
    /// </summary>
    public static JsonObject MinutesRequest(Settings s, MeetingTranscript t, MeetingJob job, Agenda agenda)
    {
        var ctx = new StringBuilder();
        ctx.AppendLine($"Fecha de la reunión: {job.Started.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.GetCultureInfo("es"))} ({(int)(job.Seconds / 60)} min)");
        ctx.AppendLine().AppendLine("Voces:").AppendLine(t.Legend());
        var projects = agenda.Projects;
        if (projects.Count > 0)
        {
            ctx.AppendLine().AppendLine("Proyectos:");
            foreach (var p in projects)
            {
                var open = agenda.Items.Where(i => !i.Done && i.Project == p.Name).Take(8).Select(i => i.Text).ToList();
                ctx.AppendLine($"- {p.Name}{(p.Status.Length > 0 ? " · " + p.Status : "")}{(p.Summary.Length > 0 ? ": " + p.Summary : "")}"
                    + (open.Count > 0 ? " · abiertas: " + string.Join("; ", open) : ""));
            }
        }
        if (t.Notes.Count > 0)
        {
            for (var i = 0; i < t.Notes.Count; i++)
                ctx.AppendLine().AppendLine($"Notas del bloque {i + 1} ({i * Block / 60:0}–{(i + 1) * Block / 60:0} min):").AppendLine(t.Notes[i]);
            ctx.AppendLine().AppendLine($"Transcripción desde el minuto {t.Notes.Count * Block / 60:0}:").AppendLine(t.Compact(t.Notes.Count * Block));
        }
        else ctx.AppendLine().AppendLine("Transcripción:").AppendLine(t.Compact());
        return Call(s, MinutesPrompt, ctx.ToString(), s.ReasoningEffort.Length > 0 ? "low" : null, true);
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
    static IEnumerable<string> Strings(JsonNode? n, string key) => Arr(n, key).Select(d => d is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0);

    /// <summary>Así empieza el Source de lo que la reunión llevó a la agenda (para rehacer el acta sin duplicar).</summary>
    public static string SourcePrefix(MeetingJob job) => $"reunión {job.Started:dd/MM HH:mm}";

    /// <summary>
    /// Lleva el acta a la agenda (proyecto, tareas suyas y de otros, seguimientos, recordatorios), devuelve el .md y
    /// la reunión con lo del acta (para consultarlo después).
    /// </summary>
    public static (string Markdown, int Tasks, MeetingJob Job) Apply(JsonObject m, MeetingJob job, Agenda agenda, DateTime now)
    {
        var title = S(m, "title");
        var project = S(m, "project");
        if (project.Length > 0) agenda.Project(project, S(m, "project_status"), "", "", now);
        var source = SourcePrefix(job) + (title.Length > 0 ? $" «{title}»" : "");
        var tasks = 0;
        // Al rehacer el acta, lo que ya se hizo no vuelve a aparecer abierto.
        var closed = agenda.Items.Where(i => i.Done && i.Source.StartsWith(SourcePrefix(job), StringComparison.Ordinal)).Select(i => Extract.Fold(i.Text)).ToHashSet();
        void Add(string text, DateTime? due, DateTime? remind, string who)
        {
            if (!closed.Contains(Extract.Fold(text))) agenda.Add(text, project, "", due, remind, "", who, source, now);
        }
        var md = new StringBuilder();
        md.AppendLine($"# {(title.Length > 0 ? title : "Reunião")}");
        md.AppendLine($"{job.Started:dd/MM/yyyy HH:mm} · {(int)(job.Seconds / 60)} min" + (project.Length > 0 ? $" · {project}" : ""));
        var people = Strings(m, "participants").ToList();
        if (people.Count > 0) md.AppendLine().AppendLine("**Participantes:** " + string.Join(", ", people));
        md.AppendLine().AppendLine(S(m, "summary"));
        void Section(string head, IEnumerable<string> lines)
        {
            var l = lines.Where(x => x.Length > 0).ToList();
            if (l.Count == 0) return;
            md.AppendLine().AppendLine($"## {head}");
            foreach (var x in l) md.AppendLine("- " + x);
        }
        static string Due(string d) => d.Length > 0 ? $" (até {d})" : "";
        var decisions = Strings(m, "decisions").ToList();
        Section("Decisões", decisions);
        var pending = Arr(m, "pending").Select(p => (S(p, "who").Length > 0 ? S(p, "who") + ": " : "") + S(p, "text")).Where(p => !p.EndsWith(": ")).ToList();
        Section("Pendências", pending);
        var mine = new List<string>();
        foreach (var t in Arr(m, "my_tasks"))
        {
            var text = S(t, "text");
            if (text.Length == 0) continue;
            Add(text, Agenda.Date(S(t, "due")), null, "");
            mine.Add(text + Due(S(t, "due")));
            tasks++;
        }
        Section("Minhas tarefas", mine);
        var others = new List<string>();
        foreach (var t in Arr(m, "others_tasks"))
        {
            var text = S(t, "text");
            if (text.Length == 0) continue;
            Add(text, Agenda.Date(S(t, "due")), null, S(t, "who"));
            others.Add($"{S(t, "who")}: {text}" + Due(S(t, "due")));
            tasks++;
        }
        Section("Tarefas dos outros", others);
        var follow = new List<string>();
        foreach (var t in Arr(m, "follow_ups"))
        {
            var text = S(t, "text");
            if (text.Length == 0) continue;
            var when = Agenda.Date(S(t, "when"));
            Add(text, null, when, S(t, "who"));
            follow.Add((S(t, "who").Length > 0 ? S(t, "who") + ": " : "") + text + (S(t, "when").Length > 0 ? $" ({S(t, "when")})" : ""));
            tasks++;
        }
        Section("Acompanhamentos", follow);
        var reminders = new List<string>();
        foreach (var t in Arr(m, "reminders"))
        {
            var text = S(t, "text");
            if (text.Length == 0) continue;
            Add(text, null, Agenda.Date(S(t, "remind")), "");
            reminders.Add(text + (S(t, "remind").Length > 0 ? $" ({S(t, "remind")})" : ""));
            tasks++;
        }
        Section("Lembretes", reminders);
        md.AppendLine().AppendLine("[Transcrição completa](transcricao.md)");
        job = job with
        {
            Title = title, Project = project, Summary = S(m, "summary"), Decisions = decisions, Pending = pending,
            Tasks = [.. mine.Select(x => "yo: " + x), .. others], FollowUps = follow, Participants = people,
        };
        return (md.ToString(), tasks, job);
    }
}

/// <summary>
/// Procesa las reuniones en segundo plano sin tocar el turno de voz. Mientras se graba, cada ~3 min de audio (cortado
/// en un silencio) se separa por voces y se transcribe; al parar solo queda el último trozo y el acta. Cada paso
/// queda guardado: si Colo se cierra, sigue al volver.
/// </summary>
public sealed class MeetingWork(Meetings meetings, Settings settings, AiClient ai, Agenda agenda)
{
    public const double Chunk = 180, Margin = 15;

    readonly SemaphoreSlim _one = new(1, 1);
    readonly SemaphoreSlim _audio = new(1, 1);
    int _again;
    static readonly Lazy<HttpClient> LongHttp = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });

    /// <summary>Comprime audio.wav → audio.m4a (en Windows con Media Foundation). null = no se comprime.</summary>
    public Func<string, string, bool>? Encode { get; set; }
    /// <summary>Whisper local para el último recurso (muestras 16 kHz mono → texto). null = no hay.</summary>
    public Func<float[], string>? LocalTranscribe { get; set; }
    /// <summary>El reconocedor de voces (baja los modelos la primera vez). null o que devuelva null = por canales.</summary>
    public Func<CancellationToken, Task<IVoices?>>? Voices { get; set; }
    public People? People { get; set; }
    /// <summary>Avisos para la onda («Acta lista…»). El bool dice si es un error.</summary>
    public event Action<string, bool>? Notice;

    public async Task RunAsync(CancellationToken ct = default)
    {
        // Pedido mientras ya trabaja: lo recoge esa misma vuelta (sin quedarse esperando hasta el próximo arranque).
        Volatile.Write(ref _again, 1);
        if (!await _one.WaitAsync(0, ct)) return;
        try
        {
            while (Interlocked.Exchange(ref _again, 0) == 1)
            {
                // Una a una hasta que no quede nada nuevo (también lo que se grabó mientras procesaba otra).
                var seen = new HashSet<string>();
                while (meetings.Pending().FirstOrDefault(j => seen.Add(j.Id)) is { } job)
                {
                    ct.ThrowIfCancellationRequested();
                    await ProcessAsync(job, ct);
                }
            }
        }
        finally { _one.Release(); }
        if (Volatile.Read(ref _again) == 1) await RunAsync(ct);
    }

    /// <summary>Durante la grabación: transcribe lo que ya hay entero (si está ocupado con otro trozo, espera a la próxima).</summary>
    public async Task LiveAsync(string dir, DateTime started, CancellationToken ct = default)
    {
        if (!await _audio.WaitAsync(0, ct)) return;
        try { await ContinueAsync(dir, started, final: false, ct); }
        finally { _audio.Release(); }
    }

    async Task ProcessAsync(MeetingJob job, CancellationToken ct)
    {
        var wav = Path.Combine(job.Dir, "audio.wav");
        var m4a = Path.Combine(job.Dir, "audio.m4a");
        try
        {
            if (job.Status is "recorded" or "encoded")
            {
                if (!File.Exists(wav)) throw new AiException("falta audio.wav");
                await _audio.WaitAsync(ct);
                try { await ContinueAsync(job.Dir, job.Started, final: true, ct); }
                finally { _audio.Release(); }
                job = meetings.Update(job with { Status = "transcribed", Error = "" });
            }
            if (job.Status == "transcribed")
            {
                var tasks = await MinutesAsync(job, ct);
                job = meetings.Find(job.Id) ?? job;
                // Con la versión comprimida guardada, el WAV grande sobra. A un nombre provisional: un .m4a cortado
                // (Colo cerrado a medias) no pasa por bueno.
                if (Encode is { } enc && File.Exists(wav) && !File.Exists(m4a))
                {
                    var part = Path.Combine(job.Dir, "audio.part.m4a");
                    try
                    {
                        File.Delete(part);
                        if (enc(wav, part)) File.Move(part, m4a);
                    }
                    catch { } // sin comprimir: el WAV se queda
                    finally { try { File.Delete(part); } catch { } }
                }
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

    /// <summary>Hace (o rehace) el acta; lo de una versión anterior que siga abierto en la agenda se quita antes.</summary>
    async Task<int> MinutesAsync(MeetingJob job, CancellationToken ct)
    {
        var t = MeetingTranscript.Load(job.Dir);
        var md = Path.Combine(job.Dir, "transcricao.md");
        if (t.Lines.Count == 0 && File.Exists(md))
            // Grabada con una versión anterior (sin transcricao.json): el acta sale del .md tal cual.
            t = new MeetingTranscript { Mode = "channels", Lines = [new(0, 0, "", await File.ReadAllTextAsync(md, ct))] };
        else
        {
            await NotesAsync(t, ct);
            t.Save(job.Dir, job.Started);
        }
        var minutes = MeetingText.ParseMinutes(await TextAsync(MeetingText.MinutesRequest(settings, t, job, agenda), ct))
            ?? throw new AiException("el acta no vino en JSON");
        agenda.RemoveOpenFrom(MeetingText.SourcePrefix(job));
        var (ata, tasks, done) = MeetingText.Apply(minutes, job, agenda, DateTime.Now);
        await File.WriteAllTextAsync(Path.Combine(job.Dir, "ata.md"), ata, ct);
        meetings.Update(done with { Status = "done", Error = "" });
        return tasks;
    }

    /// <summary>Rehace el acta (después de poner nombres a las voces).</summary>
    public async Task<string> RedoAsync(string id, CancellationToken ct = default)
    {
        var job = meetings.Find(id);
        if (job is not { Status: "done" }) return "error: esa reunión todavía no tiene acta";
        await _one.WaitAsync(ct);
        try
        {
            var tasks = await MinutesAsync(job, ct);
            var now = meetings.Find(id)!;
            Notice?.Invoke($"Acta rehecha: «{now.Title}»" + (tasks > 0 ? $" · {tasks} cosas en la agenda" : ""), false);
            return "ok";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice?.Invoke("No pude rehacer el acta: " + ex.Message, true);
            return "error: " + ex.Message;
        }
        finally { _one.Release(); }
    }

    /// <summary>Pone nombre a una voz de la reunión (y la aprende para las siguientes).</summary>
    public void Rename(string id, string speaker, string name)
    {
        if (meetings.Find(id) is not { } job) return;
        // Que no se cruce con un trozo que se esté transcribiendo (los dos guardan el mismo archivo).
        _audio.Wait();
        try
        {
            var t = MeetingTranscript.Load(job.Dir);
            t.Rename(speaker, name, People, DateTime.Now, job.Dir);
            t.Save(job.Dir, job.Started);
        }
        finally { _audio.Release(); }
        meetings.Update(job);
    }

    /// <summary>El trocito de audio de esa voz (WAV), para oír quién es.</summary>
    public byte[]? Clip(string id, string speaker)
    {
        if (meetings.Find(id) is not { } job || speaker.Any(c => !char.IsLetterOrDigit(c))) return null;
        var path = Path.Combine(job.Dir, "clips", speaker + ".wav");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Sigue la transcripción desde donde iba: trozos de ~3 min cortados en un silencio. Mientras se graba solo los
    /// que ya están enteros (con margen); al final, todo.
    /// </summary>
    async Task ContinueAsync(string dir, DateTime started, bool final, CancellationToken ct)
    {
        var wav = Path.Combine(dir, "audio.wav");
        if (!File.Exists(wav)) return;
        var t = MeetingTranscript.Load(dir);
        const int R = MeetingAudio.Rate;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var from = (long)Math.Round(t.Covered * R);
            var avail = MeetingAudio.Frames(wav) - from;
            var whole = (long)((Chunk + Margin) * R);
            if (!final && avail < whole) break;
            if (avail < R / 2) { if (final && avail > 0) { t.Covered = (from + avail) / (double)R; t.Save(dir, started); } break; }
            var (mic, pc) = MeetingAudio.Read(wav, from, Math.Min(avail, whole));
            var take = mic.Length;
            if (avail > whole) take = MeetingAudio.QuietCut(MeetingAudio.Mix(mic, pc), (int)(Chunk * R), (int)(Margin * R));
            if (take < mic.Length) { mic = mic[..take]; pc = pc[..take]; }
            await ChunkAsync(dir, t, mic, pc, from / (double)R, ct);
            t.Covered = (from + take) / (double)R;
            await NotesAsync(t, ct);
            t.Save(dir, started);
        }
    }

    /// <summary>Un trozo: quién habla cuándo, a qué voz de la reunión corresponde cada uno, y el texto.</summary>
    async Task ChunkAsync(string dir, MeetingTranscript t, float[] mic, float[] pc, double at, CancellationToken ct)
    {
        var mono = MeetingAudio.Mix(mic, pc);
        IVoices? voices = null;
        if (t.Mode != "channels" && Voices is { } get)
        {
            try { voices = await get(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { voices = null; if (t.Mode == "voices") throw new AiException("no cargó el reconocedor de voces: " + ex.Message); }
            if (voices is null && t.Mode == "voices") throw new AiException("no cargó el reconocedor de voces");
        }
        if (t.Mode == "") t.Mode = voices is null ? "channels" : "voices";

        List<Turn> turns;
        var global = new Dictionary<int, MeetingSpeaker>();
        if (voices is null)
        {
            turns = MeetingAudio.ChannelTurns(mic, pc);
            foreach (var who in turns.Select(x => x.Speaker).Distinct())
            {
                var id = who == 0 ? "S1" : "S2";
                var s = t.Speakers.FirstOrDefault(x => x.Id == id);
                if (s is null)
                {
                    s = new MeetingSpeaker { Id = id, Name = who == 0 ? "Micrófono" : "Computador" };
                    t.Speakers.Add(s);
                    t.Speakers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                }
                global[who] = s;
            }
        }
        else
        {
            turns = await Task.Run(() => voices.Turns(mono), ct);
            foreach (var g in turns.GroupBy(x => x.Speaker))
            {
                // La huella con lo más largo que dijo en este trozo (hasta 20 s).
                var parts = new List<float>();
                foreach (var x in g.OrderByDescending(x => x.End - x.Start))
                {
                    if (parts.Count >= 20 * MeetingAudio.Rate) break;
                    var a = Math.Clamp((int)(x.Start * MeetingAudio.Rate), 0, mono.Length);
                    var b = Math.Clamp((int)(x.End * MeetingAudio.Rate), a, mono.Length);
                    parts.AddRange(mono.AsSpan(a, b - a).ToArray());
                }
                var voice = parts.Count >= MeetingAudio.Rate / 2 ? await Task.Run(() => voices.Embed([.. parts]), ct) : null;
                global[g.Key] = t.Assign(voice, parts.Count / (double)MeetingAudio.Rate, People);
            }
        }

        // Cuánto habló cada voz, cuánto por el micrófono, y su trocito más largo para oírla.
        foreach (var x in turns)
        {
            var s = global[x.Speaker];
            var d = x.End - x.Start;
            s.Seconds += d;
            s.MicSeconds += d * MeetingAudio.MicShare(mic, pc, (int)(x.Start * MeetingAudio.Rate), (int)(x.End * MeetingAudio.Rate));
            if (d > s.ClipSeconds + 0.5 && s.ClipSeconds < 12)
                try
                {
                    Directory.CreateDirectory(Path.Combine(dir, "clips"));
                    var len = Math.Min(d, 12);
                    await File.WriteAllBytesAsync(Path.Combine(dir, "clips", s.Id + ".wav"), MeetingAudio.Wav(mono, x.Start, x.Start + len), ct);
                    s.ClipSeconds = len;
                }
                catch (IOException) { }
        }

        foreach (var (start, end, speaker, text) in await SpeechAsync(mono, turns, ct))
            if (text.Length > 0) t.Lines.Add(new TranscriptLine(Math.Round(at + start, 1), Math.Round(at + end, 1), global[speaker].Id, text));
    }

    /// <summary>
    /// El texto de cada turno. Con ElevenLabs, el trozo entero con tiempos por palabra y cada palabra a su turno; si
    /// no, cada grupo de turnos seguidos de la misma voz por separado (modelo de voz de la API o Whisper local).
    /// </summary>
    async Task<List<(double Start, double End, int Speaker, string Text)>> SpeechAsync(float[] mono, List<Turn> turns, CancellationToken ct)
    {
        var groups = new List<Turn>();
        foreach (var x in turns.OrderBy(x => x.Start))
            if (groups.Count > 0 && groups[^1].Speaker == x.Speaker && x.Start - groups[^1].End < 1.0 && x.End - groups[^1].Start <= 45)
                groups[^1] = groups[^1] with { End = x.End };
            else groups.Add(x);
        groups = groups.Where(g => g.End - g.Start >= 0.6).ToList();
        if (groups.Count == 0) return [];

        if (!string.IsNullOrWhiteSpace(settings.ElevenLabsKey))
        {
            var words = await ScribeWordsAsync(Core.Wav.Encode(mono), ct);
            var lines = new List<(double, double, int, string)>();
            int? cur = null;
            var sb = new StringBuilder();
            double s0 = 0, e0 = 0;
            void Flush() { if (cur is { } c && sb.ToString().Trim() is { Length: > 0 } txt) lines.Add((s0, e0, groups[c].Speaker, txt)); sb.Clear(); }
            foreach (var (text, start, end) in words)
            {
                var mid = (start + end) / 2;
                var g = groups.FindIndex(x => mid >= x.Start - 0.3 && mid <= x.End + 0.3);
                if (g < 0) g = cur ?? 0;
                if (g != cur) { Flush(); cur = g; s0 = start; }
                sb.Append(text);
                e0 = end;
            }
            Flush();
            return lines;
        }
        if (!string.IsNullOrWhiteSpace(settings.TranscribeModel))
        {
            using var gate = new SemaphoreSlim(3);
            var texts = await Task.WhenAll(groups.Select(async g =>
            {
                await gate.WaitAsync(ct);
                try { return await ai.TranscribeAsync(MeetingAudio.Wav(mono, g.Start - 0.15, g.End + 0.15), "", ct); }
                finally { gate.Release(); }
            }));
            return groups.Select((g, i) => (g.Start, g.End, g.Speaker, texts[i].Trim())).ToList();
        }
        if (LocalTranscribe is { } local)
        {
            var r = new List<(double, double, int, string)>();
            foreach (var g in groups)
            {
                var a = Math.Clamp((int)(g.Start * MeetingAudio.Rate), 0, mono.Length);
                var b = Math.Clamp((int)(g.End * MeetingAudio.Rate), a, mono.Length);
                r.Add((g.Start, g.End, g.Speaker, (await Task.Run(() => local(mono[a..b]), ct)).Trim()));
            }
            return r;
        }
        throw new AiException("no hay con qué transcribir: pon la key de ElevenLabs o un modelo de voz en Ajustes");
    }

    /// <summary>Reuniones largas: cada bloque de 20 min ya entero se resume una vez (barato), así el acta no relee todo.</summary>
    async Task NotesAsync(MeetingTranscript t, CancellationToken ct)
    {
        if (t.Compact().Length <= MeetingText.FullLimit) return;
        while ((t.Notes.Count + 1) * MeetingText.Block <= t.Covered)
        {
            var block = t.Compact(t.Notes.Count * MeetingText.Block, (t.Notes.Count + 1) * MeetingText.Block);
            t.Notes.Add(block.Length == 0 ? "(sin habla)" : await TextAsync(MeetingText.NotesRequest(settings, "Voces:\n" + t.Legend() + "\n\n" + block), ct));
        }
    }

    async Task<string> TextAsync(JsonObject request, CancellationToken ct)
    {
        var text = new StringBuilder();
        await foreach (var item in ai.StreamAsync(request, ct))
            if (item["type"]?.GetValue<string>() == "message")
                foreach (var c in item["content"] as JsonArray ?? [])
                    text.Append(c?["text"]?.GetValue<string>());
        return text.ToString().Trim();
    }

    async Task<List<(string Text, double Start, double End)>> ScribeWordsAsync(byte[] wav, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ElevenLabsStt.Url);
        req.Headers.Add("xi-api-key", settings.ElevenLabsKey.Trim());
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "trozo.wav");
        form.Add(new StringContent(string.IsNullOrWhiteSpace(settings.ElevenLabsModel) ? ElevenLabsStt.Models[0] : settings.ElevenLabsModel.Trim()), "model_id");
        form.Add(new StringContent("word"), "timestamps_granularity");
        form.Add(new StringContent("false"), "tag_audio_events");
        req.Content = form;
        using var resp = await (Http ?? LongHttp.Value).SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new AiException($"ElevenLabs {(int)resp.StatusCode}: {(body.Length > 160 ? body[..160] : body)}");
        var words = new List<(string, double, double)>();
        foreach (var w in JsonNode.Parse(body)?["words"] as JsonArray ?? [])
        {
            var type = w?["type"]?.GetValue<string>() ?? "word";
            var text = w?["text"]?.GetValue<string>() ?? "";
            if (type == "audio_event") continue;
            var start = w?["start"]?.GetValue<double>() ?? (words.Count > 0 ? words[^1].Item3 : 0);
            var end = w?["end"]?.GetValue<double>() ?? start;
            words.Add((text, start, end));
        }
        return words;
    }

    /// <summary>Para las pruebas: otro HttpClient para ElevenLabs.</summary>
    public HttpClient? Http { get; set; }
}
