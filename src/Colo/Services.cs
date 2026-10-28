using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
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

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Settings();
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) ?? new Saved();
            var s = saved.Settings;
            s.ApiKey = "";
            if (!string.IsNullOrEmpty(saved.Key))
                s.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved.Key), null, DataProtectionScope.CurrentUser));
            return s;
        }
        catch { return new Settings(); }
    }

    public static void Save(Settings s)
    {
        Directory.CreateDirectory(Dir);
        var key = s.ApiKey;
        try
        {
            s.ApiKey = "";
            var saved = new Saved
            {
                Settings = s,
                Key = string.IsNullOrEmpty(key) ? null
                    : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser)),
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { s.ApiKey = key; }
    }
}

/// <summary>Micrófono 16 kHz mono que alimenta al Listener.</summary>
sealed class Mic : IDisposable
{
    WaveInEvent? _wave;
    Listener? _listener;

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
        _wave = new WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(Listener.Rate, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 3,
        };
        _wave.DataAvailable += (_, e) =>
        {
            var pcm = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
            _listener?.Feed(pcm);
        };
        _wave.StartRecording();
    }

    public void Stop()
    {
        var w = _wave;
        _wave = null;
        if (w is null) return;
        try { w.StopRecording(); } catch { }
        w.Dispose();
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
        _tts.SpeakCompleted += (_, _) => { if (Listener is not null) Listener.Muted = false; };
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
