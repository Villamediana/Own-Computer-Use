using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

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
        var text = work.Result.Trim();
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

    /// <summary>Copia lo seleccionado con ctrl+c y devuelve el portapapeles como estaba.</summary>
    static async Task<string> ReadSelectionAsync(CancellationToken ct)
    {
        var before = Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        Sta(() => { Clipboard.Clear(); return 0; });
        Input.Combo(Colo.Core.Keys.Parse("ctrl+c"));
        string text = "";
        for (var i = 0; i < 10 && text.Length == 0; i++)
        {
            await Task.Delay(40, ct);
            text = Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : "") ?? "";
        }
        if (before is not null) Sta(() => { Clipboard.SetText(before); return 0; });
        if (text.Length == 0) return "ok: (no hay nada seleccionado)";
        return "ok:\n" + (text.Length > MaxText ? text[..MaxText] + "\n…(cortado)" : text);
    }

    public static string GetClipboard()
    {
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
