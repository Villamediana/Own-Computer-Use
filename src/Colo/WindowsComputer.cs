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
    readonly FileOps _files = new(new WindowsRecycleBin());
    readonly Web _web = new();

    /// <summary>Qué tuvo delante el usuario (lo alimenta el vigía de ventanas del Widget).</summary>
    public Colo.Core.Activity Activity { get; } = new();
    /// <summary>Lo último copiado (lo alimenta el aviso de portapapeles del Widget).</summary>
    public ClipHistory Clips { get; } = new();
    /// <summary>Cofre de contraseñas (null = sin cofre).</summary>
    public Vault? Vault { get; set; }
    public Agenda? Agenda { get; set; }
    public StageHost? Stage { get; set; }
    /// <summary>Grabar reuniones (lo lleva la onda, que tiene la pastilla y el atajo).</summary>
    public Func<ToolCall, string>? Meeting { get; set; }
    Rectangle _screen;
    double _scale = 1;
    Dictionary<int, Rectangle> _marks = [];
    // El rectángulo entero de cada marca (un nodo desplegado abarca sus hijos; la marca apunta solo a su fila).
    Dictionary<int, Rectangle> _raws = [];
    Dictionary<int, string> _names = [];
    Task? _uia;
    // Lectura adelantada (al apretar el atajo): se usa una vez, si la ventana es la misma y no es vieja.
    (IntPtr Hwnd, string Title, DateTime At, Task<(List<Element>, string?)> Work)? _ahead;
    static readonly TimeSpan AheadTtl = TimeSpan.FromSeconds(5);
    // Ventanas ya leídas hace poco: la primera lectura de una app web (Outlook nuevo, Teams, un navegador) arma
    // su árbol de accesibilidad y tarda más; ahí se espera más antes de seguir sin marcas.
    readonly Dictionary<IntPtr, DateTime> _seen = [];
    const int FirstUiaBudgetMs = 3000;

    /// <summary>Cursor azul que muestra lo que hace Colo (opcional).</summary>
    public AiCursor? Cursor { get; set; }

    public string ShotsDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Colo");

    /// <summary>Raw: el rectángulo entero que da la app (sin recortar a la pantalla), para casar los nodos de un árbol.</summary>
    sealed record Element(string Role, string Name, string Value, Rectangle Rect, string State, Rectangle Raw, string In = "");

    /// <summary>
    /// Empieza a leer la ventana activa ya (al apretar el atajo de hablar): mientras habla y se transcribe, UI
    /// Automation recorre la ventana, y el primer paso no espera.
    /// </summary>
    public void Prefetch()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || Native.IsOwnWindow(hwnd)) return;
        var b = Screen.FromHandle(hwnd).Bounds;
        var title = Native.Title(hwnd);
        // Los programas de Inicio (para open) también, si hace rato que no se leen.
        _ = Task.Run(Desktop.StartApps);
        lock (_lock)
        {
            if (_uia is { IsCompleted: false }) return;
            var work = Task.Run(() => Collect(hwnd, b));
            _uia = work;
            _ahead = (hwnd, title, DateTime.Now, work);
        }
    }

    public Task<Observation> ObserveAsync(bool withImage, CancellationToken ct) => Task.Run(() =>
    {
        var hwnd = Native.GetForegroundWindow();
        var screen = hwnd == IntPtr.Zero ? Screen.PrimaryScreen! : Screen.FromHandle(hwnd);
        var b = screen.Bounds;
        var window = Native.WindowLabel(hwnd);
        var app = Native.ProcessName(hwnd).ToLowerInvariant();

        // UI Automation en paralelo con la captura; si tarda demasiado, sigue sin marcas.
        // Si una lectura anterior sigue colgada (app que no responde), no se lanza otra encima.
        List<Element> elements = [];
        string? url = null;
        Task<(List<Element>, string?)>? uia = null;
        var title = Native.Title(hwnd);
        int budget;
        var ahead = false;
        lock (_lock)
        {
            if (_ahead is { } a && a.Hwnd == hwnd && a.Title == title && DateTime.Now - a.At < AheadTtl) { uia = a.Work; ahead = true; }
            else if (_uia is null || _uia.IsCompleted) _uia = uia = Task.Run(() => Collect(hwnd, b));
            _ahead = null;
            budget = _seen.TryGetValue(hwnd, out var seen) && DateTime.Now - seen < TimeSpan.FromMinutes(10) ? UiaBudgetMs : FirstUiaBudgetMs;
        }
        Bitmap? full = withImage ? Capture(b) : null;
        var uiaWatch = Stopwatch.StartNew();
        if (uia is not null && uia.Wait(budget, ct)) (elements, url) = uia.Result;
        if (elements.Count > 0)
            lock (_lock)
            {
                Log.Write($"UIA: {elements.Count} elementos en {uiaWatch.ElapsedMilliseconds} ms{(ahead ? " (leída al apretar el atajo)" : "")} · «{window}»");
                _seen[hwnd] = DateTime.Now;
                if (_seen.Count > 64) foreach (var old in _seen.Where(kv => DateTime.Now - kv.Value > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList()) _seen.Remove(old);
            }
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
        var raws = new Dictionary<int, Rectangle>();
        var names = new Dictionary<int, string>();
        var id = 0;
        foreach (var e in elements)
        {
            id++;
            rects[id] = e.Rect;
            raws[id] = e.Raw;
            names[id] = e.Name;
            marks.Add(new Mark(id, e.Role, e.Name, e.Value, e.State, e.In));
        }
        lock (_lock)
        {
            _screen = b;
            _scale = scale;
            _marks = rects;
            _raws = raws;
            _names = names;
        }
        var others = OtherWindows(hwnd);
        var details = app == "explorer" && Files.Explorer(hwnd) is { } ex ? Files.Details(ex)
            : url is { Length: > 0 } ? ["URL: " + (url.Length > 400 ? url[..400] + "…" : url)] : null;
        if (full is null) return new Observation([], 0, 0, window, marks, others, app, details);

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
            return new Observation(Jpeg(small, 76), w, h, window, marks, others, app, details);
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

    /// <summary>La barra de direcciones del navegador se llama así en inglés, portugués y español (Chrome, Edge, Firefox).</summary>
    internal static bool IsAddressBar(string name)
    {
        var n = Fold(name);
        return n.Contains("address") || n.Contains("endereco") || n.Contains("direccion") || n.Contains("url");
    }

    static (List<Element>, string?) Collect(IntPtr hwnd, Rectangle screen)
    {
        var browser = Browsers.Contains(Native.ProcessName(hwnd).ToLowerInvariant());
        var limit = screen.Width * screen.Height / 2;

        // Cada parte es una ida a otro proceso: a la vez. Lo que el programa abrió encima (menú, sugerencias,
        // desplegable) va primero: es lo que se va a elegir y no puede quedarse fuera por el tope de marcas.
        (List<Element> Found, string? Url) Part(IntPtr h, string what, bool main)
        {
            try { return From(h, screen, limit, browser && main, main); }
            catch (Exception ex) { Log.Write($"UIA: falló {what} · {ex.Message}"); return ([], null); }
        }
        var popups = Native.PopupsAbove(hwnd).Select(p => Task.Run(() => Part(p, "un menú", false))).ToList();
        var window = Task.Run(() => Part(hwnd, "la ventana", true));
        var tray = Task.Run(() => Part(Native.FindWindow("Shell_TrayWnd", null), "la barra de tareas", false));
        var found = new List<Element>();
        foreach (var p in popups) found.AddRange(p.Result.Found);
        var (mine, url) = window.Result;
        found.AddRange(mine);
        found.AddRange(tray.Result.Found);

        return (found
            .GroupBy(e => e.Rect).Select(g => g.First())
            .Take(MaxMarks)
            .OrderBy(e => e.Rect.Y / 8).ThenBy(e => e.Rect.X)
            .ToList(), url);
    }

    static (List<Element>, string?) From(IntPtr h, Rectangle screen, int limit, bool browser, bool main)
    {
        var found = new List<Element>();
        string? url = null;
        if (h == IntPtr.Zero) return (found, url);
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
        cr.Add(ExpandCollapsePattern.ExpandCollapseStateProperty);
        // La raíz se pide antes de activar la caché: con AutomationElementMode.None quedaría sin
        // referencia viva y FindAll falla («SafeHandle cannot be null»), o sea, nunca había marcas.
        var root = AutomationElement.FromHandle(h);
        var nodes = false;
        using (cr.Activate())
        {
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Interactive))
            {
                var br = e.Cached.BoundingRectangle;
                if (br.IsEmpty || double.IsInfinity(br.Width) || br.Width < 4 || br.Height < 4) continue;
                var raw = Rectangle.FromLTRB((int)br.Left, (int)br.Top, (int)br.Right, (int)br.Bottom);
                var r = raw;
                r.Intersect(screen);
                if (r.Width < 4 || r.Height < 4 || r.Width * r.Height > limit) continue;
                var type = e.Cached.ControlType;
                var role = Roles.FirstOrDefault(x => x.Type == type).Role ?? "elemento";
                var name = (e.Cached.Name ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (name.Length > 60) name = name[..60] + "…";
                var focused = e.GetCachedPropertyValue(AutomationElement.HasKeyboardFocusProperty, true) is true;
                var value = "";
                if (type == ControlType.Edit || type == ControlType.ComboBox)
                {
                    var v = e.GetCachedPropertyValue(ValuePattern.ValueProperty, true);
                    if (v is string str && !(e.GetCachedPropertyValue(AutomationElement.IsPasswordProperty) is true))
                    {
                        if (browser && url is null && type == ControlType.Edit && str.Length > 0 && IsAddressBar(name)) url = str;
                        // El campo con foco va entero: con 40 letras el modelo «mejoraba» un correo reescribiendo
                        // solo el principio cortado (y lo borraba). Los demás, cortos y diciendo que falta texto.
                        var max = focused ? 3000 : 40;
                        str = str.Replace('\r', ' ').Replace('\n', ' ');
                        value = str.Length > max ? $"{str[..max]}… [faltan {str.Length - max} letras: read_text]" : str;
                    }
                }
                var state = focused ? "foco"
                    : e.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is true ? (type == ControlType.TabItem ? "activa" : "elegido")
                    : e.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true) is ToggleState.On ? "marcada" : "";
                // Cuentas y carpetas del correo, secciones, «mostrar más», desplegables: si está abierto o cerrado.
                var fold = e.GetCachedPropertyValue(ExpandCollapsePattern.ExpandCollapseStateProperty, true) switch
                {
                    ExpandCollapseState.Collapsed => "plegado",
                    ExpandCollapseState.Expanded or ExpandCollapseState.PartiallyExpanded => "desplegado",
                    _ => "",
                };
                if (fold.Length > 0) state = state.Length > 0 ? $"{state}, {fold}" : fold;
                nodes |= type == ControlType.TreeItem;
                found.Add(new Element(role, name, value, r, state, raw));
            }
        }
        // Un nodo desplegado de una app web (Outlook nuevo) abarca a sus hijos: el centro caía en una carpeta del
        // medio. La marca apunta a su propia fila, hasta donde empieza el primer nodo de dentro.
        if (nodes)
        {
            var all = found.Where(o => o.Role == "nodo").Select(o => o.Raw).ToList();
            for (var i = 0; i < found.Count; i++)
            {
                if (found[i].Role != "nodo" || TreeRows.Own(found[i].Raw, all) is not { } row) continue;
                row.Intersect(screen);
                if (row.Width >= 4 && row.Height >= 4) found[i] = found[i] with { Rect = row };
            }
        }
        // Nodos de un árbol: cada uno dice dentro de qué nodo está (la cuenta de cada «Bandeja de entrada»).
        Dictionary<Rectangle, string> parents = [];
        if (main && nodes)
            try { parents = TreeParents(root); }
            catch (Exception ex) { Log.Write("UIA: falló el árbol · " + ex.Message); }
        for (var i = 0; i < found.Count; i++)
            if (found[i].Role == "nodo" && parents.TryGetValue(found[i].Raw, out var path))
                found[i] = found[i] with { In = path };
        return (found, url);
    }

    /// <summary>
    /// Para cada nodo de los árboles visibles, su camino: la cuenta y la carpeta que lo contienen («cuenta ›
    /// Bandeja de entrada»). Una sola ida por árbol: la caché trae solo los nodos, con su anidamiento.
    /// </summary>
    static Dictionary<Rectangle, string> TreeParents(AutomationElement root)
    {
        var map = new Dictionary<Rectangle, string>();
        var trees = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree)));
        var cr = new CacheRequest
        {
            TreeScope = TreeScope.Element | TreeScope.Descendants,
            TreeFilter = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)),
            AutomationElementMode = AutomationElementMode.None,
        };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        static string Short(string s)
        {
            s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length > 32 ? s[..32] + "…" : s;
        }
        void Walk(AutomationElement e, string top, string parent)
        {
            foreach (AutomationElement c in e.CachedChildren)
            {
                var br = c.Cached.BoundingRectangle;
                if (parent.Length > 0 && !br.IsEmpty && !double.IsInfinity(br.Width))
                    map[Rectangle.FromLTRB((int)br.Left, (int)br.Top, (int)br.Right, (int)br.Bottom)] = top == parent ? top : $"{top} › {parent}";
                var name = Short(c.Cached.Name ?? "");
                if (c.Cached.ControlType != ControlType.TreeItem || name.Length == 0) Walk(c, top, parent);
                else Walk(c, top.Length > 0 ? top : name, name);
            }
        }
        var n = 0;
        foreach (AutomationElement t in trees)
        {
            if (n++ >= 3) break;
            Walk(t.GetUpdatedCache(cr), "", "");
        }
        return map;
    }

    bool Target(ToolCall c, out Point p, out string error, string prefix = "")
    {
        if (!TargetCore(c, out p, out error, prefix)) return false;
        // El palco va por encima: si tapa el punto, se aparta para que el clic llegue a la ventana de debajo.
        Stage?.Uncover(p);
        return true;
    }

    bool TargetCore(ToolCall c, out Point p, out string error, string prefix)
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
        // El secreto que el modelo manda para guardar no se escribe en el registro.
        var args = c.Name == "vault" && c.Args["secret"] is not null
            ? new System.Text.Json.Nodes.JsonObject(c.Args.Where(kv => kv.Key != "secret").Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))).ToJsonString() + " +secreto"
            : c.Args.ToJsonString();
        string result;
        try { result = await ExecuteCoreAsync(c, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"acción {c.Name} {args} · ventana «{front}» · EXCEPCIÓN {ex.Message}");
            throw;
        }
        var shortResult = result.Replace('\n', ' ');
        if (shortResult.Length > 160) shortResult = shortResult[..160] + "…";
        Log.Write($"acción {c.Name} {args} · ventana «{front}» → {shortResult} ({sw.ElapsedMilliseconds} ms)");
        return result;
    }

    async Task<string> ExecuteCoreAsync(ToolCall c, CancellationToken ct)
    {
        switch (c.Name)
        {
            case "click" when c.Args["expand"] is System.Text.Json.Nodes.JsonValue:
            {
                if (!Target(c, out var p, out var err)) return err;
                var open = c.Bool("expand");
                Cursor?.Glide(p, (open ? "Desplegando" : "Plegando") + NameOf(c));
                var box = MarkRect(c);
                var r = await Task.Run(() => ExpandAt(p, box, open), ct);
                Cursor?.Pulse();
                return r;
            }
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
            case "find_on_screen":
                Cursor?.Say("Buscando «" + Short(c.Str("text")) + "»");
                return await Finder.FindAsync(c.Str("text"), c.Int("n") ?? 1, c.Bool("up"), t => Cursor?.Say(t), ct);
            case "extract":
            {
                Cursor?.Say("Buscando datos…");
                var work = Finder.AllTextAsync(ct);
                if (await Task.WhenAny(work, Task.Delay(6000, ct)) != work) return "error: la ventana tardó demasiado en dar su texto";
                return Extract.Report(await work, c.Str("kind"), c.Str("near").Trim());
            }
            case "edit_text":
                Cursor?.Say("Corrigiendo…");
                return await EditTextAsync(c.Str("find"), c.Str("replace"), ct);
            case "clipboard":
                switch (c.Str("action"))
                {
                    case "set": return Desktop.SetClipboard(c.Str("text"));
                    case "history": return Clips.List(DateTime.Now);
                    case "set_files":
                    {
                        var folder = await Task.Run(() => Files.Explorer(Native.GetForegroundWindow())?.Folder, ct);
                        try { return Desktop.SetClipboardFiles(FileOps.Expand(Strings(c, "paths"), folder)); }
                        catch (Exception e) when (e is ArgumentException or IOException) { return "error: " + e.Message; }
                    }
                    default:
                        if (c.Int("n") is not (> 1 and var n)) return Desktop.GetClipboard();
                        return Clips.Get(n) is { } old ? "ok:\n" + (old.Length > 8000 ? old[..8000] + "\n…(cortado)" : old)
                            : $"error: no tengo tantas cosas copiadas (mira clipboard history)";
                }
            case "vault":
                if (Vault is null) return "error: cofre apagado";
                return await Secrets.RunAsync(Vault, c, Cursor, ct);
            case "recent":
                return Activity.Summary(DateTime.Now, c.Int("minutes") ?? 60);
            case "web":
                Cursor?.Say(Agent.Describe(c, new Observation([], 0, 0, "", [])));
                return await _web.RunAsync(c, FileOps.Resolve("~/Downloads", null), ct);
            case "stage":
                if (Stage is null) return "error: palco apagado";
                if (c.Str("action") is "show" or "page") Cursor?.Say(Agent.Describe(c, new Observation([], 0, 0, "", [])));
                return await Stage.RunAsync(c, ct);
            case "meeting":
                return Meeting?.Invoke(c) ?? "error: grabación apagada";
            case "agenda":
                return Agenda?.Run(c, DateTime.Now) ?? "error: agenda apagada";
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
                return await OpenAsync(c.Str("target").Trim(), ct);
            case "screenshot":
                Cursor?.Say("Print");
                return Screenshot(c.Str("area") == "window");
            case "find_files":
                Cursor?.Say("Buscando archivos");
                return c.Bool("content") ? await Task.Run(() => Files.SearchContent(c.Str("query"), ct), ct) : FindFiles(c.Str("query"), ct);
            case "files":
            {
                Cursor?.Say(Agent.Describe(c, new Observation([], 0, 0, "", [])));
                var paths = Strings(c, "paths");
                if (paths.Count == 0 && c.Str("path").Length > 0) paths.Add(c.Str("path"));
                var folder = await Task.Run(() => Files.Explorer(Native.GetForegroundWindow())?.Folder, ct);
                if (c.Str("action") == "open_with")
                {
                    if (paths.Count == 0) return "error: falta paths";
                    try { return Files.OpenWith(FileOps.Resolve(paths[0], folder), c.Str("app")); }
                    catch (ArgumentException e) { return "error: " + e.Message; }
                }
                return await Task.Run(() => _files.Run(c.Str("action"), folder, paths, c.Str("to"), Strings(c, "names"), c.Str("pattern")), ct);
            }
            case "doc":
                Cursor?.Say(Agent.Describe(c, new Observation([], 0, 0, "", [])));
                return await Office.DocAsync(c, ct);
            case "sheet":
                Cursor?.Say(Agent.Describe(c, new Observation([], 0, 0, "", [])));
                return await Office.SheetAsync(c, ct);
            case "terminal":
                switch (c.Str("action"))
                {
                    case "run":
                        Cursor?.Say("Ejecutando…");
                        return await Term.RunAsync(c.Str("command"), c.Str("shell"), c.Str("cwd"), c.Num("seconds") ?? 30, c.Bool("confirmed"), ct);
                    case "wait":
                        Cursor?.Say("Esperando a la terminal…");
                        return await Term.WaitAsync(c.Num("seconds") ?? 30, Math.Clamp(c.Int("lines") ?? 25, 1, 200), ct);
                    default:
                        Cursor?.Say("Leyendo la terminal");
                        return await Term.ReadAsync(Math.Clamp(c.Int("lines") ?? 40, 1, 200), ct);
                }
            case "wait":
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(c.Num("seconds") ?? 1, 0.1, 10)), ct);
                return "ok";
            default:
                return $"error: herramienta desconocida «{c.Name}»";
        }
    }

    /// <summary>
    /// Abre y espera a ver qué salió: la ventana nueva (traída al frente; Windows no siempre deja que se ponga
    /// delante sola) o la que pasó a estar activa. Con un nombre de programa, dice además los otros instalados que
    /// se llaman parecido: el Outlook clásico que se queda en el inicio de sesión y el nuevo al lado.
    /// </summary>
    async Task<string> OpenAsync(string target, CancellationToken ct)
    {
        if (target.Length == 0) return "error: target vacío";
        Cursor?.Say("Abriendo " + (target.Length > 30 ? target[..30] + "…" : target));
        var before = Native.AppWindows().ToHashSet();
        var fgBefore = Native.GetForegroundWindow();
        var titleBefore = Native.Title(fgBefore);
        var name = Apps.IsName(target);
        List<string> similar = [];
        string opened;
        if (name)
        {
            var installed = await Task.Run(Desktop.StartApps, ct);
            similar = Apps.Matching(installed.Select(a => a.Name), target);
        }
        // El nombre exacto de Inicio («Outlook (new)») manda; si no, lo que Windows abre con ese nombre (App Paths:
        // chrome, winword…) y, si no hay, el primero de Inicio que encaja (los de la Store, como WhatsApp).
        var exact = similar.FirstOrDefault(n => Extract.Fold(n) == Extract.Fold(target));
        if (exact is not null && await Task.Run(() => Desktop.OpenStartApp(exact), ct) is { } app) opened = app;
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
                opened = target;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (similar.Count == 0 || await Task.Run(() => Desktop.OpenStartApp(similar[0]), ct) is not { } first)
                    return $"error: no encontré «{target}» ni como archivo ni entre los programas instalados";
                opened = first;
            }
        }
        var others = similar.Where(n => n != opened).Take(4).ToList();
        // Uno solo que encaja es el mismo programa con su nombre de Inicio («Google Chrome»): no es otra opción.
        var also = similar.Count > 1 && others.Count > 0 ? " · otros instalados con nombre parecido: " + string.Join(", ", others.Select(o => $"«{o}»")) : "";
        var win = await NewWindowAsync(before, fgBefore, titleBefore, name ? 6000 : 2500, ct);
        return win == IntPtr.Zero
            ? $"ok: abrí «{opened}»; aún no salió ventana nueva (puede tardar: wait y mira){also}"
            : $"ok: abrí «{opened}» → delante «{Native.WindowLabel(win)}»{also}";
    }

    /// <summary>
    /// La ventana que salió tras abrir algo (nueva, que pasó al frente, o la misma con otro título: una pestaña nueva
    /// en el navegador que ya estaba delante), ya delante; IntPtr.Zero si no salió.
    /// </summary>
    static async Task<IntPtr> NewWindowAsync(HashSet<IntPtr> before, IntPtr fgBefore, string titleBefore, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(150, ct);
            var fg = Native.GetForegroundWindow();
            var found = fg != IntPtr.Zero && !Native.IsOwnWindow(fg) && (fg != fgBefore || Native.Title(fg) != titleBefore) ? fg
                : Native.AppWindows().FirstOrDefault(h => !before.Contains(h));
            if (found == IntPtr.Zero) continue;
            if (Native.GetForegroundWindow() != found) Desktop.FocusHandle(found);
            // Que pinte algo antes de mirarla.
            await Task.Delay(400, ct);
            return found;
        }
        return IntPtr.Zero;
    }

    /// <summary>El rectángulo entero del elemento marcado (con sus hijos si es un nodo desplegado).</summary>
    Rectangle MarkRect(ToolCall c)
    {
        lock (_lock) return c.Mark() is int id && _raws.TryGetValue(id, out var r) ? r : Rectangle.Empty;
    }

    /// <summary>
    /// Despliega o pliega lo que hay en p con su ExpandCollapsePattern (sin buscar la flecha). Sube desde el punto
    /// solo dentro del elemento marcado: así una carpeta sin hijos no pliega la cuenta que la contiene.
    /// </summary>
    static string ExpandAt(Point p, Rectangle box, bool open)
    {
        var e = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
        var walker = TreeWalker.ControlViewWalker;
        for (var i = 0; i < 6 && e is not null; i++, e = walker.GetParent(e))
        {
            var r = e.Current.BoundingRectangle;
            var inside = box.IsEmpty
                ? r.Height <= 80
                : r.Left >= box.Left - 6 && r.Top >= box.Top - 6 && r.Right <= box.Right + 6 && r.Bottom <= box.Bottom + 6;
            if (!inside) break;
            if (!e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var o)) continue;
            var ec = (ExpandCollapsePattern)o;
            switch (ec.Current.ExpandCollapseState)
            {
                case ExpandCollapseState.LeafNode: return "ok: no tiene nada dentro";
                case ExpandCollapseState.Collapsed when !open: return "ok: ya estaba plegado";
                case ExpandCollapseState.Expanded when open: return "ok: ya estaba desplegado";
            }
            if (open) ec.Expand(); else ec.Collapse();
            return "ok";
        }
        return "error: eso no se despliega solo; prueba click normal en él o en su flecha";
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
        ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium", "arc", "zen", "librewolf", "waterfox", "iexplore",
         "comet", "dia", "thorium", "yandex", "floorp", "sidekick", "opera_gx", "duckduckgo"];

    internal static bool IsBrowser(string process) => Browsers.Contains(process.ToLowerInvariant());

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

    static List<string> Strings(ToolCall c, string key) =>
        c.Args[key] is System.Text.Json.Nodes.JsonArray a
            ? a.Select(x => x is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var t) ? t : "").Where(t => t.Length > 0).ToList()
            : c.Str(key) is { Length: > 0 } one ? [one] : [];

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
