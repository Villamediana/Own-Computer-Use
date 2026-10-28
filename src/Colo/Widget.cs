using System;
using System.Drawing;
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
    readonly Agent _agent;
    readonly WindowsComputer _pc = new();
    readonly AiCursor _cursor = new();
    readonly Mic _mic = new();
    readonly SpeechModels _models = new(Store.ModelsDir);
    readonly Channel<float[]> _phrases = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });
    Speaker? _speaker;
    Listener? _listener;
    LocalStt? _stt;
    bool _listening, _settingsOpen;

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

    public Widget()
    {
        _ai = new AiClient(_s);
        _agent = new Agent(_ai, _s, _pc);
        _pc.Cursor = _cursor;
        _ = _cursor.Handle;
        Text = "Colo";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _frame.Tick += (_, _) => Frame();
        _agent.Event += ev => { if (IsHandleCreated) BeginInvoke(() => OnAgent(ev)); };
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
        await StartListeningAsync();
    }

    bool Configured() =>
        !string.IsNullOrWhiteSpace(_s.ApiKey) && !string.IsNullOrWhiteSpace(_s.Model)
        && (_s.Provider == Provider.OpenAI || !string.IsNullOrWhiteSpace(_s.Endpoint))
        && (_s.SpeechEngine == SpeechEngine.Local || !string.IsNullOrWhiteSpace(_s.TranscribeModel));

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

    async Task<bool> EnsureSpeechAsync()
    {
        var needAsr = _s.SpeechEngine == SpeechEngine.Local;
        if (!_models.Ready(needAsr))
        {
            _mode = Mode.Starting;
            var progress = new Progress<double>(p => Say($"Preparando la voz · {p:P0}", Theme.Muted, 3600));
            try { await _models.DownloadAsync(needAsr, progress, CancellationToken.None); }
            catch (Exception ex)
            {
                Problem("No pude bajar la voz: " + ex.Message);
                return false;
            }
        }
        try
        {
            if (needAsr && _stt is null)
            {
                _mode = Mode.Starting;
                _stt = await Task.Run(() => new LocalStt(_models));
            }
            _listener ??= new Listener(_models.Silero, _s.Sensitivity);
            Say("", Theme.Muted, 0);
            return true;
        }
        catch (Exception ex)
        {
            Problem("Voz local: " + ex.Message);
            return false;
        }
    }

    async Task StartListeningAsync()
    {
        if (_listening) return;
        if (!Configured()) { OpenSettings(); return; }
        if (!await EnsureSpeechAsync()) return;
        var l = _listener!;
        l.Utterance -= OnUtterance;
        l.Utterance += OnUtterance;
        l.SpeakingChanged -= OnSpeaking;
        l.SpeakingChanged += OnSpeaking;
        l.Level -= OnLevel;
        l.Level += OnLevel;
        _speaker ??= TryCreateSpeaker();
        if (_speaker is not null) _speaker.Listener = l;
        try { _mic.Start(_s.MicDevice, l); }
        catch (Exception ex)
        {
            Problem("Micrófono: " + ex.Message);
            return;
        }
        _listening = true;
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

    void OnLevel(double rms) => _target = Math.Clamp(Math.Sqrt(rms) * 3.4, 0, 1);

    void OnSpeaking(bool speaking)
    {
        if (IsHandleCreated) BeginInvoke(() => _hearing = speaking && _listening);
    }

    void OnUtterance(float[] samples)
    {
        if (!_settingsOpen) _phrases.Writer.TryWrite(samples);
    }

    async Task PhraseLoopAsync()
    {
        await foreach (var samples in _phrases.Reader.ReadAllAsync())
        {
            try
            {
                string text;
                if (_s.SpeechEngine == SpeechEngine.Local)
                {
                    if (_stt is null) continue;
                    text = _stt.Transcribe(samples);
                }
                else
                {
                    text = await _ai.TranscribeAsync(Wav.Encode(samples), CancellationToken.None);
                }
                if (text.Length < 2) continue;
                BeginInvoke(() => Command(text));
            }
            catch (Exception ex)
            {
                BeginInvoke(() => Say(ex.Message, Theme.Danger, 5000));
            }
        }
    }

    void Command(string text)
    {
        if (!Configured()) { OpenSettings(); return; }
        Say("«" + text + "»", Theme.Ink, 1800 + text.Length * 45);
        _mode = Mode.Busy;
        SetClickThroughIfBusy();
        _ = _agent.Hear(text);
    }

    void OnAgent(AgentEvent ev)
    {
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
        try
        {
            using var f = new SettingsForm(_s);
            if (f.ShowDialog() != DialogResult.OK) return;
            Store.Save(_s);
            ApplyAutostart();
            StopListening();
            _listener?.Dispose();
            _listener = null;
            if (_s.SpeechEngine == SpeechEngine.Api) { _stt?.Dispose(); _stt = null; }
            if (Configured())
            {
                Say("", Theme.Muted, 0);
                _ = StartListeningAsync();
            }
            else Problem("Falta la llave o el modelo");
        }
        finally { _settingsOpen = false; }
    }

    void BuildMenu()
    {
        _menu.Font = Theme.Text(9.5f);
        _menu.ShowImageMargin = false;
        _pauseItem = new ToolStripMenuItem("Pausar", null, (_, _) => ToggleListening());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add("Detener lo que hace", null, (_, _) => _agent.Stop());
        _menu.Items.Add("Olvidar contexto", null, (_, _) => { _agent.ForgetContext(); Say("Contexto olvidado", Theme.Muted, 1500); });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Ajustes…", null, (_, _) => OpenSettings());
        _menu.Items.Add("Salir", null, (_, _) => Close());

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
            Mode.Listening => speaking ? 0.25 + _target * 0.75 : 0.10 + _target * 0.25,
            Mode.Busy => speaking ? 0.3 + _target * 0.7 : 0.38,
            Mode.Starting => 0.16,
            _ => 0,
        };
        _level += (goal - _level) * (goal > _level ? 0.35 : 0.12);
        _energy += ((_mode == Mode.Busy ? 1 : 0) - _energy) * 0.06;
        // En pausa y sin nada que mostrar, la onda queda quieta: se redibuja poco.
        var still = _mode is Mode.Paused or Mode.Problem && _level < 0.01 && _caption.Length == 0;
        _frame.Interval = still ? 120 : 16;
        Render();
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
        _tray.Visible = false;
        _tray.Dispose();
        try { Store.Save(_s); } catch { }
        base.OnFormClosing(e);
    }
}
