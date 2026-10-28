using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Colo.Core;

/// <summary>
/// Oye «Luna», y solo eso, en el propio PC: el mismo detector que la Luna del celular. Tres redes pequeñas en
/// ONNX Runtime: el espectrograma mel y el embedding de voz de openWakeWord (Apache-2.0, aprendidos con miles
/// de horas de gente real) y un clasificador sobre los últimos 1,28 s, entrenado con «luna» dichos por personas
/// reales en español (Multilingual Spoken Words) y «lua» del portugués como negativos.
/// Cada <see cref="Chunk"/> de audio a 16 kHz (80 ms) da una nota de 0 a 1; el bucle es el de
/// tools/wakeword/oww_stream.py en el repo de la Luna, paso a paso.
/// </summary>
public sealed class WakeModel : IDisposable
{
    /// <summary>80 ms a 16 kHz: una nota por trozo.</summary>
    public const int Chunk = 1280;
    const int Overlap = 480, Mels = 32, MelFrames = 76, Embedding = 96, Embeddings = 16, Prime = 25;

    readonly InferenceSession _mel, _embedding, _classifier;
    readonly SessionOptions _options = new() { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
    /// <summary>Las últimas <see cref="Overlap"/> muestras y luego el trozo nuevo: la ventana mel mira 30 ms atrás.</summary>
    readonly float[] _audio = new float[Overlap + Chunk];
    /// <summary>Los últimos <see cref="MelFrames"/> fotogramas mel; unos antes de cualquier sonido, como openWakeWord.</summary>
    readonly float[] _mels = Enumerable.Repeat(1f, MelFrames * Mels).ToArray();
    readonly float[] _embeddings = new float[Embeddings * Embedding];

    public WakeModel(byte[] mel, byte[] embedding, byte[] classifier)
    {
        _mel = new InferenceSession(mel, _options);
        _embedding = new InferenceSession(embedding, _options);
        _classifier = new InferenceSession(classifier, _options);
        // Dos segundos de silencio, para que el primer trozo real se puntúe con la ventana llena.
        var silence = new short[Chunk];
        for (var i = 0; i < Prime; i++) Score(silence);
    }

    /// <summary>El modelo que viene dentro de Colo.</summary>
    public static WakeModel Bundled() => new(Resource("melspectrogram.onnx"), Resource("embedding.onnx"), Resource("luna.onnx"));

    static byte[] Resource(string name)
    {
        using var s = typeof(WakeModel).Assembly.GetManifestResourceStream("Colo.wake." + name)
            ?? throw new InvalidOperationException("Falta el modelo " + name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>La probabilidad de que los últimos 1,28 s terminen en «Luna», tras <paramref name="chunk"/> (justo <see cref="Chunk"/> muestras).</summary>
    public float Score(ReadOnlySpan<short> chunk)
    {
        if (chunk.Length != Chunk) throw new ArgumentException($"El trozo debe tener {Chunk} muestras");
        Array.Copy(_audio, Chunk, _audio, 0, Overlap);
        for (var i = 0; i < Chunk; i++) _audio[Overlap + i] = chunk[i];

        var frames = Run(_mel, _audio, [1, _audio.Length]);
        var fresh = frames.Length / Mels * Mels;
        Array.Copy(_mels, fresh, _mels, 0, _mels.Length - fresh);
        // El mismo reescalado que aplica openWakeWord, para igualar el front end de TensorFlow de Google.
        for (var i = 0; i < frames.Length; i++) _mels[_mels.Length - frames.Length + i] = frames[i] / 10f + 2f;

        var embedding = Run(_embedding, _mels, [1, MelFrames, Mels, 1]);
        Array.Copy(_embeddings, Embedding, _embeddings, 0, _embeddings.Length - Embedding);
        Array.Copy(embedding, 0, _embeddings, _embeddings.Length - Embedding, Embedding);

        return Run(_classifier, _embeddings, [1, Embeddings, Embedding])[0];
    }

    static float[] Run(InferenceSession session, float[] input, int[] shape)
    {
        var tensor = new DenseTensor<float>(input, shape);
        using var result = session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor)]);
        return result.First().AsEnumerable<float>().ToArray();
    }

    public void Dispose()
    {
        _classifier.Dispose();
        _embedding.Dispose();
        _mel.Dispose();
        _options.Dispose();
    }
}

/// <summary>
/// Escucha «Luna» en el audio del micrófono (16 kHz mono, a trozos de cualquier tamaño) y avisa con
/// <see cref="Woke"/>. Una sola nota por encima del corte basta, como decide openWakeWord (el clasificador
/// ya mira 1,28 s); los primeros trozos tras abrir el micrófono no cuentan mientras se asienta.
/// Puntúa en su propio hilo para no frenar la captura.
/// </summary>
public sealed class WakeWord : IDisposable
{
    /// <summary>Corte medido en voces reales nunca vistas: 88 % de cerca, 78 % bajito al otro lado del cuarto, 0,27 falsos/h.</summary>
    public const float Cutoff = 0.7f;
    const int Warmup = 5, Refractory = 12;

    readonly WakeModel _model;
    readonly System.Threading.Channels.Channel<short[]> _chunks =
        System.Threading.Channels.Channel.CreateBounded<short[]>(new System.Threading.Channels.BoundedChannelOptions(64)
        { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest, SingleReader = true });
    readonly short[] _pending = new short[WakeModel.Chunk];
    int _filled, _seen;
    readonly Task _loop;

    /// <summary>Oyó «Luna»; lleva la nota.</summary>
    public event Action<float>? Woke;

    public WakeWord(WakeModel model)
    {
        _model = model;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Hilo de audio: se copia y se puntúa aparte.</summary>
    public void Feed(ReadOnlySpan<short> pcm)
    {
        while (pcm.Length > 0)
        {
            var n = Math.Min(pcm.Length, WakeModel.Chunk - _filled);
            pcm[..n].CopyTo(_pending.AsSpan(_filled));
            _filled += n;
            pcm = pcm[n..];
            if (_filled < WakeModel.Chunk) break;
            _chunks.Writer.TryWrite((short[])_pending.Clone());
            _filled = 0;
        }
    }

    /// <summary>Tras abrir el micrófono o cuando Colo deja de hablar: los primeros trozos no cuentan.</summary>
    public void Settle() => _seen = 0;

    async Task LoopAsync()
    {
        await foreach (var chunk in _chunks.Reader.ReadAllAsync())
        {
            float score;
            try { score = _model.Score(chunk); }
            catch { continue; }
            if (++_seen <= Warmup || score < Cutoff) continue;
            // Una sola vez por «Luna»: la nota sigue alta un rato; ~1,4 s sin volver a disparar.
            _seen = -Refractory;
            Woke?.Invoke(score);
        }
    }

    public void Dispose()
    {
        _chunks.Writer.TryComplete();
        try { _loop.Wait(1000); } catch { }
        _model.Dispose();
    }
}
