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
    static readonly Color Blue = Color.FromArgb(37, 99, 235);
    static readonly Color BlueLight = Color.FromArgb(96, 165, 250);

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

        using var arrow = ArrowPath(pad, pad, s * k);
        for (var i = 4; i >= 1; i--)
        {
            using var halo = new Pen(Color.FromArgb(18, Blue), i * 2.2f * s) { LineJoin = LineJoin.Round };
            g.DrawPath(halo, arrow);
        }
        using (var fill = new LinearGradientBrush(new PointF(pad, pad), new PointF(pad + 14 * k * s, pad + 22 * k * s), BlueLight, Blue))
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

    void Apply(Bitmap bmp)
    {
        var s = Dpi;
        Present(bmp, new Point(_tip.X - (int)(Pad * s), _tip.Y - (int)(Pad * s)));
    }
}
