using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;
using static Colo.Native;

namespace Colo;

/// <summary>
/// El cursor de Colo: flecha azul con una etiqueta de lo que está haciendo. Se desliza hasta el
/// objetivo antes de cada clic. No recibe clics y no aparece en las capturas que ve el modelo.
/// </summary>
/// <summary>Ventana con transparencia por píxel, que no recibe clics ni aparece en capturas.</summary>
abstract class LayeredWindow : Form
{
    protected LayeredWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>true = los clics atraviesan la ventana.</summary>
    protected virtual bool ClickThrough => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            if (ClickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <summary>Handle guardado para usarlo desde otros hilos (SetWindowPos).</summary>
    protected IntPtr Hwnd { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Hwnd = Handle;
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    protected void Present(Bitmap bmp, Point topLeft, byte alpha = 255)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbmp);
        try
        {
            var pos = new POINT { x = topLeft.X, y = topLeft.Y };
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { Invoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { } }
        else a();
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

sealed class AiCursor : LayeredWindow
{
    const int Pad = 26;                       // margen alrededor de la punta para la onda del clic
    const double FadeIn = 130, FadeOut = 220, PulseMs = 340;
    static readonly Color Blue = Color.FromArgb(37, 99, 235);
    static readonly Color BlueLight = Color.FromArgb(96, 165, 250);
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    // Estado tocado solo en el hilo del cursor (dentro de Ui).
    Point _tip;
    string _label = "";
    bool _visible, _hiding;
    double _shownAt, _hideAt, _pulseAt = -1;
    int _animating;

    float Dpi => DeviceDpi / 96f;
    static double Now => Clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// El cursor vive en su propio hilo de interfaz: así cada paso del deslizamiento se pinta al instante,
    /// sin esperar a que la onda termine su cuadro.
    /// </summary>
    public static AiCursor Create()
    {
        AiCursor? made = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            made = new AiCursor();
            _ = made.Handle;
            ready.Set();
            Application.Run();
        }) { IsBackground = true, Name = "Colo · cursor" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return made!;
    }

    /// <summary>Desliza el cursor (y el ratón) hasta p, en coordenadas físicas de pantalla. Bloquea lo que dure.</summary>
    public void Glide(Point p, string label)
    {
        var from = Point.Empty;
        Ui(() =>
        {
            _label = label;
            Appear();
            from = _tip;
            Frame();
        });
        Input.Glide(from, p, Follow);
    }

    /// <summary>Pone la punta en q sin cambiar nada más (cada paso de un deslizamiento o arrastre).</summary>
    public void Follow(Point q) => Ui(() =>
    {
        _tip = q;
        Frame();
    });

    /// <summary>Cambia la etiqueta sin moverse (escribir, teclas, print…).</summary>
    public void Say(string label) => Ui(() =>
    {
        _label = label;
        Appear();
        Frame();
    });

    /// <summary>Marca el clic: la flecha se hunde un poco y sale una onda de la punta. No bloquea.</summary>
    public void Pulse() => Ui(() =>
    {
        _pulseAt = Now;
        Animate();
    });

    /// <summary>Se desvanece; si vuelve a hacer algo antes de acabar, reaparece desde donde estaba.</summary>
    public void Away() => Ui(() =>
    {
        if (!_visible || _hiding) return;
        _hiding = true;
        _hideAt = Now - (1 - Alpha()) * FadeOut;
        Animate();
    });

    void Appear()
    {
        if (!_visible)
        {
            GetCursorPos(out var cur);
            _tip = new Point(cur.x, cur.y);
            _visible = true;
            _shownAt = Now;
            Show();
        }
        else if (_hiding)
        {
            var a = Alpha();
            _hiding = false;
            _shownAt = Now - a * FadeIn;
        }
        // Encima de todo, también de lo que se abrió después (barra de tareas, menús).
        SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Animate();
    }

    double Alpha()
    {
        var a = _hiding ? 1 - (Now - _hideAt) / FadeOut : (Now - _shownAt) / FadeIn;
        return Math.Clamp(a, 0, 1);
    }

    /// <summary>Pinta el estado de ahora. Devuelve si queda animación (aparecer, desvanecerse, onda del clic).</summary>
    bool Frame()
    {
        if (!_visible) return false;
        var a = Alpha();
        if (_hiding && a <= 0)
        {
            _visible = _hiding = false;
            _pulseAt = -1;
            Hide();
            return false;
        }
        float ripple = 0, rippleAlpha = 0, press = 1;
        if (_pulseAt >= 0)
        {
            var t = (Now - _pulseAt) / PulseMs;
            if (t >= 1) _pulseAt = -1;
            else
            {
                var o = 1 - Math.Pow(1 - t, 3);
                ripple = (float)(5 + 19 * o);
                rippleAlpha = (float)(1 - o);
                press = (float)(1 - 0.12 * Math.Sin(Math.PI * Math.Min(t * 1.8, 1)));
            }
        }
        var eased = a * a * (3 - 2 * a);
        using var bmp = Draw(_label, ripple, rippleAlpha, Dpi, press);
        var s = Dpi;
        Present(bmp, new Point(_tip.X - (int)(Pad * s), _tip.Y - (int)(Pad * s)), (byte)Math.Round(255 * eased));
        return _pulseAt >= 0 || (_hiding ? a > 0 : a < 1);
    }

    /// <summary>Mantiene los cuadros al ritmo del monitor mientras haya algo animándose.</summary>
    void Animate()
    {
        if (Interlocked.Exchange(ref _animating, 1) == 1) return;
        new Thread(() =>
        {
            var more = true;
            while (more && !IsDisposed)
            {
                WaitFrame();
                Ui(() => more = Frame());
            }
            Volatile.Write(ref _animating, 0);
        }) { IsBackground = true, Name = "Colo · cursor anim" }.Start();
    }

    /// <summary>Flecha con etiqueta; la punta queda en (Pad, Pad)·s.</summary>
    public static Bitmap Draw(string _label, float ripple, float rippleAlpha, float s, float press = 1)
    {
        const float k = 1.4f;                 // tamaño de la flecha respecto al cursor normal
        using var font = Theme.Semibold(9.5f * s);
        var label = _label.Length > 44 ? _label[..44] + "…" : _label;
        SizeF ts = SizeF.Empty;
        if (label.Length > 0)
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
                ts = pg.MeasureString(label, font);
        var pad = Pad * s;
        var tagX = pad + 21 * k * s;
        var tagY = pad + 19 * k * s;
        var tagW = ts.Width + 16 * s;
        var tagH = ts.Height + 6 * s;
        var w = (int)(label.Length > 0 ? tagX + tagW + 10 * s : pad + 40 * s);
        var h = (int)(label.Length > 0 ? tagY + tagH + 10 * s : pad + 46 * s);

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        if (rippleAlpha > 0)
        {
            using var ring = new Pen(Color.FromArgb((int)(200 * rippleAlpha), BlueLight), 2.4f * s);
            var r = ripple * s;
            g.DrawEllipse(ring, pad - r, pad - r, r * 2, r * 2);
        }

        using var arrow = ArrowPath(pad, pad, s * k * press);   // al clicar se hunde un poco hacia la punta
        for (var i = 4; i >= 1; i--)
        {
            using var halo = new Pen(Color.FromArgb(18, Blue), i * 2.2f * s) { LineJoin = LineJoin.Round };
            g.DrawPath(halo, arrow);
        }
        using (var fill = new LinearGradientBrush(new PointF(pad, pad), new PointF(pad + 14 * k * s * press, pad + 22 * k * s * press), BlueLight, Blue))
            g.FillPath(fill, arrow);
        using (var edge = new Pen(Color.White, 1.8f * s) { LineJoin = LineJoin.Round })
            g.DrawPath(edge, arrow);

        if (label.Length > 0)
        {
            var tag = new RectangleF(tagX, tagY, tagW, tagH);
            using (var shadow = new SolidBrush(Color.FromArgb(40, 15, 23, 42)))
            using (var sp = Rounded(new RectangleF(tag.X, tag.Y + 1.5f * s, tag.Width, tag.Height), tag.Height / 2))
                g.FillPath(shadow, sp);
            using (var bg = new SolidBrush(Blue))
            using (var path = Rounded(tag, tag.Height / 2))
                g.FillPath(bg, path);
            g.DrawString(label, font, Brushes.White, tag.X + 8 * s, tag.Y + 3 * s);
        }
        return bmp;
    }

    static GraphicsPath ArrowPath(float x, float y, float s)
    {
        var p = new GraphicsPath();
        p.AddPolygon(new[]
        {
            new PointF(x, y),
            new PointF(x, y + 19 * s),
            new PointF(x + 4.6f * s, y + 14.8f * s),
            new PointF(x + 8.2f * s, y + 22.2f * s),
            new PointF(x + 11.2f * s, y + 20.8f * s),
            new PointF(x + 7.7f * s, y + 13.6f * s),
            new PointF(x + 13.6f * s, y + 13.6f * s),
        });
        return p;
    }
}
