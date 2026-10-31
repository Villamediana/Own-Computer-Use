using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Colo.Core;

namespace Colo;

/// <summary>La terminal en pantalla (Windows Terminal, consola clásica…) leída por UI Automation, y comandos ocultos.</summary>
static class Term
{
    /// <summary>La terminal delante o, si no, la más arriba de las abiertas.</summary>
    static IntPtr Find()
    {
        var fg = Native.GetForegroundWindow();
        if (Terminal.Kind(Native.ProcessName(fg), Native.Title(fg)) != Shell.None) return fg;
        return Native.AppWindows().FirstOrDefault(h => Terminal.Kind(Native.ProcessName(h), Native.Title(h)) != Shell.None);
    }

    /// <summary>
    /// Lo que se ve en la terminal. Windows Terminal y conhost exponen el búfer con TextPattern: se piden solo las
    /// líneas visibles (el búfer entero puede tener miles), y si no hay, el final del documento.
    /// </summary>
    static string Text(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        AutomationElement? best = null;
        var focused = AutomationElement.FocusedElement;
        if (focused is not null && (bool)focused.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty)
            && IsInside(focused, root))
            best = focused;
        if (best is null)
        {
            var area = 0.0;
            foreach (AutomationElement e in root.FindAll(TreeScope.Subtree,
                         new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true)))
            {
                var r = e.Current.BoundingRectangle;
                if (r.IsEmpty || e.Current.IsOffscreen) continue;
                if (r.Width * r.Height > area) { best = e; area = r.Width * r.Height; }
            }
        }
        if (best is null || !best.TryGetCurrentPattern(TextPattern.Pattern, out var p)) return "";
        var tp = (TextPattern)p;
        var sb = new StringBuilder();
        try
        {
            foreach (var r in tp.GetVisibleRanges())
            {
                var t = r.GetText(-1);
                sb.Append(t);
                if (!t.EndsWith('\n')) sb.Append('\n');
            }
        }
        catch (InvalidOperationException) { }
        if (sb.ToString().Trim().Length > 0) return sb.ToString();
        // Sin rangos visibles: el búfer entero (Tail se queda con el final).
        return tp.DocumentRange.GetText(-1);
    }

    static bool IsInside(AutomationElement e, AutomationElement root)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            for (var x = e; x is not null; x = walker.GetParent(x))
                if (Automation.Compare(x, root)) return true;
        }
        catch (ElementNotAvailableException) { }
        return false;
    }

    static async Task<string?> TailAsync(IntPtr h, int lines, CancellationToken ct)
    {
        var work = Task.Run(() => Text(h), ct);
        if (await Task.WhenAny(work, Task.Delay(3000, ct)) != work) return null;
        return Terminal.Tail(await work, lines);
    }

    public static async Task<string> ReadAsync(int lines, CancellationToken ct)
    {
        var h = Find();
        if (h == IntPtr.Zero) return "error: no hay ninguna terminal abierta";
        string? tail;
        try { tail = await TailAsync(h, lines, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return "error: no pude leer la terminal (" + ex.Message + ")"; }
        if (tail is null) return "error: la terminal tardó demasiado en dar su texto";
        if (tail.Length == 0) return "ok: (terminal sin texto legible; prueba look)";
        return (Terminal.PromptBack(tail) ? "ok: (lista, esperando comando)\n" : "ok: (parece que algo corre)\n") + tail;
    }

    /// <summary>Espera a que vuelva el prompt (y la pantalla no cambie en dos lecturas seguidas) o al tope de tiempo.</summary>
    public static async Task<string> WaitAsync(double seconds, int lines, CancellationToken ct)
    {
        var h = Find();
        if (h == IntPtr.Zero) return "error: no hay ninguna terminal abierta";
        var sw = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120));
        string? last = null;
        while (true)
        {
            string? tail;
            try { tail = await TailAsync(h, Math.Max(lines, 3), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return "error: no pude leer la terminal (" + ex.Message + ")"; }
            if (tail is not null && tail == last && Terminal.PromptBack(tail))
                return $"ok: terminó ({sw.Elapsed.TotalSeconds:0.#} s)\n" + Terminal.Tail(tail, lines);
            if (sw.Elapsed > limit)
                return $"ok: sigue corriendo tras {limit.TotalSeconds:0} s\n" + Terminal.Tail(tail ?? last ?? "", lines);
            last = tail;
            await Task.Delay(450, ct);
        }
    }

    public static Task<string> RunAsync(string command, string shell, string cwd, double seconds, bool confirmed, CancellationToken ct)
    {
        command = command.Trim();
        if (command.Length == 0) return Task.FromResult("error: falta command");
        if (Terminal.Dangerous(command) && !confirmed)
            return Task.FromResult("error: este comando borra, mueve o cambia algo. Pregúntale con done si lo ejecutas; si dice que sí, repítelo con confirmed=true.");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(seconds <= 0 ? 30 : seconds, 1, 120));
        if (shell == "cmd")
            // /s: cmd quita solo las comillas de fuera y deja el comando tal cual (con sus propias comillas).
            return Terminal.RunAsync("cmd.exe", $"/d /s /c \"chcp 65001 >nul & {command}\"", cwd, timeout, ct);
        // En base64 (UTF-16) el comando llega intacto, sin pelear con las comillas de la línea de comandos.
        var script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return Terminal.RunAsync("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, cwd, timeout, ct);
    }
}
