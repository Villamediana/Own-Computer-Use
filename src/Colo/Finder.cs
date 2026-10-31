using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Colo.Core;

namespace Colo;

/// <summary>
/// Buscar en la ventana activa lo que no está a la vista (más abajo en un hilo de correo, una lista larga, una
/// página) y dejarlo visible; y todo su texto, también el de fuera de pantalla, para sacar datos.
/// </summary>
static class Finder
{
    const int MaxPages = 15;
    const int MaxText = 80_000;
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    sealed record Hit(AutomationElement? Element, TextPatternRange? Range, string Text, bool Offscreen);

    public static Task<string> FindAsync(string text, int n, bool up, Action<string>? say, CancellationToken ct) => Task.Run(() =>
    {
        text = text.Trim();
        if (text.Length == 0) return "error: falta text";
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "error: no hay ventana activa";
        var root = AutomationElement.FromHandle(hwnd);
        var want = Extract.Fold(text);
        var sw = Stopwatch.StartNew();
        string? seen = null;
        for (var page = 0; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var hits = Search(root, want, text);
            if (hits.Count > 0)
            {
                var k = Math.Clamp(n, 1, hits.Count);
                var hit = hits[k - 1];
                Show(hit);
                var others = hits.Count > 1
                    ? "\nTodas: " + string.Join(" | ", hits.Take(8).Select((h, i) => $"{i + 1}) {Short(h.Text, want)}")) : "";
                var moved = page > 0 ? $" (tras desplazar {page} páginas)" : "";
                return $"ok: {hits.Count} resultado(s){moved}; la {k} queda a la vista: «{Short(hit.Text, want)}». En el siguiente turno está en la lista de elementos.{others}";
            }
            if (sw.Elapsed > Budget || page == MaxPages) break;
            say?.Invoke(up ? "Buscando más arriba…" : "Buscando más abajo…");
            seen ??= Visible(root);
            if (!ScrollPage(root, up)) break;
            Thread.Sleep(320); // listas virtuales y páginas que cargan al desplazar
            var now = Visible(root);
            // Nada cambió tras desplazar: se llegó al final.
            if (now == seen) break;
            seen = now;
        }
        return $"ok: no encontré «{text}» en esta ventana ({(up ? "hasta arriba" : "hasta abajo")}). Si está plegado («mostrar más», «…»), ábrelo; si no, prueba la otra dirección o la búsqueda de la app.";
    }, ct);

    /// <summary>Elementos cuyo nombre contiene el texto (también fuera de pantalla) y, si no hay, el texto de los documentos.</summary>
    static List<Hit> Search(AutomationElement root, string want, string text)
    {
        var hits = new List<Hit>();
        var cr = new CacheRequest { TreeFilter = Automation.ControlViewCondition };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.IsOffscreenProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        AutomationElementCollection all;
        using (cr.Activate()) all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var taken = new List<(string Name, System.Windows.Rect Rect)>();
        foreach (AutomationElement e in all)
        {
            var name = e.Cached.Name ?? "";
            if (name.Length == 0 || !Extract.Fold(name).Contains(want)) continue;
            var rect = e.Cached.BoundingRectangle;
            // El ítem y su texto de dentro dicen lo mismo: se queda el de fuera (es el que se desplaza).
            if (taken.Any(t => t.Name == name && (t.Rect.IsEmpty || rect.IsEmpty || t.Rect.IntersectsWith(rect)))) continue;
            taken.Add((name, rect));
            hits.Add(new Hit(e, null, name, e.Cached.IsOffscreen));
        }
        // Pestañas, títulos y menús que lo nombran van al final: lo que busca está en el contenido.
        static bool Chrome(Hit h) => h.Element!.Cached.ControlType is var t
            && (t == ControlType.TabItem || t == ControlType.TitleBar || t == ControlType.MenuItem || t == ControlType.Window);
        if (hits.Count > 0) return hits.OrderBy(Chrome).ToList();

        foreach (AutomationElement d in root.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true)))
        {
            if (!d.TryGetCurrentPattern(TextPattern.Pattern, out var p)) continue;
            var doc = ((TextPattern)p).DocumentRange;
            var range = doc.FindText(text, false, true);
            for (var i = 0; range is not null && i < 20; i++)
            {
                var around = range.Clone();
                around.ExpandToEnclosingUnit(TextUnit.Line);
                hits.Add(new Hit(null, range, around.GetText(300), false));
                var rest = doc.Clone();
                rest.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
                range = rest.FindText(text, false, true);
            }
            if (hits.Count > 0) break;
        }
        return hits;
    }

    static void Show(Hit hit)
    {
        try
        {
            if (hit.Range is { } r)
            {
                r.ScrollIntoView(true);
                r.Select();
                return;
            }
            // El elemento o alguno de sus padres sabe desplazarse hasta quedar visible.
            var walker = TreeWalker.ControlViewWalker;
            for (var e = hit.Element; e is not null; e = walker.GetParent(e))
                if (e.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var si))
                {
                    ((ScrollItemPattern)si).ScrollIntoView();
                    return;
                }
            // Páginas web: el foco también lleva hasta él.
            if (hit.Offscreen && hit.Element is { } el && el.Current.IsKeyboardFocusable) el.SetFocus();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException) { }
    }

    /// <summary>Una página arriba o abajo en el contenedor desplazable más grande. false = no había por dónde.</summary>
    static bool ScrollPage(AutomationElement root, bool up)
    {
        AutomationElement? best = null;
        var area = 0.0;
        foreach (AutomationElement e in root.FindAll(TreeScope.Subtree,
                     new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true)))
        {
            try
            {
                var sp = (ScrollPattern)e.GetCurrentPattern(ScrollPattern.Pattern);
                if (!sp.Current.VerticallyScrollable) continue;
                var pct = sp.Current.VerticalScrollPercent;
                if (up ? pct <= 0 : pct >= 100) continue;
                var r = e.Current.BoundingRectangle;
                if (r.Width * r.Height > area) { best = e; area = r.Width * r.Height; }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException) { }
        }
        if (best is not null)
        {
            try
            {
                ((ScrollPattern)best.GetCurrentPattern(ScrollPattern.Pattern)).ScrollVertical(up ? ScrollAmount.LargeDecrement : ScrollAmount.LargeIncrement);
                return true;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException) { }
        }
        // Sin ScrollPattern (algunas apps web): la rueda en el centro de la ventana.
        var w = root.Current.BoundingRectangle;
        if (w.IsEmpty) return false;
        Input.Scroll(new System.Drawing.Point((int)(w.Left + w.Width / 2), (int)(w.Top + w.Height / 2)), up ? "up" : "down", 8);
        return true;
    }

    /// <summary>Huella de lo que se ve, para saber si desplazar cambió algo.</summary>
    static string Visible(AutomationElement root)
    {
        var cr = new CacheRequest { TreeFilter = Automation.ControlViewCondition, AutomationElementMode = AutomationElementMode.None };
        cr.Add(AutomationElement.NameProperty);
        var sb = new StringBuilder();
        using (cr.Activate())
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsOffscreenProperty, false)))
            {
                sb.Append(e.Cached.Name).Append('|');
                if (sb.Length > 20_000) break;
            }
        return sb.ToString();
    }

    static string Short(string s, string want)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (s.Length <= 100) return s;
        var i = Math.Max(0, Extract.Fold(s).IndexOf(want, StringComparison.Ordinal));
        var from = Math.Clamp(i - 40, 0, s.Length - 1);
        var len = Math.Min(100, s.Length - from);
        return (from > 0 ? "…" : "") + s.Substring(from, len) + (from + len < s.Length ? "…" : "");
    }

    /// <summary>Todo el texto de la ventana activa, también lo de fuera de pantalla: documentos y nombres de los elementos.</summary>
    public static Task<string> AllTextAsync(CancellationToken ct) => Task.Run(() =>
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "";
        var root = AutomationElement.FromHandle(hwnd);
        var sb = new StringBuilder();
        foreach (AutomationElement d in root.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)))
        {
            if (sb.Length >= MaxText) break;
            if (d.TryGetCurrentPattern(TextPattern.Pattern, out var p))
                sb.AppendLine(((TextPattern)p).DocumentRange.GetText(MaxText - sb.Length));
        }
        var cr = new CacheRequest { TreeFilter = Automation.ControlViewCondition, AutomationElementMode = AutomationElementMode.None };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(ValuePattern.ValueProperty);
        cr.Add(AutomationElement.IsPasswordProperty);
        string? last = null;
        using (cr.Activate())
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                if (sb.Length >= MaxText) break;
                var t = e.Cached.Name ?? "";
                if (e.GetCachedPropertyValue(ValuePattern.ValueProperty, true) is string v && v.Length > 0
                    && e.GetCachedPropertyValue(AutomationElement.IsPasswordProperty) is not true)
                    t = t.Length == 0 ? v : $"{t}: {v}";
                if (t.Trim().Length == 0 || t == last) continue;
                sb.AppendLine(t.Trim());
                last = t;
            }
        return sb.ToString();
    }, ct);
}
