using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;
using Colo.Core;
using Microsoft.Win32;

namespace Colo;

/// <summary>
/// Colo en pantalla: una pastilla blanca con una onda, siempre encima. Aparece mientras mantienes el atajo
/// (sigue tu voz), brilla cuando está trabajando y se va cuando no hay nada que contar.
/// Clic = pausar/seguir · arrastrar = mover · clic derecho = menú.
/// </summary>
sealed class Widget : LayeredWindow
{

    // ── lógica
    readonly Settings _s = Store.Load();
    readonly AiClient _ai;
    readonly ElevenLabsStt _eleven;
    readonly Agent _agent;
    readonly WindowsComputer _pc = new();
    readonly AiCursor _cursor = AiCursor.Create();
    readonly Mic _mic = new();
    readonly SpeechModels _models = new(Store.ModelsDir, System.IO.Path.Combine(AppContext.BaseDirectory, "models"));
    readonly Channel<(float[] Samples, bool Dictate)> _phrases = Channel.CreateUnbounded<(float[], bool)>(new UnboundedChannelOptions { SingleReader = true });
    Speaker? _speaker;
    Listener? _listener;
    LocalStt? _stt;
    McpHub? _mcp;
    bool _listening, _settingsOpen;
    SettingsWindow? _settingsWindow;
    // diagnóstico del audio
    DateTime _lastLevelPost;
    double _peak;
    List<short>? _probe;
    readonly object _probeLock = new();
    DateTime _peakSince = DateTime.Now;

    // ── dibujo
    readonly WavePainter _painter = new();
    // 15 y no 16: el reloj de Windows salta de 15,6 en 15,6 ms y con 16 cada cuadro esperaba dos saltos (32 fps).
    const int FrameMs = 15;
    readonly System.Windows.Forms.Timer _frame = new() { Interval = FrameMs };
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    double _lastFrame;
    // Rebotes: cuándo entró la frase, y el golpe de «gota» al cambiar de estado (fuerza y signo).
    double _captionAt = -1e9, _kickAt = -1e9, _kickPower;
    Mode _lastMode = Mode.Starting;
    bool _lastHearing;
    Mode _mode = Mode.Starting;
    bool _hearing;
    double _t, _level, _target, _energy;
    string _caption = "";
    Color _captionColor = Theme.Ink;
    DateTime _captionUntil;
    Point _pos;
    readonly NotifyIcon _tray = new();
    readonly PopMenu _menu = new();

    // ── atajo: mientras está apretado guarda lo que dices; al soltar, transcribe y actúa (o dicta).
    const string Talk = "talk", Dictate = "dictate";
    KeyHook? _keys;
    volatile string? _holding;
    double _presence = 1;
    // ── GPT-Live: la llamada sigue abierta hasta que la tarea termina y se calla.
    LiveCall? _live;
    LivePlayer? _livePlayer;
    string? _liveTask;
    string _liveSaid = "";
    DateTime _liveQuietSince;
    static readonly TimeSpan LiveHangup = TimeSpan.FromSeconds(8);
    readonly TokenMeter _tokens = new(System.IO.Path.Combine(Store.Dir, "usage.json"));
    readonly Memory _memory = new(System.IO.Path.Combine(Store.Dir, "memory.json"));
    long _orderTokens;

    // ── arrastre
    bool _pressed, _moved;
    Point _pressAt, _pressPos;

    internal Agent Brain => _agent;

    public Widget()
    {
        _ai = new AiClient(_s);
        _eleven = new ElevenLabsStt(_s);
        _agent = new Agent(_ai, _s, _pc) { Memory = _memory };
        _pc.Cursor = _cursor;
        Text = "Colo";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _frame.Tick += (_, _) => Frame();
        _mic.Failed += reason => Post(() => OnMicFailed(reason));
        _agent.Event += ev => Post(() => OnAgent(ev));
        BuildMenu();
        _ = Task.Run(PhraseLoopAsync);
    }

    protected override bool ClickThrough => false;

    float S => DeviceDpi / 96f;

    // ───────────────────────── arranque

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var w = (int)(WavePainter.CanvasW * S);
        var h = (int)(WavePainter.CanvasH * S);
        _pos = _s.WidgetX is int x && _s.WidgetY is int y && OnScreen(new Rectangle(x, y, w, h))
            ? new Point(x, y)
            : new Point(wa.X + (wa.Width - w) / 2, wa.Bottom - h + (int)(4 * S));
        Bounds = new Rectangle(_pos, new Size(w, h));
        _frame.Start();
        RegisterHotkeys();
        SetupKeys();
        ApplyAutostart();
        _ = StartAsync();
    }

    static bool OnScreen(Rectangle r)
    {
        foreach (var sc in Screen.AllScreens)
            if (sc.WorkingArea.IntersectsWith(r)) return true;
        return false;
    }

    async Task StartAsync()
    {
        if (!Configured())
        {
            Problem(Missing() + " · toca para abrir");
            OpenSettings();
            return;
        }
        _ = StartMcpAsync();
        await StartListeningAsync();
    }

    int _mcpGeneration;

    /// <summary>Arranca (o reinicia) los servidores MCP y le pasa sus tools al agente.</summary>
    async Task StartMcpAsync()
    {
        var generation = ++_mcpGeneration;
        var old = _mcp;
        _agent.Extra = null;
        _mcp = null;
        if (old is not null) await old.DisposeAsync();
        if (!_s.McpServers.Any(m => m.Enabled)) return;
        var hub = await McpHub.StartAsync(_s.McpServers.ToList());
        // Si mientras arrancaba se guardaron otros ajustes, este ya no vale.
        if (generation != _mcpGeneration || IsDisposed)
        {
            await hub.DisposeAsync();
            return;
        }
        _mcp = hub;
        _agent.Extra = hub;
        if (hub.Problems.Count > 0) Say("MCP · " + hub.Problems[0], Theme.Danger, 7000);
    }

    /// <summary>BeginInvoke que no revienta si la ventana todavía no existe o ya se cerró.</summary>
    void Post(Action a)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    void OnMicFailed(string reason)
    {
        // Bluetooth que se desconecta, otro programa que toma el micrófono…: la próxima vez que aprietes se abre de nuevo.
        _hearing = false;
        Say("Micrófono: " + reason, Theme.Danger, 5000);
    }

    // ── el micrófono solo está abierto mientras se usa (atajo apretado o la prueba de Ajustes)
    readonly object _micGate = new();
    Task _micChain = Task.CompletedTask;
    int _micUsers;

    void MicAcquire()
    {
        var l = _listener;
        if (l is null) return;
        lock (_micGate)
        {
            if (_micUsers++ > 0) return;
            MicChain(() =>
            {
                _mic.Start(_s.MicId, l);
                Post(() => VoiceStatus($"Micrófono «{_mic.DeviceName}» abierto"));
            });
        }
    }

    void MicRelease(bool all = false)
    {
        lock (_micGate)
        {
            if (_micUsers == 0) return;
            _micUsers = all ? 0 : _micUsers - 1;
            if (_micUsers > 0) return;
            MicChain(() =>
            {
                _mic.Stop();
                Post(() => VoiceStatus($"Listo · mantén {_s.TalkHotkey} y habla para probar"));
            });
        }
    }

    // Abrir y cerrar en orden y fuera del hilo de la interfaz (ahí corre el gancho del teclado: no puede esperar).
    void MicChain(Action a) => _micChain = _micChain.ContinueWith(_ =>
    {
        try { a(); }
        catch (Exception ex)
        {
            Log.Write("micrófono: " + ex);
            Post(() =>
            {
                Say("Micrófono: " + ex.Message, Theme.Danger, 6000);
                VoiceStatus("No pude abrir el micrófono: " + ex.Message);
            });
        }
    }, TaskScheduler.Default);

    bool Configured() => Missing() is null;

    /// <summary>Qué falta para funcionar, dicho con el nombre que tiene en Ajustes; null = nada.</summary>
    string? Missing()
    {
        if (string.IsNullOrWhiteSpace(_s.ApiKey)) return "Falta la API key en Ajustes › Modelo";
        if (string.IsNullOrWhiteSpace(_s.Model)) return "Falta elegir el modelo en Ajustes › Modelo";
        if (_s.Provider == Provider.Azure && string.IsNullOrWhiteSpace(_s.Endpoint)) return "Falta el endpoint en Ajustes › Modelo";
        // En vivo, el motor de transcripción solo hace falta para el atajo de dictar.
        if (!_s.Live || _s.DictateHotkey.Length > 0)
        {
            if (_s.SpeechEngine == SpeechEngine.Api && string.IsNullOrWhiteSpace(_s.TranscribeModel)) return "Falta el modelo de transcripción en Ajustes › Voz";
            if (_s.SpeechEngine == SpeechEngine.ElevenLabs && string.IsNullOrWhiteSpace(_s.ElevenLabsKey)) return "Falta la API key de ElevenLabs en Ajustes › Voz";
        }
        if (_s.Live && string.IsNullOrWhiteSpace(_s.LiveModel)) return "Falta el modelo de GPT-Live en Ajustes › Voz";
        if (_s.VoiceOwn && (_s.Live || _s.SpeechEngine == SpeechEngine.Api) && string.IsNullOrWhiteSpace(_s.VoiceKey))
            return "Falta la API key de la conexión de la voz en Ajustes › Voz";
        return null;
    }

    void ApplyAutostart()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run is null) return;
            if (_s.StartWithWindows) run.SetValue("Colo", $"\"{Application.ExecutablePath}\"");
            else if (run.GetValue("Colo") is not null) run.DeleteValue("Colo");
        }
        catch { }
    }

    // ───────────────────────── voz

    Task<bool>? _speechReady;

    /// <summary>Una sola preparación a la vez (la descarga de 670 MB no puede correr dos veces).</summary>
    Task<bool> EnsureSpeechAsync()
    {
        if (_speechReady is { IsCompleted: false }) return _speechReady;
        return _speechReady = PrepareSpeechAsync();
    }

    async Task<bool> PrepareSpeechAsync()
    {
        var model = SpeechModels.Find(_s.LocalModel);
        var needAsr = _s.SpeechEngine == SpeechEngine.Local;
        if (needAsr && !_models.Ready(model))
        {
            _mode = Mode.Starting;
            Log.Write($"voz: descargando {model.File} a {_models.Dir}");
            var lastLogged = -1;
            var progress = new Progress<double>(p =>
            {
                Say($"Preparando la voz · {p:P0}", Theme.Muted, 3600);
                VoiceStatus($"Preparando la voz local · {p:P0}");
                var tenth = (int)(p * 10);
                if (tenth != lastLogged) { lastLogged = tenth; Log.Write($"voz: descarga {p:P0}"); }
            });
            try { await _models.DownloadAsync(model, progress, CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("voz: descarga falló · " + ex);
                Problem("No pude bajar la voz: " + ex.Message);
                VoiceStatus("No pude bajar la voz: " + ex.Message);
                return false;
            }
            Log.Write("voz: descarga completa");
        }
        try
        {
            if (needAsr && _stt is null)
            {
                _mode = Mode.Starting;
                VoiceStatus("Cargando la voz local…");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var path = _models.PathOf(model);
                _stt = await Task.Run(() => new LocalStt(path) { Language = _s.Language });
                // Prueba real: un segundo de silencio tiene que pasar por Whisper sin error.
                var probe = await Task.Run(() => _stt.Transcribe(new float[Listener.Rate]));
                Log.Write($"voz: Whisper {model.Id} listo en {sw.ElapsedMilliseconds} ms · idioma {_s.Language} · {new System.IO.FileInfo(path).Length:N0} bytes · prueba «{probe}»");
            }
            if (_listener is null)
            {
                // Sin detector por volumen: las frases salen solo del atajo.
                _listener = new Listener(_s.Sensitivity) { Detect = false };
                Log.Write($"voz: detector por volumen listo (sensibilidad {_s.Sensitivity})");
            }
            Say("", Theme.Muted, 0);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("voz: no arrancó · " + ex);
            Problem("Voz local: " + ex.Message);
            VoiceStatus("La voz local no arrancó: " + ex.Message);
            return false;
        }
    }

    /// <param name="forTest">Desde Ajustes: la prueba del micrófono no necesita la key del modelo.</param>
    async Task StartListeningAsync(bool forTest = false)
    {
        if (_listening) return;
        if (!forTest && !Configured()) { OpenSettings(); return; }
        if (!await EnsureSpeechAsync() || _listening || (_settingsOpen && !forTest)) return;
        var l = _listener!;
        l.Level -= OnLevel;
        l.Level += OnLevel;
        l.Raw -= OnRaw;
        l.Raw += OnRaw;
        _speaker ??= TryCreateSpeaker();
        if (_speaker is not null) _speaker.Listener = l;
        // Listo para el atajo; el micrófono se abre al apretarlo.
        _listening = true;
        VoiceStatus($"Listo · mantén {_s.TalkHotkey} y habla para probar");
        if (!forTest) Say($"Mantén {_s.TalkHotkey} para hablarme", Theme.Muted, 3500);
        _mode = _agent.Busy ? Mode.Busy : Mode.Listening;
    }

    Speaker? TryCreateSpeaker()
    {
        try { return new Speaker(_s); } catch { return null; }
    }

    void StopListening()
    {
        _holding = null;
        _listener?.EndTake();
        MicRelease(all: true);
        _listening = false;
        _hearing = false;
        _target = 0;
        if (!_agent.Busy) _mode = Mode.Paused;
    }

    void ToggleListening()
    {
        if (_mode == Mode.Problem && !Configured()) { OpenSettings(); return; }
        if (_listening) StopListening();
        else _ = StartListeningAsync();
    }

    // Hilo de audio: cada ~40 ms.
    void OnLevel(double rms)
    {
        _target = Math.Clamp(Math.Sqrt(rms) * 3.4, 0, 1);
        var now = DateTime.Now;
        if (rms > _peak) _peak = rms;
        if ((now - _peakSince).TotalSeconds >= 15)
        {
            Log.Write($"audio: pico {_peak:0.0000} en 15 s");
            _peak = 0;
            _peakSince = now;
        }
        if (_settingsWindow is not null && (now - _lastLevelPost).TotalMilliseconds > 60)
        {
            _lastLevelPost = now;
            Post(() => _settingsWindow?.VoiceLevel(rms));
        }
    }

    // ───────────────────────── atajo

    void SetupKeys()
    {
        _keys ??= new KeyHook();
        var k = _keys.Keys;
        k.Pressed -= OnPressed;
        k.Pressed += OnPressed;
        k.Released -= OnReleased;
        k.Released += OnReleased;
        k.Cancelled -= OnCancelled;
        k.Cancelled += OnCancelled;
        if (!k.Set(Talk, _s.TalkHotkey)) Log.Write($"atajo: «{_s.TalkHotkey}» no vale");
        if (!k.Set(Dictate, _s.DictateHotkey)) Log.Write($"atajo: «{_s.DictateHotkey}» no vale");
    }

    // Llegan desde el gancho de teclado: se contesta al momento y lo demás va por la cola de la interfaz.
    void OnPressed(string id) => Post(() => Press(id));
    void OnReleased(string id) => Post(() => Release(id));
    void OnCancelled(string id) => Post(() => Cancel(id));

    void Press(string id)
    {
        if (!_listening)
        {
            if (!Configured()) { OpenSettings(); return; }
            Say("El micrófono está en pausa · clic en la onda para seguir", Theme.Muted, 2500);
            return;
        }
        _speaker?.Stop();
        if (_listener is null) return;
        _listener.Muted = false;
        _holding = id;
        MicAcquire();
        _hearing = true;
        _settingsWindow?.VoiceVad(true);
        Kick(-0.9);     // aparece estirándose hacia arriba, como una gota que cae y rebota
        if (id == Talk && _s.Live && _settingsWindow is null)
        {
            _livePlayer?.Flush();
            _liveSaid = "";
            StartLive();
            return;
        }
        _listener.BeginTake();
        Log.Write($"atajo: {(id == Talk ? "hablar" : "dictar")} apretado");
    }

    void Release(string id)
    {
        if (_holding != id) return;
        _holding = null;
        _hearing = false;
        _settingsWindow?.VoiceVad(false);
        if (id == Talk && _live is not null && _listener?.Taking != true)
        {
            _live.Mute();
            _liveQuietSince = DateTime.Now;
            MicRelease();
            return;
        }
        // Un poco de cola: al soltar rápido no se pierde la última sílaba.
        _ = Task.Delay(150).ContinueWith(_ => Post(() => FinishTake(id == Dictate)));
    }

    void Cancel(string id)
    {
        if (_holding != id) return;
        _holding = null;
        _hearing = false;
        _settingsWindow?.VoiceVad(false);
        MicRelease();
        if (_live is not null && _listener?.Taking != true) { _live.Mute(); return; }
        _listener?.EndTake();
        Log.Write("atajo: cancelado (era otro atajo)");
    }

    void FinishTake(bool dictate)
    {
        MicRelease();
        // Ya volvió a apretar: lo de antes se junta con lo nuevo.
        if (_holding is not null || _listener is null) return;
        var samples = _listener.EndTake();
        var seconds = samples.Length / (double)Listener.Rate;
        var peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        Log.Write($"atajo: soltado · {seconds:0.0} s · pico {peak:0.000}");
        if (seconds < 0.5) { Say("Mantén las teclas mientras hablas", Theme.Muted, 2200); return; }
        if (peak < 0.003) { Say($"No me llega sonido de «{_mic.DeviceName}». Revisa el micrófono en Ajustes › Voz.", Theme.Danger, 6000); return; }
        _phrases.Writer.TryWrite((samples, dictate));
    }

    // ───────────────────────── GPT-Live

    void StartLive()
    {
        if (_live is not null)
        {
            _live.Unmute();
            return;
        }
        try { _livePlayer ??= new LivePlayer(); }
        catch (Exception ex) { Log.Write("live: sin altavoz · " + ex.Message); }
        var call = new LiveCall();
        _live = call;
        _liveTask = null;
        _liveQuietSince = DateTime.Now;
        call.Trace += t => Log.Write("live: " + t);
        call.Audio += pcm => _livePlayer?.Add(pcm);
        call.Said += d => Post(() =>
        {
            if (_live != call) return;
            _liveSaid += d;
            var t = _liveSaid.Trim();
            Say(t.Length > 140 ? "…" + t[^140..] : t, Theme.Ink, 4000);
        });
        call.Delegated += (id, request) => Post(() => OnDelegated(call, id, request));
        call.Closed += reason => Post(() => { if (_live == call) EndLive("se cerró: " + reason); });
        Say("Conectando…", Theme.Muted, 3000);
        _ = Task.Run(async () =>
        {
            try
            {
                // Lo que dices mientras conecta queda en cola y sale detrás de session.start.
                await call.StartAsync(_s, CancellationToken.None);
                Post(() =>
                {
                    Log.Write($"live: llamada abierta {call.SessionId} · {_s.LiveModel} · voz {_s.LiveVoice}");
                    if (_live == call && _holding != Talk) call.Mute();
                    if (_caption == "Conectando…") Say("", Theme.Muted, 0);
                });
            }
            catch (Exception ex)
            {
                Log.Write("live: no abrió · " + ex.Message);
                Post(() =>
                {
                    Say(ex.Message, Theme.Danger, 6000);
                    if (_live == call) EndLive("no abrió");
                });
            }
        });
    }

    void OnDelegated(LiveCall call, string id, string request)
    {
        if (_live != call) return;
        Log.Write($"live: tarea {id} «{request}»");
        if (request.Count(char.IsLetter) < 2)
        {
            call.Commentary(id, "No te entendí bien; pídeselo otra vez.");
            return;
        }
        _liveTask = id;
        Command(request);
    }

    /// <summary>Cuelga cuando la tarea acabó, ya no habla y no le hablas desde hace unos segundos.</summary>
    void MaybeEndLive()
    {
        if (_live is null) return;
        var now = DateTime.Now;
        if (_holding is not null || _agent.Busy || _liveTask is not null || _livePlayer?.Playing == true) { _liveQuietSince = now; return; }
        var last = _livePlayer is { } p && p.LastAudio > _liveQuietSince ? p.LastAudio : _liveQuietSince;
        if (now - last > LiveHangup) EndLive("tarea terminada");
    }

    void EndLive(string why)
    {
        var call = _live;
        if (call is null) return;
        _live = null;
        _liveTask = null;
        Log.Write("live: cuelgo · " + why);
        _ = Task.Run(async () => { try { await call.DisposeAsync(); } catch { } });
    }

    // Hilo de audio: junta 5 s para la prueba «Grabar y analizar».
    void OnRaw(ReadOnlySpan<short> pcm)
    {
        // En llamada, lo que dices con el atajo apretado va directo a GPT-Live.
        if (_holding == Talk && _live is { } live && _listener?.Taking != true) live.Append(pcm);
        short[]? done = null;
        lock (_probeLock)
        {
            if (_probe is null) return;
            foreach (var v in pcm) _probe.Add(v);
            if (_probe.Count >= Listener.Rate * 5)
            {
                done = _probe.ToArray();
                _probe = null;
            }
        }
        if (done is not null)
        {
            Post(() => MicRelease());
            _ = Task.Run(() => AnalyzeProbe(done));
        }
    }

    void StartProbe()
    {
        if (!_listening) { _settingsWindow?.ProbeResult(new { error = "La voz todavía no está lista." }); return; }
        MicAcquire();
        lock (_probeLock) _probe = new List<short>(Listener.Rate * 5);
        Log.Write("prueba: grabando 5 s");
    }

    void AnalyzeProbe(short[] pcm)
    {
        try
        {
            var wav = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Log.FilePath)!, "prueba.wav");
            System.IO.File.WriteAllBytes(wav, Wav.Encode(pcm.Select(v => v / 32768f).ToArray()));
            var r = SpeechProbe.Analyze(pcm, _s.Sensitivity, _stt);
            Log.Write($"prueba: {r.Seconds:0.0} s · pico {r.Peak:0.000} · rms {r.Rms:0.0000} · dc {r.Dc:0.0000} · frases {r.Phrases} · Whisper «{r.Text}» · {wav}");
            Post(() => _settingsWindow?.ProbeResult(new
            {
                text = r.Text,
                peak = r.Peak,
                rms = r.Rms,
                dc = r.Dc,
                phrases = r.Phrases,
                file = wav,
            }));
        }
        catch (Exception ex)
        {
            Log.Write("prueba: falló · " + ex);
            Post(() => _settingsWindow?.ProbeResult(new { error = ex.Message }));
        }
    }

    void VoiceStatus(string text) => _settingsWindow?.VoiceStatus(text);

    async Task PhraseLoopAsync()
    {
        await foreach (var (samples, dictate) in _phrases.Reader.ReadAllAsync())
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string text;
                if (_s.SpeechEngine == SpeechEngine.Local)
                {
                    if (_stt is null) { Log.Write("voz: frase descartada, Whisper no está cargado"); continue; }
                    _stt.Prompt = Glossary();
                    text = _stt.Transcribe(samples);
                }
                else if (_s.SpeechEngine == SpeechEngine.ElevenLabs)
                {
                    text = await _eleven.TranscribeAsync(Wav.Encode(samples), CancellationToken.None);
                }
                else
                {
                    text = await _ai.TranscribeAsync(Wav.Encode(samples), Glossary(), CancellationToken.None);
                }
                var ms = sw.ElapsedMilliseconds;
                Log.Write($"voz: {samples.Length / 16000.0:0.0} s → «{text}» ({ms} ms)");
                Post(() =>
                {
                    // Con Ajustes abierto es la prueba del micrófono: se muestra, no se ejecuta.
                    if (_settingsWindow is not null) { _settingsWindow.VoiceHeard(text, ms); return; }
                    // Una letra suelta («¡Y!», «Eh.») es ruido: ni se manda al modelo. «Sí» o «no» sí pasan.
                    if (text.Count(char.IsLetter) < 2) { if (text.Length > 0) Log.Write($"voz: descartado «{text}» (sin palabras)"); return; }
                    if (dictate) { DictateNow(text); return; }
                    Command(text);
                });
            }
            catch (Exception ex)
            {
                Log.Write("voz: transcripción falló · " + ex);
                Post(() => Say(ex.Message, Theme.Danger, 5000));
            }
        }
    }

    /// <summary>Glosario para la transcripción: palabras de las órdenes y los programas abiertos.</summary>
    static string Glossary() => LocalStt.PromptFor(Native.AppWindows().Select(Native.ProcessName));

    /// <summary>Atajo de dictar: el modelo lo deja escrito con tu tono y va donde está el cursor.</summary>
    async void DictateNow(string heard)
    {
        Say("Escribiendo…", Theme.Muted, 8000);
        var window = Native.WindowLabel(Native.GetForegroundWindow());
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Mientras el modelo lo pule, se busca dónde escribir: los dos a la vez, para no sumar tiempo.
            var polish = Task.Run(() => Dictation.PolishAsync(_ai, _s, heard, window, CancellationToken.None));
            var where = Task.Run(() =>
            {
                try { return Desktop.EnsureTextFocus(); }
                catch (Exception ex) { return "no pude mirar el foco · " + ex.Message; }
            });
            var text = await polish;
            var target = await where;
            Log.Write($"dicta: «{heard}» → «{text}» ({sw.ElapsedMilliseconds} ms) en «{window}», escribe en {target}");
            if (text.Length == 0) { Say("", Theme.Muted, 0); return; }
            Say("«" + text + "»", Theme.Muted, 1500 + text.Length * 30);
            Input.Type(text);
        }
        catch (Exception ex)
        {
            Log.Write("dicta: falló · " + ex.Message);
            Say("No pude escribir el dictado: " + ex.Message, Theme.Danger, 6000);
        }
    }

    /// <summary>Lo que se ejecuta al oír una frase (también lo usa la prueba de punta a punta).</summary>
    internal void Command(string text)
    {
        if (!Configured()) { OpenSettings(); return; }
        Log.Write($"orden: «{text}»");
        Say("«" + text + "»", Theme.Ink, 1800 + text.Length * 45);
        _mode = Mode.Busy;
        SetClickThroughIfBusy();
        _ = _agent.Hear(text);
    }

    void OnAgent(AgentEvent ev)
    {
        if (ev.Kind != AgentEventKind.Thinking) Log.Write($"agente {ev.Kind}: {ev.Text}");
        if (ev.Kind is AgentEventKind.Done or AgentEventKind.Error or AgentEventKind.Ignored or AgentEventKind.Stopped)
            Log.Write($"tokens: {Interlocked.Exchange(ref _orderTokens, 0):N0} esta orden · hoy {_tokens.TodayTokens:N0} · total {_tokens.AllTokens:N0}");
        switch (ev.Kind)
        {
            case AgentEventKind.Thinking:
            case AgentEventKind.Action:
                _mode = Mode.Busy;
                break;
            case AgentEventKind.Message:
                var said = Expressive.Clean(ev.Text);
                Say(said, Theme.Ink, 2500 + said.Length * 50);
                break;
            case AgentEventKind.Done:
                // Las [etiquetas] de emoción son para la voz; en la onda va el texto limpio.
                var reply = Expressive.Clean(ev.Text);
                Say(reply, Theme.Ink, Math.Min(9000, 2500 + reply.Length * 60));
                // En llamada lo dice GPT-Live con su voz; si no, la voz de las respuestas.
                if (!LiveResult(c => c.Commentary(_liveTask!, reply)) && _s.SpeakReplies) _speaker?.Say(ev.Text);
                Idle();
                break;
            case AgentEventKind.Ignored:
                LiveResult(c => c.Thinking(_liveTask!, "En el PC no había nada que hacer con eso."));
                Idle();
                break;
            case AgentEventKind.Listening:
                // Espera a que termine la idea: la onda vuelve a escuchar.
                _mode = Mode.Listening;
                break;
            case AgentEventKind.Stopped:
                Say("Detenido", Theme.Muted, 1500);
                LiveResult(c => c.Commentary(_liveTask!, "Lo detuve."));
                Idle();
                break;
            case AgentEventKind.Error:
                Say(ev.Text, Theme.Danger, 6000);
                LiveResult(c => c.Commentary(_liveTask!, "No pude: " + ev.Text));
                Idle();
                break;
        }
        SetClickThroughIfBusy();
    }

    /// <summary>Devuelve el resultado a la tarea que delegó GPT-Live (si la hay). false = no había llamada.</summary>
    bool LiveResult(Action<LiveCall> send)
    {
        if (_live is not { } call || _liveTask is null) return false;
        send(call);
        _liveTask = null;
        _liveQuietSince = DateTime.Now;
        return true;
    }

    /// <summary>«Probar voz» en Ajustes: habla con la voz elegida aunque aún no se haya guardado.</summary>
    void TrySpeaking(Settings trial)
    {
        var sample = trial.Language == "pt"
            ? "[cheerfully] Oi! [warmly] É assim que eu vou te responder."
            : "[cheerfully] ¡Hola! [warmly] Así es como te voy a responder.";
        try
        {
            var speaker = new Speaker(trial);
            speaker.Say(sample);
            // Se suelta solo al terminar; un objeto por prueba, para no tocar el que usa la escucha.
            _ = Task.Delay(15_000).ContinueWith(_ => speaker.Dispose());
        }
        catch (Exception ex) { Log.Write("voz: prueba falló · " + ex.Message); }
    }

    void Idle()
    {
        _cursor.Away();
        _mode = _listening ? Mode.Listening : Mode.Paused;
    }

    // Mientras trabaja, los clics atraviesan la onda para no estorbar al ratón de Colo.
    bool _clickThrough;
    void SetClickThroughIfBusy()
    {
        var want = _mode == Mode.Busy;
        if (want == _clickThrough || !IsHandleCreated) return;
        _clickThrough = want;
        Native.SetClickThrough(Handle, want);
    }

    void Problem(string text)
    {
        _mode = Mode.Problem;
        Say(text, Theme.Danger, 600_000);
    }

    void Say(string text, Color color, int ms)
    {
        if (text != _caption && text.Length > 0) _captionAt = _clock.Elapsed.TotalMilliseconds;
        _caption = text;
        _captionColor = color;
        _captionUntil = DateTime.Now.AddMilliseconds(ms);
    }

    // ───────────────────────── ajustes / menú

    void OpenSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        // Con Ajustes abierto nada se mueve solo por la pantalla.
        _agent.Stop();
        bool saved;
        try
        {
            using var f = new SettingsWindow(_s, _memory);
            _settingsWindow = f;
            f.RecordTest += StartProbe;
            f.SayTest += TrySpeaking;
            f.RecordingKeys += on =>
            {
                if (_keys is null) return;
                _keys.Paused = on;
                _keys.Keys.Reset();
            };
            var testing = false;
            bool CanTest() => _s.SpeechEngine == SpeechEngine.Local
                || (_s.SpeechEngine == SpeechEngine.ElevenLabs && _s.ElevenLabsKey.Length > 0) || Configured();
            f.Ready += () =>
            {
                if (_listening) f.VoiceStatus($"Listo · mantén {_s.TalkHotkey} y habla para probar");
                else if (CanTest())
                {
                    // Para la prueba se enciende aunque falte la key del modelo.
                    testing = true;
                    f.VoiceStatus("Encendiendo el micrófono…");
                    _ = StartListeningAsync(forTest: true);
                }
                else f.VoiceStatus("Para probar esta voz, pon primero su llave");
            };
            // Cada cambio se guarda al momento (sin botón); si ahora ya se puede probar la voz, se enciende.
            f.Saved += () =>
            {
                Store.Save(_s);
                if (!_listening && !testing && CanTest())
                {
                    testing = true;
                    f.VoiceStatus("Encendiendo el micrófono…");
                    _ = StartListeningAsync(forTest: true);
                }
            };
            f.ShowDialog();
            saved = f.Changed;
        }
        finally
        {
            if (_keys is not null) _keys.Paused = false;
            _settingsOpen = false;
            _settingsWindow = null;
        }
        if (!saved && !Configured()) StopListening();
        Log.Write(saved ? $"ajustes guardados · {_s.Provider} · modelo {_s.Model} · voz {_s.SpeechEngine} · mic «{(_s.MicId.Length > 0 ? _s.MicId : "predeterminado")}» · ver {_s.Vision}" : "ajustes cerrados sin cambios");
        if (!saved) return;

        Store.Save(_s);
        RegisterHotkeys();
        SetupKeys();
        EndLive("ajustes cambiados");
        _ = StartMcpAsync();
        ApplyAutostart();
        StopListening();
        _listener?.Dispose();
        _listener = null;
        // El modelo o el idioma pueden haber cambiado: se vuelve a cargar (tarda ~0,1 s).
        _stt?.Dispose();
        _stt = null;
        if (Configured())
        {
            Say("", Theme.Muted, 0);
            _ = StartListeningAsync();
        }
        else Problem(Missing()!);
    }

    void BuildMenu()
    {
        _ai.Used += u =>
        {
            _tokens.Add(u);
            Interlocked.Add(ref _orderTokens, u.Total);
        };
        _tray.Text = "Colo";
        _tray.Icon = Icon ?? SystemIcons.Application;
        _tray.Visible = true;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleListening();
            else if (e.Button == MouseButtons.Right) ShowMenu();
        };
    }

    void ShowMenu()
    {
        var (title, dot) = _agent.Busy ? ("Trabajando…", Color.FromArgb(245, 158, 11))
            : _listening ? ($"Listo · mantén {_s.TalkHotkey}", Theme.Blue) : ("En pausa", Color.FromArgb(161, 161, 170));
        var detail = $"Tokens · hoy {TokenMeter.Short(_tokens.TodayTokens)} · total {TokenMeter.Short(_tokens.AllTokens)}";
        _menu.Open(Cursor.Position, title, detail, dot,
        [
            new(_listening ? "Pausar" : "Seguir", _listening ? PopMenu.Icon.Pause : PopMenu.Icon.Play, ToggleListening),
            new("Detener lo que hace", PopMenu.Icon.Stop, () => _agent.Stop()),
            new("Ajustes…", PopMenu.Icon.Settings, OpenSettings, GapBefore: true),
            new("Salir", PopMenu.Icon.Power, Close, Danger: true),
        ]);
    }

    // ───────────────────────── ratón

    RectangleF PillRect() => WavePainter.PillRect(S);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _moved = false;
        _pressAt = Cursor.Position;
        _pressPos = _pos;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;
        var c = Cursor.Position;
        var dx = c.X - _pressAt.X;
        var dy = c.Y - _pressAt.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _moved = true;
        _pos = new Point(_pressPos.X + dx, _pressPos.Y + dy);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            ShowMenu();
            return;
        }
        if (!_pressed) return;
        _pressed = false;
        if (_moved)
        {
            _s.WidgetX = _pos.X;
            _s.WidgetY = _pos.Y;
            try { Store.Save(_s); } catch { }
        }
        else if (PillRect().Contains(e.Location)) ToggleListening();
    }

    // ───────────────────────── onda

    void Frame()
    {
        // Todo va por el tiempo real, no por cuadros: igual de suave aunque un tic llegue tarde.
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        _t += dt;
        // Los factores se pensaron por cuadro de 16 ms; así valen igual con cualquier ritmo.
        double Ease(double perFrame) => 1 - Math.Pow(1 - perFrame, dt / 0.016);
        var speaking = _hearing && _listening;
        var goal = _mode switch
        {
            // La onda sigue el volumen del micrófono aunque todavía no haya una frase: así se ve que oye.
            Mode.Listening => speaking ? 0.3 + _target * 0.7 : 0.10 + _target * 0.7,
            Mode.Busy => speaking ? 0.3 + _target * 0.7 : 0.38,
            Mode.Starting => 0.16,
            _ => 0,
        };
        _level += (goal - _level) * Ease(goal > _level ? 0.35 : 0.12);
        _energy += ((_mode == Mode.Busy ? 1 : 0) - _energy) * Ease(0.06);
        // En pausa y sin nada que mostrar, la onda queda quieta: se redibuja poco.
        // Cada cambio de estado sacude la gota: al ponerse a trabajar se aplasta fuerte, al oírte un poco.
        if (_mode != _lastMode)
        {
            Kick(_mode == Mode.Busy ? 1 : _mode == Mode.Problem ? -0.8 : 0.55);
            _lastMode = _mode;
        }
        if (_hearing != _lastHearing)
        {
            if (_hearing) Kick(0.45);
            _lastHearing = _hearing;
        }
        var kicking = !Spring.Settled(_clock.Elapsed.TotalMilliseconds - _kickAt, 420, 0.32);
        var still = _mode is Mode.Paused or Mode.Problem && _level < 0.01 && _caption.Length == 0 && !kicking;
        _frame.Interval = still ? 120 : FrameMs;
        MaybeEndLive();
        // Sin nada que hacer ni contar, la onda se desvanece; con el atajo vuelve subiendo un poco.
        var shown = _holding is not null || _agent.Busy || _live is not null || !_listening || _settingsOpen
            || _mode == Mode.Problem || _caption.Length > 0;
        _presence += ((shown ? 1 : 0) - _presence) * Ease(shown ? 0.22 : 0.12);
        if (!shown && _presence < 0.02)
        {
            _presence = 0;
            if (Visible) Hide();
            _frame.Interval = 120;
            return;
        }
        Render();
    }

    void Kick(double power)
    {
        _kickAt = _clock.Elapsed.TotalMilliseconds;
        _kickPower = power;
    }

    void Render()
    {
        _painter.Scale = S;
        _painter.Mode = _mode;
        _painter.Level = _level;
        _painter.T = _t;
        _painter.Energy = _energy;
        var left = (_captionUntil - DateTime.Now).TotalMilliseconds;
        if (_caption.Length > 0 && left <= 0) _caption = "";
        _painter.Caption = _caption;
        _painter.CaptionColor = _captionColor;
        _painter.CaptionAlpha = (float)Math.Clamp(left / 350.0, 0, 1);
        var now = _clock.Elapsed.TotalMilliseconds;
        var sinceCaption = now - _captionAt;
        _painter.CaptionAlpha *= (float)Math.Clamp(sinceCaption / 140, 0, 1);
        // Sube desde abajo pasándose un poco y se asienta; al irse se eleva un pelo mientras se apaga.
        _painter.CaptionRise = (float)((1 - Spring.Step(sinceCaption, 460, 0.5)) * 10 - (1 - Math.Clamp(left / 350.0, 0, 1)) * 4);
        _painter.CaptionScale = (float)Spring.Lerp(0.86, 1, sinceCaption, 460, 0.5);
        _painter.Squash = (float)(_kickPower * 0.75 * Spring.Kick(now - _kickAt, 420, 0.32) + _level * 0.18);
        using var bmp = _painter.Paint();
        var rise = (int)((1 - _presence) * 10 * S);
        Present(bmp, new Point(_pos.X, _pos.Y + rise), (byte)Math.Round(255 * Math.Clamp(_presence, 0, 1)));
        if (!Visible) Show();
    }

    // ───────────────────────── atajos

    const int HotToggle = 1, HotStop = 2;

    void RegisterHotkeys()
    {
        Native.UnregisterHotKey(Handle, HotToggle);
        Native.UnregisterHotKey(Handle, HotStop);
        Native.RegisterHotKey(Handle, HotToggle, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x20);
        Native.RegisterHotKey(Handle, HotStop, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x58);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            if (m.WParam == HotToggle) ToggleListening();
            else if (m.WParam == HotStop) _agent.Stop();
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Native.UnregisterHotKey(Handle, HotToggle);
        Native.UnregisterHotKey(Handle, HotStop);
        _keys?.Dispose();
        EndLive("salir");
        _livePlayer?.Dispose();
        _frame.Stop();
        _agent.Stop();
        _mic.Dispose();
        try { _mcp?.DisposeAsync().AsTask().Wait(2000); } catch { }
        _tray.Visible = false;
        _tray.Dispose();
        try { Store.Save(_s); } catch { }
        base.OnFormClosing(e);
    }
}
