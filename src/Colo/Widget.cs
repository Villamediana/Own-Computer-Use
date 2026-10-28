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
/// Colo en pantalla: una pastilla blanca con una onda, siempre encima. La onda respira mientras
/// escucha, sigue la voz cuando oyes hablar y brilla cuando está trabajando. Escucha siempre.
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
    readonly AiCursor _cursor = new();
    readonly Mic _mic = new();
    readonly SpeechModels _models = new(Store.ModelsDir, System.IO.Path.Combine(AppContext.BaseDirectory, "models"));
    readonly Channel<float[]> _phrases = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });
    Speaker? _speaker;
    Listener? _listener;
    LocalStt? _stt;
    McpHub? _mcp;
    bool _listening, _settingsOpen;
    SettingsWindow? _settingsWindow;
    // diagnóstico del audio
    DateTime _listenSince, _lastSound, _lastSilenceWarning, _lastLevelPost;
    double _peak;
    List<short>? _probe;
    readonly object _probeLock = new();
    DateTime _peakSince = DateTime.Now;

    // ── dibujo
    readonly WavePainter _painter = new();
    readonly System.Windows.Forms.Timer _frame = new() { Interval = 16 };
    Mode _mode = Mode.Starting;
    bool _hearing;
    double _t, _level, _target, _energy;
    string _caption = "";
    Color _captionColor = Theme.Ink;
    DateTime _captionUntil;
    Point _pos;
    readonly NotifyIcon _tray = new();
    readonly ContextMenuStrip _menu = new();
    ToolStripMenuItem? _pauseItem;

    // ── arrastre
    bool _pressed, _moved;
    Point _pressAt, _pressPos;

    internal Agent Brain => _agent;

    public Widget()
    {
        _ai = new AiClient(_s);
        _eleven = new ElevenLabsStt(_s);
        _agent = new Agent(_ai, _s, _pc);
        _pc.Cursor = _cursor;
        _ = _cursor.Handle;
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
            Problem("Toca para configurar");
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

    int _micRetries;

    void OnMicFailed(string reason)
    {
        if (!_listening) return;
        _listening = false;
        _hearing = false;
        UpdateMenu();
        // Bluetooth que se desconecta, otro programa que toma el micrófono…: se reintenta solo.
        if (++_micRetries > 5)
        {
            Problem("Micrófono: " + reason);
            return;
        }
        Say("Micrófono cortado, reintentando…", Theme.Muted, 3000);
        _ = RetryMicAsync();
    }

    async Task RetryMicAsync()
    {
        await Task.Delay(2000);
        if (!_listening && !_settingsOpen && _mode != Mode.Paused) await StartListeningAsync();
    }

    bool Configured() =>
        !string.IsNullOrWhiteSpace(_s.ApiKey) && !string.IsNullOrWhiteSpace(_s.Model)
        && (_s.Provider == Provider.OpenAI || !string.IsNullOrWhiteSpace(_s.Endpoint))
        && _s.SpeechEngine switch
        {
            SpeechEngine.Api => !string.IsNullOrWhiteSpace(_s.TranscribeModel),
            SpeechEngine.ElevenLabs => !string.IsNullOrWhiteSpace(_s.ElevenLabsKey),
            _ => true,
        };

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
                _listener = new Listener(_s.Sensitivity);
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
        l.Utterance -= OnUtterance;
        l.Utterance += OnUtterance;
        l.SpeakingChanged -= OnSpeaking;
        l.SpeakingChanged += OnSpeaking;
        l.Level -= OnLevel;
        l.Level += OnLevel;
        l.Raw -= OnRaw;
        l.Raw += OnRaw;
        _speaker ??= TryCreateSpeaker();
        if (_speaker is not null) _speaker.Listener = l;
        try { _mic.Start(_s.MicId, l); }
        catch (Exception ex)
        {
            Log.Write("micrófono: no abrió · " + ex);
            Problem("Micrófono: " + ex.Message);
            VoiceStatus("No pude abrir el micrófono: " + ex.Message);
            return;
        }
        _listening = true;
        _listenSince = _lastSilenceWarning = DateTime.Now;
        _lastSound = DateTime.MinValue;
        VoiceStatus($"Escuchando con «{_mic.DeviceName}»");
        _micRetries = 0;
        _mode = _agent.Busy ? Mode.Busy : Mode.Listening;
        UpdateMenu();
    }

    static Speaker? TryCreateSpeaker()
    {
        try { return new Speaker(); } catch { return null; }
    }

    void StopListening()
    {
        _mic.Stop();
        _listening = false;
        _hearing = false;
        _target = 0;
        if (!_agent.Busy) _mode = Mode.Paused;
        UpdateMenu();
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
        if (rms > 0.0001) _lastSound = now;
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

    void OnSpeaking(bool speaking)
    {
        Log.Write(speaking ? "voz: empieza a hablar" : "voz: termina la frase");
        Post(() =>
        {
            _hearing = speaking && _listening;
            _settingsWindow?.VoiceVad(speaking);
        });
    }

    void OnUtterance(float[] samples) => _phrases.Writer.TryWrite(samples);

    // Hilo de audio: junta 5 s para la prueba «Grabar y analizar».
    void OnRaw(ReadOnlySpan<short> pcm)
    {
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
        if (done is not null) _ = Task.Run(() => AnalyzeProbe(done));
    }

    void StartProbe()
    {
        if (!_listening) { _settingsWindow?.ProbeResult(new { error = "El micrófono no está escuchando." }); return; }
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
        await foreach (var samples in _phrases.Reader.ReadAllAsync())
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string text;
                if (_s.SpeechEngine == SpeechEngine.Local)
                {
                    if (_stt is null) { Log.Write("voz: frase descartada, Whisper no está cargado"); continue; }
                    _stt.Prompt = LocalStt.PromptFor(Native.AppWindows().Select(Native.ProcessName));
                    text = _stt.Transcribe(samples);
                }
                else if (_s.SpeechEngine == SpeechEngine.ElevenLabs)
                {
                    text = await _eleven.TranscribeAsync(Wav.Encode(samples), CancellationToken.None);
                }
                else
                {
                    text = await _ai.TranscribeAsync(Wav.Encode(samples), CancellationToken.None);
                }
                var ms = sw.ElapsedMilliseconds;
                Log.Write($"voz: {samples.Length / 16000.0:0.0} s → «{text}» ({ms} ms)");
                Post(() =>
                {
                    // Con Ajustes abierto es la prueba del micrófono: se muestra, no se ejecuta.
                    if (_settingsWindow is not null) { _settingsWindow.VoiceHeard(text, ms); return; }
                    if (text.Length < 2) { Say("No entendí", Theme.Muted, 1200); return; }
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
        switch (ev.Kind)
        {
            case AgentEventKind.Thinking:
            case AgentEventKind.Action:
                _mode = Mode.Busy;
                break;
            case AgentEventKind.Message:
                Say(ev.Text, Theme.Ink, 2500 + ev.Text.Length * 50);
                break;
            case AgentEventKind.Done:
                Say(ev.Text, Theme.Ink, Math.Min(9000, 2500 + ev.Text.Length * 60));
                if (_s.SpeakReplies) _speaker?.Say(ev.Text);
                Idle();
                break;
            case AgentEventKind.Ignored:
                Idle();
                break;
            case AgentEventKind.Stopped:
                Say("Detenido", Theme.Muted, 1500);
                Idle();
                break;
            case AgentEventKind.Error:
                Say(ev.Text, Theme.Danger, 6000);
                Idle();
                break;
        }
        SetClickThroughIfBusy();
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
            using var f = new SettingsWindow(_s);
            _settingsWindow = f;
            f.RecordTest += StartProbe;
            f.Ready += () =>
            {
                if (_listening) f.VoiceStatus($"Escuchando con «{_mic.DeviceName}»");
                else if (_s.SpeechEngine == SpeechEngine.Local
                         || (_s.SpeechEngine == SpeechEngine.ElevenLabs && _s.ElevenLabsKey.Length > 0) || Configured())
                {
                    // Para la prueba se enciende aunque falte la key del modelo.
                    f.VoiceStatus("Encendiendo el micrófono…");
                    _ = StartListeningAsync(forTest: true);
                }
                else f.VoiceStatus("Para probar esta voz, guarda primero su llave");
            };
            saved = f.ShowDialog() == DialogResult.OK;
        }
        finally
        {
            _settingsOpen = false;
            _settingsWindow = null;
        }
        if (!saved && !Configured()) StopListening();
        Log.Write(saved ? $"ajustes guardados · {_s.Provider} · modelo {_s.Model} · voz {_s.SpeechEngine} · mic «{(_s.MicId.Length > 0 ? _s.MicId : "predeterminado")}» · ver {_s.Vision}" : "ajustes cerrados sin guardar");
        if (!saved) return;

        Store.Save(_s);
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
        else Problem("Falta la llave o el modelo");
    }

    void BuildMenu()
    {
        _menu.Renderer = new MenuRenderer();
        _menu.Font = Theme.Text(10f);
        _menu.ShowImageMargin = false;
        _menu.ShowCheckMargin = false;
        _menu.Padding = new Padding(0, 5, 0, 5);
        _menu.BackColor = Color.White;
        ToolStripMenuItem Item(string text, Action onClick, string? tag = null)
        {
            var item = new ToolStripMenuItem(text, null, (_, _) => onClick()) { Padding = new Padding(6, 5, 18, 5), Tag = tag };
            _menu.Items.Add(item);
            return item;
        }
        _pauseItem = Item("Pausar", ToggleListening);
        Item("Detener lo que hace", () => _agent.Stop());
        Item("Olvidar contexto", () => { _agent.ForgetContext(); Say("Contexto olvidado", Theme.Muted, 1500); });
        _menu.Items.Add(new ToolStripSeparator());
        Item("Ajustes…", OpenSettings);
        Item("Salir", Close, "danger");
        // Esquinas redondeadas de Windows 11 en el menú.
        _menu.Opening += (_, _) => Native.RoundCorners(_menu.Handle);

        _tray.Text = "Colo";
        _tray.Icon = Icon ?? SystemIcons.Application;
        _tray.ContextMenuStrip = _menu;
        _tray.Visible = true;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleListening(); };
    }

    void UpdateMenu()
    {
        if (_pauseItem is not null) _pauseItem.Text = _listening ? "Pausar" : "Escuchar";
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
            _menu.Show(Cursor.Position);
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
        _t += 0.016;
        var speaking = _hearing && _listening;
        var goal = _mode switch
        {
            // La onda sigue el volumen del micrófono aunque todavía no haya una frase: así se ve que oye.
            Mode.Listening => speaking ? 0.3 + _target * 0.7 : 0.10 + _target * 0.7,
            Mode.Busy => speaking ? 0.3 + _target * 0.7 : 0.38,
            Mode.Starting => 0.16,
            _ => 0,
        };
        _level += (goal - _level) * (goal > _level ? 0.35 : 0.12);
        _energy += ((_mode == Mode.Busy ? 1 : 0) - _energy) * 0.06;
        // En pausa y sin nada que mostrar, la onda queda quieta: se redibuja poco.
        var still = _mode is Mode.Paused or Mode.Problem && _level < 0.01 && _caption.Length == 0;
        _frame.Interval = still ? 120 : 16;
        CheckSilence();
        Render();
    }

    /// <summary>Si el micrófono no entrega nada de sonido, se dice en lugar de quedarse callado.</summary>
    void CheckSilence()
    {
        if (!_listening || _settingsOpen) return;
        var now = DateTime.Now;
        var quietFor = (now - (_lastSound > _listenSince ? _lastSound : _listenSince)).TotalSeconds;
        if (quietFor < 12 || (now - _lastSilenceWarning).TotalSeconds < 45) return;
        _lastSilenceWarning = now;
        Log.Write($"audio: {quietFor:0} s sin sonido de «{_mic.DeviceName}»");
        Say($"No me llega sonido de «{_mic.DeviceName}». Revisa el micrófono en Ajustes › Voz.", Theme.Danger, 7000);
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
        using var bmp = _painter.Paint();
        Present(bmp, _pos);
        if (!Visible) Show();
    }

    // ───────────────────────── atajos

    const int HotToggle = 1, HotStop = 2;

    void RegisterHotkeys()
    {
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
