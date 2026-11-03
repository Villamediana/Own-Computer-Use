using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Colo.Core;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Colo;

/// <summary>
/// Graba una reunión: el micrófono (lo que dice él) en el canal izquierdo y el sonido del PC (los demás) en el
/// derecho, a 16 kHz, en un WAV que se va cerrando cada pocos segundos (si algo se cae, lo grabado queda). Solo
/// graba: transcribir y el acta van después, en segundo plano. Módulo aparte: si se quita, nada más cambia.
/// </summary>
sealed class Recorder : IDisposable
{
    const int Rate = MeetingAudio.Rate;

    WasapiCapture? _mic;
    WasapiLoopbackCapture? _pc;
    Downsampler? _micRs, _pcRs;
    readonly List<float> _micBuf = [], _pcBuf = [];
    readonly object _lock = new();
    readonly Stopwatch _clock = new();
    WaveFileWriter? _writer;
    System.Threading.Timer? _tick;
    long _written;
    int _ticks;

    public RecState State { get; private set; } = RecState.Idle;
    public string Dir { get; private set; } = "";
    public DateTime Started { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Colo", "Reuniões");

    public void Start(string micId)
    {
        if (State != RecState.Idle) return;
        Started = DateTime.Now;
        Dir = Path.Combine(Root, Started.ToString("yyyy-MM-dd HHmm"));
        if (Directory.Exists(Dir)) Dir += Started.ToString("ss");
        Directory.CreateDirectory(Dir);
        try { Open(micId); }
        catch
        {
            _mic?.Dispose(); _mic = null;
            _pc?.Dispose(); _pc = null;
            _writer?.Dispose(); _writer = null;
            try { Directory.Delete(Dir, true); } catch { }
            throw;
        }
    }

    void Open(string micId)
    {
        _writer = new WaveFileWriter(Path.Combine(Dir, "audio.wav"), new WaveFormat(Rate, 16, 2));
        using var en = new MMDeviceEnumerator();
        MMDevice? mic = null;
        if (micId.Length > 0) try { mic = en.GetDevice(micId); } catch { }
        mic ??= en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        _mic = new WasapiCapture(mic, true, 50);
        _micRs = new Downsampler(_mic.WaveFormat.SampleRate);
        _mic.DataAvailable += (_, e) => Feed(e, _mic.WaveFormat, _micRs, _micBuf);
        try
        {
            _pc = new WasapiLoopbackCapture();
            _pcRs = new Downsampler(_pc.WaveFormat.SampleRate);
            _pc.DataAvailable += (_, e) => Feed(e, _pc.WaveFormat, _pcRs, _pcBuf);
        }
        catch (Exception ex) { Log.Write("reunión: sin sonido del PC: " + ex.Message); _pc = null; }

        State = RecState.Recording;
        _clock.Restart();
        _mic.StartRecording();
        _pc?.StartRecording();
        _tick = new System.Threading.Timer(_ => Tick(), null, 100, 100);
        Log.Write($"reunión: grabando en {Dir} (mic «{mic.FriendlyName}»)");
    }

    void Feed(WaveInEventArgs e, WaveFormat f, Downsampler rs, List<float> buf)
    {
        if (State != RecState.Recording || e.BytesRecorded == 0) return;
        var mono = Downsampler.Mono(e.Buffer, e.BytesRecorded, f);
        lock (_lock) rs.Feed(mono, buf);
    }

    /// <summary>
    /// Escribe hasta donde marca el reloj: el canal que no trajo datos (el PC en silencio no manda nada) se completa
    /// con silencio, así los dos siguen alineados.
    /// </summary>
    void Tick()
    {
        lock (_lock)
        {
            if (State != RecState.Recording || _writer is null) return;
            var target = (long)(_clock.Elapsed.TotalSeconds * Rate);
            var n = (int)Math.Min(target - _written, Rate * 2);
            if (n <= 0) return;
            // Relojes de dispositivos distintos se desvían un poco: si un canal acumula más de 1 s, se recorta.
            Trim(_micBuf, n);
            Trim(_pcBuf, n);
            var frame = new byte[n * 4];
            for (var i = 0; i < n; i++)
            {
                var l = i < _micBuf.Count ? _micBuf[i] : 0f;
                var r = i < _pcBuf.Count ? _pcBuf[i] : 0f;
                BitConverter.TryWriteBytes(frame.AsSpan(i * 4), (short)Math.Clamp(l * 32767f, -32768f, 32767f));
                BitConverter.TryWriteBytes(frame.AsSpan(i * 4 + 2), (short)Math.Clamp(r * 32767f, -32768f, 32767f));
            }
            _micBuf.RemoveRange(0, Math.Min(n, _micBuf.Count));
            _pcBuf.RemoveRange(0, Math.Min(n, _pcBuf.Count));
            _writer.Write(frame, 0, frame.Length);
            _written += n;
            if (++_ticks % 30 == 0) _writer.Flush();
        }
    }

    static void Trim(List<float> buf, int n)
    {
        if (buf.Count > n + Rate) buf.RemoveRange(0, buf.Count - n - Rate / 5);
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (State != RecState.Recording) return;
            Tick();
            State = RecState.Paused;
            _clock.Stop();
            _micBuf.Clear();
            _pcBuf.Clear();
            _writer?.Flush();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (State != RecState.Paused) return;
            State = RecState.Recording;
            _clock.Start();
        }
    }

    /// <summary>Cierra el archivo; devuelve los segundos grabados.</summary>
    public double Stop()
    {
        if (State == RecState.Idle) return 0;
        lock (_lock)
        {
            if (State == RecState.Recording) Tick();
            State = RecState.Idle;
            _clock.Stop();
        }
        _tick?.Dispose();
        try { _mic?.StopRecording(); } catch { }
        try { _pc?.StopRecording(); } catch { }
        _mic?.Dispose();
        _pc?.Dispose();
        lock (_lock) { _writer?.Dispose(); _writer = null; }
        var seconds = _written / (double)Rate;
        Log.Write($"reunión: parada, {seconds:0} s en {Dir}");
        return seconds;
    }

    public void Dispose() => Stop();

    /// <summary>WAV → AAC (.m4a, ~20 MB por hora) con Media Foundation de Windows. Pide 48 kHz: se remuestrea.</summary>
    public static bool Encode(string wav, string m4a)
    {
        MediaFoundationApi.Startup();
        using var reader = new WaveFileReader(wav);
        using var resampled = new MediaFoundationResampler(reader, new WaveFormat(48000, 16, 2)) { ResamplerQuality = 60 };
        MediaFoundationEncoder.EncodeToAac(resampled, m4a, 64000);
        return File.Exists(m4a);
    }
}

/// <summary>
/// Pastilla pequeña mientras graba: ● y el tiempo, y dos botones a la vista: pausar/seguir y parar (guarda la
/// reunión). No toma el foco.
/// </summary>
sealed class RecordPill : Form
{
    readonly Func<(RecState State, TimeSpan Elapsed)> _read;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    readonly ToolTip _tip = new();
    int _hover = -1; // 0 = pausar/seguir, 1 = parar
    public event Action? Toggle;
    public event Action? StopAsked;

    public RecordPill(Func<(RecState, TimeSpan)> read)
    {
        _read = read;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        DoubleBuffered = true;
        _timer.Tick += (_, _) => Invalidate();
        MouseMove += (_, e) => SetHover(Hit(e.Location));
        MouseLeave += (_, _) => SetHover(-1);
        MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            switch (Hit(e.Location))
            {
                case 0: Toggle?.Invoke(); break;
                case 1: StopAsked?.Invoke(); break;
            }
        };
    }

    float S => DeviceDpi / 96f;
    RectangleF Button(int i) => new(Width - (i == 0 ? 66 : 36) * S, (Height - 28 * S) / 2, 28 * S, 28 * S);

    int Hit(Point p) => Button(1).Contains(p) ? 1 : Button(0).Contains(p) ? 0 : -1;

    void SetHover(int h)
    {
        if (h == _hover) return;
        _hover = h;
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        var paused = _read().State == RecState.Paused;
        _tip.SetToolTip(this, h switch { 0 => paused ? "Seguir grabando" : "Pausar", 1 => "Parar y guardar la reunión", _ => "" });
        Invalidate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public void ShowNear(Rectangle widget)
    {
        var s = S;
        Size = new Size((int)(172 * s), (int)(36 * s));
        var wa = Screen.FromRectangle(widget).WorkingArea;
        Location = new Point(Math.Clamp(widget.X + (widget.Width - Width) / 2, wa.Left, wa.Right - Width),
            Math.Max(wa.Top, widget.Top - Height - (int)(6 * s)));
        using var path = Round(new Rectangle(Point.Empty, Size), Height / 2);
        Region = new Region(path);
        _timer.Start();
        Show();
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { _timer.Dispose(); _tip.Dispose(); base.OnFormClosed(e); }

    static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var (state, elapsed) = _read();
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var s = S;
        using (var border = new Pen(Color.FromArgb(228, 228, 231), 1))
        using (var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
            g.DrawPath(border, path);
        var cy = Height / 2f;
        var recording = state == RecState.Recording;
        // El punto late despacio mientras graba; en pausa queda gris y quieto.
        var alpha = recording ? (int)(170 + 85 * Math.Sin(Environment.TickCount64 / 300.0)) : 255;
        using (var dot = new SolidBrush(recording ? Color.FromArgb(alpha, 220, 38, 38) : Color.FromArgb(180, 180, 186)))
            g.FillEllipse(dot, 14 * s, cy - 5 * s, 10 * s, 10 * s);
        var text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        using (var font = new Font("Segoe UI Semibold", 10.5f))
        using (var ink = new SolidBrush(recording ? Color.FromArgb(17, 17, 19) : Color.FromArgb(132, 132, 140)))
        {
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, ink, 30 * s, cy - size.Height / 2);
        }

        for (var i = 0; i < 2; i++)
        {
            var b = Button(i);
            if (_hover == i)
                using (var bg = new SolidBrush(Color.FromArgb(241, 241, 244)))
                    g.FillEllipse(bg, b);
            var c = new PointF(b.X + b.Width / 2, b.Y + b.Height / 2);
            if (i == 0 && recording)
                using (var bars = new SolidBrush(Color.FromArgb(60, 60, 67)))
                {
                    g.FillRectangle(bars, c.X - 4.5f * s, c.Y - 5.5f * s, 3.2f * s, 11 * s);
                    g.FillRectangle(bars, c.X + 1.3f * s, c.Y - 5.5f * s, 3.2f * s, 11 * s);
                }
            else if (i == 0)
                using (var play = new SolidBrush(Color.FromArgb(220, 38, 38)))
                    g.FillPolygon(play, new PointF[] { new(c.X - 3.5f * s, c.Y - 6 * s), new(c.X + 6 * s, c.Y), new(c.X - 3.5f * s, c.Y + 6 * s) });
            else
                using (var sq = new SolidBrush(Color.FromArgb(17, 17, 19)))
                using (var path = Round(new RectangleF(c.X - 5 * s, c.Y - 5 * s, 10 * s, 10 * s), 2 * s))
                    g.FillPath(sq, path);
        }
    }
}
