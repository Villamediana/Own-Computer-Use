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
/// Pastilla pequeña mientras graba: ● y el tiempo (‖ en pausa). Clic = pausa o sigue; clic derecho = parar y guardar.
/// No toma el foco.
/// </summary>
sealed class RecordPill : Form
{
    readonly Func<(RecState State, TimeSpan Elapsed)> _read;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
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
        Cursor = Cursors.Hand;
        _timer.Tick += (_, _) => Invalidate();
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) StopAsked?.Invoke(); else Toggle?.Invoke(); };
        var tip = new ToolTip();
        tip.SetToolTip(this, "Clic: pausar o seguir · clic derecho: parar y guardar");
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
        var s = DeviceDpi / 96f;
        Size = new Size((int)(128 * s), (int)(34 * s));
        var wa = Screen.FromRectangle(widget).WorkingArea;
        Location = new Point(Math.Clamp(widget.X + (widget.Width - Width) / 2, wa.Left, wa.Right - Width),
            Math.Max(wa.Top, widget.Top - Height - (int)(6 * s)));
        using var path = Round(new Rectangle(Point.Empty, Size), Height / 2);
        Region = new Region(path);
        _timer.Start();
        Show();
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { _timer.Dispose(); base.OnFormClosed(e); }

    static GraphicsPath Round(Rectangle r, int radius)
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
        var s = DeviceDpi / 96f;
        using (var border = new Pen(Color.FromArgb(228, 228, 231), 1))
        using (var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2))
            g.DrawPath(border, path);
        var cy = Height / 2f;
        var recording = state == RecState.Recording;
        // El punto late despacio mientras graba.
        var alpha = recording ? (int)(170 + 85 * Math.Sin(Environment.TickCount64 / 300.0)) : 255;
        if (recording)
            using (var dot = new SolidBrush(Color.FromArgb(alpha, 220, 38, 38)))
                g.FillEllipse(dot, 14 * s, cy - 5 * s, 10 * s, 10 * s);
        else
            using (var bars = new SolidBrush(Color.FromArgb(132, 132, 140)))
            {
                g.FillRectangle(bars, 14 * s, cy - 5 * s, 3.5f * s, 10 * s);
                g.FillRectangle(bars, 20 * s, cy - 5 * s, 3.5f * s, 10 * s);
            }
        var text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        if (!recording) text += "  pausa";
        using var font = new Font("Segoe UI Semibold", 10.5f);
        using var ink = new SolidBrush(recording ? Color.FromArgb(17, 17, 19) : Color.FromArgb(132, 132, 140));
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, ink, 32 * s, cy - size.Height / 2);
    }
}
