using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Colo.Core;

/// <summary>
/// Convierte lo que entrega el micrófono (formato real del aparato: 44,1/48 kHz, estéreo, float o PCM)
/// a 16 kHz mono 16 bits, que es lo que esperan el detector de voz y Whisper.
/// Sin esto, un headset de 48 kHz leído como 16 kHz suena tres veces más lento y no se detecta voz.
/// </summary>
public delegate void PcmSink(ReadOnlySpan<short> pcm);

public sealed class AudioConverter
{
    readonly BufferedWaveProvider _buffer;
    readonly ISampleProvider _out;
    readonly float[] _floats = new float[4096];
    readonly short[] _pcm = new short[4096];
    readonly ChannelStats _stats;

    /// <summary>Tras unos segundos: volumen de cada canal y si se parecen (para micrófonos raros).</summary>
    public string? ChannelReport => _stats.Report;

    public WaveFormat Input { get; }

    public AudioConverter(WaveFormat input)
    {
        Input = Standard(input);
        _buffer = new BufferedWaveProvider(Input)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        ISampleProvider sp = _stats = new ChannelStats(_buffer.ToSampleProvider());
        if (sp.WaveFormat.Channels > 1) sp = new MonoMix(sp);
        if (sp.WaveFormat.SampleRate != Listener.Rate) sp = new WdlResamplingSampleProvider(sp, Listener.Rate);
        _out = sp;
    }

    public string Describe() =>
        $"{Input.SampleRate} Hz, {Input.Channels} canal(es), {(Input.Encoding == WaveFormatEncoding.IeeeFloat ? "float" : "PCM")} {Input.BitsPerSample} bits → 16 kHz mono";

    /// <summary>Mete bytes del micrófono y entrega el audio convertido en trozos.</summary>
    public void Push(byte[] data, int count, PcmSink sink)
    {
        _buffer.AddSamples(data, 0, count);
        int n;
        while ((n = _out.Read(_floats, 0, _floats.Length)) > 0)
        {
            for (var i = 0; i < n; i++) _pcm[i] = (short)Math.Clamp(_floats[i] * 32767f, -32768f, 32767f);
            sink(_pcm.AsSpan(0, n));
        }
    }

    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    /// <summary>WASAPI entrega WaveFormatExtensible; aquí se pasa al formato simple equivalente.</summary>
    static WaveFormat Standard(WaveFormat f)
    {
        if (f is not WaveFormatExtensible ext) return f;
        var sub = ext.SubFormat;
        if (sub == IeeeFloat)
            return WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels);
        return new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels);
    }

    /// <summary>Mide cada canal durante los primeros 5 s sin tocar el audio.</summary>
    sealed class ChannelStats(ISampleProvider source) : ISampleProvider
    {
        readonly int _ch = source.WaveFormat.Channels;
        readonly double[] _sq = new double[Math.Max(1, source.WaveFormat.Channels)];
        double _cross;
        long _frames;
        public string? Report { get; private set; }
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var got = source.Read(buffer, offset, count);
            if (Report is not null) return got;
            for (var i = 0; i + _ch <= got; i += _ch)
            {
                for (var c = 0; c < _ch; c++) _sq[c] += buffer[offset + i + c] * buffer[offset + i + c];
                if (_ch > 1) _cross += buffer[offset + i] * buffer[offset + i + 1];
                _frames++;
            }
            if (_frames >= WaveFormat.SampleRate * 5L)
            {
                var rms = _sq.Select(v => Math.Sqrt(v / _frames)).ToArray();
                var corr = _ch > 1 && _sq[0] > 0 && _sq[1] > 0 ? _cross / Math.Sqrt(_sq[0] * _sq[1]) : 1;
                Report = $"canales rms [{string.Join(", ", rms.Select(r => r.ToString("0.0000")))}], correlación 1-2 {corr:0.00}";
            }
            return got;
        }
    }

    /// <summary>Promedio de todos los canales (sirve para 2, 4, 6… canales).</summary>
    sealed class MonoMix(ISampleProvider source) : ISampleProvider
    {
        float[] _in = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var ch = source.WaveFormat.Channels;
            if (_in.Length < count * ch) _in = new float[count * ch];
            var got = source.Read(_in, 0, count * ch) / ch;
            for (var i = 0; i < got; i++)
            {
                float sum = 0;
                for (var c = 0; c < ch; c++) sum += _in[i * ch + c];
                buffer[offset + i] = sum / ch;
            }
            return got;
        }
    }
}
