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

    protected void Present(Bitmap bmp, Point topLeft)
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
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
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
    static readonly Color Blue = Color.FromArgb(37, 140, 235);
    static readonly Color Sky = Color.FromArgb(110, 196, 250);
    static readonly Color Deep = Color.FromArgb(29, 78, 216);

    Point _tip;
    string _label = "";
    bool _visible;

    float Dpi => DeviceDpi / 96f;

    /// <summary>Desliza el cursor hasta p (coordenadas físicas de pantalla). Bloquea ~0,1–0,2 s.</summary>
    public void Glide(Point p, string label)
    {
        if (!_visible)
        {
            GetCursorPos(out var cur);
            _tip = new Point(cur.x, cur.y);
        }
        _label = label;
        Ui(() => Render(0, 0));
        var from = _tip;
        var dist = Math.Sqrt(Math.Pow(p.X - from.X, 2) + Math.Pow(p.Y - from.Y, 2));
        var ms = Math.Clamp(dist / 5, 70, 190);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < ms)
        {
            var t = sw.Elapsed.TotalMilliseconds / ms;
            var k = 1 - Math.Pow(1 - t, 3);
            MoveTip(new Point((int)(from.X + (p.X - from.X) * k), (int)(from.Y + (p.Y - from.Y) * k)));
            Thread.Sleep(8);
        }
        MoveTip(p);
    }

    /// <summary>Cambia la etiqueta sin moverse (escribir, teclas, print…).</summary>
    public void Say(string label)
    {
        if (!_visible)
        {
            GetCursorPos(out var cur);
            _tip = new Point(cur.x, cur.y);
        }
        _label = label;
        Ui(() => Render(0, 0));
    }

    /// <summary>Onda corta en la punta, para marcar el clic.</summary>
    public void Pulse()
    {
        for (var i = 1; i <= 6; i++)
        {
            var t = i / 6f;
            Ui(() => Render(6 + 16 * t, 1 - t));
            Thread.Sleep(14);
        }
        Ui(() => Render(0, 0));
    }

    public void Away() => Ui(() =>
    {
        _visible = false;
        Hide();
    });

    void MoveTip(Point p)
    {
        _tip = p;
        var s = Dpi;
        if (Hwnd == IntPtr.Zero) return;
        SetWindowPos(Hwnd, HWND_TOPMOST, p.X - (int)(Pad * s), p.Y - (int)(Pad * s), 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void Render(float ripple, float rippleAlpha)
    {
        using var bmp = Draw(_label, ripple, rippleAlpha, Dpi);
        Apply(bmp);
        if (!_visible)
        {
            _visible = true;
            Show();
            MoveTip(_tip);
        }
    }

    /// <summary>Flecha con etiqueta; la punta queda en (Pad, Pad)·s.</summary>
    public static Bitmap Draw(string _label, float ripple, float rippleAlpha, float s)
    {
        using var font = Theme.Semibold(9.5f * s);
        var label = _label.Length > 44 ? _label[..44] + "…" : _label;
        SizeF ts = SizeF.Empty;
        if (label.Length > 0)
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
                ts = pg.MeasureString(label, font);
        var pad = Pad * s;
        var tagX = pad + 31 * s;
        var tagY = pad + 30 * s;
        var tagW = ts.Width + 16 * s;
        var tagH = ts.Height + 6 * s;
        var w = (int)(label.Length > 0 ? tagX + tagW + 14 * s : pad + 56 * s);
        var h = (int)(label.Length > 0 ? tagY + tagH + 14 * s : pad + 56 * s);

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        if (rippleAlpha > 0)
        {
            using var ring = new Pen(Color.FromArgb((int)(210 * rippleAlpha), Sky), 2.6f * s);
            var r = ripple * s;
            g.DrawEllipse(ring, pad - r, pad - r, r * 2, r * 2);
        }

        // flecha: punta arriba a la izquierda (es el punto exacto del clic)
        using var arrow = ArrowPath(pad, pad, s * 1.4f);
        for (var i = 5; i >= 1; i--)
        {
            using var glow = new Pen(Color.FromArgb(16, Sky), i * 3f * s) { LineJoin = LineJoin.Round };
            g.DrawPath(glow, arrow);
        }
        using (var round = new Pen(Deep, 3.2f * s) { LineJoin = LineJoin.Round })
            g.DrawPath(round, arrow);
        using (var fill = new LinearGradientBrush(new PointF(pad, pad), new PointF(pad + 26 * s, pad + 28 * s), Sky, Blue))
            g.FillPath(fill, arrow);
        using (var shine = new Pen(Color.FromArgb(150, 255, 255, 255), 1.1f * s) { LineJoin = LineJoin.Round })
            g.DrawPath(shine, arrow);

        if (label.Length > 0)
        {
            var tag = new RectangleF(tagX, tagY, tagW, tagH);
            for (var i = 4; i >= 1; i--)
            {
                var gr = tag;
                gr.Inflate(i * 1.6f * s, i * 1.6f * s);
                using var glow = new SolidBrush(Color.FromArgb(14, Sky));
                using var gp = Rounded(gr, 7 * s + i * 1.6f * s);
                g.FillPath(glow, gp);
            }
            using var path = Rounded(tag, 7 * s);
            using (var bg = new LinearGradientBrush(tag, Color.FromArgb(147, 204, 250), Color.FromArgb(110, 180, 244), 90f))
                g.FillPath(bg, path);
            using (var edge = new Pen(Color.FromArgb(70, 37, 99, 235), 1f * s))
                g.DrawPath(edge, path);
            using var ink = new SolidBrush(Color.FromArgb(12, 38, 92));
            g.DrawString(label, font, ink, tag.X + 8 * s, tag.Y + 3 * s);
        }
        return bmp;
    }

    /// <summary>Punta de flecha tipo navegación, con muesca atrás.</summary>
    static GraphicsPath ArrowPath(float x, float y, float s)
    {
        var p = new GraphicsPath();
        p.AddPolygon(new[]
        {
            new PointF(x, y),
            new PointF(x + 24 * s, y + 9.5f * s),
            new PointF(x + 12.5f * s, y + 12.5f * s),
            new PointF(x + 9.5f * s, y + 24 * s),
        });
        return p;
    }

    void Apply(Bitmap bmp)
    {
        var s = Dpi;
        Present(bmp, new Point(_tip.X - (int)(Pad * s), _tip.Y - (int)(Pad * s)));
    }
}
