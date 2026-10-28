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
        s.Endpoint ??= "";
        s.Model ??= "";
        s.TranscribeModel ??= "";
        s.ReasoningEffort ??= "low";
        s.ElevenLabsModel ??= "scribe_v2";
        // Si una key no se puede descifrar (otro usuario de Windows), se pierde solo esa key, no los ajustes.
        s.ApiKey = Unprotect(saved?.Key);
        s.ElevenLabsKey = Unprotect(saved?.ElevenKey);
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
            var saved = new Saved { Settings = copy, Key = Protect(copy.ApiKey), ElevenKey = Protect(copy.ElevenLabsKey) };
            copy.ApiKey = "";
            copy.ElevenLabsKey = "";
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
        capture.DataAvailable += (_, e) =>
        {
            if (first)
            {
                first = false;
                Log.Write($"micrófono: llegan datos ({e.BytesRecorded} bytes por bloque)");
            }
            // Un error procesando el audio no puede matar la grabación en silencio.
            try { converter.Push(e.Buffer, e.BytesRecorded, pcm => listener.Feed(pcm)); }
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

/// <summary>Voz de Windows para leer las respuestas (opcional). Silencia el micrófono mientras habla.</summary>
sealed class Speaker : IDisposable
{
    readonly SpeechSynthesizer _tts = new();
    public Listener? Listener { get; set; }

    public Speaker()
    {
        _tts.SetOutputToDefaultAudioDevice();
        // Al cancelar una frase para decir otra también llega SpeakCompleted: solo se reabre el
        // micrófono cuando de verdad terminó de hablar, para que no se escuche a sí misma.
        _tts.SpeakCompleted += (_, _) =>
        {
            if (Listener is not null && _tts.State == SynthesizerState.Ready) Listener.Muted = false;
        };
    }

    public void Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _tts.SpeakAsyncCancelAll();
        if (Listener is not null) Listener.Muted = true;
        _tts.SpeakAsync(text);
    }

    public void Dispose() => _tts.Dispose();
}
