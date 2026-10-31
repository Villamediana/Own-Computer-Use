using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Colo.Core;

public enum Shell { None, PowerShell, Cmd, Unix }

/// <summary>Terminales: de qué tipo es la ventana, el final de su texto, si volvió el prompt y comandos en segundo plano.</summary>
public static class Terminal
{
    static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsterminal", "openconsole", "conhost", "cmd", "powershell", "pwsh", "wsl", "bash", "mintty",
        "alacritty", "wezterm-gui", "tabby", "hyper", "warp", "kitty", "putty", "mobaxterm", "termius",
    };

    /// <summary>Qué shell hay en esa ventana (None = no es una terminal).</summary>
    public static Shell Kind(string process, string title)
    {
        if (!Hosts.Contains(process)) return Shell.None;
        var t = title.ToLowerInvariant();
        if (process.Equals("cmd", StringComparison.OrdinalIgnoreCase)) return Shell.Cmd;
        if (process is "powershell" or "pwsh") return Shell.PowerShell;
        if (process.ToLowerInvariant() is "wsl" or "bash" or "mintty" or "putty" or "mobaxterm" or "termius" or "kitty") return Shell.Unix;
        // Windows Terminal y similares: el título dice qué perfil (o qué programa) corre.
        if (t.Contains("powershell") || t.Contains("pwsh") || t.StartsWith("ps ")) return Shell.PowerShell;
        if (t.Contains("cmd") || t.Contains("command prompt") || t.Contains("símbolo del sistema") || t.Contains("prompt de comando")) return Shell.Cmd;
        if (t.Contains("ubuntu") || t.Contains("debian") || t.Contains("wsl") || t.Contains("bash") || t.Contains("zsh")
            || t.Contains("mingw") || t.Contains("@") || t.StartsWith("~") || t.StartsWith("/")) return Shell.Unix;
        return Shell.PowerShell;
    }

    const string Common = """
 ↑/↓ historial · tab completa · esc borra la línea · ctrl+c corta lo que corre · ctrl+l limpia.
 Salir de un programa: q (less, man, git log), esc y :q (vim), ctrl+d o exit (python, node, ssh, agentes),
 ctrl+c dos veces (agentes tipo claude/codex), ctrl+b d (tmux).
 Para un comando: type_text con submit y después terminal wait (devuelve cómo acabó).
""";

    /// <summary>Atajos y sintaxis de esa terminal, para el turno en que está delante.</summary>
    public static string Hint(Shell shell) => shell switch
    {
        Shell.PowerShell => "Terminal PowerShell: ctrl+r busca en el historial; encadena con ; (no &&); ls, cd, cat, $env:X.\n" + Common,
        Shell.Cmd => "Terminal cmd: F7 lista el historial; encadena con &&; dir, cd, type, set X, cls.\n" + Common,
        Shell.Unix => "Terminal bash/WSL: ctrl+r busca en el historial; !! repite; ctrl+a/ctrl+e inicio/fin de línea; cd - vuelve.\n" + Common,
        _ => "",
    };

    /// <summary>Las últimas líneas con algo (sin las vacías del final).</summary>
    public static string Tail(string text, int lines)
    {
        var all = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        while (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
        return string.Join("\n", all.Skip(Math.Max(0, all.Count - Math.Max(1, lines))));
    }

    static readonly Regex Prompt = new(
        @"(^PS [^\n]*>$)|(^[A-Za-z]:\\[^\n]*>$)|([\w\]\)~/:.-]\s?[$#]$)|([A-Za-z~/\]\)]\s?%$)|([❯➜λ»]$)|(^>>>$)|(^[$#%]$)",
        RegexOptions.Compiled);

    /// <summary>La última línea parece un prompt esperando el siguiente comando (PowerShell, cmd, bash, python…).</summary>
    public static bool PromptBack(string tail)
    {
        var last = tail.Replace("\r", "").Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return last.Length > 0 && Prompt.IsMatch(last);
    }

    // Límites de palabra que también cortan en «-»: Format-Table no es format, pkill no es kill.
    static readonly Regex Danger = new(
        @"(?<![\w-])(rm|del|erase|rd|rmdir|remove-item|ri|format|format-volume|clear-disk|diskpart|mkfs(\.\w+)?|shutdown|restart-computer|stop-computer|reg\s+delete|remove-itemproperty|takeown|icacls|cipher\s+/w|vssadmin|bcdedit|dd|kill|killall|pkill|stop-process|taskkill|set-executionpolicy|move-item|mv|move|ren|rename-item|truncate)(?![\w-])"
        + @"|git\s+(push\s+.*(-f(?![\w-])|--force)|reset\s+--hard|clean\s+-\w*f|branch\s+-D|checkout\s+--\s|restore\s)"
        + @"|\bdrop\s+(table|database|schema)\b|\bchmod\s+-R\b|\bchown\s+-R\b"
        + @"|(^|[^>2])>\s*[^\s>&|]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Borra, sobrescribe, mata procesos o cambia el sistema: run pide que el usuario lo confirme antes.</summary>
    public static bool Dangerous(string command) => Danger.IsMatch(command);

    /// <summary>Corta la salida larga dejando el principio y, sobre todo, el final (donde están los errores).</summary>
    public static string Clip(string output, int max = 6000)
    {
        output = output.TrimEnd();
        if (output.Length <= max) return output;
        var head = max / 4;
        var tail = max - head;
        return output[..head] + $"\n…({output.Length - max} letras cortadas)…\n" + output[^tail..];
    }

    /// <summary>
    /// Ejecuta un comando oculto y devuelve lo que escribió (salida y errores juntos, en orden aproximado).
    /// Si pasa el tiempo, mata el proceso y devuelve lo que alcanzó a salir.
    /// </summary>
    public static async Task<string> RunAsync(string file, string arguments, string? cwd, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = cwd is { Length: > 0 } && Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        var output = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.Start();
        // Sin entrada: un comando que pregunta algo termina en vez de quedarse esperando.
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var timedOut = false;
        try { await p.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            timedOut = true;
        }
        if (!timedOut) p.WaitForExit(); // vacía lo que quede en los lectores asíncronos
        string text;
        lock (output) text = Clip(output.ToString());
        if (timedOut) return $"ok: lo corté tras {timeout.TotalSeconds:0} s sin terminar. Salida:\n{text}";
        var code = p.ExitCode;
        if (text.Length == 0) text = "(sin salida)";
        return code == 0 ? "ok:\n" + text : $"ok: terminó con código {code}:\n{text}";
    }
}
