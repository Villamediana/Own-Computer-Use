using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;
using Colo.Core;
using static Colo.Native;

namespace Colo;

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

/// <summary>
/// El cursor de Colo: flecha azul con una etiqueta de lo que está haciendo. Se desliza hasta el
/// objetivo antes de cada clic. No recibe clics y no aparece en las capturas que ve el modelo.
/// Todo lo que cambia entra con muelle: aparece con rebote, la etiqueta se estira al texto nuevo,
/// el clic hunde la flecha y suelta ondas y chispas.
/// </summary>
sealed class AiCursor : LayeredWindow
{
    const int Pad = 34;                       // margen alrededor de la punta para ondas y chispas
    const float K = 1.4f;                     // tamaño de la flecha respecto al cursor normal
    const double FadeIn = 120, FadeOut = 240, ClickMs = 560, PressMs = 70;
    static readonly Color Blue = Color.FromArgb(37, 99, 235);
    static readonly Color BlueDeep = Color.FromArgb(29, 78, 216);
    static readonly Color BlueMid = Color.FromArgb(59, 130, 246);
    static readonly Color BlueLight = Color.FromArgb(96, 165, 250);
    static readonly Color Sky = Color.FromArgb(56, 189, 248);
    static readonly Color Navy = Color.FromArgb(15, 23, 42);
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    // Estado tocado solo en el hilo del cursor (dentro de Ui).
    Point _tip;
    string _label = "", _prevLabel = "";
    bool _visible, _hiding;
    double _shownAt, _hideAt, _labelAt = -1e9;
    readonly List<double> _clicks = [];
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
            Appear();
            SetLabel(label);
            from = _tip;
            Frame();
        });
        Input.Glide(from, p, Follow);
    }

    /// <summary>Pone la punta en q sin cambiar nada más (cada paso de un deslizamiento o arrastre).</summary>
    public void Follow(Point q) => Ui(() =>
    {
        _tip = q;
        // Si nada cambia de aspecto, basta mover la ventana: repintar la capa entera cada paso esperaba
        // al compositor y el deslizamiento perdía cuadros. Con algo animándose, lo pinta Animate.
        if (!_visible || Volatile.Read(ref _animating) == 1) return;
        var s = Dpi;
        SetWindowPos(Hwnd, HWND_TOPMOST, q.X - (int)(Pad * s), q.Y - (int)(Pad * s), 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    });

    /// <summary>Cambia la etiqueta sin moverse (escribir, teclas, print…).</summary>
    public void Say(string label) => Ui(() =>
    {
        Appear();
        SetLabel(label);
        Frame();
    });

    /// <summary>Marca el clic (dos veces si es doble): la flecha se hunde y rebota, salen ondas y chispas. No bloquea.</summary>
    public void Pulse(bool twice = false) => Ui(() =>
    {
        if (!_visible) return;
        _clicks.Add(Now);
        if (twice) _clicks.Add(Now + 120);
        Animate();
    });

    /// <summary>Se desvanece encogiéndose; si vuelve a hacer algo antes de acabar, reaparece desde donde estaba.</summary>
    public void Away() => Ui(() =>
    {
        if (!_visible || _hiding) return;
        _hiding = true;
        _hideAt = Now - (1 - Alpha()) * FadeOut;
        Animate();
    });

    void SetLabel(string label)
    {
        if (label == _label) return;
        _prevLabel = _label;
        _label = label;
        _labelAt = Now;
        Animate();
    }

    void Appear()
    {
        if (!_visible)
        {
            GetCursorPos(out var cur);
            _tip = new Point(cur.x, cur.y);
            _visible = true;
            _shownAt = Now;
            _label = _prevLabel = "";
            _clicks.Clear();
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

    /// <summary>Cómo se ve en el instante t (ms) de cada cosa; separado para dibujarlo igual en las pruebas.</summary>
    public struct Look
    {
        public float Arrow;          // escala de la flecha: rebote al aparecer, se hunde y rebota al clicar
        public float TagScale;       // la etiqueta brota desde la flecha
        public float TagAlpha;
        public float TagWidth;       // ancho de la etiqueta mientras se estira al texto nuevo (≤0 = el del texto)
        public string Prev;          // texto que se va
        public float Mix;            // 0 = solo el anterior, 1 = solo el nuevo
        public float[] Clicks;       // progreso 0–1 de cada clic en curso

        public static Look Still => new() { Arrow = 1, TagScale = 1, TagAlpha = 1, Prev = "", Mix = 1, Clicks = [] };
    }

    /// <summary>Calcula el aspecto de ahora; devuelve si algo sigue moviéndose.</summary>
    Look Compute(double now, float s, out bool moving)
    {
        var look = Look.Still;
        moving = false;

        // Aparece desde pequeña con un rebote; al irse se encoge un poco.
        var sinceShown = now - _shownAt;
        look.Arrow = (float)Spring.Lerp(0.35, 1, sinceShown, 420, 0.48);
        moving |= !Spring.Settled(sinceShown, 420, 0.48);
        if (_hiding) look.Arrow *= (float)(1 - 0.3 * (1 - Alpha()));

        // Clic: se hunde rápido hacia la punta y vuelve pasándose un poco.
        var live = new List<float>();
        _clicks.RemoveAll(c => now - c > ClickMs + 400);
        foreach (var c in _clicks)
        {
            var ms = now - c;
            if (ms < 0) { moving = true; continue; }
            float press;
            if (ms < PressMs) { var u = ms / PressMs; press = (float)(1 - 0.17 * u * (2 - u)); }
            else press = (float)Spring.Lerp(0.83, 1, ms - PressMs, 340, 0.36);
            look.Arrow *= press;
            if (ms < ClickMs) live.Add((float)(ms / ClickMs));
            moving |= ms < ClickMs || !Spring.Settled(ms - PressMs, 340, 0.36);
        }
        look.Clicks = [.. live];

        // Etiqueta: brota si no había, y si cambia se estira al ancho nuevo mientras el texto se cruza.
        var sinceLabel = now - _labelAt;
        if (_label.Length > 0)
        {
            if (_prevLabel.Length == 0)
            {
                look.TagScale = (float)Spring.Lerp(0.4, 1, sinceLabel, 440, 0.5);
                look.TagAlpha = (float)Math.Clamp(sinceLabel / 110, 0, 1);
                look.Prev = "";
                moving |= !Spring.Settled(sinceLabel, 440, 0.5);
            }
            else
            {
                var w0 = TagWidth(_prevLabel, s);
                var w1 = TagWidth(_label, s);
                look.TagWidth = (float)Spring.Lerp(w0, w1, sinceLabel, 400, 0.62);
                look.Mix = (float)Math.Clamp(sinceLabel / 170, 0, 1);
                look.Prev = look.Mix < 1 ? _prevLabel : "";
                look.TagScale = (float)(1 - 0.06 * Math.Exp(-sinceLabel / 90) * Math.Cos(sinceLabel / 60));
                moving |= !Spring.Settled(sinceLabel, 400, 0.62);
            }
        }
        moving |= _hiding ? Alpha() > 0 : Alpha() < 1;
        return look;
    }

    /// <summary>Pinta el estado de ahora. Devuelve si queda animación.</summary>
    bool Frame()
    {
        if (!_visible) return false;
        var a = Alpha();
        if (_hiding && a <= 0)
        {
            _visible = _hiding = false;
            _clicks.Clear();
            Hide();
            return false;
        }
        var s = Dpi;
        var look = Compute(Now, s, out var moving);
        var eased = a * a * (3 - 2 * a);
        using var bmp = Draw(_label, s, look);
        Present(bmp, new Point(_tip.X - (int)(Pad * s), _tip.Y - (int)(Pad * s)), (byte)Math.Round(255 * eased));
        return moving;
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
            // Lo último que pidió Follow mientras animaba: deja la ventana en la punta.
            Ui(() => { if (_visible) Frame(); });
        }) { IsBackground = true, Name = "Colo · cursor anim" }.Start();
    }

    // ───────────────────────── dibujo

    static string Clip(string label) => label.Length > 44 ? label[..44] + "…" : label;

    static float TagWidth(string label, float s)
    {
        if (label.Length == 0) return 0;
        using var font = Theme.Semibold(9.5f * s);
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        return g.MeasureString(Clip(label), font).Width + 18 * s;
    }

    /// <summary>Flecha quieta con etiqueta (para las capturas de prueba).</summary>
    public static Bitmap Draw(string label, float s) => Draw(label, s, Look.Still);

    /// <summary>Flecha con etiqueta; la punta queda en (Pad, Pad)·s.</summary>
    public static Bitmap Draw(string rawLabel, float s, Look look)
    {
        using var font = Theme.Semibold(9.5f * s);
        var label = Clip(rawLabel);
        var prev = Clip(look.Prev ?? "");
        SizeF ts = SizeF.Empty;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
            ts = pg.MeasureString(label.Length > 0 ? label : "Ag", font);
        var pad = Pad * s;
        var tagX = pad + 20 * K * s;
        var tagY = pad + 19.5f * K * s;
        var natural = label.Length > 0 ? ts.Width + 18 * s : 0;
        var tagW = look.TagWidth > 0 ? look.TagWidth : natural;
        var tagH = ts.Height + 7 * s;
        var hasTag = label.Length > 0 || prev.Length > 0;
        var w = (int)Math.Ceiling(hasTag ? Math.Max(tagX + Math.Max(tagW, natural) * 1.08f + 12 * s, pad * 2) : pad * 2 + 10 * s);
        var h = (int)Math.Ceiling(hasTag ? Math.Max(tagY + tagH * 1.08f + 12 * s, pad * 2) : pad * 2 + 30 * s);

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        foreach (var t in look.Clicks ?? []) DrawClick(g, pad, s, t);

        if (hasTag && look.TagAlpha > 0.01f) DrawTag(g, font, label, prev, look, tagX, tagY, tagW, tagH, s);
        DrawArrow(g, pad, s, look.Arrow);
        return bmp;
    }

    /// <summary>Onda doble que se abre desde la punta, un brillo suave y chispas que salen y se apagan.</summary>
    static void DrawClick(Graphics g, float pad, float s, float t)
    {
        var o = 1 - Math.Pow(1 - t, 3);
        var fade = 1 - t;

        var disk = (float)(5 + 9 * o) * s;
        using (var b = new SolidBrush(Color.FromArgb((int)(60 * fade * fade), BlueLight)))
            g.FillEllipse(b, pad - disk, pad - disk, disk * 2, disk * 2);

        var r1 = (float)(5 + 22 * o) * s;
        using (var ring = new Pen(Color.FromArgb((int)(210 * fade), BlueMid), (0.7f + 2.1f * fade) * s))
            g.DrawEllipse(ring, pad - r1, pad - r1, r1 * 2, r1 * 2);

        var t2 = Math.Clamp((t - 0.16) / 0.84, 0, 1);
        if (t2 > 0)
        {
            var o2 = 1 - Math.Pow(1 - t2, 3);
            var r2 = (float)(4 + 14 * o2) * s;
            using var ring2 = new Pen(Color.FromArgb((int)(150 * (1 - t2)), Sky), (0.6f + 1.4f * (float)(1 - t2)) * s);
            g.DrawEllipse(ring2, pad - r2, pad - r2, r2 * 2, r2 * 2);
        }

        // Chispas: ocho puntitos en abanico, los pares un poco más lejos, que frenan al salir.
        for (var i = 0; i < 8; i++)
        {
            var ang = (i * 45 + 22.5) * Math.PI / 180;
            var reach = (i % 2 == 0 ? 25 : 19) * s;
            var d = (float)(6 * s + reach * o);
            var r = (float)((i % 2 == 0 ? 2.0 : 1.5) * s * (1 - 0.6 * t));
            var c = i % 3 == 0 ? Sky : i % 3 == 1 ? BlueLight : BlueMid;
            using var dot = new SolidBrush(Color.FromArgb((int)(230 * Math.Pow(fade, 1.4)), c));
            g.FillEllipse(dot, pad + (float)Math.Cos(ang) * d - r, pad + (float)Math.Sin(ang) * d - r, r * 2, r * 2);
        }
    }

    static void DrawArrow(Graphics g, float pad, float s, float scale)
    {
        var k = K * Math.Max(scale, 0.05f);
        using var arrow = ArrowPath(pad, pad, s * k);

        // Sombra suave hacia abajo, en lugar de un halo: parece apoyada sobre la pantalla.
        using (var shadow = (GraphicsPath)arrow.Clone())
        {
            using var m = new Matrix();
            m.Translate(0.8f * s, 2.4f * s);
            shadow.Transform(m);
            for (var i = 5; i >= 1; i--)
            {
                using var soft = new Pen(Color.FromArgb(9, Navy), i * 1.7f * s) { LineJoin = LineJoin.Round };
                g.DrawPath(soft, shadow);
            }
            using var core = new SolidBrush(Color.FromArgb(34, Navy));
            g.FillPath(core, shadow);
        }
        // Un velo azul muy leve alrededor, para que se vea sobre fondos oscuros.
        using (var veil = new Pen(Color.FromArgb(22, BlueLight), 5f * s) { LineJoin = LineJoin.Round })
            g.DrawPath(veil, arrow);

        var top = new PointF(pad, pad);
        var bottom = new PointF(pad + 9 * k * s, pad + 22 * k * s);
        using (var fill = new LinearGradientBrush(top, bottom, BlueLight, BlueDeep))
        {
            fill.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(125, 183, 255), BlueMid, BlueDeep],
                Positions = [0f, 0.45f, 1f],
            };
            g.FillPath(fill, arrow);
        }

        // Brillo de vidrio en la mitad izquierda, que se apaga hacia abajo.
        var state = g.Save();
        g.SetClip(arrow);
        using (var sheen = new LinearGradientBrush(new PointF(pad, pad), new PointF(pad + 7 * k * s, pad + 16 * k * s),
                   Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
            g.FillPolygon(sheen, [new PointF(pad - 2 * s, pad - 2 * s), new PointF(pad + 9 * k * s, pad + 9 * k * s), new PointF(pad - 2 * s, pad + 17 * k * s)]);
        g.Restore(state);

        using (var edge = new Pen(Color.White, 1.9f * s) { LineJoin = LineJoin.Round })
            g.DrawPath(edge, arrow);
    }

    static void DrawTag(Graphics g, Font font, string label, string prev, Look look, float x, float y, float w, float h, float s)
    {
        var state = g.Save();
        // Brota desde la esquina que toca la flecha.
        g.TranslateTransform(x, y);
        g.ScaleTransform(look.TagScale, look.TagScale);
        g.TranslateTransform(-x, -y);
        var alpha = Math.Clamp(look.TagAlpha, 0, 1);
        int A(int v) => (int)(v * alpha);

        var tag = new RectangleF(x, y, Math.Max(w, h), h);
        for (var i = 3; i >= 1; i--)
        {
            var r = tag;
            r.Inflate(i * 0.9f * s, i * 0.6f * s);
            r.Offset(0, 1.6f * s + i * 0.5f * s);
            using var sb = new SolidBrush(Color.FromArgb(A(13), Navy));
            using var sp = Rounded(r, r.Height / 2);
            g.FillPath(sb, sp);
        }
        using (var path = Rounded(tag, tag.Height / 2))
        {
            using (var bg = new LinearGradientBrush(new PointF(0, tag.Top), new PointF(0, tag.Bottom),
                       Color.FromArgb(A(255), BlueMid), Color.FromArgb(A(255), Blue)))
                g.FillPath(bg, path);
            // Luz fina en el borde de arriba, como el canto de un cristal.
            var lit = g.Save();
            g.SetClip(new RectangleF(tag.X, tag.Y, tag.Width, tag.Height * 0.5f));
            using (var rim = new Pen(Color.FromArgb(A(70), 255, 255, 255), 1f * s))
                g.DrawPath(rim, path);
            g.Restore(lit);

            g.SetClip(path, CombineMode.Intersect);
            var tx = tag.X + 9 * s;
            var ty = tag.Y + 3.5f * s;
            var slide = (1 - look.Mix) * 5 * s;
            if (prev.Length > 0 && look.Mix < 1)
                using (var old = new SolidBrush(Color.FromArgb(A((int)(255 * (1 - look.Mix))), 255, 255, 255)))
                    g.DrawString(prev, font, old, tx, ty - (5 * s - slide));
            using (var ink = new SolidBrush(Color.FromArgb(A((int)(255 * look.Mix)), 255, 255, 255)))
                g.DrawString(label, font, ink, tx, ty + slide);
        }
        g.Restore(state);
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
