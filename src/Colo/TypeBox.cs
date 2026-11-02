using System;
using System.Drawing;
using System.Windows.Forms;

namespace Colo;

/// <summary>
/// Modo prueba (Ajustes › Voz › «Escribir en vez de hablar»): al soltar el atajo de hablar o de dictar aparece esta
/// caja y lo escrito ocupa el lugar de lo transcrito; el resto del camino es el mismo. Enter envía, Mayús+Enter salta
/// de línea, Esc o clic fuera cancela. Al enviar, el foco vuelve a la ventana de antes (el dictado escribe allí y el
/// agente ve esa ventana). Módulo aparte: si se quita, solo se pierde este modo.
/// </summary>
sealed class TypeBox : Form
{
    readonly TextBox _text = new()
    {
        BorderStyle = BorderStyle.None, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.None,
        BackColor = Theme.Bg, ForeColor = Theme.Ink,
    };
    readonly Label _head = new() { AutoSize = false, ForeColor = Theme.Muted, BackColor = Theme.Bg };
    readonly Label _hint = new() { AutoSize = false, ForeColor = Theme.Muted, BackColor = Theme.Bg, TextAlign = ContentAlignment.MiddleRight };
    IntPtr _back;
    bool _sent;

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
        _text.Font = Theme.Text(12.5f);
        _head.Font = Theme.Semibold(9f);
        _hint.Font = Theme.Text(8.5f);
        _hint.Text = "Enter envía · Esc cancela";
        Controls.AddRange([_head, _hint, _text]);
        _text.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Dismiss(false); } };
        Deactivate += (_, _) => { if (Visible && !_sent) Dismiss(false); };
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

    /// <summary>Abre encima de la onda. dictate cambia solo el título.</summary>
    public void Open(bool dictate, Rectangle widget)
    {
        _back = Native.GetForegroundWindow();
        if (_back == Handle || Native.IsOwnWindow(_back)) _back = IntPtr.Zero;
        _sent = false;
        _head.Text = dictate ? "Dictar · se escribe donde estaba el cursor" : "Hablarle a Colo";
        var s = DeviceDpi / 96f;
        int pad = (int)(16 * s), w = (int)(520 * s), headH = (int)(20 * s), textH = (int)(66 * s), hintH = (int)(18 * s);
        Size = new Size(w, pad + headH + (int)(6 * s) + textH + hintH + pad);
        _head.SetBounds(pad, pad, w - 2 * pad, headH);
        _text.SetBounds(pad, pad + headH + (int)(6 * s), w - 2 * pad, textH);
        _hint.SetBounds(pad, Height - pad - hintH, w - 2 * pad, hintH);
        var wa = Screen.FromRectangle(widget).WorkingArea;
        Location = new Point(Math.Clamp(widget.X + (widget.Width - Width) / 2, wa.Left, wa.Right - Width),
            Math.Clamp(widget.Top - Height - (int)(8 * s), wa.Top, wa.Bottom - Height));
        _text.Text = "";
        Show();
        Native.Focus(Handle);
        Activate();
        _text.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Color.FromArgb(228, 228, 231), 1);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    void Send()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0) return;
        _sent = true;
        Dismiss(true);
        Sent?.Invoke(text);
    }

    void Dismiss(bool keep)
    {
        Hide();
        // Devuelve el foco a donde estaba (también al cancelar: la caja no se queda con él).
        if (_back != IntPtr.Zero) Native.Focus(_back);
        if (!keep) _text.Text = "";
    }
}
