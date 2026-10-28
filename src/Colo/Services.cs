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
        s.ApiKey = "";
        // Si la key no se puede descifrar (otro usuario de Windows), se pierde solo la key, no los ajustes.
        try
        {
            if (!string.IsNullOrEmpty(saved?.Key))
                s.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved.Key), null, DataProtectionScope.CurrentUser));
        }
        catch { }
        return s;
    }

    /// <summary>Guarda una copia: el objeto en uso (que el agente está leyendo) no se toca.</summary>
    public static void Save(Settings s)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
            var key = copy.ApiKey;
            copy.ApiKey = "";
            var saved = new Saved
            {
                Settings = copy,
                Key = string.IsNullOrEmpty(key) ? null
                    : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser)),
            };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(saved, Pretty));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}

/// <summary>Micrófono 16 kHz mono que alimenta al Listener.</summary>
sealed class Mic : IDisposable
{
    WaveInEvent? _wave;
    Listener? _listener;
    ManualResetEventSlim? _stopped;

    /// <summary>El micrófono se cortó solo (desconectado, otro programa lo tomó…).</summary>
    public event Action<string>? Failed;

    public static List<string> Devices()
    {
        var list = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++) list.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return list;
    }

    public bool Running => _wave is not null;

    /// <param name="device">-1 = predeterminado de Windows.</param>
    public void Start(int device, Listener listener)
    {
        Stop();
        _listener = listener;
        var stopped = new ManualResetEventSlim(false);
        var wave = new WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(Listener.Rate, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 3,
        };
        wave.DataAvailable += (_, e) =>
        {
            var pcm = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
            listener.Feed(pcm);
        };
        wave.RecordingStopped += (_, e) =>
        {
            stopped.Set();
            // Parada que no pedimos: avisar para reintentar.
            if (ReferenceEquals(_wave, wave)) Failed?.Invoke(e.Exception?.Message ?? "el micrófono se detuvo");
        };
        _wave = wave;
        _stopped = stopped;
        wave.StartRecording();
    }

    /// <summary>Para y espera a que el hilo de audio termine: después se puede liberar el Listener sin riesgo.</summary>
    public void Stop()
    {
        var w = _wave;
        var stopped = _stopped;
        _wave = null;
        _stopped = null;
        if (w is null) return;
        try { w.StopRecording(); } catch { }
        stopped?.Wait(1500);
        w.Dispose();
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
