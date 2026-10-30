using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;

namespace Colo.Core;

public sealed record WhisperModel(string Id, string File, long Size, string Title);

/// <summary>Modelos de Whisper (whisper.cpp, formato ggml) que se bajan una vez a %LOCALAPPDATA%\Colo\models\whisper.</summary>
public sealed class SpeechModels(string dir, string? bundledDir = null)
{
    const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static readonly WhisperModel[] Whisper =
    [
        new("base", "ggml-base-q5_1.bin", 59_707_625, "Whisper base · 60 MB · el más rápido"),
        new("small", "ggml-small-q5_1.bin", 190_085_487, "Whisper small · 190 MB · más preciso"),
    ];

    public string Dir { get; } = dir;

    public static WhisperModel Find(string? id) => Whisper.FirstOrDefault(m => m.Id == id) ?? Whisper[0];

    /// <summary>El que trae el instalador (junto a Colo.exe) si está entero; si no, el descargado.</summary>
    public string PathOf(WhisperModel m)
    {
        if (bundledDir is not null)
        {
            var bundled = Path.Combine(bundledDir, "whisper", m.File);
            if (Valid(bundled, m)) return bundled;
        }
        return DownloadPath(m);
    }

    string DownloadPath(WhisperModel m) => Path.Combine(Dir, "whisper", m.File);

    static bool Valid(string path, WhisperModel m) => File.Exists(path) && new FileInfo(path).Length == m.Size;

    /// <summary>Existe y con el tamaño exacto (un archivo cortado o una página de error no cuentan).</summary>
    public bool Ready(WhisperModel m) => Valid(PathOf(m), m);

    public async Task DownloadAsync(WhisperModel m, IProgress<double>? progress, CancellationToken ct)
    {
        var path = DownloadPath(m);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using (var resp = await http.GetAsync(Url + m.File, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress?.Report(Math.Min(0.99, (double)done / m.Size));
            }
        }
        if (new FileInfo(part).Length != m.Size)
        {
            File.Delete(part);
            throw new IOException($"{m.File} llegó incompleto; vuelve a intentarlo");
        }
        File.Move(part, path, overwrite: true);
        progress?.Report(1);
    }
}

/// <summary>
/// Recibe el PCM 16 kHz del micrófono. Con el atajo, guarda lo que se dice mientras está apretado
/// (<see cref="BeginTake"/> / <see cref="EndTake"/>, con 300 ms de antes para no comerse la primera sílaba).
/// El detector por volumen (piso de ruido adaptativo) corta frases solas para la prueba del micrófono.
/// </summary>
public sealed class Listener : IDisposable
{
    public const int Rate = 16000;

    readonly EnergyDetector _energy;
    readonly object _lock = new();
    long _pos;
    bool _speaking, _disposed;

    public bool Muted { get; set; }
    /// <summary>false = sin detector por volumen: las frases solo salen del atajo.</summary>
    public bool Detect { get; set; } = true;
    const int Preroll = Rate * 3 / 10;
    readonly Queue<short> _recent = new();
    List<short>? _take;
    // Trozos mientras se mantiene el atajo: en cada pausa (≥ 0,5 s tras ≥ 4 s) sale lo dicho hasta ahí, para
    // ir transcribiéndolo; al soltar solo queda el último trozo y la espera no crece con lo largo que hablaste.
    const int TakeFrame = Rate / 50, MinChunk = Rate * 4, PauseFrames = 25;
    int _cut, _frameN, _silentRun, _voicedRun;
    double _frameSum, _peakRms;
    float _takePeak;

    /// <summary>Un trozo de lo dicho con el atajo apretado, cortado en una pausa.</summary>
    public event Action<float[]>? TakeChunk;
    /// <summary>De la última toma entera (no solo el último trozo): duración y pico.</summary>
    public double LastTakeSeconds { get; private set; }
    public float LastTakePeak { get; private set; }
    public event Action<float[]>? Utterance;
    public event Action<bool>? SpeakingChanged;
    public event Action<double>? Level;
    /// <summary>El audio ya convertido (16 kHz mono), antes del detector. Para diagnóstico.</summary>
    public event PcmSink? Raw;

    /// <param name="sensitivity">1 (poco sensible) a 10 (muy sensible).</param>
    public Listener(int sensitivity) => _energy = new EnergyDetector(sensitivity);

    public void Feed(ReadOnlySpan<short> pcm)
    {
        double sum = 0;
        foreach (var s in pcm) sum += (double)s * s;
        if (pcm.Length > 0) Level?.Invoke(Math.Sqrt(sum / pcm.Length) / 32768.0);
        Raw?.Invoke(pcm);

        lock (_lock)
        {
            if (_disposed) return;
            if (_take is not null)
            {
                foreach (var s in pcm) TakeSample(s);
                return;
            }
            foreach (var s in pcm)
            {
                _recent.Enqueue(s);
                if (_recent.Count > Preroll) _recent.Dequeue();
            }
            if (Muted || !Detect)
            {
                ResetLocked();
                return;
            }
            foreach (var s in pcm)
            {
                var phrase = _energy.Push(s, ++_pos);
                if (_energy.InSpeech != _speaking)
                {
                    _speaking = _energy.InSpeech;
                    SpeakingChanged?.Invoke(_speaking);
                }
                if (phrase is not null) Utterance?.Invoke(phrase.Samples);
            }
        }
    }

    void TakeSample(short s)
    {
        _take!.Add(s);
        _takePeak = Math.Max(_takePeak, Math.Abs(s / 32768f));
        _frameSum += (double)s * s;
        if (++_frameN < TakeFrame) return;
        var rms = Math.Sqrt(_frameSum / TakeFrame) / 32768.0;
        _frameN = 0;
        _frameSum = 0;
        _peakRms = Math.Max(_peakRms, rms);
        // Silencio relativo a lo fuerte que hablas en esta toma (el micrófono de cada uno es distinto).
        if (rms < Math.Max(0.003, _peakRms * 0.12)) _silentRun++;
        else { _silentRun = 0; _voicedRun++; }
        if (_take.Count - _cut < MinChunk || _silentRun < PauseFrames || _voicedRun < PauseFrames) return;
        var end = _take.Count - _silentRun * TakeFrame / 2;
        var chunk = new float[end - _cut];
        for (var i = 0; i < chunk.Length; i++) chunk[i] = _take[_cut + i] / 32768f;
        _cut = end;
        _voicedRun = 0;
        TakeChunk?.Invoke(chunk);
    }

    /// <summary>Empieza a guardar todo lo que llega (aunque esté en silencio), con lo de los últimos 300 ms.</summary>
    public void BeginTake()
    {
        lock (_lock)
        {
            _take = [.. _recent];
            _recent.Clear();
            _cut = _frameN = _silentRun = _voicedRun = 0;
            _frameSum = _peakRms = 0;
            _takePeak = 0;
            foreach (var v in _take) _takePeak = Math.Max(_takePeak, Math.Abs(v / 32768f));
            ResetLocked();
        }
    }

    public bool Taking { get { lock (_lock) return _take is not null; } }

    /// <summary>Lo guardado desde el último trozo entregado (o desde <see cref="BeginTake"/>); vacío si no se estaba guardando.</summary>
    public float[] EndTake()
    {
        lock (_lock)
        {
            var take = _take;
            _take = null;
            if (take is null) return [];
            LastTakeSeconds = take.Count / (double)Rate;
            LastTakePeak = _takePeak;
            return take.Skip(_cut).Select(v => v / 32768f).ToArray();
        }
    }

    public void Reset()
    {
        lock (_lock) ResetLocked();
    }

    void ResetLocked()
    {
        _energy.Reset();
        if (_speaking) SpeakingChanged?.Invoke(_speaking = false);
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
    }
}

/// <summary>Transcripción local con Whisper (whisper.cpp) en el procesador.</summary>
public sealed class LocalStt : IDisposable
{
    readonly WhisperFactory _factory;
    readonly object _lock = new();
    readonly int _threads = Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
    bool _disposed;

    /// <summary>Idioma fijo («es», «pt», «en»…) o «auto». Fijo es mucho más fiable en frases cortas.</summary>
    public string Language { get; set; } = "es";
    /// <summary>Contexto de audio proporcional a la frase (más rápido). false = ventana completa de 30 s.</summary>
    public bool ShortContext { get; set; } = true;
    /// <summary>Texto previo que orienta a Whisper hacia estas palabras (nombres de apps, «pestaña», «aba»…).</summary>
    public string Prompt { get; set; } = "";

    /// <summary>
    /// Palabras de las órdenes (en español y portugués) más los programas abiertos. Medido con frases de
    /// prueba: acierta «URL», «Backlog», «Comet» donde sin esto salía «wrl», «baclo», «comete».
    /// </summary>
    public static string PromptFor(IEnumerable<string> apps)
    {
        var names = apps.Where(a => a.Length > 1).Select(a => char.ToUpperInvariant(a[0]) + a[1..])
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12);
        // El idioma no va fijo; esto lo orienta (en frases cortas, sin pista, a veces sale inglés).
        return "Habla español o português. Glosario: pestaña, aba, guía, navegador, barra de direcciones, endereço, URL, escribe, escreve, clic, clica, "
            + string.Join(", ", names) + ".";
    }

    public LocalStt(string modelPath) => _factory = WhisperFactory.FromPath(modelPath);

    /// <summary>
    /// Dónde están las DLL nativas de Whisper (carpeta con whisper.dll y sus ggml-*). Hay que fijarlo antes
    /// del primer LocalStt. Solo CPU: así no se pierde tiempo buscando CUDA/Vulkan.
    /// </summary>
    public static void UseNativeFolder(string? folder)
    {
        Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = [Whisper.net.LibraryLoader.RuntimeLibrary.Cpu];
        if (folder is not null)
            Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = Path.Combine(folder, OperatingSystem.IsWindows() ? "whisper.dll" : "libwhisper.so");
    }

    public string Transcribe(float[] samples, int rate = Listener.Rate)
    {
        if (rate != Listener.Rate) samples = Resample(samples, rate, Listener.Rate);
        lock (_lock)
        {
            if (_disposed || samples.Length < Listener.Rate / 5) return "";
            // Whisper siempre procesa ventanas de 30 s (contexto 1500). Para una frase corta basta un
            // contexto proporcional a su duración (50 por segundo + margen): el encoder va mucho más rápido.
            var ctx = ShortContext ? Math.Clamp(samples.Length * 50 / Listener.Rate + 128, 256, 1500) : 1500;
            var builder = _factory.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(Language) ? "auto" : Language)
                .WithThreads(_threads)
                .WithNoContext()
                .WithAudioContextSize(ctx)
                .WithNoSpeechThreshold(0.6f);
            if (Prompt.Length > 0) builder = builder.WithPrompt(Prompt);
            using var processor = builder.Build();
            var text = new StringBuilder();
            foreach (var seg in processor.ProcessAsync(samples).ToBlockingEnumerable())
            {
                // Con ruido Whisper a veces inventa («Gracias por ver el video»): esos tramos traen
                // probabilidad alta de «no hay voz».
                if (seg.NoSpeechProbability > 0.6f) continue;
                text.Append(seg.Text);
            }
            var result = Clean(text.ToString());
            // Con ruido Whisper puede repetir el glosario que se le dio: eso no lo dijo nadie.
            return result.StartsWith("Glosario", StringComparison.OrdinalIgnoreCase) || result.StartsWith("Habla español", StringComparison.OrdinalIgnoreCase) ? "" : result;
        }
    }

    /// <summary>Quita marcas como [Música] o (risas) y espacios sobrantes.</summary>
    public static string Clean(string text) =>
        Regex.Replace(Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|♪", " "), @"\s+", " ").Trim();

    static float[] Resample(float[] x, int from, int to)
    {
        var y = new float[(int)((long)x.Length * to / from)];
        for (var i = 0; i < y.Length; i++)
        {
            var p = (double)i * from / to;
            var k = (int)p;
            var t = (float)(p - k);
            y[i] = k + 1 < x.Length ? x[k] * (1 - t) + x[k + 1] * t : x[^1];
        }
        return y;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _factory.Dispose();
        }
    }
}

public static class Wav
{
    public static byte[] Encode(ReadOnlySpan<float> samples, int rate = Listener.Rate)
    {
        var data = samples.Length * 2;
        var b = new byte[44 + data];
        using var w = new BinaryWriter(new MemoryStream(b));
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
        return b;
    }

    /// <summary>Lee un WAV PCM 16-bit mono (para pruebas).</summary>
    public static float[] Read(string path, out int rate)
    {
        var b = File.ReadAllBytes(path);
        rate = BitConverter.ToInt32(b, 24);
        var i = 12;
        while (i + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, i, 4);
            var len = BitConverter.ToInt32(b, i + 4);
            if (id == "data")
            {
                var n = Math.Min(len, b.Length - i - 8) / 2;
                var r = new float[n];
                for (var k = 0; k < n; k++) r[k] = BitConverter.ToInt16(b, i + 8 + k * 2) / 32768f;
                return r;
            }
            i += 8 + len;
        }
        throw new InvalidDataException("WAV sin datos");
    }
}

/// <summary>Detector de frases por volumen con piso de ruido adaptativo (frames de 20 ms).</summary>
sealed class EnergyDetector(int sensitivity)
{
    const int Frame = Listener.Rate / 50;   // 20 ms
    const int StartFrames = 4;              // 80 ms de voz para empezar
    const int EndFrames = 40;               // 0,8 s de silencio cierra la frase
    const int MinVoiced = 12;               // 240 ms de voz como mínimo
    const int MaxFrames = 20 * 50;
    const int Preroll = 15;                 // 300 ms antes del arranque

    public sealed record Phrase(float[] Samples, long Start, long End);

    readonly short[] _frame = new short[Frame];
    readonly Queue<short[]> _pre = new();
    readonly List<short> _speech = [];
    int _fill, _run, _silence, _voiced, _frames;
    bool _in;
    long _start;
    double _noise = 0.003;

    double Factor => 5.0 - 0.35 * (Math.Clamp(sensitivity, 1, 10) - 1);

    public bool InSpeech => _in;

    public void Reset()
    {
        _fill = _run = _silence = _voiced = _frames = 0;
        _in = false;
        _speech.Clear();
        _pre.Clear();
    }

    /// <summary>Un sample; devuelve la frase cuando se cierra.</summary>
    public Phrase? Push(short s, long pos)
    {
        _frame[_fill++] = s;
        if (_fill < Frame) return null;
        _fill = 0;
        double sum = 0;
        foreach (var v in _frame) sum += (double)v * v;
        var rms = Math.Sqrt(sum / Frame) / 32768.0;
        var voiced = rms > Math.Max(0.004, _noise * Factor);

        if (!_in)
        {
            _noise += (rms - _noise) * (rms > _noise ? 0.005 : 0.05);
            _pre.Enqueue((short[])_frame.Clone());
            if (_pre.Count > Preroll) _pre.Dequeue();
            _run = voiced ? _run + 1 : 0;
            if (_run < StartFrames) return null;
            _in = true;
            _speech.Clear();
            foreach (var f in _pre) _speech.AddRange(f);
            _start = pos - _speech.Count;
            _pre.Clear();
            _voiced = _frames = _run;
            _silence = 0;
            return null;
        }

        _speech.AddRange(_frame);
        _frames++;
        if (voiced) { _voiced++; _silence = 0; } else _silence++;
        if (_silence < EndFrames && _frames < MaxFrames) return null;

        _in = false;
        _run = 0;
        var phrase = new Phrase(_speech.Select(v => v / 32768f).ToArray(), _start, pos);
        _speech.Clear();
        return _voiced >= MinVoiced ? phrase : null;
    }
}

/// <summary>Diagnóstico de la prueba del micrófono: frases que encuentra el detector y qué entiende Whisper.</summary>
public static class SpeechProbe
{
    public sealed record Result(double Seconds, double Peak, double Rms, double Dc, string Text, int Phrases);

    public static Result Analyze(short[] pcm, int sensitivity, LocalStt? stt)
    {
        double sum = 0, sq = 0, peak = 0;
        foreach (var v in pcm)
        {
            var f = v / 32768.0;
            sum += f;
            sq += f * f;
            peak = Math.Max(peak, Math.Abs(f));
        }
        var n = Math.Max(1, pcm.Length);
        using var l = new Listener(sensitivity);
        var phrases = 0;
        l.Utterance += _ => phrases++;
        for (var i = 0; i < pcm.Length; i += 640) l.Feed(pcm.AsSpan(i, Math.Min(640, pcm.Length - i)));
        l.Feed(new short[Listener.Rate * 2]); // silencio para cerrar la última frase
        var text = stt?.Transcribe(pcm.Select(v => v / 32768f).ToArray()) ?? "(Whisper no está cargado)";
        return new Result(pcm.Length / (double)Listener.Rate, peak, Math.Sqrt(sq / n), sum / n, text, phrases);
    }
}
