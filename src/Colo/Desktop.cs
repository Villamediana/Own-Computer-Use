using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Colo.Core;

namespace Colo;

/// <summary>Ventanas, lectura de texto por UI Automation y portapapeles.</summary>
static class Desktop
{
    const int MaxText = 8000;

    public static string Window(string action, string name)
    {
        var windows = Native.AppWindows();
        if (action == "list")
            return "ok:\n" + string.Join("\n", windows.Take(30).Select(h => $"{Native.Title(h)}  [{Native.ProcessName(h)}]"));

        IntPtr target;
        if (string.IsNullOrWhiteSpace(name)) target = Native.GetForegroundWindow();
        else
        {
            var want = WindowsComputer.Fold(name.Trim());
            target = windows.FirstOrDefault(h =>
                WindowsComputer.Fold(Native.Title(h)).Contains(want) || WindowsComputer.Fold(Native.ProcessName(h)).Contains(want));
            if (target == IntPtr.Zero) return $"error: no hay ventana con «{name}» (usa window list)";
        }
        switch (action)
        {
            case "focus":
            {
                // Hay programas que no contestan a tiempo: el cambio de foco nunca debe colgar a Colo.
                var ok = false;
                var t = new Thread(() => ok = Native.Focus(target)) { IsBackground = true };
                t.Start();
                if (!t.Join(1500)) return "error: la ventana no respondió";
                return ok ? $"ok: {Native.Title(target)}" : "error: Windows no dejó cambiar el foco";
            }
            case "minimize": Native.ShowWindow(target, Native.SW_MINIMIZE); return "ok";
            case "maximize": Native.ShowWindow(target, Native.SW_MAXIMIZE); return "ok";
            case "close": Native.PostMessage(target, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return "ok";
            default: return $"error: acción desconocida «{action}»";
        }
    }

    public static async Task<string> ReadTextAsync(string what, CancellationToken ct)
    {
        if (what == "selection") return await ReadSelectionAsync(ct);
        var hwnd = Native.GetForegroundWindow();
        var work = Task.Run(() => what switch { "focused" => Focused(), "list" => ListText(hwnd), _ => WindowText(hwnd) });
        if (await Task.WhenAny(work, Task.Delay(4000, ct)) != work) return "error: la ventana tardó demasiado en dar su texto";
        string text;
        try { text = (await work).Trim(); }
        catch (Exception ex) { return "error: no pude leer el texto (" + ex.Message + "); prueba look"; }
        if (text.Length == 0) return what == "list" ? "ok: (no hay filas de lista a la vista; prueba read_text window)" : "ok: (sin texto legible; prueba look)";
        return "ok:\n" + (text.Length > MaxText ? text[..MaxText] + "\n…(cortado)" : text);
    }

    /// <summary>
    /// Las filas visibles de las listas, enteras y en orden (en la lista de elementos van cortadas): los correos de
    /// una bandeja con remitente, asunto, hora y comienzo; archivos; resultados. Una fila sin nombre junta sus celdas.
    /// </summary>
    static string ListText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var root = AutomationElement.FromHandle(hwnd);
        var cr = new CacheRequest
        {
            TreeScope = TreeScope.Element | TreeScope.Children,
            TreeFilter = Automation.ControlViewCondition,
            AutomationElementMode = AutomationElementMode.None,
        };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(SelectionItemPattern.IsSelectedProperty);
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
        var rows = new List<(double Y, double X, string Text)>();
        var seen = new HashSet<string>();
        using (cr.Activate())
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
            {
                var name = e.Cached.Name ?? "";
                if (name.Trim().Length == 0)
                    try { name = string.Join(" · ", e.CachedChildren.Cast<AutomationElement>().Select(c => (c.Cached.Name ?? "").Trim()).Where(t => t.Length > 0)); }
                    catch (InvalidOperationException) { }
                name = string.Join(' ', name.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
                if (name.Length == 0 || !seen.Add(name)) continue;
                if (e.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is true) name += " (elegido)";
                var r = e.Cached.BoundingRectangle;
                rows.Add((r.IsEmpty ? 0 : r.Top, r.IsEmpty ? 0 : r.Left, name));
                if (rows.Count >= 120) break;
            }
        var sb = new StringBuilder();
        foreach (var row in rows.OrderBy(r => Math.Round(r.Y / 8)).ThenBy(r => r.X))
        {
            if (sb.Length >= MaxText) break;
            sb.Append("- ").AppendLine(row.Text);
        }
        return sb.ToString();
    }

    static string WindowText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var root = AutomationElement.FromHandle(hwnd);
        var sb = new StringBuilder();
        // Páginas web, correos y documentos exponen su contenido como Document con TextPattern.
        var docs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        foreach (AutomationElement d in docs)
        {
            if (sb.Length >= MaxText) break;
            if (d.TryGetCurrentPattern(TextPattern.Pattern, out var p))
                sb.AppendLine(((TextPattern)p).DocumentRange.GetText(MaxText - sb.Length));
        }
        if (sb.ToString().Trim().Length >= 40) return sb.ToString();

        // Sin documento: los textos visibles de la ventana, en orden.
        sb.Clear();
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
        var cr = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(ValuePattern.ValueProperty);
        cr.Add(AutomationElement.IsPasswordProperty);
        string? last = null;
        using (cr.Activate())
        {
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
            {
                if (sb.Length >= MaxText) break;
                var t = e.Cached.Name;
                if (e.GetCachedPropertyValue(ValuePattern.ValueProperty, true) is string v && v.Length > 0
                    && e.GetCachedPropertyValue(AutomationElement.IsPasswordProperty) is not true)
                    t = string.IsNullOrEmpty(t) ? v : $"{t}: {v}";
                if (string.IsNullOrWhiteSpace(t) || t == last) continue;
                sb.AppendLine(t.Trim());
                last = t;
            }
        }
        return sb.ToString();
    }

    /// <summary>El texto del campo con foco tal cual (sin «ok:»).</summary>
    public static string FocusedRaw()
    {
        try { return Focused(); } catch { return ""; }
    }

    /// <summary>Lo seleccionado tal cual (sin «ok:»); null si no hay.</summary>
    public static async Task<string?> SelectionRawAsync(CancellationToken ct)
    {
        var r = await ReadSelectionAsync(ct, max: int.MaxValue);
        return r.StartsWith("ok:\n", StringComparison.Ordinal) ? r[4..] : null;
    }

    static string Focused()
    {
        var e = AutomationElement.FocusedElement;
        if (e is null) return "";
        if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
        {
            var sel = ((TextPattern)tp).GetSelection().Select(r => r.GetText(MaxText)).FirstOrDefault(t => t.Length > 0);
            return sel ?? ((TextPattern)tp).DocumentRange.GetText(MaxText);
        }
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && !e.Current.IsPassword)
            return ((ValuePattern)vp).Current.Value;
        return e.Current.Name;
    }

    /// <summary>Se escribe ahí: un campo, un editor o un terminal (TextPattern), y no algo de solo lectura.</summary>
    static bool Writable(AutomationElement? e)
    {
        if (e is null) return false;
        try
        {
            if (!e.Current.IsEnabled) return false;
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && ((ValuePattern)vp).Current.IsReadOnly) return false;
            return e.Current.ControlType == ControlType.Edit || e.TryGetCurrentPattern(TextPattern.Pattern, out _);
        }
        catch (ElementNotAvailableException) { return false; }
    }

    static readonly Condition TextFields = new AndCondition(
        new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
        new PropertyCondition(AutomationElement.IsEnabledProperty, true),
        new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));

    /// <summary>
    /// Antes de escribir un dictado: si el foco ya está donde se escribe, nada. Si no (clic en un botón, la
    /// ventana sin campo activo), el campo bajo el ratón; si no hay, el campo de la ventana activa más cercano
    /// al ratón (en Teams, la caja de mensaje). Devuelve dónde va a escribir, para el registro.
    /// </summary>
    public static string EnsureTextFocus()
    {
        if (Direct() is { } here) return here;
        Native.GetCursorPos(out var m);
        return Nearest(Native.GetForegroundWindow(), new System.Windows.Point(m.x, m.y));
    }

    /// <summary>
    /// El dictado va directo solo si el foco ya está en un campo («el foco») o el ratón está encima de uno (clic
    /// ahí, «bajo el ratón»). null = ninguna de las dos: decide el modelo entre <see cref="Inputs"/>.
    /// </summary>
    public static string? Direct()
    {
        if (Writable(AutomationElement.FocusedElement)) return "el foco";
        Native.GetCursorPos(out var m);
        var under = AutomationElement.FromPoint(new System.Windows.Point(m.x, m.y));
        for (var i = 0; i < 4 && under is not null && !Writable(under); i++)
            under = TreeWalker.ControlViewWalker.GetParent(under);
        if (!Writable(under)) return null;
        // Clic donde está el ratón: el cursor de texto queda ahí.
        Input.ClickNow(new System.Drawing.Point(m.x, m.y));
        return "bajo el ratón";
    }

    /// <summary>Un campo donde se puede escribir, en alguna de las ventanas abiertas.</summary>
    public sealed record TextInput(string Label, IntPtr Window, System.Drawing.Point Center);

    /// <summary>
    /// Los campos de las ventanas abiertas (la activa primero, luego de arriba hacia abajo, también en el otro
    /// monitor), para que el modelo elija dónde va el dictado. Cada ventana se lee en paralelo y con tope de
    /// tiempo: la que tarda se queda fuera.
    /// </summary>
    public static List<TextInput> Inputs(int maxWindows = 6, int perWindow = 4)
    {
        var fg = Native.GetForegroundWindow();
        var windows = new List<IntPtr>();
        if (fg != IntPtr.Zero && !Native.IsOwnWindow(fg)) windows.Add(fg);
        foreach (var h in Native.AppWindows())
            if (windows.Count < maxWindows && h != fg && !Native.IsIconic(h)) windows.Add(h);
        var scans = windows.Select(h => Task.Run(() =>
        {
            try { return FieldsOf(h, perWindow); }
            catch { return []; }
        })).ToArray();
        Task.WaitAll(scans, 1200);
        var result = new List<TextInput>();
        foreach (var t in scans)
            if (t.IsCompletedSuccessfully) result.AddRange(t.Result);
        return result;
    }

    static List<TextInput> FieldsOf(IntPtr h, int max)
    {
        var app = Native.ProcessName(h);
        var title = Native.Title(h);
        if (title.Length > 50) title = title[..50] + "…";
        var browser = WindowsComputer.IsBrowser(app);
        var cr = new CacheRequest();
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.IsValuePatternAvailableProperty);
        cr.Add(ValuePattern.IsReadOnlyProperty);
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true)));
        var root = AutomationElement.FromHandle(h);
        AutomationElementCollection found;
        using (cr.Activate()) found = root.FindAll(TreeScope.Descendants, cond);
        var list = new List<TextInput>();
        foreach (AutomationElement e in found)
        {
            if (list.Count >= max) break;
            if (e.GetCachedPropertyValue(AutomationElement.IsValuePatternAvailableProperty) is true
                && e.GetCachedPropertyValue(ValuePattern.IsReadOnlyProperty, true) is true) continue;
            var type = (ControlType)e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty);
            // La página entera de un navegador tiene texto pero no es un campo donde escribir.
            if (browser && type == ControlType.Document) continue;
            if (e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty) is not System.Windows.Rect r
                || r.IsEmpty || r.Width < 8 || r.Height < 8) continue;
            var name = ((e.GetCachedPropertyValue(AutomationElement.NameProperty) as string) ?? "").Replace('\n', ' ').Trim();
            if (name.Length > 40) name = name[..40] + "…";
            var what = name.Length > 0 ? $"«{name}»" : Terminal.Kind(app, title) != Shell.None ? "la terminal" : "un campo";
            list.Add(new TextInput($"{what} en «{title}» [{app}]", h, new System.Drawing.Point((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2))));
        }
        return list;
    }

    /// <summary>Como hace Colo: trae la ventana, lleva la mano (el cursor azul) hasta el campo, clic, y ahí se escribe.</summary>
    public static string GoTo(TextInput t, AiCursor? cursor)
    {
        if (Native.GetForegroundWindow() != t.Window) Native.Focus(t.Window);
        cursor?.Glide(t.Center, "Clic");
        Input.Click(t.Center, "left", false);
        cursor?.Pulse();
        Thread.Sleep(60);
        return t.Label;
    }

    static string Nearest(IntPtr fg, System.Windows.Point mouse)
    {
        if (fg == IntPtr.Zero) return "nada (sin ventana)";
        // Todo lo que hace falta de cada campo viene en una sola ida (en Teams o un navegador el árbol es enorme).
        var cr = new CacheRequest();
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.IsTextPatternAvailableProperty);
        cr.Add(AutomationElement.IsValuePatternAvailableProperty);
        cr.Add(ValuePattern.IsReadOnlyProperty);
        AutomationElement? best = null;
        var bestDistance = double.MaxValue;
        var root = AutomationElement.FromHandle(fg);
        AutomationElementCollection found;
        using (cr.Activate()) found = root.FindAll(TreeScope.Descendants, TextFields);
        foreach (AutomationElement e in found)
        {
            if (e.GetCachedPropertyValue(AutomationElement.IsValuePatternAvailableProperty) is true
                && e.GetCachedPropertyValue(ValuePattern.IsReadOnlyProperty, true) is true) continue;
            if (e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) != ControlType.Edit
                && e.GetCachedPropertyValue(AutomationElement.IsTextPatternAvailableProperty) is not true) continue;
            if (e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty) is not System.Windows.Rect r
                || r.IsEmpty || r.Width < 8 || r.Height < 8) continue;
            var dx = Math.Max(0, Math.Max(r.Left - mouse.X, mouse.X - r.Right));
            var dy = Math.Max(0, Math.Max(r.Top - mouse.Y, mouse.Y - r.Bottom));
            var d = dx * dx + dy * dy;
            if (d < bestDistance) { best = e; bestDistance = d; }
        }
        if (best is null) return "nada (no hay campo de texto)";
        var box = (System.Windows.Rect)best.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
        var name = best.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "";
        // Clic en el centro: así también se activa en las páginas web, donde SetFocus no siempre basta.
        Input.ClickNow(new System.Drawing.Point((int)(box.Left + box.Width / 2), (int)(box.Top + box.Height / 2)));
        return $"«{name}» (el campo más cercano)";
    }

    static (DateTime At, List<(string Name, string Id)> Apps)? _startApps;
    static readonly object _appsLock = new();

    /// <summary>
    /// Los programas de Inicio (también los de la Store, como WhatsApp o el Outlook nuevo, que no tienen un .exe que
    /// se pueda llamar por nombre). Leerlos tarda: se guardan 10 minutos.
    /// </summary>
    public static List<(string Name, string Id)> StartApps()
    {
        lock (_appsLock)
        {
            if (_startApps is { } c && DateTime.Now - c.At < TimeSpan.FromMinutes(10)) return c.Apps;
            List<(string, string)> apps;
            try
            {
                apps = Sta(() =>
                {
                    var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                    dynamic folder = shell.GetType().InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, ["shell:AppsFolder"])!;
                    var list = new List<(string, string)>();
                    foreach (var item in folder.Items()) list.Add(((string)item.Name, (string)item.Path));
                    return list;
                });
            }
            catch (Exception ex)
            {
                Log.Write("Inicio: no pude leer los programas · " + ex.Message);
                // Se reintenta en un minuto, no en cada open.
                _startApps = (DateTime.Now - TimeSpan.FromMinutes(9), []);
                return [];
            }
            _startApps = (DateTime.Now, apps);
            return apps;
        }
    }

    /// <summary>
    /// Abre un programa de Inicio por su nombre: el exacto o, si no hay, el primero que lo contiene. null = no hay
    /// ninguno con ese nombre.
    /// </summary>
    public static string? OpenStartApp(string name)
    {
        var want = WindowsComputer.Fold(name.Trim());
        if (want.Length == 0) return null;
        var apps = StartApps();
        var found = apps.Where(a => WindowsComputer.Fold(a.Name) == want).Concat(apps.Where(a => WindowsComputer.Fold(a.Name).Contains(want)))
            .Select(a => ((string Name, string Id)?)a).FirstOrDefault();
        if (found is not { } app) return null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.Id) { UseShellExecute = true })?.Dispose();
        return app.Name;
    }

    /// <summary>Trae esa ventana al frente sin colgarse si el programa no contesta.</summary>
    public static bool FocusHandle(IntPtr h)
    {
        var ok = false;
        var t = new Thread(() => ok = Native.Focus(h)) { IsBackground = true };
        t.Start();
        return t.Join(1500) && ok;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    /// <summary>Hasta entonces los cambios del portapapeles son de Colo leyendo la selección: no van al historial.</summary>
    public static DateTime QuietUntil;

    /// <summary>Copia lo seleccionado con ctrl+c y deja el texto que había en el portapapeles.</summary>
    static async Task<string> ReadSelectionAsync(CancellationToken ct, int max = MaxText)
    {
        QuietUntil = DateTime.Now.AddSeconds(3);
        var before = Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        var seq = GetClipboardSequenceNumber();
        Input.Combo(Colo.Core.Keys.Parse("ctrl+c"));
        // Sin selección el portapapeles no cambia: así no se confunde con lo que ya había.
        for (var i = 0; i < 12 && GetClipboardSequenceNumber() == seq; i++) await Task.Delay(40, ct);
        if (GetClipboardSequenceNumber() == seq) { QuietUntil = DateTime.Now; return "ok: (no hay nada seleccionado)"; }
        await Task.Delay(30, ct);
        var text = Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : "") ?? "";
        if (before is not null) Sta(() => { Clipboard.SetText(before); return 0; });
        QuietUntil = DateTime.Now.AddMilliseconds(600);
        if (text.Length == 0) return "ok: (lo seleccionado no es texto)";
        return "ok:\n" + (text.Length > max ? text[..max] + "\n…(cortado)" : text);
    }

    public static string GetClipboard()
    {
        // Una clave copiada del cofre (o de un gestor de contraseñas) no se lee: no va al modelo.
        if (Sta(() => Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")))
            return "ok: (el portapapeles tiene una clave protegida; pégala con ctrl+v sin leerla)";
        var t = Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        if (t is null) return "ok: (el portapapeles no tiene texto)";
        return "ok:\n" + (t.Length > MaxText ? t[..MaxText] + "\n…(cortado)" : t);
    }

    public static string SetClipboard(string text)
    {
        if (text.Length == 0) return "error: text vacío";
        Sta(() => { Clipboard.SetText(text); return 0; });
        return "ok";
    }

    /// <summary>Archivos al portapapeles (como «Copiar» en el Explorador): se pegan con ctrl+v en WhatsApp, Teams, Outlook…</summary>
    public static string SetClipboardFiles(IReadOnlyList<string> paths)
    {
        var missing = paths.Where(p => !System.IO.File.Exists(p) && !System.IO.Directory.Exists(p)).ToList();
        if (missing.Count > 0) return "error: no existe " + string.Join(", ", missing);
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange(paths.ToArray());
        Sta(() => { Clipboard.SetFileDropList(list); return 0; });
        return $"ok: {paths.Count} archivo(s) en el portapapeles; ctrl+v donde quieras adjuntarlos";
    }

    /// <summary>El portapapeles exige un hilo STA.</summary>
    public static T Sta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            for (var i = 0; ; i++)
            {
                try { result = f(); return; }
                catch (System.Runtime.InteropServices.ExternalException) when (i < 4) { Thread.Sleep(30); }
                catch (Exception ex) { error = ex; return; }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }
}
