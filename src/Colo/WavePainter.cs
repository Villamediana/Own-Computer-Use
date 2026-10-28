using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Colo;

enum Mode { Starting, Listening, Busy, Paused, Problem }

/// <summary>Dibuja la pastilla con la onda y el globo de texto.</summary>
sealed class WavePainter
{
    public const int CanvasW = 340, CanvasH = 132, PillW = 132, PillH = 44;

    public float Scale = 1;
    public Mode Mode;
    public double Level, T, Energy;
    public string Caption = "";
    public Color CaptionColor = Theme.Ink;
    public float CaptionAlpha = 1;

    public static RectangleF PillRect(float s) =>
        new((CanvasW - PillW) / 2f * s, (CanvasH - PillH - 14) * s, PillW * s, PillH * s);

    public Bitmap Paint()
    {
        var s = Scale;
        var bmp = new Bitmap((int)(CanvasW * s), (int)(CanvasH * s), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        var pill = PillRect(s);
        DrawShadow(g, pill, s);
        using (var white = new SolidBrush(Color.FromArgb(252, 255, 255, 255)))
        using (var p = Theme.Round(pill, pill.Height / 2))
        {
            g.FillPath(white, p);
            using var edge = new Pen(Color.FromArgb(18, 0, 0, 0), 1f);
            g.DrawPath(edge, p);
        }
        DrawWave(g, pill, s);
        DrawCaption(g, pill, s);
        return bmp;
    }

    static void DrawShadow(Graphics g, RectangleF pill, float s)
    {
        for (var i = 8; i >= 1; i--)
        {
            var r = pill;
            r.Inflate(i * 1.3f * s, i * 1.1f * s);
            r.Offset(0, 4 * s);
            using var b = new SolidBrush(Color.FromArgb(4, 15, 23, 42));
            using var p = Theme.Round(r, r.Height / 2);
            g.FillPath(b, p);
        }
    }

    void DrawWave(Graphics g, RectangleF pill, float s)
    {
        var left = pill.X + 22 * s;
        var right = pill.Right - 22 * s;
        var cy = pill.Y + pill.Height / 2;
        var width = right - left;
        var maxAmp = pill.Height * 0.36f;

        if (Mode is Mode.Paused or Mode.Problem && Level < 0.01)
        {
            var color = Mode == Mode.Problem ? Color.FromArgb(150, Theme.Danger) : Color.FromArgb(200, 200, 206);
            using var flat = new Pen(color, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(flat, left, cy, right, cy);
            return;
        }

        // respiración suave para que se note que está viva aunque no hables
        var breath = Mode == Mode.Listening ? 0.6 + 0.4 * Math.Sin(T * 1.8) : 1;
        var amp = (float)(maxAmp * Level * breath) + 0.8f * s;
        var speed = 2.4 + 3.2 * Energy;

        // azul cuando escucha; azul → violeta cuando trabaja
        Color Mix(Color a, Color b, double k) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
        var shimmer = 0.5 + 0.5 * Math.Sin(T * 2.2);
        var c1 = Mix(Color.FromArgb(37, 99, 235), Color.FromArgb(124, 58, 237), Energy * shimmer);
        var c2 = Mix(Color.FromArgb(96, 165, 250), Color.FromArgb(56, 189, 248), Energy * (1 - shimmer));
        var c3 = Mix(Color.FromArgb(165, 180, 252), Color.FromArgb(192, 132, 252), Energy);

        (Color c, float alpha, double freq, double phase, float scale, float widthPx)[] curves =
        [
            (c3, 0.55f, 1.7, 2.1, 0.75f, 2.0f),
            (c2, 0.75f, 2.3, 4.2, 0.9f, 2.0f),
            (c1, 1.0f, 1.25, 0.0, 1.0f, 2.4f),
        ];
        const int N = 48;
        var pts = new PointF[N + 1];
        foreach (var (c, alpha, freq, phase, scale, pw) in curves)
        {
            for (var i = 0; i <= N; i++)
            {
                var x = i / (double)N;
                var env = Math.Pow(Math.Sin(Math.PI * x), 2);
                var y = Math.Sin(2 * Math.PI * freq * x + T * speed * (0.8 + freq * 0.2) + phase);
                pts[i] = new PointF(left + (float)(x * width), cy + (float)(y * env * amp * scale));
            }
            using var pen = new Pen(Color.FromArgb((int)(255 * alpha), c), pw * s) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawCurve(pen, pts, 0.4f);
        }
    }

    /// <summary>Texto discreto sobre la onda: pequeño, en cursiva, gris y casi transparente, sin caja.</summary>
    void DrawCaption(Graphics g, RectangleF pill, float s)
    {
        if (Caption.Length == 0 || CaptionAlpha <= 0) return;
        var alpha = CaptionAlpha;
        var text = Caption.Length > 90 ? Caption[..90] + "…" : Caption;
        using var font = new Font(Theme.Family, 8.5f * s, FontStyle.Italic);
        var maxW = (CanvasW - 20) * s;
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisWord };
        var size = g.MeasureString(text, font, (int)maxW, fmt);
        var r = new RectangleF((CanvasW * s - maxW) / 2, pill.Y - size.Height - 6 * s, maxW, size.Height + 2);
        if (r.Y < 1) r.Y = 1;
        // Un halo blanco muy suave para que se lea sobre fondos oscuros sin parecer una caja.
        using (var halo = new SolidBrush(Color.FromArgb((int)(70 * alpha), 255, 255, 255)))
            foreach (var (dx, dy) in new[] { (-1f, 0f), (1f, 0f), (0f, -1f), (0f, 1f) })
                g.DrawString(text, font, halo, new RectangleF(r.X + dx, r.Y + dy, r.Width, r.Height), fmt);
        var tone = CaptionColor == Theme.Danger ? Color.FromArgb(200, 70, 70) : Color.FromArgb(105, 105, 115);
        using var ink = new SolidBrush(Color.FromArgb((int)(185 * alpha), tone));
        g.DrawString(text, font, ink, r, fmt);
    }

}
