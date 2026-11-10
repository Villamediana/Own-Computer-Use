using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using static Colo.Native;

namespace Colo;

/// <summary>
/// Menú de la onda (clic derecho y bandeja): tarjeta blanca redondeada con sombra, cabecera con el estado y
/// los tokens, iconos de Windows. Entra con un fundido y un leve deslizamiento; el resaltado sigue al ratón.
/// </summary>
sealed class PopMenu : LayeredWindow
{
    public enum Icon { Pause, Play, Stop, Settings, Power, Update }
    public sealed record Entry(string Text, Icon Glyph, Action Run, bool Danger = false, bool GapBefore = false);

    const float Pad = 18, Radius = 12, ItemH = 32, HeadH = 50, Gap = 7, MinW = 190, MaxW = 300;
    const int OpenMs = 170, CloseMs = 110;

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 15 };
    readonly Stopwatch _clock = new();
    List<Entry> _items = [];
    string _title = "", _detail = "";
    Color _dot = Theme.Blue;
    bool _upward, _closing;
    int _hover = -1;
    float _hoverY = -1, _hoverA;
    Action? _after;
    Point _anchor;
    // Ancho de la tarjeta: lo que pide el texto más largo (antes fijo, y un título largo salía cortado).
    float _w = MinW;

    // Segoe UI fija: con la «Variable» GDI+ mide mal y corta letras.
    static Font TitleFont() => new("Segoe UI Semibold", 9.75f);
    static Font DetailFont() => new("Segoe UI", 8.25f);
    static Font ItemFont() => new("Segoe UI", 9.5f);
    static readonly StringFormat Exact = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        Trimming = StringTrimming.EllipsisCharacter,
        LineAlignment = StringAlignment.Center,
    };

    float S => DeviceDpi / 96f;

    public PopMenu()
    {
        _tick.Tick += (_, _) => Frame();
        KeyPreview = true;
    }

    protected override bool ClickThrough => false;
    protected override bool ShowWithoutActivation => false;

    // Se activa para enterarse de un clic fuera (Deactivate) y del teclado.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle &= ~WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public bool IsOpen => Visible && !_closing;

    /// <summary>Contenido del menú, sin mostrarlo (también lo usa Preview para dibujarlo en un PNG).</summary>
    internal void Load(string title, string detail, Color dot, List<Entry> items, int hover = -1)
    {
        _items = items;
        _title = title;
        _detail = detail;
        _dot = dot;
        _hover = hover;
        _hoverY = hover >= 0 ? RowTop(hover) : -1;
        _hoverA = hover >= 0 ? 1 : 0;
        _closing = false;
        _after = null;
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var tf = TitleFont();
        using var df = DetailFont();
        using var itf = ItemFont();
        var need = Math.Max(30 + g.MeasureString(title, tf, 1000, Exact).Width, 30 + g.MeasureString(detail, df, 1000, Exact).Width);
        foreach (var it in items) need = Math.Max(need, 44 + g.MeasureString(it.Text, itf, 1000, Exact).Width);
        _w = Math.Clamp(need + 18, MinW, MaxW);
    }

    public void Open(Point at, string title, string detail, Color dot, List<Entry> items)
    {
        Load(title, detail, dot, items);
        var size = CanvasSize();
        var wa = Screen.FromPoint(at).WorkingArea;
        // Con la onda abajo, el menú sube desde el puntero; si no cabe, baja.
        _upward = at.Y - size.Height + Pad * S >= wa.Top;
        var x = (int)Math.Clamp(at.X - Pad * S, wa.Left, wa.Right - size.Width);
        var y = _upward ? (int)(at.Y - size.Height + Pad * S) : (int)(at.Y - Pad * S);
        _anchor = new Point(x, Math.Clamp(y, wa.Top - (int)(Pad * S), wa.Bottom - size.Height + (int)(Pad * S)));
        Bounds = new Rectangle(_anchor, size);
        _clock.Restart();
        Render();
        if (!Visible) Show();
        Activate();
        _tick.Start();
    }

    Size CanvasSize()
    {
        var gaps = _items.Count(i => i.GapBefore);
        var h = HeadH + _items.Count * ItemH + gaps * Gap + 10;
        return new Size((int)((_w + Pad * 2) * S), (int)((h + Pad * 2) * S));
    }

    void Dismiss(Action? then = null)
    {
        if (_closing) return;
        _closing = true;
        _after = then;
        _clock.Restart();
        _tick.Start();
    }

    void Frame()
    {
        var ms = _clock.Elapsed.TotalMilliseconds;
        if (_closing && ms >= CloseMs)
        {
            _tick.Stop();
            Hide();
            var then = _after;
            _after = null;
            then?.Invoke();
            return;
        }
        // El resaltado se desliza hasta la fila nueva.
        var target = _hover >= 0 ? RowTop(_hover) : _hoverY;
        _hoverY = _hoverY < 0 ? target : _hoverY + (target - _hoverY) * 0.35f;
        _hoverA += ((_hover >= 0 ? 1 : 0) - _hoverA) * 0.3f;
        Render();
        if (!_closing && ms > OpenMs && Math.Abs(target - _hoverY) < 0.3f && Math.Abs((_hover >= 0 ? 1 : 0) - _hoverA) < 0.02f)
            _tick.Stop();
    }

    float RowTop(int i)
    {
        var y = Pad + HeadH + 5;
        for (var k = 0; k <= i; k++)
        {
            if (_items[k].GapBefore) y += Gap;
            if (k < i) y += ItemH;
        }
        return y;
    }

    static float EaseOut(double t) => (float)(1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3));

    void Render()
    {
        var ms = _clock.Elapsed.TotalMilliseconds;
        using var bmp = Draw(_closing ? 1 - EaseOut(ms / CloseMs) : EaseOut(ms / OpenMs));
        Present(bmp, _anchor);
    }

    /// <summary>El menú en un momento de la animación: shown 0 = oculto, 1 = abierto del todo.</summary>
    internal Bitmap Draw(float shown)
    {
        var size = CanvasSize();
        var s = S;
        var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(s, s);
            // Entra deslizándose 8 px desde el lado del puntero y con una pizca de escala.
            var lift = (1 - shown) * 8 * (_upward ? 1 : -1);
            var scale = 0.97f + 0.03f * shown;
            var card = new RectangleF(Pad, Pad, _w, size.Height / s - Pad * 2);
            var pivotY = _upward ? card.Bottom : card.Top;
            g.TranslateTransform(card.Left, pivotY + lift);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-card.Left, -pivotY);

            var alpha = (int)(255 * shown);
            for (var i = 6; i >= 1; i--)
            {
                using var sp = Theme.Round(new RectangleF(card.X - i, card.Y - i + 4, card.Width + i * 2, card.Height + i * 2), Radius + i);
                using var sb = new SolidBrush(Color.FromArgb((int)(5 * shown), 15, 23, 42));
                g.FillPath(sb, sp);
            }
            using (var path = Theme.Round(card, Radius))
            {
                using var bg = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
                g.FillPath(bg, path);
                using var border = new Pen(Color.FromArgb((int)(28 * shown), 15, 23, 42), 1f / s);
                g.DrawPath(border, path);
            }

            // Cabecera: punto de estado, título y detalle (tokens).
            using (var dot = new SolidBrush(Color.FromArgb(alpha, _dot)))
                g.FillEllipse(dot, card.X + 15, card.Y + 15.5f, 7, 7);
            var textW = card.Width - 30 - 14;
            using (var f = TitleFont())
            using (var b = new SolidBrush(Color.FromArgb(alpha, Theme.Ink)))
                g.DrawString(_title, f, b, new RectangleF(card.X + 30, card.Y + 9, textW, 20), Exact);
            using (var f = DetailFont())
            using (var b = new SolidBrush(Color.FromArgb(alpha, Theme.Muted)))
                g.DrawString(_detail, f, b, new RectangleF(card.X + 30, card.Y + 27, textW, 16), Exact);
            using (var line = new Pen(Color.FromArgb((int)(255 * shown), 238, 238, 241), 1f / s))
                g.DrawLine(line, card.X + 14, card.Y + HeadH, card.Right - 14, card.Y + HeadH);

            if (_hoverY >= 0 && _hoverA > 0.01f)
            {
                using var hp = Theme.Round(new RectangleF(card.X + 5, _hoverY, _w - 10, ItemH), 8);
                using var hb = new SolidBrush(Color.FromArgb((int)(alpha * _hoverA), 243, 244, 246));
                g.FillPath(hb, hp);
            }

            using var text = ItemFont();
            for (var i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                var top = RowTop(i);
                if (it.GapBefore)
                    using (var sep = new Pen(Color.FromArgb((int)(255 * shown), 238, 238, 241), 1f / s))
                        g.DrawLine(sep, card.X + 14, top - Gap / 2f - 1, card.Right - 14, top - Gap / 2f - 1);
                var ink = Color.FromArgb(alpha, it.Danger ? Theme.Danger : i == _hover ? Theme.Blue : Theme.Muted);
                DrawIcon(g, it.Glyph, new PointF(card.X + 24, top + ItemH / 2), ink);
                using var tb = new SolidBrush(Color.FromArgb(alpha, it.Danger ? Theme.Danger : Theme.Ink));
                g.DrawString(it.Text, text, tb, new RectangleF(card.X + 42, top, card.Width - 42 - 12, ItemH), Exact);
            }
        }
        return bmp;
    }

    /// <summary>Iconos de trazo fino dibujados a mano (14 px): iguales en cualquier Windows, sin depender de fuentes.</summary>
    static void DrawIcon(Graphics g, Icon icon, PointF c, Color color)
    {
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        switch (icon)
        {
            case Icon.Pause:
                using (var a = Theme.Round(new RectangleF(c.X - 4.5f, c.Y - 5.5f, 3, 11), 1.2f)) g.FillPath(brush, a);
                using (var b = Theme.Round(new RectangleF(c.X + 1.5f, c.Y - 5.5f, 3, 11), 1.2f)) g.FillPath(brush, b);
                break;
            case Icon.Play:
                g.FillPolygon(brush, [new PointF(c.X - 3.5f, c.Y - 6), new PointF(c.X + 5.5f, c.Y), new PointF(c.X - 3.5f, c.Y + 6)]);
                break;
            case Icon.Stop:
                using (var r = Theme.Round(new RectangleF(c.X - 5, c.Y - 5, 10, 10), 2.5f)) g.DrawPath(pen, r);
                break;
            case Icon.Settings:
                g.DrawEllipse(pen, c.X - 4.6f, c.Y - 4.6f, 9.2f, 9.2f);
                g.DrawEllipse(pen, c.X - 1.6f, c.Y - 1.6f, 3.2f, 3.2f);
                for (var k = 0; k < 8; k++)
                {
                    var a = k * Math.PI / 4;
                    var (dx, dy) = ((float)Math.Cos(a), (float)Math.Sin(a));
                    g.DrawLine(pen, c.X + dx * 5.2f, c.Y + dy * 5.2f, c.X + dx * 7f, c.Y + dy * 7f);
                }
                break;
            case Icon.Power:
                g.DrawArc(pen, c.X - 6, c.Y - 5.5f, 12, 12, -50, 280);
                g.DrawLine(pen, c.X, c.Y - 7, c.X, c.Y - 0.5f);
                break;
            case Icon.Update:
            {
                // Flecha en círculo: el arco abierto arriba y la punta al final, en el sentido del giro.
                const float R = 5.5f;
                g.DrawArc(pen, c.X - R, c.Y - R, 2 * R, 2 * R, -50, 290);
                var a = 240 * Math.PI / 180;
                var end = new PointF(c.X + R * (float)Math.Cos(a), c.Y + R * (float)Math.Sin(a));
                var (tx, ty) = (-(float)Math.Sin(a), (float)Math.Cos(a));
                var (nx, ny) = (-ty, tx);
                g.FillPolygon(brush,
                [
                    new PointF(end.X + tx * 3f, end.Y + ty * 3f),
                    new PointF(end.X - tx * 1.2f + nx * 2.6f, end.Y - ty * 1.2f + ny * 2.6f),
                    new PointF(end.X - tx * 1.2f - nx * 2.6f, end.Y - ty * 1.2f - ny * 2.6f),
                ]);
                break;
            }
        }
    }

    int HitRow(Point p)
    {
        var x = p.X / S;
        var y = p.Y / S;
        if (x < Pad || x > Pad + _w) return -1;
        for (var i = 0; i < _items.Count; i++)
        {
            var top = RowTop(i);
            if (y >= top && y < top + ItemH) return i;
        }
        return -1;
    }

    void SetHover(int i)
    {
        if (i == _hover) return;
        _hover = i;
        if (!_tick.Enabled) _tick.Start();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_closing) SetHover(HitRow(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_closing) SetHover(-1);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var i = HitRow(e.Location);
        if (i >= 0) Dismiss(_items[i].Run);
        else if (e.Location.X / S < Pad || e.Location.X / S > Pad + _w) Dismiss();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Dismiss();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Escape: Dismiss(); break;
            case Keys.Down: SetHover((_hover + 1) % _items.Count); break;
            case Keys.Up: SetHover((_hover - 1 + _items.Count) % _items.Count); break;
            case Keys.Enter when _hover >= 0: Dismiss(_items[_hover].Run); break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}
