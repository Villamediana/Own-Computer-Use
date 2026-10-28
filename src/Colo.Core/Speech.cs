using SherpaOnnx;

namespace Colo.Core;

/// <summary>Modelos locales: Silero VAD (corta las frases) y Parakeet TDT 0.6B v3 (transcribe, 25 idiomas).</summary>
public sealed class SpeechModels(string dir)
{
    const string Parakeet = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/";
    const string SileroUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx";

    public string Dir { get; } = dir;
    public string Silero => Path.Combine(Dir, "silero_vad.onnx");
    public string Encoder => Path.Combine(Dir, "parakeet-v3", "encoder.int8.onnx");
    public string Decoder => Path.Combine(Dir, "parakeet-v3", "decoder.int8.onnx");
    public string Joiner => Path.Combine(Dir, "parakeet-v3", "joiner.int8.onnx");
    public string Tokens => Path.Combine(Dir, "parakeet-v3", "tokens.txt");

    (string Url, string Path)[] Files(bool asr) => asr
        ? [(SileroUrl, Silero), (Parakeet + "encoder.int8.onnx", Encoder), (Parakeet + "decoder.int8.onnx", Decoder),
           (Parakeet + "joiner.int8.onnx", Joiner), (Parakeet + "tokens.txt", Tokens)]
        : [(SileroUrl, Silero)];

    public bool Ready(bool asr) => Files(asr).All(f => File.Exists(f.Path));

    /// <summary>Descarga lo que falte (~670 MB con Parakeet). progress: 0..1.</summary>
    public async Task DownloadAsync(bool asr, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var missing = Files(asr).Where(f => !File.Exists(f.Path)).ToList();
        var sizes = new long[missing.Count];
        for (var i = 0; i < missing.Count; i++)
        {
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, missing[i].Url), ct);
            sizes[i] = head.Content.Headers.ContentLength ?? 1;
        }
        var total = Math.Max(1, sizes.Sum());
        long done = 0;
        var buf = new byte[1 << 16];
        for (var i = 0; i < missing.Count; i++)
        {
            var (url, path) = missing[i];
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var part = path + ".part";
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(part);
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress?.Report(Math.Min(1, (double)done / total));
                }
            }
            File.Move(part, path, overwrite: true);
        }
        progress?.Report(1);
    }
}

/// <summary>
/// Micrófono siempre abierto: recibe PCM 16 kHz y entrega cada frase al terminar
/// (Silero VAD, ~0,6 s de silencio).
/// </summary>
public sealed class Listener : IDisposable
{
    public const int Rate = 16000;
    const int Window = 512;

    readonly VoiceActivityDetector _vad;
    readonly float[] _chunk = new float[Window];
    int _fill;
    bool _speaking;

    public bool Muted { get; set; }
    public event Action<float[]>? Utterance;
    public event Action<bool>? SpeakingChanged;
    public event Action<double>? Level;

    /// <param name="sensitivity">1 (poco sensible) a 10 (muy sensible).</param>
    public Listener(string sileroPath, int sensitivity)
    {
        var cfg = new VadModelConfig();
        cfg.SileroVad.Model = sileroPath;
        cfg.SileroVad.Threshold = SensitivityToThreshold(sensitivity);
        cfg.SileroVad.MinSilenceDuration = 0.6f;
        cfg.SileroVad.MinSpeechDuration = 0.25f;
        cfg.SileroVad.MaxSpeechDuration = 20f;
        cfg.SileroVad.WindowSize = Window;
        cfg.SampleRate = Rate;
        cfg.NumThreads = 1;
        _vad = new VoiceActivityDetector(cfg, 30);
    }

    public static float SensitivityToThreshold(int s) => 0.75f - 0.05f * (Math.Clamp(s, 1, 10) - 1);

    public void Feed(ReadOnlySpan<short> pcm)
    {
        double sum = 0;
        foreach (var s in pcm) sum += (double)s * s;
        if (pcm.Length > 0) Level?.Invoke(Math.Sqrt(sum / pcm.Length) / 32768.0);

        if (Muted)
        {
            if (_speaking || _fill > 0) Reset();
            return;
        }
        foreach (var s in pcm)
        {
            _chunk[_fill++] = s / 32768f;
            if (_fill < Window) continue;
            _fill = 0;
            _vad.AcceptWaveform(_chunk);
            var speaking = _vad.IsSpeechDetected();
            if (speaking != _speaking)
            {
                _speaking = speaking;
                SpeakingChanged?.Invoke(speaking);
            }
            Drain();
        }
    }

    void Drain()
    {
        while (!_vad.IsEmpty())
        {
            var seg = _vad.Front().Samples;
            _vad.Pop();
            if (seg.Length > Rate / 4) Utterance?.Invoke(seg);
        }
    }

    public void Reset()
    {
        _vad.Reset();
        _fill = 0;
        if (_speaking) SpeakingChanged?.Invoke(_speaking = false);
    }

    public void Dispose() => _vad.Dispose();
}

/// <summary>Transcripción local con Parakeet TDT v3 (CPU).</summary>
public sealed class LocalStt : IDisposable
{
    readonly OfflineRecognizer _rec;
    readonly object _lock = new();

    public LocalStt(SpeechModels m)
    {
        var cfg = new OfflineRecognizerConfig();
        cfg.FeatConfig.SampleRate = Listener.Rate;
        cfg.FeatConfig.FeatureDim = 80;
        cfg.ModelConfig.Transducer.Encoder = m.Encoder;
        cfg.ModelConfig.Transducer.Decoder = m.Decoder;
        cfg.ModelConfig.Transducer.Joiner = m.Joiner;
        cfg.ModelConfig.Tokens = m.Tokens;
        cfg.ModelConfig.ModelType = "nemo_transducer";
        cfg.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        cfg.ModelConfig.Provider = "cpu";
        cfg.DecodingMethod = "greedy_search";
        _rec = new OfflineRecognizer(cfg);
    }

    public string Transcribe(float[] samples, int rate = Listener.Rate)
    {
        lock (_lock)
        {
            using var stream = _rec.CreateStream();
            stream.AcceptWaveform(rate, samples);
            _rec.Decode(stream);
            return stream.Result.Text.Trim();
        }
    }

    public void Dispose() => _rec.Dispose();
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
