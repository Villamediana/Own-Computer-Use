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
