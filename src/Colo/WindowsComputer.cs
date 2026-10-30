using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Colo.Core;

namespace Colo;

/// <summary>
/// Pantalla real: captura de la pantalla de la ventana activa, elementos de UI Automation
/// numerados encima, y ejecución de las acciones con ratón y teclado.
/// </summary>
sealed class WindowsComputer : IComputer
{
    const int MaxSide = 1366;
    const int UiaBudgetMs = 1500;
    const int MaxMarks = 160;

    static readonly Color[] Palette =
    [
        Color.FromArgb(228, 87, 46), Color.FromArgb(37, 99, 235), Color.FromArgb(22, 163, 74),
        Color.FromArgb(192, 38, 211), Color.FromArgb(202, 138, 4), Color.FromArgb(14, 116, 144),
    ];

    readonly object _lock = new();
    Rectangle _screen;
    double _scale = 1;
    Dictionary<int, Rectangle> _marks = [];
    Dictionary<int, string> _names = [];
    Task? _uia;

    /// <summary>Cursor azul que muestra lo que hace Colo (opcional).</summary>
    public AiCursor? Cursor { get; set; }

    public string ShotsDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Colo");

    sealed record Element(string Role, string Name, string Value, Rectangle Rect, string State);

    public Task<Observation> ObserveAsync(bool withImage, CancellationToken ct) => Task.Run(() =>
    {
        var hwnd = Native.GetForegroundWindow();
        var screen = hwnd == IntPtr.Zero ? Screen.PrimaryScreen! : Screen.FromHandle(hwnd);
        var b = screen.Bounds;
        var window = Native.WindowLabel(hwnd);

        // UI Automation en paralelo con la captura; si tarda demasiado, sigue sin marcas.
        // Si una lectura anterior sigue colgada (app que no responde), no se lanza otra encima.
        List<Element> elements = [];
        Task<List<Element>>? uia = null;
        lock (_lock)
        {
            if (_uia is null || _uia.IsCompleted) _uia = uia = Task.Run(() => Collect(hwnd, b));
        }
        Bitmap? full = withImage ? Capture(b) : null;
        var uiaWatch = Stopwatch.StartNew();
        if (uia is not null && uia.Wait(UiaBudgetMs, ct)) elements = uia.Result;
        // Sin marcas el modelo trabaja solo con la captura (más lento y menos preciso): que quede en el registro.
        if (elements.Count == 0)
            Log.Write(uia is null ? $"UIA: lectura anterior sigue colgada · «{window}»"
                : uia.IsCompleted ? $"UIA: 0 elementos · «{window}»"
                : $"UIA: no contestó en {uiaWatch.ElapsedMilliseconds} ms · «{window}»");
        // Sin elementos legibles (juego, escritorio remoto…) la lista no sirve: va la captura.
        if (full is null && elements.Count == 0) full = Capture(b);

        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(b.Width, b.Height));
        var marks = new List<Mark>();
        var rects = new Dictionary<int, Rectangle>();
        var names = new Dictionary<int, string>();
        var id = 0;
        foreach (var e in elements)
        {
            id++;
            rects[id] = e.Rect;
            names[id] = e.Name;
            marks.Add(new Mark(id, e.Role, e.Name, e.Value, e.State));
        }
        lock (_lock)
        {
            _screen = b;
            _scale = scale;
            _marks = rects;
            _names = names;
        }
        var others = OtherWindows(hwnd);
        if (full is null) return new Observation([], 0, 0, window, marks, others);

        using (full)
        {
            var w = (int)Math.Round(b.Width * scale);
            var h = (int)Math.Round(b.Height * scale);
            using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, 0, 0, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
                foreach (var (n, rect) in rects)
                {
                    var color = Palette[n % Palette.Length];
                    var r = new RectangleF((float)((rect.X - b.X) * scale), (float)((rect.Y - b.Y) * scale),
                        (float)(rect.Width * scale), (float)(rect.Height * scale));
                    using var pen = new Pen(Color.FromArgb(220, color), 1.4f);
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                    var label = n.ToString();
                    var size = g.MeasureString(label, font);
                    var ly = r.Y - size.Height + 1 < 0 ? r.Y : r.Y - size.Height + 1;
                    using var bg = new SolidBrush(color);
                    g.FillRectangle(bg, r.X, ly, size.Width, size.Height - 1);
                    g.DrawString(label, font, Brushes.White, r.X, ly - 1);
                }
            }
            return new Observation(Jpeg(small, 76), w, h, window, marks, others);
        }
    }, ct);

    static List<string> OtherWindows(IntPtr front)
    {
        try
        {
            return Native.AppWindows().Where(h => h != front).Take(12).Select(h =>
            {
                var title = Native.Title(h);
                if (title.Length > 50) title = title[..50] + "…";
                return $"{title} [{Native.ProcessName(h)}]";
            }).ToList();
        }
        catch { return []; }
    }

    static Bitmap Capture(Rectangle b)
    {
        var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    static byte[] Jpeg(Bitmap bmp, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, p);
        return ms.ToArray();
    }

    static readonly (ControlType Type, string Role)[] Roles =
    [
        (ControlType.Button, "botón"), (ControlType.SplitButton, "botón"), (ControlType.Hyperlink, "enlace"),
        (ControlType.Edit, "campo"), (ControlType.ComboBox, "lista"), (ControlType.CheckBox, "casilla"),
        (ControlType.RadioButton, "opción"), (ControlType.MenuItem, "menú"), (ControlType.TabItem, "pestaña"),
        (ControlType.ListItem, "ítem"), (ControlType.TreeItem, "nodo"), (ControlType.DataItem, "fila"),
        (ControlType.Slider, "deslizador"), (ControlType.Spinner, "número"),
    ];

    static readonly Condition Interactive = new AndCondition(
        new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
        new OrCondition(Roles.Select(r => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, r.Type)).ToArray()));

    static List<Element> Collect(IntPtr hwnd, Rectangle screen)
    {
        var found = new List<Element>();
        var limit = screen.Width * screen.Height / 2;

        void From(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            var cr = new CacheRequest
            {
                TreeFilter = Automation.ControlViewCondition,
                AutomationElementMode = AutomationElementMode.None,
            };
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(AutomationElement.BoundingRectangleProperty);
            cr.Add(AutomationElement.IsPasswordProperty);
            cr.Add(ValuePattern.ValueProperty);
            cr.Add(AutomationElement.HasKeyboardFocusProperty);
            cr.Add(SelectionItemPattern.IsSelectedProperty);
            cr.Add(TogglePattern.ToggleStateProperty);
            // La raíz se pide antes de activar la caché: con AutomationElementMode.None quedaría sin
            // referencia viva y FindAll falla («SafeHandle cannot be null»), o sea, nunca había marcas.
            var root = AutomationElement.FromHandle(h);
            using (cr.Activate())
            {
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Interactive))
                {
                    var br = e.Cached.BoundingRectangle;
                    if (br.IsEmpty || double.IsInfinity(br.Width) || br.Width < 4 || br.Height < 4) continue;
                    var r = Rectangle.FromLTRB((int)br.Left, (int)br.Top, (int)br.Right, (int)br.Bottom);
                    r.Intersect(screen);
                    if (r.Width < 4 || r.Height < 4 || r.Width * r.Height > limit) continue;
                    var type = e.Cached.ControlType;
                    var role = Roles.FirstOrDefault(x => x.Type == type).Role ?? "elemento";
                    var name = (e.Cached.Name ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
                    if (name.Length > 60) name = name[..60] + "…";
                    var value = "";
                    if (type == ControlType.Edit || type == ControlType.ComboBox)
                    {
                        var v = e.GetCachedPropertyValue(ValuePattern.ValueProperty, true);
                        if (v is string str && !(e.GetCachedPropertyValue(AutomationElement.IsPasswordProperty) is true))
                            value = str.Length > 40 ? str[..40].Replace('\n', ' ') + "…" : str.Replace('\n', ' ');
                    }
                    var state = e.GetCachedPropertyValue(AutomationElement.HasKeyboardFocusProperty, true) is true ? "foco"
                        : e.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is true ? (type == ControlType.TabItem ? "activa" : "elegido")
                        : e.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true) is ToggleState.On ? "marcada" : "";
                    found.Add(new Element(role, name, value, r, state));
                }
            }
        }

        try { From(hwnd); } catch (Exception ex) { Log.Write("UIA: falló la ventana · " + ex.Message); }
        if (found.Count < MaxMarks)
            try { From(Native.FindWindow("Shell_TrayWnd", null)); } catch (Exception ex) { Log.Write("UIA: falló la barra de tareas · " + ex.Message); }

        return found
            .GroupBy(e => e.Rect).Select(g => g.First())
            .Take(MaxMarks)
            .OrderBy(e => e.Rect.Y / 8).ThenBy(e => e.Rect.X)
            .ToList();
    }

    bool Target(ToolCall c, out Point p, out string error, string prefix = "")
    {
        lock (_lock)
        {
            error = "";
            if (c.Mark(prefix + "mark") is int id)
            {
                if (_marks.TryGetValue(id, out var r))
                {
                    p = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                    return true;
                }
                p = default;
                error = $"error: no existe la marca {id}";
                return false;
            }
            if (c.Int(prefix + "x") is int x && c.Int(prefix + "y") is int y)
            {
                p = new Point(_screen.X + (int)Math.Round(x / _scale), _screen.Y + (int)Math.Round(y / _scale));
                if (_screen.Contains(p)) return true;
                error = $"error: ({x},{y}) está fuera de la captura";
                return false;
            }
            p = new Point(_screen.X + _screen.Width / 2, _screen.Y + _screen.Height / 2);
            error = "error: falta mark o x,y";
            return false;
        }
    }

    /// <summary>Ejecuta y deja en el registro qué se pidió, sobre qué ventana y qué pasó.</summary>
    public async Task<string> ExecuteAsync(ToolCall c, CancellationToken ct)
    {
        var front = Native.WindowLabel(Native.GetForegroundWindow());
        var sw = Stopwatch.StartNew();
        string result;
        try { result = await ExecuteCoreAsync(c, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"acción {c.Name} {c.Args.ToJsonString()} · ventana «{front}» · EXCEPCIÓN {ex.Message}");
            throw;
        }
        var shortResult = result.Replace('\n', ' ');
        if (shortResult.Length > 160) shortResult = shortResult[..160] + "…";
        Log.Write($"acción {c.Name} {c.Args.ToJsonString()} · ventana «{front}» → {shortResult} ({sw.ElapsedMilliseconds} ms)");
        return result;
    }

    async Task<string> ExecuteCoreAsync(ToolCall c, CancellationToken ct)
    {
        switch (c.Name)
        {
            case "click":
            {
                if (!Target(c, out var p, out var err)) return err;
                var verb = c.Bool("double") ? "Doble clic" : c.Str("button") == "right" ? "Clic derecho" : "Clic";
                Cursor?.Glide(p, verb + NameOf(c));
                Input.Click(p, c.Str("button"), c.Bool("double"));
                Cursor?.Pulse(c.Bool("double"));
                return "ok";
            }
            case "type_text":
            {
                if (c.Mark() is not null)
                {
                    if (!Target(c, out var p, out var err)) return err;
                    Cursor?.Glide(p, "Clic" + NameOf(c));
                    Input.Click(p, "left", false);
                    Cursor?.Pulse();
                    await Task.Delay(60, ct);
                }
                Cursor?.Say("Escribiendo…");
                if (c.Bool("replace")) { Input.Combo(Colo.Core.Keys.Parse("ctrl+a")); await Task.Delay(30, ct); }
                Input.Type(c.Str("text"));
                if (c.Bool("submit")) { await Task.Delay(30, ct); Input.Combo(Colo.Core.Keys.Parse("enter")); }
                return "ok";
            }
            case "dictate":
            {
                // Sin un campo con foco, el que más sentido tiene (bajo el ratón o el más cercano).
                var where = await Task.Run(Desktop.EnsureTextFocus, ct);
                Cursor?.Say("Escribiendo…");
                Input.Type(c.Str("text"));
                return where == "el foco" ? "ok" : "ok: escrito en " + where;
            }
            case "drag":
            {
                if (!Target(c, out var from, out var err, "from_")) return err;
                if (!Target(c, out var to, out err, "to_")) return err;
                Cursor?.Glide(from, "Arrastrando");
                Input.Drag(from, to, Cursor is null ? null : Cursor.Follow);
                return "ok";
            }
            case "browser":
                return await Browser(c, ct);
            case "window":
                Cursor?.Say(c.Str("action") == "list" ? "Ventanas" : "Ventana");
                return Desktop.Window(c.Str("action"), c.Str("name"));
            case "read_text":
                Cursor?.Say("Leyendo…");
                return await Desktop.ReadTextAsync(c.Str("what"), ct);
            case "edit_text":
                Cursor?.Say("Corrigiendo…");
                return await EditTextAsync(c.Str("find"), c.Str("replace"), ct);
            case "clipboard":
                return c.Str("action") == "set" ? Desktop.SetClipboard(c.Str("text")) : Desktop.GetClipboard();
            case "press_keys":
            {
                var keys = Colo.Core.Keys.Parse(c.Str("keys"));
                var n = Math.Clamp(c.Int("repeat") ?? 1, 1, 50);
                Cursor?.Say(c.Str("keys").ToLowerInvariant());
                for (var i = 0; i < n; i++)
                {
                    Input.Combo(keys);
                    if (n > 1) await Task.Delay(25, ct);
                }
                return "ok";
            }
            case "scroll" when c.Str("to") is "top" or "bottom":
                Cursor?.Say(c.Str("to") == "top" ? "Al principio" : "Al final");
                Input.Combo(Colo.Core.Keys.Parse(c.Str("to") == "top" ? "ctrl+home" : "ctrl+end"));
                return "ok";
            case "scroll":
            {
                var hasTarget = c.Mark() is not null || c.Int("x") is not null;
                if (!Target(c, out var p, out var err) && hasTarget) return err;
                Cursor?.Glide(p, c.Str("direction") switch { "up" => "Subiendo", "down" => "Bajando", _ => "Desplazando" });
                Input.Scroll(p, c.Str("direction"), Math.Clamp(c.Int("amount") ?? 5, 1, 30));
                return "ok";
            }
            case "open":
            {
                var target = c.Str("target").Trim();
                if (target.Length == 0) return "error: target vacío";
                Cursor?.Say("Abriendo " + (target.Length > 30 ? target[..30] + "…" : target));
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
                catch (System.ComponentModel.Win32Exception)
                {
                    // No es un archivo ni un .exe: un programa de Inicio por su nombre (los de la Store, como WhatsApp).
                    var app = await Task.Run(() => Desktop.OpenStartApp(target), ct);
                    if (app is null) return $"error: no encontré «{target}» ni como archivo ni entre los programas instalados";
                    await Task.Delay(1200, ct);
                    return $"ok: abrí «{app}»";
                }
                await Task.Delay(900, ct);
                return "ok";
            }
            case "screenshot":
                Cursor?.Say("Print");
                return Screenshot(c.Str("area") == "window");
            case "find_files":
                Cursor?.Say("Buscando archivos");
                return FindFiles(c.Str("query"), ct);
            case "wait":
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(c.Num("seconds") ?? 1, 0.1, 10)), ct);
                return "ok";
            default:
                return $"error: herramienta desconocida «{c.Name}»";
        }
    }

    /// <summary>
    /// Cambia un trozo del texto con foco sin reescribirlo entero: lo busca con UI Automation, lo selecciona y
    /// escribe encima (así se conserva el formato en Word, correos…). Sin TextPattern, por el valor del campo.
    /// </summary>
    static async Task<string> EditTextAsync(string find, string replace, CancellationToken ct)
    {
        if (find.Length == 0) return "error: falta find";
        var selected = await Task.Run(() =>
        {
            var el = AutomationElement.FocusedElement;
            if (el is null) return "error: no hay ningún campo con foco";
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
            {
                var range = ((TextPattern)tp).DocumentRange.FindText(find, false, true);
                if (range is null) return $"error: no encontré «{find}» en el texto con foco";
                range.Select();
                return "selected";
            }
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && !((ValuePattern)vp).Current.IsReadOnly)
            {
                var value = ((ValuePattern)vp).Current.Value ?? "";
                var i = value.IndexOf(find, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return $"error: no encontré «{find}» en el campo";
                ((ValuePattern)vp).SetValue(value[..i] + replace + value[(i + find.Length)..]);
                return "ok";
            }
            return "error: este campo no deja editar por partes; usa read_text focused y type_text con replace";
        }, ct);
        if (selected != "selected") return selected;
        await Task.Delay(50, ct);
        if (replace.Length > 0) Input.Type(replace);
        else Input.Combo(Colo.Core.Keys.Parse("delete"));
        return "ok";
    }

    static readonly Dictionary<string, (string Keys, string Label)> BrowserKeys = new()
    {
        ["back"] = ("alt+left", "Atrás"),
        ["forward"] = ("alt+right", "Adelante"),
        ["reload"] = ("f5", "Recargando"),
        ["close_tab"] = ("ctrl+w", "Cerrando pestaña"),
        ["next_tab"] = ("ctrl+tab", "Pestaña siguiente"),
        ["prev_tab"] = ("ctrl+shift+tab", "Pestaña anterior"),
    };

    /// <summary>Navegador por teclado: funciona igual en Chrome, Edge y Firefox, y es más rápido que buscar botones.</summary>
    static readonly string[] Browsers =
        ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium", "arc", "zen", "librewolf", "waterfox", "iexplore"];

    async Task<string> Browser(ToolCall c, CancellationToken ct)
    {
        var action = c.Str("action");
        // Los atajos del navegador en otra app escribirían donde no toca (p. ej. la URL dentro del Bloc de notas):
        // si delante no hay un navegador, se trae al frente el último navegador abierto.
        var front = Native.ProcessName(Native.GetForegroundWindow()).ToLowerInvariant();
        if (!Browsers.Contains(front))
        {
            var browser = Native.AppWindows().FirstOrDefault(h => Browsers.Contains(Native.ProcessName(h).ToLowerInvariant()));
            if (browser == IntPtr.Zero)
                return "error: no hay ningún navegador abierto. Usa open con la URL (abre el navegador predeterminado).";
            var focus = Desktop.Window("focus", Native.ProcessName(browser));
            if (focus.StartsWith("error", StringComparison.Ordinal)) return focus;
            await Task.Delay(150, ct);
        }
        async Task Keys(string combo, int after = 70)
        {
            Input.Combo(Colo.Core.Keys.Parse(combo));
            await Task.Delay(after, ct);
        }
        switch (action)
        {
            case "go":
            case "search":
            {
                var text = action == "go" ? c.Str("url") : c.Str("text");
                if (text.Length == 0) return "error: falta " + (action == "go" ? "url" : "text");
                Cursor?.Say(action == "go" ? "Abriendo " + Short(text) : "Buscando «" + Short(text) + "»");
                await Keys("ctrl+l", 90);
                Input.Type(text);
                await Keys("enter", 700);
                return "ok";
            }
            case "new_tab":
                Cursor?.Say("Pestaña nueva");
                await Keys("ctrl+t", 150);
                if (c.Str("url").Length > 0)
                {
                    Input.Type(c.Str("url"));
                    await Keys("enter", 700);
                }
                return "ok";
            case "find":
                if (c.Str("text").Length == 0) return "error: falta text";
                Cursor?.Say("Buscando en la página");
                await Keys("ctrl+f", 150);
                Input.Type(c.Str("text"));
                await Keys("enter");
                return "ok";
            case "tab":
            {
                var n = Math.Clamp(c.Int("n") ?? 1, 1, 9);
                Cursor?.Say($"Pestaña {n}");
                await Keys($"ctrl+{n}");
                return "ok";
            }
            default:
                if (!BrowserKeys.TryGetValue(action, out var k)) return $"error: acción desconocida «{action}»";
                Cursor?.Say(k.Label);
                await Keys(k.Keys, action == "reload" ? 600 : 120);
                return "ok";
        }
    }

    static string Short(string s) => s.Length > 30 ? s[..30] + "…" : s;

    string NameOf(ToolCall c)
    {
        lock (_lock)
            return c.Mark() is int id && _names.TryGetValue(id, out var n) && n.Length > 0
                ? $" «{(n.Length > 28 ? n[..28] + "…" : n)}»" : "";
    }

    internal static string Fold(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    static string FindFiles(string query, CancellationToken ct)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "error: query vacío";
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var home = F(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            F(Environment.SpecialFolder.Desktop), F(Environment.SpecialFolder.MyDocuments), Path.Combine(home, "Downloads"),
            F(Environment.SpecialFolder.MyPictures), F(Environment.SpecialFolder.MyVideos), F(Environment.SpecialFolder.MyMusic),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var hits = new List<FileSystemInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();
        foreach (var root in roots)
        {
            try
            {
                foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
                {
                    if (sw.ElapsedMilliseconds > 2500 || ct.IsCancellationRequested) break;
                    var name = Fold(e.Name);
                    if (words.All(name.Contains) && seen.Add(e.FullName)) hits.Add(e);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (hits.Count == 0) return $"ok: nada con «{query}»";
        return "ok:\n" + string.Join("\n", hits.OrderByDescending(h => h.LastWriteTime).Take(15)
            .Select(h => $"{h.FullName}{(h is DirectoryInfo ? "\\" : "")} ({h.LastWriteTime:dd/MM/yyyy})"));
    }

    string Screenshot(bool windowOnly)
    {
        var hwnd = Native.GetForegroundWindow();
        Rectangle area;
        if (windowOnly && Native.GetWindowRect(hwnd, out var wr))
            area = Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
        else
            area = (hwnd == IntPtr.Zero ? Screen.PrimaryScreen! : Screen.FromHandle(hwnd)).Bounds;
        if (area.Width < 2 || area.Height < 2) return "error: área vacía";

        using var bmp = Capture(area);
        Directory.CreateDirectory(ShotsDir);
        var path = Path.Combine(ShotsDir, $"print-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        bmp.Save(path, ImageFormat.Png);

        Exception? failure = null;
        try { Desktop.Sta(() => { Clipboard.SetImage(bmp); return 0; }); }
        catch (Exception ex) { failure = ex; }
        return failure is null
            ? $"ok: en el portapapeles (pegar con ctrl+v) y guardado en {path}"
            : $"error: guardado en {path} pero no entró al portapapeles ({failure.Message})";
    }
}
