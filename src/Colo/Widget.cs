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
    // La toma en curso: los trozos ya mandados a transcribir, en orden (se van mandando en cada pausa).
    readonly object _partsGate = new();
    List<Task<string>>? _parts;
    // Transcribiendo o escribiendo un dictado: la onda se queda con el spinner.
    int _thinking;
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
    readonly Vault _vault = Secrets.Open(System.IO.Path.Combine(Store.Dir, "vault.json"));
    // ── agenda (1.17): tareas, listas, recordatorios y proyectos; los avisos van en su propio hilo.
    readonly Agenda _agenda = new(System.IO.Path.Combine(Store.Dir, "agenda.json"));
    Reminders? _reminders;
    // ── palco (1.19): la ventana se crea la primera vez que el modelo muestra algo.
    readonly StageHost _stage;
    // ── reuniones (1.20): un atajo por toques graba mic + sonido del PC; el acta sale después, en segundo plano.
    const string Record = "record";
    readonly Meetings _meetings = new(System.IO.Path.Combine(Store.Dir, "meetings.json"));
    readonly RecordKey _recKey = new();
    readonly MeetingWork _meetingWork;
    Recorder? _recorder;
    RecordPill? _pill;
    // Quién habla (1.24): huellas de voz de las personas ya nombradas y el reconocedor, bajado la primera vez que se graba.
    readonly People _people = new(System.IO.Path.Combine(Store.Dir, "people.json"));
    readonly VoiceKit _voiceKit = new(VoicesDir());
    SherpaVoices? _voices;
    readonly SemaphoreSlim _voicesGate = new(1, 1);
    System.Threading.Timer? _liveTimer;
    long _orderTokens;

    // ── arrastre
    bool _pressed, _moved;
    Point _pressAt, _pressPos;

    internal Agent Brain => _agent;

    public Widget()
    {
        _ai = new AiClient(_s);
        _eleven = new ElevenLabsStt(_s);
        _agent = new Agent(_ai, _s, _pc) { Memory = _memory, Vault = _vault, Agenda = _agenda };
        _pc.Cursor = _cursor;
        _pc.Vault = _vault;
        _pc.Agenda = _agenda;
        _stage = new StageHost(this, _ai, _s);
        _pc.Stage = _stage;
        _agent.Stage = _stage.Line;
        // Una pregunta que propone la página (con clic del usuario) es como si la dijera.
        _stage.Ask += q => Post(() => Command(q));
        _stage.Speak += text => Post(() => { _speaker ??= TryCreateSpeaker(); _speaker?.Say(text); });
        _stage.Problem += text => Post(() => Say(text, Theme.Danger, 6000));
        _agenda.MoreSearch = _meetings.Search;
        _meetingWork = new MeetingWork(_meetings, _s, _ai, _agenda)
        {
            Encode = Recorder.Encode,
            LocalTranscribe = samples => _stt?.Transcribe(samples) ?? "",
            Voices = VoicesAsync,
            People = _people,
        };
        _meetingWork.Audio += (model, seconds) => _tokens.AddAudio(model, seconds);
        _meetingWork.Notice += (text, error) => Post(() =>
        {
            Say(text, error ? Theme.Danger : Theme.Blue, 10_000);
            try { _tray.ShowBalloonTip(8_000, "Colo · reunión", text, error ? ToolTipIcon.Warning : ToolTipIcon.Info); } catch { }
        });
        _pc.Meeting = c =>
        {
            var r = "error: Colo se está cerrando";
            try { Invoke(() => r = MeetingTool(c)); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
            return r;
        };
        Text = "Colo";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _frame.Tick += (_, _) => Frame();
        _mic.Failed += reason => Post(() => OnMicFailed(reason));
        _agent.Event += ev => Post(() => OnAgent(ev));
        BuildMenu();
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
        Native.AddClipboardFormatListener(Handle);
        _watch = new System.Threading.Timer(_ => WatchWindow(), null, 2000, 2000);
        _reminders = new Reminders(_agenda, due => Post(() => Remind(due)), () => !_s.DisabledTools.Contains("agenda"));
        RegisterHotkeys();
        SetupKeys();
        ApplyAutostart();
        _ = StartAsync();
        // Reuniones que quedaron a medias (Colo cerrado mientras transcribía): siguen, sin estorbar el arranque.
        _ = Task.Run(async () =>
        {
            await Task.Delay(20_000);
            try { foreach (var dir in _meetings.Recover(Recorder.Root, _recorder?.Dir)) Log.Write("reunión: recuperada " + dir); }
            catch (Exception ex) { Log.Write("reunión: recuperar · " + ex.Message); }
            await RunMeetingsAsync();
        });
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
            // Instalado, arranca siempre la copia instalada (un .exe suelto de Descargas abierto un día no la cambia).
            var installed = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Colo", "Colo.exe");
            var exe = System.IO.File.Exists(installed) ? installed : Application.ExecutablePath;
            if (_s.StartWithWindows) run.SetValue("Colo", $"\"{exe}\"");
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
        l.TakeChunk -= OnTakeChunk;
        l.TakeChunk += OnTakeChunk;
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
        try
        {
            var s = new Speaker(_s);
            s.Billed += (model, seconds) => _tokens.AddAudio(model, seconds);
            return s;
        }
        catch { return null; }
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
        if (!k.Set(Record, _s.RecordHotkey)) Log.Write($"atajo: «{_s.RecordHotkey}» no vale");
    }

    // Llegan desde el gancho de teclado: se contesta al momento y lo demás va por la cola de la interfaz.
    // Grabar cuenta al soltar: si con el atajo abajo llega otra tecla (Win+Alt+R de la Game Bar…), era otro atajo
    // y se cancela sin tocar la grabación.
    // Modo prueba «escribir en vez de hablar»: el micrófono no se toca; al soltar sale la caja.
    void OnPressed(string id) { if (id != Record && !_s.TypeInstead) Post(() => Press(id)); }
    void OnReleased(string id) => Post(() => { if (id == Record) RecordTap(); else if (_s.TypeInstead) TypeIt(id == Dictate); else Release(id); });
    void OnCancelled(string id) { if (id != Record && !_s.TypeInstead) Post(() => Cancel(id)); }

    TypeBox? _typeBox;
    bool _typeDictate;

    /// <summary>La caja del modo prueba: lo escrito sigue el mismo camino que lo transcrito.</summary>
    void TypeIt(bool dictate)
    {
        if (_typeBox is null)
        {
            _typeBox = new TypeBox();
            _typeBox.Sent += text => Post(async () =>
            {
                Log.Write($"escrito ({(_typeDictate ? "dictar" : "hablar")}): «{text}»");
                await Task.Delay(150); // que el foco termine de volver a la ventana de antes
                await Heard(text, 0, _typeDictate);
            });
        }
        _typeDictate = dictate;
        _typeBox.Open(dictate, Bounds);
    }

    // ───────────────────────── reuniones

    void RecordTap() => Meeting(_recKey.Tap(_recorder?.State ?? RecState.Idle, DateTime.Now));

    string Meeting(RecAction a)
    {
        switch (a)
        {
            case RecAction.Start:
                if (_recorder is not null) return "ok: ya estaba grabando";
                try
                {
                    _recorder = new Recorder();
                    _recorder.Start(_s.MicId);
                }
                catch (Exception ex)
                {
                    Log.Write("reunión: no pude grabar " + ex.Message);
                    _recorder = null;
                    Say("No pude grabar: " + ex.Message, Theme.Danger, 6000);
                    return "error: no pude grabar: " + ex.Message;
                }
                _pill ??= NewPill();
                _pill.ShowNear(Bounds);
                StartLiveTranscript(_recorder);
                // Los modelos de voces se bajan ya (la primera vez), mientras la reunión empieza.
                _ = Task.Run(() => VoicesAsync(CancellationToken.None));
                Say("● Grabando la reunión", Theme.Danger, 2500);
                return $"ok: grabando (se guarda en {_recorder.Dir})";
            case RecAction.Pause:
                if (_recorder?.State != RecState.Recording) return "error: no está grabando";
                _recorder.Pause();
                _pill?.Invalidate();
                Say("Grabación en pausa", Theme.Muted, 2500);
                return "ok: en pausa";
            case RecAction.Resume:
                if (_recorder?.State != RecState.Paused) return "error: no está en pausa";
                _recorder.Resume();
                _pill?.Invalidate();
                Say("● Grabando otra vez", Theme.Danger, 2000);
                return "ok: grabando otra vez";
            case RecAction.Stop:
                return StopRecording();
            default:
                return "ok";
        }
    }

    RecordPill NewPill()
    {
        var p = new RecordPill(() => (_recorder?.State ?? RecState.Idle, _recorder?.Elapsed ?? TimeSpan.Zero));
        p.Toggle += () => Meeting(_recorder?.State == RecState.Paused ? RecAction.Resume : RecAction.Pause);
        p.StopAsked += () => Meeting(RecAction.Stop);
        return p;
    }

    string StopRecording()
    {
        if (_recorder is not { } rec) return "error: no está grabando";
        _recorder = null;
        _liveTimer?.Dispose();
        _liveTimer = null;
        _pill?.Hide();
        var seconds = rec.Stop();
        if (seconds < 3)
        {
            try { System.IO.Directory.Delete(rec.Dir, true); } catch { }
            Say("Grabación descartada (muy corta)", Theme.Muted, 2500);
            return "ok: descartada (muy corta)";
        }
        _meetings.Add(rec.Dir, rec.Started, seconds);
        _ = Task.Run(RunMeetingsAsync);
        Say("Reunión guardada · la transcribo en segundo plano", Theme.Blue, 4000);
        return $"ok: guardada ({(int)(seconds / 60)} min) en {rec.Dir}; el acta sale sola en unos minutos";
    }

    /// <summary>
    /// Dónde van los modelos de voces. sherpa-onnx lee la ruta como UTF-8 y .NET se la pasa en ANSI: con una letra
    /// como la «ó» de «MiguelOsório» no encuentra el modelo y Colo se caía al primer trozo. Carpeta solo ASCII: la
    /// misma con su nombre corto (8.3) o, si el disco no los tiene, ProgramData.
    /// </summary>
    static string VoicesDir()
    {
        static bool Ascii(string s) => s.All(ch => ch < 128);
        var dir = System.IO.Path.Combine(Store.ModelsDir, "voces");
        if (Ascii(dir)) return dir;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            var shortDir = Native.ShortPath(dir);
            if (Ascii(shortDir)) return shortDir;
        }
        catch { }
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Colo", "voces");
    }

    /// <summary>El reconocedor de voces; null si no se pudo bajar o cargar (la reunión sigue por canales).</summary>
    async Task<IVoices?> VoicesAsync(CancellationToken ct)
    {
        if (_voices is not null) return _voices;
        // Uno solo a la vez: la descarga al empezar a grabar y el primer trozo no cargan los modelos dos veces.
        await _voicesGate.WaitAsync(ct);
        try
        {
            if (_voices is not null) return _voices;
            if (!await _voiceKit.EnsureAsync(ct)) return null;
            SherpaNative.Prepare();
            return _voices = new SherpaVoices(_voiceKit, Math.Clamp(Environment.ProcessorCount / 4, 1, 4));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write("voces: no disponibles · " + ex.Message);
            return null;
        }
        finally { _voicesGate.Release(); }
    }

    /// <summary>Mientras graba, cada minuto se transcribe lo que ya hay entero: al parar casi no queda nada.</summary>
    void StartLiveTranscript(Recorder rec)
    {
        _liveTimer?.Dispose();
        _liveTimer = new System.Threading.Timer(_ =>
        {
            if (_recorder != rec) return;
            _ = Task.Run(async () =>
            {
                try { await _meetingWork.LiveAsync(rec.Dir, rec.Started); }
                catch (Exception ex) { Log.Write("reunión: transcribir en vivo · " + ex.Message); }
            });
        }, null, 60_000, 60_000);
    }

    async Task RunMeetingsAsync()
    {
        try { await _meetingWork.RunAsync(); }
        catch (Exception ex) { Log.Write("reunión: " + ex.Message); }
    }

    /// <summary>La herramienta meeting (en el hilo de la interfaz).</summary>
    string MeetingTool(ToolCall c)
    {
        switch (c.Str("action"))
        {
            case "start": return Meeting(RecAction.Start);
            case "pause": return Meeting(RecAction.Pause);
            case "resume": return Meeting(RecAction.Resume);
            case "stop": return Meeting(RecAction.Stop);
            case "list": return _meetings.List(DateTime.Now);
            case "read": return _meetings.Read(c.Str("query"));
            case "retry":
                foreach (var j in _meetings.All.Where(j => j.Status != "done" && j.Attempts >= 3))
                    _meetings.Update(j with { Attempts = 0, Error = "" });
                _ = Task.Run(RunMeetingsAsync);
                return "ok: reintentando las que fallaron";
            default:
                var pending = _meetings.Pending().Count;
                var now = _recorder is { } r
                    ? $"{(r.State == RecState.Paused ? "en pausa" : "grabando")} ({r.Elapsed:h\\:mm\\:ss})"
                    : "sin grabar";
                return $"ok: {now}" + (pending > 0 ? $"; {pending} reunión(es) procesándose" : "");
        }
    }

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
        lock (_partsGate) _parts = [];
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
        lock (_partsGate) _parts = null;
        Log.Write("atajo: cancelado (era otro atajo)");
    }

    void FinishTake(bool dictate)
    {
        MicRelease();
        // Ya volvió a apretar: lo de antes se junta con lo nuevo.
        if (_holding is not null || _listener is null) return;
        var rest = _listener.EndTake();
        List<Task<string>> parts;
        lock (_partsGate)
        {
            parts = _parts ?? [];
            _parts = null;
        }
        var seconds = _listener.LastTakeSeconds;
        var peak = _listener.LastTakePeak;
        Log.Write($"atajo: soltado · {seconds:0.0} s · pico {peak:0.000} · {parts.Count} trozos ya enviados");
        if (seconds < 0.5) { Say("Mantén las teclas mientras hablas", Theme.Muted, 2200); return; }
        if (peak < 0.003) { Say($"No me llega sonido de «{_mic.DeviceName}». Revisa el micrófono en Ajustes › Voz.", Theme.Danger, 6000); return; }
        if (rest.Length >= Listener.Rate / 5) parts.Add(TranscribeAsync(rest));
        _ = CompleteTakeAsync(parts, dictate);
    }

    // Hilo de audio: una pausa mientras mantienes el atajo; ese trozo ya se transcribe.
    void OnTakeChunk(float[] chunk)
    {
        lock (_partsGate)
        {
            if (_parts is null) return;
            _parts.Add(TranscribeAsync(chunk));
            Log.Write($"voz: trozo {_parts.Count} de {chunk.Length / 16000.0:0.0} s enviado mientras hablas");
        }
    }

    Task<string> TranscribeAsync(float[] samples) => Task.Run(async () =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text;
        if (_s.SpeechEngine == SpeechEngine.Local)
        {
            var stt = _stt ?? throw new InvalidOperationException("Whisper no está cargado");
            stt.Prompt = Glossary();
            text = stt.Transcribe(samples);
        }
        else
        {
            var wav = Wav.Encode(samples);
            var glossary = Glossary();
            text = await Hedged(ct => _s.SpeechEngine == SpeechEngine.ElevenLabs
                ? _eleven.TranscribeAsync(wav, ct)
                : _ai.TranscribeAsync(wav, glossary, ct));
        }
        Log.Write($"voz: {samples.Length / 16000.0:0.0} s → «{text}» ({sw.ElapsedMilliseconds} ms)");
        _tokens.AddAudio(_s.SpeechEngine switch
        {
            SpeechEngine.Local => Prices.Local,
            SpeechEngine.ElevenLabs => _s.ElevenLabsModel,
            _ => _s.TranscribeModel,
        }, samples.Length / (double)Listener.Rate);
        return text;
    });

    /// <summary>
    /// El servicio a veces tarda 7–12 s en contestar lo que suele hacer en 0,5 s (visto en el registro): cada 2,5 s
    /// sin respuesta se manda otra vez (hasta 3) y vale la primera que llegue (el audio cuesta centésimos de centavo).
    /// Una sola repetición no bastaba: a veces las dos se quedaban colgadas 10 s.
    /// </summary>
    static async Task<string> Hedged(Func<CancellationToken, Task<string>> call)
    {
        using var cts = new CancellationTokenSource();
        var pending = new List<Task<string>> { call(cts.Token) };
        Exception? last = null;
        while (pending.Count > 0)
        {
            var wait = pending.Count < 3 ? Task.Delay(2500) : null;
            var any = wait is null ? await Task.WhenAny(pending) : await Task.WhenAny(pending.Append(wait));
            if (any == wait)
            {
                Log.Write("voz: la transcripción tarda, la pido otra vez");
                pending.Add(call(cts.Token));
                continue;
            }
            var done = (Task<string>)any;
            pending.Remove(done);
            if (done.IsCompletedSuccessfully)
            {
                cts.Cancel();
                return done.Result;
            }
            last = done.Exception?.InnerException ?? last;
        }
        throw last ?? new InvalidOperationException("transcripción sin respuesta");
    }

    /// <summary>Junta los trozos en orden y hace lo que toca. Corre en el hilo de la interfaz.</summary>
    async Task CompleteTakeAsync(List<Task<string>> parts, bool dictate)
    {
        if (parts.Count == 0) return;
        _thinking++;
        Say("Transcribiendo…", Theme.Muted, 30_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var text = string.Join(" ", (await Task.WhenAll(parts)).Select(t => t.Trim()).Where(t => t.Length > 0));
            var ms = sw.ElapsedMilliseconds;
            Log.Write($"voz: {parts.Count} trozo(s) → «{text}» · espera al soltar {ms} ms");
            if (_caption == "Transcribiendo…") Say("", Theme.Muted, 0);
            await Heard(text, ms, dictate);
        }
        catch (Exception ex)
        {
            Log.Write("voz: transcripción falló · " + ex);
            Say(ex.Message, Theme.Danger, 5000);
        }
        finally { _thinking--; }
    }

    /// <summary>Lo que se hace con el texto oído (o escrito en el modo prueba).</summary>
    async Task Heard(string text, long ms, bool dictate)
    {
        // Con Ajustes abierto es la prueba del micrófono: se muestra, no se ejecuta.
        if (_settingsWindow is not null) { _settingsWindow.VoiceHeard(text, ms); return; }
        // Una letra suelta («¡Y!», «Eh.») es ruido: ni se manda al modelo. «Sí» o «no» sí pasan.
        if (text.Count(char.IsLetter) < 2) { if (text.Length > 0) Log.Write($"voz: descartado «{text}» (sin palabras)"); return; }
        if (dictate) { await DictateNow(text, ms); return; }
        Command(text);
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
        var liveModel = _s.LiveModel;
        call.Billed += seconds => _tokens.AddAudio(liveModel, seconds);
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

    /// <summary>Glosario para la transcripción: palabras de las órdenes y los programas abiertos.</summary>
    static string Glossary() => LocalStt.PromptFor(Native.AppWindows().Select(Native.ProcessName));

    /// <summary>
    /// Atajo de dictar: el modelo lo deja escrito con tu tono y va donde está el cursor. Se escribe a medida que
    /// llega (no espera la respuesta entera) y, mientras tanto, se busca dónde escribir.
    /// </summary>
    async Task DictateNow(string heard, long sttMs)
    {
        Say("Escribiendo…", Theme.Muted, 8000);
        var window = Native.WindowLabel(Native.GetForegroundWindow());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Con el foco en un campo o el ratón encima de uno, va ahí. Si no, el modelo elige entre los campos de las
        // ventanas abiertas en la misma llamada del dictado (1ª línea @n) y Colo va hasta él, clic y escribe.
        string? direct;
        List<Desktop.TextInput> inputs = [];
        try
        {
            direct = await Task.Run(Desktop.Direct);
            if (direct is null) inputs = await Task.Run(() => Desktop.Inputs());
        }
        catch (Exception ex) { direct = "no pude mirar el foco · " + ex.Message; }
        var focusMs = sw.ElapsedMilliseconds;
        var target = direct ?? (inputs.Count == 0 ? "nada (no hay campos)" : "");
        if (direct is null && inputs.Count == 1) target = await Task.Run(() => Desktop.GoTo(inputs[0], _cursor));
        var place = direct is null && inputs.Count > 1 ? new Dictation.Place(inputs.Select(i => i.Label).ToList()) : null;
        long first = -1;

        var chunks = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var typer = Task.Run(async () =>
        {
            var start = true;
            void Write(string c)
            {
                var t = start ? c.TrimStart() : c;
                if (t.Length == 0) return;
                if (start) first = sw.ElapsedMilliseconds;
                start = false;
                Input.Type(t);
            }
            await foreach (var c in chunks.Reader.ReadAllAsync())
            {
                if (place is null) { Write(c); continue; }
                var wasDecided = place.Decided;
                var t = place.Feed(c);
                if (!wasDecided && place.Decided) target = Desktop.GoTo(inputs[place.Choice - 1], _cursor);
                Write(t);
            }
            if (place is { Decided: false })
            {
                var rest = place.Flush();
                target = Desktop.GoTo(inputs[place.Choice - 1], _cursor);
                Write(rest);
            }
        });
        try
        {
            var places = place is null ? null : inputs.Select(i => i.Label).ToList();
            var text = await Task.Run(() => Dictation.PolishAsync(_ai, _s, heard, window, CancellationToken.None, d => chunks.Writer.TryWrite(d), places));
            chunks.Writer.TryComplete();
            await typer;
            // La mano azul solo salió si Colo fue hasta un campo: se retira al terminar.
            if (direct is null && inputs.Count > 0) _cursor.Away();
            var chosen = place is null ? "" : $" · el modelo eligió @{place.Choice} de {inputs.Count}";
            Log.Write($"dicta: transcribir {sttMs} ms · buscar campo {focusMs} ms · 1ª letra {first} ms · total {sw.ElapsedMilliseconds} ms · en {target}{chosen} · «{heard}» → «{(place is null ? text : Dictation.WithoutPlace(text))}»");
            Say("", Theme.Muted, 0);
        }
        catch (Exception ex)
        {
            chunks.Writer.TryComplete();
            Log.Write("dicta: falló · " + ex.Message);
            Say("No pude escribir el dictado: " + ex.Message, Theme.Danger, 6000);
        }
    }

    /// <summary>Lo que se ejecuta al oír una frase (también lo usa la prueba de punta a punta).</summary>
    internal void Command(string text)
    {
        if (!Configured()) { OpenSettings(); return; }
        Log.Write($"orden: «{text}»");
        // Lo que entendió no se muestra: no ayuda; se ve lo que hace y lo que responde.
        Say("", Theme.Ink, 0);
        _mode = Mode.Busy;
        SetClickThroughIfBusy();
        _ = _agent.Hear(text);
    }

    void OnAgent(AgentEvent ev)
    {
        if (ev.Kind != AgentEventKind.Thinking) Log.Write($"agente {ev.Kind}: {ev.Text}");
        if (ev.Kind is AgentEventKind.Done or AgentEventKind.Error or AgentEventKind.Ignored or AgentEventKind.Stopped)
            Log.Write($"tokens: {Interlocked.Exchange(ref _orderTokens, 0):N0} esta orden · hoy {_tokens.TodayTokens:N0} · total {_tokens.AllTokens:N0} · {TokenMeter.Usd(_tokens.Cost(_s.Prices).Today)} hoy");
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
                // Sin esto la onda se iba sin decir nada y parecía colgado.
                Say("No entendí, repite", Theme.Muted, 2500);
                LiveResult(c => c.Thinking(_liveTask!, "En el PC no había nada que hacer con eso."));
                Idle();
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
            speaker.Billed += (model, seconds) => _tokens.AddAudio(model, seconds);
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

    /// <summary>Recordatorio que toca: en la onda, en la bandeja (por si la onda no se ve) y en voz si habla las respuestas.</summary>
    void Remind(IReadOnlyList<AgendaItem> due)
    {
        var text = string.Join(" · ", due.Select(i => i.Text));
        Say("⏰ " + text, Theme.Blue, 15_000);
        System.Media.SystemSounds.Asterisk.Play();
        try { _tray.ShowBalloonTip(10_000, "Colo · recordatorio", string.Join("\n", due.Select(i => $"#{i.Id} {i.Text}")), ToolTipIcon.Info); } catch { }
        if (_s.SpeakReplies) { _speaker ??= TryCreateSpeaker(); _speaker?.Say(text); }
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
            using var f = new SettingsWindow(_s, _memory, _tokens, _vault, _agenda, new MeetingDesk(_meetings, _meetingWork, _people));
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
            : _listening ? ("Listo", Theme.Blue) : ("En pausa", Color.FromArgb(161, 161, 170));
        var (today, all) = _tokens.Cost(_s.Prices);
        var detail = $"Hoy {TokenMeter.Usd(today)} · total {TokenMeter.Usd(all)}";
        _menu.Open(Cursor.Position, title, detail, dot,
        [
            new(_listening ? "Pausar" : "Seguir", _listening ? PopMenu.Icon.Pause : PopMenu.Icon.Play, ToggleListening),
            new("Detener lo que hace", PopMenu.Icon.Stop, () => _agent.Stop()),
            .. (_recorder is null ? Array.Empty<PopMenu.Entry>() : [new PopMenu.Entry("Parar la reunión y guardarla", PopMenu.Icon.Stop, () => Meeting(RecAction.Stop))]),
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
        var shown = _holding is not null || _thinking > 0 || _agent.Busy || _live is not null || !_listening || _settingsOpen
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
        _painter.Spinning = _thinking > 0 && _holding is null;
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

    // ── lo que hace el usuario (para recent y el historial del portapapeles; solo en memoria)
    System.Threading.Timer? _watch;
    IntPtr _watchHwnd;
    string _watchTitle = "", _watchLabel = "";

    void WatchWindow()
    {
        try
        {
            var h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero || Native.IsOwnWindow(h)) return;
            var title = Native.Title(h);
            if (h != _watchHwnd || title != _watchTitle)
            {
                _watchHwnd = h;
                _watchTitle = title;
                _watchLabel = Native.WindowLabel(h);
            }
            _pc.Activity.Seen(_watchLabel, DateTime.Now);
        }
        catch { }
    }

    void OnClipboardChanged()
    {
        if (DateTime.Now < Desktop.QuietUntil) return;
        try
        {
            // Los gestores de contraseñas marcan lo que copian para que nadie lo guarde.
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") || Clipboard.ContainsData("Clipboard Viewer Ignore")) return;
            if (Clipboard.ContainsText()) _pc.Clips.Add(Clipboard.GetText(), DateTime.Now);
        }
        catch { } // otro programa lo tiene abierto: este se pierde, no pasa nada
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) OnClipboardChanged();
        if (m.Msg == Native.WM_HOTKEY)
        {
            if (m.WParam == HotToggle) ToggleListening();
            else if (m.WParam == HotStop) _agent.Stop();
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Native.RemoveClipboardFormatListener(Handle);
        _watch?.Dispose();
        _reminders?.Dispose();
        // Cerrar Colo grabando no pierde la reunión: se guarda y se procesa al volver.
        _liveTimer?.Dispose();
        if (_recorder is { } rec)
        {
            _recorder = null;
            var seconds = rec.Stop();
            if (seconds >= 3) _meetings.Add(rec.Dir, rec.Started, seconds);
            else try { System.IO.Directory.Delete(rec.Dir, true); } catch { }
        }
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
