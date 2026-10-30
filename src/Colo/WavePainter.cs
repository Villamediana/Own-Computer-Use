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
    /// <summary>Desplazamiento (px a 96 ppp) y escala de la frase mientras entra con rebote.</summary>
    public float CaptionRise, CaptionScale = 1;
    /// <summary>Gota que se aplasta y estira: &gt;0 más ancha y baja, &lt;0 más alta y estrecha.</summary>
    public float Squash;
    /// <summary>Transcribiendo o escribiendo: la onda queda baja y un arco da vueltas encima.</summary>
    public bool Spinning;

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

        // Gota: se aplasta y estira alrededor de su centro sin cambiar de volumen a ojo.
        var state = g.Save();
        var cx = pill.X + pill.Width / 2;
        var cy = pill.Y + pill.Height / 2;
        var sq = Math.Clamp(Squash, -1, 1);
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(1 + 0.07f * sq, 1 - 0.09f * sq);
        g.TranslateTransform(-cx, -cy);
        using (var body = Theme.Round(pill, pill.Height / 2))
        {
            DrawShadow(g, pill, body, s);
            DrawGlass(g, pill, body, s);
        }
        // Transcribiendo o escribiendo: las ondas se van y queda el spinner sobre el blanco de la pastilla.
        if (Spinning) DrawSpinner(g, pill, s);
        else DrawWave(g, pill, s);
        g.Restore(state);
        DrawCaption(g, pill, s);
        return bmp;
    }

    static void DrawShadow(Graphics g, RectangleF pill, GraphicsPath body, float s)
    {
        // Fuera de la gota: con el cuerpo translúcido, la sombra debajo lo ensuciaría de gris.
        var state = g.Save();
        g.SetClip(body, CombineMode.Exclude);
        for (var i = 9; i >= 1; i--)
        {
            var r = pill;
            r.Inflate(i * 1.4f * s, i * 1.1f * s);
            r.Offset(0, 3.5f * s + i * 0.25f * s);
            using var b = new SolidBrush(Color.FromArgb(5, 15, 23, 60));
            using var p = Theme.Round(r, r.Height / 2);
            g.FillPath(b, p);
        }
        g.Restore(state);
    }

    /// <summary>
    /// Vidro como una gota de agua: cuerpo blanco translúcido (se ve un poco lo de detrás), brillo
    /// especular arriba, luz que se junta en el borde de abajo, canto fino de luz y un tinte del color de
    /// la onda que se nota más cuando oye o trabaja.
    /// </summary>
    void DrawGlass(Graphics g, RectangleF pill, GraphicsPath body, float s)
    {
        using (var fill = new LinearGradientBrush(new PointF(0, pill.Top), new PointF(0, pill.Bottom),
                   Color.FromArgb(222, 255, 255, 255), Color.FromArgb(200, 244, 248, 255)))
            g.FillPath(fill, body);

        var state = g.Save();
        g.SetClip(body, CombineMode.Intersect);

        // Tinte desde el borde hacia dentro: azul al oír, violeta al trabajar.
        var tint = (float)Math.Clamp(0.25 + Level * 0.6 + Energy * 0.5, 0, 1);
        using (var glow = new PathGradientBrush(body))
        {
            var edge = Color.FromArgb((int)(70 * tint), Energy > 0.5 ? Color.FromArgb(167, 139, 250) : Color.FromArgb(125, 183, 255));
            glow.CenterColor = Color.FromArgb(0, 255, 255, 255);
            glow.SurroundColors = [edge];
            glow.FocusScales = new PointF(0.82f, 0.25f);
            g.FillPath(glow, body);
        }

        // Luz que atraviesa la gota y se junta abajo.
        var low = new RectangleF(pill.X + pill.Width * 0.16f, pill.Bottom - pill.Height * 0.42f, pill.Width * 0.68f, pill.Height * 0.5f);
        using (var caustic = new GraphicsPath())
        {
            caustic.AddEllipse(low);
            using var cb = new PathGradientBrush(caustic)
            {
                CenterColor = Color.FromArgb(120, 255, 255, 255),
                SurroundColors = [Color.FromArgb(0, 255, 255, 255)],
                CenterPoint = new PointF(low.X + low.Width / 2, low.Bottom - low.Height * 0.2f),
            };
            g.FillPath(cb, caustic);
        }

        // Brillo especular: una franja ancha arriba, más blanca al centro y que se apaga hacia abajo.
        var hi = new RectangleF(pill.X + pill.Height * 0.32f, pill.Y + 2.2f * s, pill.Width - pill.Height * 0.64f, pill.Height * 0.42f);
        using (var hp = Theme.Round(hi, hi.Height / 2))
        using (var hb = new LinearGradientBrush(new PointF(0, hi.Top), new PointF(0, hi.Bottom),
                   Color.FromArgb(205, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
            g.FillPath(hb, hp);
        g.Restore(state);

        // Canto: luz arriba, casi nada abajo.
        using var rimBrush = new LinearGradientBrush(new PointF(0, pill.Top - 1), new PointF(0, pill.Bottom + 1),
            Color.FromArgb(240, 255, 255, 255), Color.FromArgb(40, 120, 130, 160));
        using var rim = new Pen(rimBrush, 1.1f * s);
        g.DrawPath(rim, body);
        using var outline = new Pen(Color.FromArgb(16, 15, 23, 42), 0.8f * s);
        var o = pill;
        o.Inflate(0.9f * s, 0.9f * s);
        using var op = Theme.Round(o, o.Height / 2);
        g.DrawPath(outline, op);
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

    /// <summary>Spinner en el centro de la pastilla: aro azul tenue y un arco que gira y se estira.</summary>
    void DrawSpinner(Graphics g, RectangleF pill, float s)
    {
        var cx = pill.X + pill.Width / 2;
        var cy = pill.Y + pill.Height / 2;
        var r = 8.5f * s;
        using (var track = new Pen(Color.FromArgb(40, 37, 99, 235), 2.4f * s))
            g.DrawEllipse(track, cx - r, cy - r, r * 2, r * 2);
        // Gira a ritmo constante y el arco se estira y encoge, como los de Windows.
        var start = (float)(T * 320 % 360);
        var sweep = (float)(60 + 170 * (0.5 + 0.5 * Math.Sin(T * 3.1)));
        using var arc = new Pen(Color.FromArgb(37, 99, 235), 2.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, cx - r, cy - r, r * 2, r * 2, start, sweep);
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
        var r = new RectangleF((CanvasW * s - maxW) / 2, pill.Y - size.Height - 6 * s + CaptionRise * s, maxW, size.Height + 2);
        if (r.Y < 1) r.Y = 1;
        // Entra desde abajo con un rebote; crece desde el centro de su línea de base.
        var state = g.Save();
        var bx = r.X + r.Width / 2;
        var by = r.Bottom;
        g.TranslateTransform(bx, by);
        g.ScaleTransform(CaptionScale, CaptionScale);
        g.TranslateTransform(-bx, -by);
        // Un halo blanco muy suave para que se lea sobre fondos oscuros sin parecer una caja.
        using (var halo = new SolidBrush(Color.FromArgb((int)(70 * alpha), 255, 255, 255)))
            foreach (var (dx, dy) in new[] { (-1f, 0f), (1f, 0f), (0f, -1f), (0f, 1f) })
                g.DrawString(text, font, halo, new RectangleF(r.X + dx, r.Y + dy, r.Width, r.Height), fmt);
        var tone = CaptionColor == Theme.Danger ? Color.FromArgb(200, 70, 70) : Color.FromArgb(105, 105, 115);
        using var ink = new SolidBrush(Color.FromArgb((int)(185 * alpha), tone));
        g.DrawString(text, font, ink, r, fmt);
        g.Restore(state);
    }

}
