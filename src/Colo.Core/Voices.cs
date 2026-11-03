using System.Text.Json;
using SherpaOnnx;

namespace Colo.Core;

// ───────────────────────── quién habla, por la voz (reuniones)
//
// sherpa-onnx (k2-fsa, Apache 2.0) en local: pyannote segmentation 3.0 parte el audio en turnos y el modelo
// ERes2Net de 3D-Speaker da la «huella» de cada voz (512 números). Probado con el audio de ejemplo de cuatro
// voces de sherpa-onnx: ERes2Net con umbral 0,9 separa las cuatro; wespeaker y CAM++ las juntaban. Coseno entre
// tramos de la misma voz ≥ 0,44 (media 0,62); de voces distintas ≤ 0,26.
// Módulo aparte: sin los modelos (o si no cargan) las reuniones siguen, con «micrófono» y «computador» como voces.

/// <summary>Un turno de habla dentro de un trozo: segundos desde el inicio del trozo y la voz local (0, 1…).</summary>
public sealed record Turn(double Start, double End, int Speaker);

public interface IVoices
{
    /// <summary>Quién habla cuándo (16 kHz mono). Vacío si no hay voz.</summary>
    List<Turn> Turns(float[] mono);
    /// <summary>La huella de la voz en ese audio.</summary>
    float[] Embed(float[] mono);
}

/// <summary>Los dos modelos (≈45 MB), bajados la primera vez que se graba una reunión.</summary>
public sealed class VoiceKit(string dir)
{
    public static readonly (string File, string Url)[] Files =
    [
        ("segmentacion.onnx", "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx"),
        ("voz.onnx", "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"),
    ];

    readonly SemaphoreSlim _one = new(1, 1);
    public string Dir { get; } = dir;
    public string Segmentation => Path.Combine(Dir, Files[0].File);
    public string Voice => Path.Combine(Dir, Files[1].File);
    public bool Ready => Files.All(f => File.Exists(Path.Combine(Dir, f.File)));

    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        if (Ready) return true;
        await _one.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            foreach (var (file, url) in Files)
            {
                var path = Path.Combine(Dir, file);
                if (File.Exists(path)) continue;
                var part = path + ".part";
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(part);
                    await src.CopyToAsync(dst, ct);
                }
                File.Move(part, path, overwrite: true);
            }
            return true;
        }
        finally { _one.Release(); }
    }
}

/// <summary>sherpa-onnx en CPU, pocos hilos: corre mientras la reunión sigue y no debe frenar el PC.</summary>
public sealed class SherpaVoices : IVoices, IDisposable
{
    readonly OfflineSpeakerDiarization _diarize;
    readonly SpeakerEmbeddingExtractor _embed;
    readonly object _lock = new();

    public SherpaVoices(VoiceKit kit, int threads = 2)
    {
        var c = new OfflineSpeakerDiarizationConfig();
        c.Segmentation.Pyannote.Model = kit.Segmentation;
        c.Segmentation.NumThreads = threads;
        c.Embedding.Model = kit.Voice;
        c.Embedding.NumThreads = threads;
        c.Clustering.NumClusters = -1;
        c.Clustering.Threshold = 0.9f;
        c.MinDurationOn = 0.3f;
        c.MinDurationOff = 0.5f;
        // Un modelo que sherpa no encuentra o no carga no lanza nada: deja el puntero nulo y el primer uso tumba el
        // proceso entero (sin excepción ni log). Se mira aquí y se convierte en un error normal.
        _diarize = Alive(new OfflineSpeakerDiarization(c), kit.Segmentation);
        _embed = Alive(new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = kit.Voice, NumThreads = threads }), kit.Voice);
    }

    static T Alive<T>(T native, string model) where T : class
    {
        var f = typeof(T).GetField("_handle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (f?.GetValue(native) is System.Runtime.InteropServices.HandleRef h && h.Handle == IntPtr.Zero)
            throw new InvalidOperationException("sherpa-onnx no pudo cargar " + Path.GetFileName(model));
        return native;
    }

    public List<Turn> Turns(float[] mono)
    {
        if (mono.Length < MeetingAudio.Rate) return [];
        lock (_lock) return _diarize.Process(mono).Select(s => new Turn(s.Start, s.End, s.Speaker)).ToList();
    }

    public float[] Embed(float[] mono)
    {
        lock (_lock)
        {
            var s = _embed.CreateStream();
            s.AcceptWaveform(MeetingAudio.Rate, mono);
            s.InputFinished();
            // Con muy poco audio sale todo NaN: eso no es una huella (y estropearía el centroide).
            var v = _embed.IsReady(s) ? _embed.Compute(s) : [];
            return v.Any(float.IsNaN) ? [] : v;
        }
    }

    public void Dispose() { }
}

public static class VoiceMath
{
    /// <summary>Misma voz si el coseno pasa de aquí (dentro de una reunión: centroides con bastante audio).</summary>
    public const double Same = 0.40;
    /// <summary>Para ponerle nombre solo, algo más exigente: equivocarse de persona es peor que no saberla.</summary>
    public const double Known = 0.45;

    public static double Cos(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length) return -1;
        double d = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? -1 : d / Math.Sqrt(na * nb);
    }

    /// <summary>Media ponderada de dos huellas (normalizadas): el centroide de una voz que va sumando audio.</summary>
    public static float[] Mix(float[]? a, double wa, float[] b, double wb)
    {
        if (a is null || a.Length != b.Length || wa <= 0) return Norm(b);
        var na = Norm(a);
        var nb = Norm(b);
        var r = new float[a.Length];
        for (var i = 0; i < r.Length; i++) r[i] = (float)((na[i] * wa + nb[i] * wb) / (wa + wb));
        return Norm(r);
    }

    static float[] Norm(float[] v)
    {
        var n = Math.Sqrt(v.Sum(x => (double)x * x));
        return n == 0 ? v : v.Select(x => (float)(x / n)).ToArray();
    }
}

/// <summary>
/// Las personas que el usuario ya nombró, con la huella de su voz: en la reunión siguiente se reconocen solas.
/// Se guarda en people.json; solo números, ningún audio.
/// </summary>
public sealed class People
{
    public sealed record Person(string Name, float[] Voice, int Meetings, DateTime Updated);

    readonly string _path;
    readonly object _lock = new();
    List<Person> _all = [];

    public People(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _all = JsonSerializer.Deserialize<List<Person>>(File.ReadAllText(path)) ?? []; }
        catch { _all = []; }
    }

    public IReadOnlyList<string> Names { get { lock (_lock) return _all.Select(p => p.Name).OrderBy(n => n).ToList(); } }

    public string? Match(float[]? voice)
    {
        if (voice is not { Length: > 0 }) return null;
        lock (_lock)
        {
            var best = _all.Select(p => (p.Name, Score: VoiceMath.Cos(p.Voice, voice))).OrderByDescending(x => x.Score).FirstOrDefault();
            return best.Score >= VoiceMath.Known ? best.Name : null;
        }
    }

    /// <summary>El usuario dijo que esa voz es name: se suma a su huella (o se crea).</summary>
    public void Learn(string name, float[]? voice, DateTime now)
    {
        name = name.Trim();
        if (name.Length == 0 || voice is not { Length: > 0 }) return;
        lock (_lock)
        {
            var i = _all.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) _all.Add(new Person(name, VoiceMath.Mix(null, 0, voice, 1), 1, now));
            else _all[i] = _all[i] with { Voice = VoiceMath.Mix(_all[i].Voice, _all[i].Meetings, voice, 1), Meetings = _all[i].Meetings + 1, Updated = now };
            Save();
        }
    }

    public void Forget(string name)
    {
        lock (_lock)
        {
            if (_all.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0) Save();
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_all));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch { }
    }
}
