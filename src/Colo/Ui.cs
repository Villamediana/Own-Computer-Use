using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Colo;

static class Theme
{
    public static readonly Color Bg = Color.White;
    public static readonly Color Fill = Color.FromArgb(244, 244, 245);
    public static readonly Color FillHover = Color.FromArgb(236, 236, 238);
    public static readonly Color Ink = Color.FromArgb(17, 17, 19);
    public static readonly Color Muted = Color.FromArgb(132, 132, 140);
    public static readonly Color Blue = Color.FromArgb(37, 99, 235);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);

    static readonly Dictionary<string, bool> Installed = [];

    static bool Has(string family)
    {
        if (Installed.TryGetValue(family, out var ok)) return ok;
        using var f = new Font(family, 10);
        return Installed[family] = string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase);
    }

    public static string Family => Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static string DisplayFamily => Has("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";

    public static Font Text(float size, FontStyle style = FontStyle.Regular) => new(Family, size, style);
    public static Font Semibold(float size) =>
        Has("Segoe UI Variable Text Semibold") ? new("Segoe UI Variable Text Semibold", size)
        : Has("Segoe UI Semibold") ? new("Segoe UI Semibold", size) : new(Family, size, FontStyle.Bold);
    public static Font Display(float size) =>
        Has("Segoe UI Variable Display Semibold") ? new("Segoe UI Variable Display Semibold", size)
        : Has("Segoe UI Semibold") ? new("Segoe UI Semibold", size) : new(DisplayFamily, size, FontStyle.Bold);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var d = radius * 2;
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Botón redondeado, negro (principal) o gris claro.</summary>
sealed class PillButton : Control
{
    bool _hover, _down;
    public bool Primary { get; set; }

    public PillButton(string text, bool primary)
    {
        Text = text;
        Primary = primary;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Font = Theme.Semibold(9.5f);
        Height = 38;
        Margin = new Padding(0);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AutoWidth();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        AutoWidth();
    }

    void AutoWidth() => Width = TextRenderer.MeasureText(Text, Font).Width + (int)(36 * DeviceDpi / 96f);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var bg = Primary
            ? (_down ? Color.FromArgb(50, 50, 54) : _hover ? Color.FromArgb(38, 38, 42) : Theme.Ink)
            : (_down ? Color.FromArgb(226, 226, 230) : _hover ? Theme.FillHover : Theme.Fill);
        if (!Enabled) bg = Color.FromArgb(120, bg);
        using (var b = new SolidBrush(bg))
        using (var p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
            g.FillPath(b, p);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Primary ? Color.White : Theme.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Control segmentado estilo iOS: pista gris y opción elegida en blanco.</summary>
sealed class Segmented : Control
{
    readonly string[] _items;
    int _index;
    public event EventHandler? Changed;

    public Segmented(params string[] items)
    {
        _items = items;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = Theme.Semibold(9f);
        Height = 36;
        Width = 400;
        Margin = new Padding(0);
    }

    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            Invalidate();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Index = Math.Clamp(e.X * _items.Length / Math.Max(1, Width), 0, _items.Length - 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var s = DeviceDpi / 96f;
        var h = Height - 1f;
        using (var track = new SolidBrush(Theme.Fill))
        using (var p = Theme.Round(new RectangleF(0, 0, Width - 1, h), 10 * s))
            g.FillPath(track, p);
        var w = (Width - 1f) / _items.Length;
        var knob = new RectangleF(w * _index + 3 * s, 3 * s, w - 6 * s, h - 6 * s);
        for (var i = 3; i >= 1; i--)
            using (var sh = new SolidBrush(Color.FromArgb(7, 0, 0, 0)))
            using (var sp = Theme.Round(new RectangleF(knob.X - i * 0.5f, knob.Y + i * 0.6f, knob.Width + i, knob.Height + i * 0.4f), 8 * s))
                g.FillPath(sh, sp);
        using (var kb = new SolidBrush(Color.White))
        using (var kp = Theme.Round(knob, 8 * s))
            g.FillPath(kb, kp);
        for (var i = 0; i < _items.Length; i++)
        {
            var r = Rectangle.Round(new RectangleF(w * i, 0, w, h));
            TextRenderer.DrawText(g, _items[i], Font, r, i == _index ? Theme.Ink : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Campo relleno gris claro, sin borde, para un TextBox o ComboBox.</summary>
sealed class Field : Panel
{
    public Field(Control inner)
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Padding = new Padding(12, 10, 10, 6);
        Height = 40;
        Width = 400;
        Margin = new Padding(0);
        inner.Dock = DockStyle.Fill;
        inner.BackColor = Theme.Fill;
        inner.ForeColor = Theme.Ink;
        inner.Font = Theme.Text(10f);
        Controls.Add(inner);
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = DeviceDpi / 96f;
        using var b = new SolidBrush(Theme.Fill);
        using var p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 10 * s);
        g.FillPath(b, p);
        if (ContainsFocus)
            using (var pen = new Pen(Color.FromArgb(90, Theme.Blue), 1.5f * s))
                g.DrawPath(pen, p);
    }
}

/// <summary>Interruptor estilo iOS.</summary>
sealed class Toggle : Control
{
    bool _on;
    public event EventHandler? Changed;

    public Toggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        Size = new Size(40, 24);
        Margin = new Padding(0);
    }

    public bool On
    {
        get => _on;
        set { _on = value; Invalidate(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); On = !On; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var b = new SolidBrush(_on ? Theme.Blue : Color.FromArgb(220, 220, 224)))
        using (var p = Theme.Round(r, r.Height / 2))
            g.FillPath(b, p);
        var d = r.Height - 4;
        var x = _on ? r.Right - d - 2 : 2;
        using (var sh = new SolidBrush(Color.FromArgb(30, 0, 0, 0))) g.FillEllipse(sh, x, 3, d, d);
        g.FillEllipse(Brushes.White, x, 2, d, d);
    }
}

/// <summary>Fila "título (y descripción) ··· interruptor".</summary>
sealed class ToggleRow : Panel
{
    public Toggle Toggle { get; } = new();

    public ToggleRow(string text, string? detail = null, int width = 520)
    {
        Width = width;
        Height = detail is null ? 40 : 54;
        Margin = new Padding(0, 2, 0, 2);
        var title = new Label { Text = text, AutoSize = true, Font = Theme.Text(10f), ForeColor = Theme.Ink, Location = new Point(0, detail is null ? 10 : 7) };
        Controls.Add(title);
        if (detail is not null)
            Controls.Add(new Label { Text = detail, AutoSize = true, Font = Theme.Text(8.75f), ForeColor = Theme.Muted, Location = new Point(0, 29) });
        Controls.Add(Toggle);
        Layout += (_, _) => Toggle.Location = new Point(Width - Toggle.Width, (Height - Toggle.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var line = new Pen(Color.FromArgb(236, 236, 238));
        e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>Navegación lateral estilo ajustes de ChatGPT / macOS.</summary>
sealed class NavList : Control
{
    readonly string[] _items;
    int _index, _hover = -1;
    public event EventHandler? Changed;
    const int ItemH = 36;

    public NavList(params string[] items)
    {
        _items = items;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = Theme.Text(10f);
        Height = items.Length * (ItemH + 2);
    }

    public int Index
    {
        get => _index;
        set { _index = value; Invalidate(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    int At(int y) => y / (int)((ItemH + 2) * DeviceDpi / 96f);

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); var h = At(e.Y); if (h != _hover) { _hover = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var i = At(e.Y);
        if (i >= 0 && i < _items.Length) Index = i;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var s = DeviceDpi / 96f;
        for (var i = 0; i < _items.Length; i++)
        {
            var r = new RectangleF(0, i * (ItemH + 2) * s, Width - 1, ItemH * s);
            if (i == _index || i == _hover)
            {
                using var b = new SolidBrush(i == _index ? Color.FromArgb(232, 232, 235) : Color.FromArgb(240, 240, 242));
                using var p = Theme.Round(r, 8 * s);
                g.FillPath(b, p);
            }
            using var font = i == _index ? Theme.Semibold(10f) : Theme.Text(10f);
            TextRenderer.DrawText(g, _items[i], font, Rectangle.Round(new RectangleF(r.X + 12 * s, r.Y, r.Width - 12 * s, r.Height)),
                Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Tarjeta con borde suave (filas de servidores MCP).</summary>
sealed class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Theme.Bg;
        Padding = new Padding(16, 12, 16, 12);
        Margin = new Padding(0, 0, 0, 10);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = DeviceDpi / 96f;
        using var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 12 * s);
        using var b = new SolidBrush(Theme.Bg);
        g.FillPath(b, p);
        using var pen = new Pen(Color.FromArgb(228, 228, 232), 1f);
        g.DrawPath(pen, p);
    }
}

/// <summary>Texto clicable discreto («Editar», «Quitar»).</summary>
sealed class LinkText : Label
{
    public LinkText(string text, bool danger = false)
    {
        Text = text;
        AutoSize = true;
        Cursor = Cursors.Hand;
        Font = Theme.Semibold(9f);
        ForeColor = danger ? Theme.Danger : Theme.Ink;
        Margin = new Padding(12, 0, 0, 0);
    }
}
