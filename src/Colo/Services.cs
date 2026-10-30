using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Threading;
using Colo.Core;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Colo;

/// <summary>Configuración en %APPDATA%\Colo; la API key va cifrada con DPAPI (solo este usuario de Windows).</summary>
static class Store
{
    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Colo");
    public static string ModelsDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colo", "models");
    static string FilePath => Path.Combine(Dir, "settings.json");

    sealed class Saved
    {
        public Settings Settings { get; set; } = new();
        public string? Key { get; set; }
        public string? ElevenKey { get; set; }
        public string? VoiceKey { get; set; }
    }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    static readonly object Gate = new();

    public static Settings Load()
    {
        Settings s;
        Saved? saved;
        try
        {
            if (!File.Exists(FilePath)) return new Settings();
            saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            s = saved?.Settings ?? new Settings();
        }
        catch { return new Settings(); }

        // Archivos de versiones anteriores o editados a mano pueden traer null.
        s.DisabledTools ??= [];
        s.McpServers ??= [];
        s.Prices ??= [];
        s.Endpoint ??= "";
        s.Model ??= "";
        s.TranscribeModel ??= "";
        s.ReasoningEffort ??= "low";
        s.ElevenLabsModel ??= "scribe_v2";
        s.VoiceEndpoint ??= "";
        s.LiveModel ??= "gpt-live-1";
        s.LiveVoice ??= "marin";
        if (!Colo.Core.Keys.Hold(s.TalkHotkey ?? "", out _)) s.TalkHotkey = "Ctrl+Win";
        if (!string.IsNullOrEmpty(s.DictateHotkey) && !Colo.Core.Keys.Hold(s.DictateHotkey, out _)) s.DictateHotkey = "";
        s.DictateHotkey ??= "";
        // Si una key no se puede descifrar (otro usuario de Windows), se pierde solo esa key, no los ajustes.
        s.ApiKey = Unprotect(saved?.Key);
        s.ElevenLabsKey = Unprotect(saved?.ElevenKey);
        s.VoiceKey = Unprotect(saved?.VoiceKey);
        return s;
    }

    static string? Protect(string? text) => string.IsNullOrEmpty(text) ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    static string Unprotect(string? blob)
    {
        try
        {
            return string.IsNullOrEmpty(blob) ? ""
                : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    /// <summary>Guarda una copia: el objeto en uso (que el agente está leyendo) no se toca.</summary>
    public static void Save(Settings s)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
            var saved = new Saved { Settings = copy, Key = Protect(copy.ApiKey), ElevenKey = Protect(copy.ElevenLabsKey), VoiceKey = Protect(copy.VoiceKey) };
            copy.ApiKey = "";
            copy.ElevenLabsKey = "";
            copy.VoiceKey = "";
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(saved, Pretty));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}

/// <summary>
/// Micrófono por WASAPI (la API de audio actual de Windows) en su formato real, convertido a 16 kHz mono
/// para el Listener. La API vieja (WinMM) entregaba los headsets de 48 kHz sin convertir y no se detectaba voz.
/// </summary>
sealed class Mic : IDisposable
{
    WasapiCapture? _capture;
    Listener? _listener;
    ManualResetEventSlim? _stopped;

    /// <summary>El micrófono se cortó solo (desconectado, otro programa lo tomó…).</summary>
    public event Action<string>? Failed;

    public sealed record Device(string Id, string Name);

    public static List<Device> Devices()
    {
        var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new Device(d.ID, d.FriendlyName)).ToList();
    }

    public bool Running => _capture is not null;
    public string DeviceName { get; private set; } = "";

    /// <param name="id">ID de WASAPI; vacío (o ya no conectado) = el predeterminado de Windows.</param>
    public void Start(string id, Listener listener)
    {
        Stop();
        _listener = listener;
        // Se crea y arranca fuera del hilo de la interfaz: así RecordingStopped no queda esperando en la cola
        // de la UI mientras Stop() espera por él.
        var (capture, name) = Task.Run(() => Open(id)).GetAwaiter().GetResult();
        var converter = new AudioConverter(capture.WaveFormat);
        var stopped = new ManualResetEventSlim(false);
        var errors = 0;
        var first = true;
        var reported = false;
        capture.DataAvailable += (_, e) =>
        {
            if (first)
            {
                first = false;
                Log.Write($"micrófono: llegan datos ({e.BytesRecorded} bytes por bloque)");
            }
            // Un error procesando el audio no puede matar la grabación en silencio.
            try
            {
                converter.Push(e.Buffer, e.BytesRecorded, pcm => listener.Feed(pcm));
                if (!reported && converter.ChannelReport is { } report)
                {
                    reported = true;
                    Log.Write("micrófono: " + report);
                }
            }
            catch (Exception ex)
            {
                if (errors++ < 3) Log.Write("audio: error procesando · " + ex);
                if (errors == 1) Failed?.Invoke("error procesando el audio: " + ex.Message);
            }
        };
        capture.RecordingStopped += (_, e) =>
        {
            stopped.Set();
            // Parada que no pedimos: avisar para reintentar.
            if (ReferenceEquals(_capture, capture))
            {
                Log.Write("micrófono: se detuvo solo · " + (e.Exception?.Message ?? "sin error"));
                Failed?.Invoke(e.Exception?.Message ?? "el micrófono se detuvo");
            }
        };
        _capture = capture;
        _stopped = stopped;
        DeviceName = name;
        Task.Run(capture.StartRecording).GetAwaiter().GetResult();
        Log.Write($"micrófono: abierto «{name}» · {converter.Describe()}");
    }

    static (WasapiCapture, string) Open(string id)
    {
        var en = new MMDeviceEnumerator();
        MMDevice? device = null;
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                device = en.GetDevice(id);
                if (device.State != DeviceState.Active) device = null;
            }
            catch { device = null; }
            if (device is null) Log.Write("micrófono: el elegido no está conectado, uso el predeterminado");
        }
        device ??= en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        return (new WasapiCapture(device, true, 50), device.FriendlyName);
    }

    /// <summary>Para y espera a que el hilo de audio termine: después se puede liberar el Listener sin riesgo.</summary>
    public void Stop()
    {
        var c = _capture;
        var stopped = _stopped;
        _capture = null;
        _stopped = null;
        if (c is null) return;
        try { c.StopRecording(); } catch { }
        stopped?.Wait(1500);
        c.Dispose();
        stopped?.Dispose();
        _listener?.Reset();
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Lee las respuestas en voz alta: con una voz de ElevenLabs (expresiva, con [etiquetas]) o con la de Windows,
/// que también es el respaldo si ElevenLabs falla. Silencia el micrófono mientras habla.
/// </summary>
sealed class Speaker : IDisposable
{
    readonly Settings _s;
    readonly SpeechSynthesizer _tts = new();
    readonly ElevenLabsTts _eleven = new();
    readonly object _gate = new();
    CancellationTokenSource? _cts;
    WasapiOut? _out;
    int _turn;
    public Listener? Listener { get; set; }

    public Speaker(Settings s)
    {
        _s = s;
        _tts.SetOutputToDefaultAudioDevice();
        // Al cancelar una frase para decir otra también llega SpeakCompleted: solo se reabre el
        // micrófono cuando de verdad terminó de hablar, para que no se escuche a sí misma.
        _tts.SpeakCompleted += (_, _) =>
        {
            if (Listener is not null && _tts.State == SynthesizerState.Ready) Listener.Muted = false;
        };
    }

    bool UseEleven => _s.ReplyVoice == ReplyVoice.ElevenLabs
        && !string.IsNullOrWhiteSpace(_s.ElevenLabsKey) && !string.IsNullOrWhiteSpace(_s.ElevenVoiceId);

    public void Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Stop();
        if (Listener is not null) Listener.Muted = true;
        if (!UseEleven) { _tts.SpeakAsync(Expressive.Clean(text)); return; }
        var cts = new CancellationTokenSource();
        int turn;
        lock (_gate) { _cts = cts; turn = ++_turn; }
        _ = ElevenAsync(text, turn, cts.Token);
    }

    async Task ElevenAsync(string text, int turn, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(_s.ElevenTtsModel) ? ElevenLabsTts.Models[0] : _s.ElevenTtsModel;
        var sw = Stopwatch.StartNew();
        var started = false;
        try
        {
            // Suena con el primer trozo: no se espera a que ElevenLabs genere la frase entera.
            using var resp = await _eleven.SpeechStreamAsync(_s.ElevenLabsKey, _s.ElevenVoiceId, model, text, ct);
            await using var audio = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new BufferedWaveProvider(new WaveFormat(ElevenLabsTts.Rate, 16, 1))
            {
                BufferDuration = TimeSpan.FromMinutes(2),
                ReadFully = true,
            };
            var chunk = new byte[8192];
            var odd = -1;
            long total = 0;
            int n;
            while ((n = await audio.ReadAsync(chunk, ct)) > 0)
            {
                // Muestras de 16 bits: un byte suelto espera al siguiente trozo.
                var start = 0;
                if (odd >= 0) { buffer.AddSamples([(byte)odd, chunk[0]], 0, 2); start = 1; odd = -1; }
                var even = (n - start) & ~1;
                buffer.AddSamples(chunk, start, even);
                if (start + even < n) odd = chunk[n - 1];
                total += n;
                if (!started && buffer.BufferedDuration >= TimeSpan.FromMilliseconds(180))
                {
                    started = true;
                    Log.Write($"voz: ElevenLabs {model} empieza a sonar a los {sw.ElapsedMilliseconds} ms");
                    if (!Play(buffer, turn)) return;
                }
            }
            if (!started && total > 0 && !Play(buffer, turn)) return;
            Log.Write($"voz: ElevenLabs «{text}» → {total / 2.0 / ElevenLabsTts.Rate:0.0} s de audio en {sw.ElapsedMilliseconds} ms");
            // ReadFully da silencio cuando se vacía: se para a mano al terminar lo recibido.
            while (buffer.BufferedBytes > 0) await Task.Delay(40, ct);
            await Task.Delay(120, ct);
            StopIf(turn);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("voz: ElevenLabs falló, uso la voz de Windows · " + ex.Message);
            if (!ct.IsCancellationRequested && !started) _tts.SpeakAsync(Expressive.Clean(text));
        }
    }

    /// <summary>Empieza a sonar lo que vaya llegando a <paramref name="buffer"/>; false si ya se pidió otra cosa.</summary>
    bool Play(IWaveProvider buffer, int turn)
    {
        using var devices = new MMDeviceEnumerator();
        var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var mix = device.AudioClient.MixFormat;
        ISampleProvider samples = buffer.ToSampleProvider();
        if (mix.SampleRate != ElevenLabsTts.Rate) samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);
        // Al formato del altavoz (float, sus canales): WASAPI compartido no convierte solo.
        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        output.Init(new SampleToWaveProvider(new Fan(samples, mix.Channels)));
        output.PlaybackStopped += async (_, _) =>
        {
            output.Dispose();
            // Un poco de margen para que la cola del audio no entre por el micrófono.
            await Task.Delay(250);
            lock (_gate) if (turn != _turn) return;
            if (Listener is not null) Listener.Muted = false;
        };
        lock (_gate)
        {
            if (turn != _turn) { output.Dispose(); return false; }
            _out = output;
        }
        output.Play();
        return true;
    }

    void StopIf(int turn)
    {
        WasapiOut? playing;
        lock (_gate)
        {
            if (turn != _turn) return;
            playing = _out;
            _out = null;
        }
        try { playing?.Stop(); } catch { }
    }

    public void Stop()
    {
        WasapiOut? playing;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            playing = _out;
            _out = null;
            _turn++;
        }
        try { playing?.Stop(); } catch { }
        _tts.SpeakAsyncCancelAll();
    }

    public void Dispose()
    {
        Stop();
        _tts.Dispose();
    }
}

/// <summary>Mono repetido en cada canal del altavoz.</summary>
sealed class Fan(ISampleProvider mono, int channels) : ISampleProvider
{
    float[] _buf = [];
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(mono.WaveFormat.SampleRate, channels);

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / channels;
        if (_buf.Length < frames) _buf = new float[frames];
        var n = mono.Read(_buf, 0, frames);
        for (var i = 0; i < n; i++)
            for (var c = 0; c < channels; c++)
                buffer[offset + i * channels + c] = _buf[i];
        return n * channels;
    }
}

/// <summary>Voz de GPT-Live: va sonando lo que llega (PCM16 mono 24 kHz) y se corta al momento si le hablas.</summary>
sealed class LivePlayer : IDisposable
{
    readonly BufferedWaveProvider _buffer = new(new WaveFormat(LiveCall.Rate, 16, 1))
    {
        BufferDuration = TimeSpan.FromMinutes(3),
        DiscardOnBufferOverflow = true,
        ReadFully = true,
    };
    readonly WasapiOut _out;

    public LivePlayer()
    {
        using var devices = new MMDeviceEnumerator();
        var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var mix = device.AudioClient.MixFormat;
        ISampleProvider samples = _buffer.ToSampleProvider();
        if (mix.SampleRate != LiveCall.Rate) samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);
        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        _out.Init(new SampleToWaveProvider(new Fan(samples, mix.Channels)));
        _out.Play();
    }

    public void Add(byte[] pcm)
    {
        _buffer.AddSamples(pcm, 0, pcm.Length & ~1);
        LastAudio = DateTime.Now;
    }

    /// <summary>Lo que falta por sonar.</summary>
    public bool Playing => _buffer.BufferedBytes > 0;
    public DateTime LastAudio { get; private set; } = DateTime.Now;

    public void Flush() => _buffer.ClearBuffer();

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}
