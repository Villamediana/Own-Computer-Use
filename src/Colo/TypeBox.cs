using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Colo;

/// <summary>
/// Modo prueba (Ajustes › Voz › «Escribir en vez de hablar»): al soltar el atajo de hablar o de dictar aparece esta
/// caja y lo escrito ocupa el lugar de lo transcrito; el resto del camino es el mismo. Enter envía, Mayús+Enter salta
/// de línea, ↑ con la caja vacía trae lo último, Esc o clic fuera cancela. Crece con el texto (hasta 6 líneas). Al
/// enviar, el foco vuelve a la ventana de antes (el dictado escribe allí y el agente ve esa ventana).
/// Módulo aparte: si se quita, solo se pierde este modo.
/// </summary>
sealed class TypeBox : Form
{
    static readonly Color Line = Color.FromArgb(228, 228, 231);
    static readonly Color Soft = Color.FromArgb(239, 244, 255);

    readonly TextBox _text = new()
    {
        BorderStyle = BorderStyle.None, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.None,
        BackColor = Theme.Bg, ForeColor = Theme.Ink, AcceptsReturn = false,
    };
    readonly Label _ghost = new() { AutoSize = false, ForeColor = Color.FromArgb(160, 160, 168), BackColor = Theme.Bg, Cursor = Cursors.IBeam };
    readonly Label _hint = new() { AutoSize = false, ForeColor = Theme.Muted, BackColor = Theme.Bg, TextAlign = ContentAlignment.MiddleLeft };
    readonly SendButton _send = new();
    readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    IntPtr _back;
    bool _sent, _dictate;
    string _last = "";
    Rectangle _widget;

    /// <summary>Lo escrito (el foco ya volvió a la ventana de antes).</summary>
    public event Action<string>? Sent;

    public TypeBox()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Bg;
        KeyPreview = true;
        DoubleBuffered = true;
        _text.Font = Theme.Text(13.5f);
        _ghost.Font = Theme.Text(13.5f);
        _hint.Font = Theme.Text(8.5f);
        _hint.Text = "Enter envía · Mayús+Enter, otra línea · ↑ repite lo último";
        Controls.AddRange([_send, _hint, _text, _ghost]);
        _ghost.BringToFront();
        _ghost.Click += (_, _) => _text.Focus();
        _send.Click += (_, _) => Send();
        _text.TextChanged += (_, _) => { _ghost.Visible = _text.TextLength == 0; _send.Ready = _text.Text.Trim().Length > 0; Fit(); };
        _text.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Shift)
            {
                e.SuppressKeyPress = true;
                _text.SelectedText = Environment.NewLine;
            }
            else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Send(); }
            else if (e.KeyCode == Keys.Up && _text.TextLength == 0 && _last.Length > 0)
            {
                e.SuppressKeyPress = true;
                _text.Text = _last;
                _text.SelectionStart = _text.TextLength;
            }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Dismiss(false); } };
        Deactivate += (_, _) => { if (Visible && !_sent) Dismiss(false); };
        _fade.Tick += (_, _) =>
        {
            Opacity = Math.Min(1, Opacity + 0.16);
            if (Opacity >= 1) _fade.Stop();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: fuera de Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
    }

    float S => DeviceDpi / 96f;
    int Pad => (int)(18 * S);
    int HeadH => (int)(26 * S);
    int _sep;

    /// <summary>Abre encima de la onda.</summary>
    public void Open(bool dictate, Rectangle widget)
    {
        _back = Native.GetForegroundWindow();
        if (_back == Handle || Native.IsOwnWindow(_back)) _back = IntPtr.Zero;
        _sent = false;
        _dictate = dictate;
        _widget = widget;
        _ghost.Text = dictate ? "Escribe lo que dictarías…" : "Escribe lo que le dirías a Colo…";
        _text.Text = "";
        Fit();
        Opacity = 0;
        Show();
        _fade.Start();
        Native.Focus(Handle);
        Activate();
        _text.Focus();
        Invalidate();
    }

    /// <summary>Alto según las líneas escritas (1 a 6), creciendo hacia arriba desde encima de la onda.</summary>
    void Fit()
    {
        var w = (int)(560 * S);
        var inner = w - 2 * Pad;
        var line = TextRenderer.MeasureText("Ag", _text.Font).Height;
        var needed = TextRenderer.MeasureText(_text.Text.Length == 0 ? "A" : _text.Text + (_text.Text.EndsWith('\n') ? "A" : ""), _text.Font,
            new Size(inner, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        var lines = Math.Clamp((int)Math.Ceiling(needed / (double)line), 1, 6);
        var textH = lines * line + (int)(4 * S);
        _text.ScrollBars = needed > 6 * line ? ScrollBars.Vertical : ScrollBars.None;
        // De arriba abajo: cabecera, texto, línea fina, pie (pista a la izquierda, botón a la derecha).
        var textTop = Pad + HeadH;
        _sep = textTop + textH + (int)(10 * S);
        var b = (int)(34 * S);
        var footTop = _sep + (int)(10 * S);
        var h = footTop + b + (int)(14 * S);
        var wa = Screen.FromRectangle(_widget).WorkingArea;
        Bounds = new Rectangle(Math.Clamp(_widget.X + (_widget.Width - w) / 2, wa.Left, wa.Right - w),
            Math.Clamp(_widget.Top - h - (int)(10 * S), wa.Top, wa.Bottom - h), w, h);
        _text.SetBounds(Pad, textTop, inner, textH);
        _ghost.SetBounds(Pad, textTop, inner, line + (int)(4 * S));
        _send.SetBounds(w - Pad - b, footTop, b, b);
        _hint.SetBounds(Pad, footTop, inner - b - (int)(8 * S), b);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(Line, 1)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        // Cabecera: una pastilla con lo que va a pasar con el texto, y Esc a la derecha.
        using var font = Theme.Semibold(9f);
        var label = _dictate ? "Dictar · va donde estaba el cursor" : "Hablarle a Colo";
        var size = TextRenderer.MeasureText(label, font);
        var dot = (int)(7 * S);
        var pill = new Rectangle(Pad - (int)(2 * S), Pad - (int)(4 * S), size.Width + dot + (int)(22 * S), (int)(24 * S));
        using (var path = Round(pill, pill.Height / 2))
        using (var fill = new SolidBrush(Soft))
            g.FillPath(fill, path);
        using (var blue = new SolidBrush(Theme.Blue))
            g.FillEllipse(blue, pill.X + (int)(10 * S), pill.Y + (pill.Height - dot) / 2, dot, dot);
        TextRenderer.DrawText(g, label, font, new Point(pill.X + (int)(10 * S) + dot + (int)(6 * S), pill.Y + (pill.Height - size.Height) / 2), Theme.Blue);
        using var small = Theme.Text(8.5f);
        var esc = "Esc";
        var es = TextRenderer.MeasureText(esc, small);
        var key = new Rectangle(Width - Pad - es.Width - (int)(12 * S), pill.Y + (int)(2 * S), es.Width + (int)(12 * S), pill.Height - (int)(4 * S));
        using (var kp = Round(key, (int)(6 * S)))
        using (var kb = new Pen(Line, 1))
            g.DrawPath(kb, kp);
        TextRenderer.DrawText(g, esc, small, key, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        // Una línea fina separa el pie.
        using var sep = new Pen(Color.FromArgb(242, 242, 244), 1);
        g.DrawLine(sep, Pad, _sep, Width - Pad, _sep);
    }

    static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = Math.Max(1, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void Send()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0) return;
        _sent = true;
        _last = text;
        Dismiss(true);
        Sent?.Invoke(text);
    }

    void Dismiss(bool keep)
    {
        _fade.Stop();
        Hide();
        // Devuelve el foco a donde estaba (también al cancelar: la caja no se queda con él).
        if (_back != IntPtr.Zero) Native.Focus(_back);
        if (!keep) _text.Text = "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fade.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Botón redondo con una flecha: azul cuando hay algo que enviar.</summary>
    sealed class SendButton : Control
    {
        bool _ready, _hover;

        public bool Ready { get => _ready; set { if (_ready == value) return; _ready = value; Invalidate(); } }

        public SendButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = _ready ? (_hover ? Color.FromArgb(29, 78, 216) : Theme.Blue) : Color.FromArgb(228, 228, 231);
            using (var fill = new SolidBrush(color)) g.FillEllipse(fill, 0, 0, Width - 1, Height - 1);
            float cx = Width / 2f, cy = Height / 2f, a = Width * 0.2f;
            using var pen = new Pen(_ready ? Color.White : Color.FromArgb(160, 160, 168), Width * 0.075f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLine(pen, cx, cy + a, cx, cy - a);
            g.DrawLines(pen, [new PointF(cx - a * 0.8f, cy - a * 0.2f), new PointF(cx, cy - a), new PointF(cx + a * 0.8f, cy - a * 0.2f)]);
        }
    }
}
