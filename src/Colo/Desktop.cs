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
        var work = Task.Run(() => what == "focused" ? Focused() : WindowText(hwnd));
        if (await Task.WhenAny(work, Task.Delay(4000, ct)) != work) return "error: la ventana tardó demasiado en dar su texto";
        string text;
        try { text = (await work).Trim(); }
        catch (Exception ex) { return "error: no pude leer el texto (" + ex.Message + "); prueba look"; }
        if (text.Length == 0) return "ok: (sin texto legible; prueba look)";
        return "ok:\n" + (text.Length > MaxText ? text[..MaxText] + "\n…(cortado)" : text);
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

    /// <summary>
    /// Se escribe ahí: un campo, un editor o un terminal (TextPattern), y no algo de solo lectura. La página de
    /// un navegador también tiene TextPattern, pero escribir «en la página» no va a ningún sitio.
    /// </summary>
    static bool Writable(AutomationElement? e)
    {
        if (e is null) return false;
        try
        {
            if (!e.Current.IsEnabled) return false;
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && ((ValuePattern)vp).Current.IsReadOnly) return false;
            var type = e.Current.ControlType;
            if (type == ControlType.Document && WindowsComputer.IsBrowser(ProcessOf(e.Current.ProcessId))) return false;
            return type == ControlType.Edit || e.TryGetCurrentPattern(TextPattern.Pattern, out _);
        }
        catch (ElementNotAvailableException) { return false; }
    }

    static string ProcessOf(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName.ToLowerInvariant(); }
        catch { return ""; }
    }

    static readonly Condition TextFields = new AndCondition(
        new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
        new PropertyCondition(AutomationElement.IsEnabledProperty, true),
        new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));

    /// <summary>Un sitio donde puede ir un dictado: la ventana y, si hace falta, dónde hacer clic.</summary>
    public sealed record TextTarget(string Label, IntPtr Window, System.Drawing.Point? Click);

    /// <summary>La última ventana donde se escribió un dictado (suele ser donde se sigue escribiendo).</summary>
    static IntPtr _lastWindow;
    static DateTime _lastAt;

    /// <summary>
    /// Dónde escribir un dictado. Si el foco ya está en un campo, o el ratón encima de uno, eso (Done) y nada más.
    /// Si no, los sitios posibles: el mejor campo de la ventana activa (nunca la barra de direcciones), el de la
    /// ventana bajo el ratón (puede estar en el otro monitor), la última donde se dictó y las terminales abiertas.
    /// Con más de uno, el modelo elige por lo que dijo (comando → terminal, mensaje → el chat).
    /// </summary>
    public static (string? Done, List<TextTarget> Options) FindTextTargets()
    {
        var focused = AutomationElement.FocusedElement;
        var inAddress = Writable(focused) && AddressBar(focused);
        if (Writable(focused) && !inAddress) return ("el foco", []);
        Native.GetCursorPos(out var m);
        var mouse = new System.Windows.Point(m.x, m.y);
        var under = AutomationElement.FromPoint(mouse);
        for (var i = 0; i < 4 && under is not null && !Writable(under); i++)
            under = TreeWalker.ControlViewWalker.GetParent(under);
        if (Writable(under) && !AddressBar(under))
        {
            // Clic donde está el ratón: el cursor de texto queda ahí.
            Input.ClickNow(new System.Drawing.Point(m.x, m.y));
            return ("bajo el ratón", []);
        }
        var options = new List<TextTarget>();
        var seen = new HashSet<IntPtr>();
        // El foco en la barra de direcciones puede ser a propósito (va a dictar una dirección): una opción más.
        if (inAddress) options.Add(new TextTarget($"barra de direcciones de «{Native.Title(Native.GetForegroundWindow())}»", Native.GetForegroundWindow(), null));
        void Add(IntPtr h)
        {
            if (h == IntPtr.Zero || Native.IsOwnWindow(h) || Native.IsIconic(h) || !seen.Add(h) || options.Count >= 6) return;
            var app = Native.ProcessName(h);
            var title = Native.Title(h);
            if (title.Length > 50) title = title[..50] + "…";
            if (Terminal.Kind(app, title) != Shell.None)
            {
                options.Add(new TextTarget($"terminal «{title}» [{app}]", h, null));
                return;
            }
            try
            {
                if (BestField(h, mouse) is { } f) options.Add(new TextTarget($"«{f.Name}» em «{title}» [{app}]", h, f.Center));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }
        Add(Native.GetForegroundWindow());
        Add(Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { x = m.x, y = m.y }), 2)); // GA_ROOT
        if (DateTime.Now - _lastAt < TimeSpan.FromMinutes(20) && Native.IsWindowVisible(_lastWindow)) Add(_lastWindow);
        foreach (var h in Native.AppWindows())
            if (Terminal.Kind(Native.ProcessName(h), Native.Title(h)) != Shell.None) Add(h);
        return (null, options);
    }

    /// <summary>Lleva el foco al sitio elegido (trae su ventana y hace clic en el campo).</summary>
    public static string Apply(TextTarget t)
    {
        if (Native.GetForegroundWindow() != t.Window) Native.Focus(t.Window);
        if (t.Click is { } p)
        {
            System.Threading.Thread.Sleep(60);
            Input.ClickNow(p);
        }
        _lastWindow = t.Window;
        _lastAt = DateTime.Now;
        return t.Label;
    }

    /// <summary>Para la herramienta dictate del modelo: el foco o el primer sitio posible.</summary>
    public static string EnsureTextFocus()
    {
        var (done, options) = FindTextTargets();
        if (done is not null) return done;
        return options.Count == 0 ? "nada (no hay campo de texto)" : Apply(options[0]);
    }

    /// <summary>Barra de direcciones o buscador del propio navegador: un dictado ahí sería una búsqueda en internet.</summary>
    static bool AddressBar(AutomationElement? e)
    {
        try { return e is not null && WindowsComputer.IsBrowser(ProcessOf(e.Current.ProcessId)) && WindowsComputer.IsAddressBar(e.Current.Name ?? ""); }
        catch (ElementNotAvailableException) { return false; }
    }

    /// <summary>
    /// El campo de esa ventana: el más cercano al ratón si el ratón está dentro; si no, el más grande (el cuerpo
    /// de un correo, la caja del chat). Nunca la barra de direcciones.
    /// </summary>
    static (string Name, System.Drawing.Point Center)? BestField(IntPtr hwnd, System.Windows.Point mouse)
    {
        var browser = WindowsComputer.IsBrowser(Native.ProcessName(hwnd).ToLowerInvariant());
        // Todo lo que hace falta de cada campo viene en una sola ida (en Teams o un navegador el árbol es enorme).
        var cr = new CacheRequest();
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.IsTextPatternAvailableProperty);
        cr.Add(AutomationElement.IsValuePatternAvailableProperty);
        cr.Add(ValuePattern.IsReadOnlyProperty);
        var root = AutomationElement.FromHandle(hwnd);
        var win = root.Current.BoundingRectangle;
        var inside = !win.IsEmpty && win.Contains(mouse);
        AutomationElementCollection found;
        using (cr.Activate()) found = root.FindAll(TreeScope.Descendants, TextFields);
        AutomationElement? best = null;
        var bestScore = double.MaxValue;
        foreach (AutomationElement e in found)
        {
            if (e.GetCachedPropertyValue(AutomationElement.IsValuePatternAvailableProperty) is true
                && e.GetCachedPropertyValue(ValuePattern.IsReadOnlyProperty, true) is true) continue;
            var type = e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty);
            if (type != ControlType.Edit && e.GetCachedPropertyValue(AutomationElement.IsTextPatternAvailableProperty) is not true) continue;
            if (browser && (type == ControlType.Document || WindowsComputer.IsAddressBar(e.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? ""))) continue;
            if (e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty) is not System.Windows.Rect r
                || r.IsEmpty || r.Width < 8 || r.Height < 8) continue;
            double score;
            if (inside)
            {
                var dx = Math.Max(0, Math.Max(r.Left - mouse.X, mouse.X - r.Right));
                var dy = Math.Max(0, Math.Max(r.Top - mouse.Y, mouse.Y - r.Bottom));
                score = dx * dx + dy * dy;
            }
            else score = -r.Width * r.Height;
            if (score < bestScore) { best = e; bestScore = score; }
        }
        if (best is null) return null;
        var box = (System.Windows.Rect)best.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
        var name = (best.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "").Trim();
        if (name.Length > 40) name = name[..40] + "…";
        // Clic en el centro: así también se activa en las páginas web, donde SetFocus no siempre basta.
        return (name.Length > 0 ? name : "campo de texto", new System.Drawing.Point((int)(box.Left + box.Width / 2), (int)(box.Top + box.Height / 2)));
    }

    /// <summary>
    /// Abre un programa instalado por su nombre como sale en Inicio (también los de la Store, como WhatsApp, que
    /// no tienen un .exe que se pueda llamar por nombre). null = no hay ninguno con ese nombre.
    /// </summary>
    public static string? OpenStartApp(string name)
    {
        var want = WindowsComputer.Fold(name.Trim());
        if (want.Length == 0) return null;
        var found = Sta(() =>
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic folder = shell.GetType().InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, ["shell:AppsFolder"])!;
            (string Name, string Id)? exact = null, partial = null;
            foreach (var item in folder.Items())
            {
                string n = item.Name, id = item.Path;
                var f = WindowsComputer.Fold(n);
                if (f == want) { exact = (n, id); break; }
                if (partial is null && f.Contains(want)) partial = (n, id);
            }
            return exact ?? partial;
        });
        if (found is not { } app) return null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.Id) { UseShellExecute = true })?.Dispose();
        return app.Name;
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
