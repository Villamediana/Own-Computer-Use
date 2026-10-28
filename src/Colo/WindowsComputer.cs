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
    const int UiaBudgetMs = 1200;
    const int MaxMarks = 220;

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

    /// <summary>Cursor azul que muestra lo que hace Colo (opcional).</summary>
    public AiCursor? Cursor { get; set; }

    public string ShotsDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Colo");

    sealed record Element(string Role, string Name, Rectangle Rect);

    public Task<Observation> ObserveAsync(CancellationToken ct) => Task.Run(() =>
    {
        var hwnd = Native.GetForegroundWindow();
        var screen = hwnd == IntPtr.Zero ? Screen.PrimaryScreen! : Screen.FromHandle(hwnd);
        var b = screen.Bounds;
        var window = Native.WindowLabel(hwnd);

        // UI Automation en paralelo con la captura; si tarda demasiado, sigue sin marcas.
        var uia = Task.Run(() => Collect(hwnd, b));

        using var full = Capture(b);
        var elements = uia.Wait(UiaBudgetMs, ct) ? uia.Result : [];

        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(b.Width, b.Height));
        var w = (int)Math.Round(b.Width * scale);
        var h = (int)Math.Round(b.Height * scale);
        using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var marks = new List<Mark>();
        var rects = new Dictionary<int, Rectangle>();
        var names = new Dictionary<int, string>();
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            var id = 0;
            foreach (var e in elements)
            {
                id++;
                rects[id] = e.Rect;
                names[id] = e.Name;
                marks.Add(new Mark(id, e.Role, e.Name));
                var color = Palette[id % Palette.Length];
                var r = new RectangleF((float)((e.Rect.X - b.X) * scale), (float)((e.Rect.Y - b.Y) * scale),
                    (float)(e.Rect.Width * scale), (float)(e.Rect.Height * scale));
                using var pen = new Pen(Color.FromArgb(220, color), 1.4f);
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                var label = id.ToString();
                var size = g.MeasureString(label, font);
                var lx = r.X;
                var ly = r.Y - size.Height + 1 < 0 ? r.Y : r.Y - size.Height + 1;
                using var bg = new SolidBrush(color);
                g.FillRectangle(bg, lx, ly, size.Width, size.Height - 1);
                g.DrawString(label, font, Brushes.White, lx, ly - 1);
            }
        }

        lock (_lock)
        {
            _screen = b;
            _scale = scale;
            _marks = rects;
            _names = names;
        }
        return new Observation(Jpeg(small, 78), w, h, window, marks);
    }, ct);

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
        (ControlType.Edit, "campo de texto"), (ControlType.ComboBox, "lista desplegable"), (ControlType.CheckBox, "casilla"),
        (ControlType.RadioButton, "opción"), (ControlType.MenuItem, "menú"), (ControlType.TabItem, "pestaña"),
        (ControlType.ListItem, "elemento"), (ControlType.TreeItem, "nodo"), (ControlType.DataItem, "fila"),
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
            using (cr.Activate())
            {
                var root = AutomationElement.FromHandle(h);
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
                    found.Add(new Element(role, name, r));
                }
            }
        }

        try { From(hwnd); } catch { }
        if (found.Count < MaxMarks)
            try { From(Native.FindWindow("Shell_TrayWnd", null)); } catch { }

        return found
            .GroupBy(e => e.Rect).Select(g => g.First())
            .Take(MaxMarks)
            .OrderBy(e => e.Rect.Y / 8).ThenBy(e => e.Rect.X)
            .ToList();
    }

    bool Target(ToolCall c, out Point p, out string error)
    {
        lock (_lock)
        {
            error = "";
            if (c.Int("mark") is int id)
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
            if (c.Int("x") is int x && c.Int("y") is int y)
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

    public async Task<string> ExecuteAsync(ToolCall c, CancellationToken ct)
    {
        switch (c.Name)
        {
            case "click":
            {
                if (!Target(c, out var p, out var err)) return err;
                var verb = c.Bool("double") ? "Doble clic" : c.Str("button") == "right" ? "Clic derecho" : "Clic";
                Cursor?.Glide(p, verb + NameOf(c));
                Input.Click(p, c.Str("button"), c.Bool("double"));
                Cursor?.Pulse();
                return "ok";
            }
            case "type_text":
                Cursor?.Say("Escribiendo…");
                Input.Type(c.Str("text"));
                return "ok";
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
            case "scroll":
            {
                var hasTarget = c.Int("mark") is not null || c.Int("x") is not null;
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
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
                await Task.Delay(1200, ct);
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

    string NameOf(ToolCall c)
    {
        lock (_lock)
            return c.Int("mark") is int id && _names.TryGetValue(id, out var n) && n.Length > 0
                ? $" «{(n.Length > 28 ? n[..28] + "…" : n)}»" : "";
    }

    static string Fold(string s)
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

        // El portapapeles exige un hilo STA.
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { Clipboard.SetImage(bmp); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return failure is null
            ? $"ok: en el portapapeles (pegar con ctrl+v) y guardado en {path}"
            : $"error: guardado en {path} pero no entró al portapapeles ({failure.Message})";
    }
}
