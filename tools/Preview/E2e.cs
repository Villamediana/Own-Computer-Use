using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Colo;
using Colo.Core;

/// <summary>
/// Prueba de punta a punta en un Windows real: Colo entero (onda, cursor, agente, modelo de verdad)
/// recibe órdenes como si las hubiera oído y se comprueba en la pantalla que pasó lo pedido.
/// Uso: Preview e2e &lt;carpeta de salida&gt; &lt;prueba.html&gt;  (COLO_ENDPOINT, COLO_KEY, COLO_MODEL en el entorno)
/// </summary>
static class E2e
{
    sealed record Case(string Name, Action Arrange, string Order, Func<string?> Check);

    public static int Run(string outDir, string page)
    {
        Directory.CreateDirectory(outDir);
        var key = Environment.GetEnvironmentVariable("COLO_KEY") ?? "";
        if (key.Length == 0) { Console.WriteLine("falta COLO_KEY"); return 2; }
        Store.Save(new Settings
        {
            Provider = Provider.Azure,
            Endpoint = Environment.GetEnvironmentVariable("COLO_ENDPOINT") ?? "",
            ApiKey = key,
            Model = Environment.GetEnvironmentVariable("COLO_MODEL") ?? "gpt-5.6-luna",
        });

        Widget? widget = null;
        var loaded = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            widget = new Widget();
            widget.Load += (_, _) => loaded.Set();
            Application.Run(widget);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        if (!loaded.Wait(30_000)) { Console.WriteLine("la onda no arrancó"); return 2; }
        Thread.Sleep(2000);

        var chrome = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        }.FirstOrDefault(File.Exists);
        var profile = Path.Combine(Path.GetTempPath(), "colo-e2e-chrome");

        var cases = new List<Case>
        {
            new("bloc", () =>
            {
                Process.Start("notepad.exe")?.Dispose();
                Focus(WaitWindow("notepad", 15_000));
            }, "escribe hola mundo", () =>
                NotepadText() is var t && t.Contains("hola mundo", StringComparison.OrdinalIgnoreCase) ? null : $"el Bloc tiene «{NotepadText()}»"),

            new("pestaña", () =>
            {
                if (chrome is null) throw new InvalidOperationException("no hay Chrome");
                Process.Start(chrome, $"--no-first-run --no-default-browser-check --force-renderer-accessibility --start-maximized --user-data-dir=\"{profile}\" \"{new Uri(Path.GetFullPath(page)).AbsoluteUri}\"")?.Dispose();
                WaitTitle("chrome", "Prueba Colo", 60_000);
                Focus(WaitWindow("notepad", 5_000));
            }, "abre una pestaña nueva en el navegador que tenía abierto", () =>
                Front() is var (proc, title) && proc == "chrome" && !title.Contains("Prueba Colo") ? null : $"delante: {Front()}"),

            new("botón", () => { }, "vuelve a la pestaña de la página de prueba y pulsa el botón Aceptar", () =>
                Front() is var (proc, title) && proc == "chrome" && title.Contains("Aceptado") ? null : $"delante: {Front()}"),

            new("campo", () => { }, "en el campo nombre escribe Miguel", () =>
                Front() is var (proc, title) && proc == "chrome" && title.Contains("Nombre: Miguel") ? null : $"delante: {Front()}"),

            new("dibujo", () => { }, "haz clic en el cuadrado rojo", () =>
                Front() is var (proc, title) && proc == "chrome" && title.Contains("Rojo") ? null : $"delante: {Front()}"),

            new("print", () => { }, "toma un print", () =>
                Sta(() => Clipboard.ContainsImage()) ? null : "no hay imagen en el portapapeles"),
        };

        var report = new StringBuilder();
        var failed = 0;
        var n = 0;
        foreach (var c in cases)
        {
            n++;
            string? problem;
            var events = new List<string>();
            var sw = Stopwatch.StartNew();
            try
            {
                c.Arrange();
                Thread.Sleep(800);
                var done = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnEvent(AgentEvent e)
                {
                    lock (events) events.Add($"{sw.ElapsedMilliseconds,6} ms  {e.Kind}: {e.Text}");
                    if (e.Kind is AgentEventKind.Done or AgentEventKind.Error or AgentEventKind.Ignored or AgentEventKind.Stopped)
                        done.TrySetResult(e);
                }
                widget!.Brain.Event += OnEvent;
                widget.Invoke(() => widget.Command(c.Order));
                var finished = done.Task.Wait(TimeSpan.FromSeconds(30));
                widget.Brain.Event -= OnEvent;
                if (!finished)
                {
                    widget.Brain.Stop();
                    problem = "no terminó en 30 s";
                }
                else
                {
                    // La página o la ventana pueden tardar un poco en reflejarlo.
                    problem = c.Check();
                    for (var i = 0; i < 10 && problem is not null; i++)
                    {
                        Thread.Sleep(300);
                        problem = c.Check();
                    }
                    if (done.Task.Result.Kind == AgentEventKind.Error) problem ??= "el agente dio error";
                }
            }
            catch (Exception ex) { problem = "excepción: " + ex.Message; }

            Shot(Path.Combine(outDir, $"{n}-{c.Name}.png"));
            var line = $"{(problem is null ? "OK  " : "FALLA")} {c.Name} «{c.Order}» ({sw.ElapsedMilliseconds} ms){(problem is null ? "" : " → " + problem)}";
            Console.WriteLine(line);
            lock (events) foreach (var e in events) Console.WriteLine("      " + e);
            report.AppendLine(line);
            lock (events) foreach (var e in events) report.AppendLine("      " + e);
            if (problem is not null) failed++;
        }

        report.AppendLine().AppendLine($"{cases.Count - failed}/{cases.Count} bien");
        File.WriteAllText(Path.Combine(outDir, "informe.txt"), report.ToString());
        try { File.Copy(Log.FilePath, Path.Combine(outDir, "colo.log"), true); } catch { }
        Console.WriteLine($"{cases.Count - failed}/{cases.Count} bien");
        widget!.BeginInvoke(() => Application.ExitThread());
        return failed;
    }

    /// <summary>Sin modelo: abre el Bloc y la página de prueba y lista los elementos que ve UI Automation.</summary>
    public static int Marks(string page)
    {
        var pc = new WindowsComputer();
        var failed = 0;
        void Show(string name, string process)
        {
            Focus(WaitWindow(process, 60_000));
            Thread.Sleep(1500);
            var obs = pc.ObserveAsync(false, CancellationToken.None).Result;
            Console.WriteLine($"{name}: {obs.Marks.Count} elementos · {obs.Window}");
            foreach (var m in obs.Marks.Take(25)) Console.WriteLine($"   [{m.Id}] {m.Role} «{m.Name}» {m.Value}");
            if (!obs.Marks.Any(m => m.Name.Length > 0)) failed++;
        }
        Process.Start("notepad.exe")?.Dispose();
        Show("Bloc", "notepad");
        var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        if (File.Exists(chrome))
        {
            var profile = Path.Combine(Path.GetTempPath(), "colo-marks-chrome");
            Process.Start(chrome, $"--no-first-run --no-default-browser-check --start-maximized --user-data-dir=\"{profile}\" \"{new Uri(Path.GetFullPath(page)).AbsoluteUri}\"")?.Dispose();
            WaitTitle("chrome", "Prueba Colo", 60_000);
            Show("Chrome", "chrome");
        }
        try { Console.WriteLine(File.ReadAllText(Log.FilePath)); } catch { }
        return failed;
    }

    static IntPtr WaitWindow(string process, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var h = Native.AppWindows().FirstOrDefault(w => Native.ProcessName(w).Equals(process, StringComparison.OrdinalIgnoreCase));
            if (h != IntPtr.Zero) return h;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"no apareció la ventana de {process}");
    }

    static void WaitTitle(string process, string title, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (Native.AppWindows().Any(w => Native.ProcessName(w).Equals(process, StringComparison.OrdinalIgnoreCase) && Native.Title(w).Contains(title)))
                return;
            Thread.Sleep(250);
        }
        throw new TimeoutException($"{process} no mostró «{title}»");
    }

    static void Focus(IntPtr h)
    {
        if (!Native.Focus(h)) throw new InvalidOperationException("no pude traer al frente " + Native.WindowLabel(h));
    }

    static (string Process, string Title) Front()
    {
        var h = Native.GetForegroundWindow();
        return (Native.ProcessName(h).ToLowerInvariant(), Native.Title(h));
    }

    static string NotepadText()
    {
        var h = Native.AppWindows().FirstOrDefault(w => Native.ProcessName(w).Equals("notepad", StringComparison.OrdinalIgnoreCase));
        if (h == IntPtr.Zero) return "";
        var root = AutomationElement.FromHandle(h);
        var edit = root.FindFirst(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        if (edit is null) return "";
        if (edit.TryGetCurrentPattern(TextPattern.Pattern, out var tp)) return ((TextPattern)tp).DocumentRange.GetText(-1);
        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) return ((ValuePattern)vp).Current.Value;
        return "";
    }

    static T Sta<T>(Func<T> f)
    {
        T result = default!;
        var t = new Thread(() => result = f());
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    static void Shot(string path)
    {
        try
        {
            var b = Screen.PrimaryScreen!.Bounds;
            using var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
            bmp.Save(path, ImageFormat.Png);
        }
        catch (Exception ex) { Console.WriteLine("captura: " + ex.Message); }
    }
}
