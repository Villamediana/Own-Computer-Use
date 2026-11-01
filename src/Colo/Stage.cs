using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Colo.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Colo;

/// <summary>
/// El palco: ventana sin bordes al lado de la pantalla donde se ve lo que el diseñador crea (gráficos, explicaciones
/// animadas, 3D, mapas, vídeos…) o una página tal cual (una letra, un PDF). Aparece sin quitar el foco, así el dictado y
/// los clics siguen donde estaban. Se crea la primera vez que hace falta y se queda caliente, oculta.
///
/// Seguridad: el host vive en https://colo.stage y lo creado en https://palco.colo (otra carpeta, otro origen) dentro
/// de un iframe; solo se aceptan mensajes del host, y una pregunta de vuelta al modelo pide un clic.
/// Módulo aparte: si se quita (con la herramienta stage), nada más cambia.
/// </summary>
sealed class Stage : Form
{
    const string HostUrl = "https://colo.stage/stage.html";
    const int Edge = 6;
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colo", "stage");
    static readonly string HostDir = Path.Combine(Root, "host");
    static readonly string DocsDir = Path.Combine(Root, "docs");

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    // Con una página de fuera (page) la barra del host no está: esta tira propia deja arrastrar, abrir y cerrar.
    readonly Panel _strip = new() { Dock = DockStyle.Top, Visible = false };
    readonly Label _where = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    string _pageUrl = "";
    TaskCompletionSource? _hostReady;
    TaskCompletionSource<JsonNode>? _rendered;
    int _n;
    bool _page;

    public event Action<string>? Ask;
    public event Action<string>? Speak;
    public event Action? Retry;
    public event Action? Closed;

    static bool Dark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0; }
        catch { return false; }
    }

    public Stage()
    {
        Text = "Colo · Palco";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Dark() ? Color.FromArgb(14, 14, 17) : Color.FromArgb(251, 250, 248);
        _web.DefaultBackgroundColor = BackColor;
        // Un borde fino de la ventana queda fuera del WebView: por ahí se agarra para cambiar el tamaño.
        Padding = new Padding(Edge);
        MinimumSize = new Size(380, 320);
        Controls.Add(_web);
        BuildStrip();
    }

    void BuildStrip()
    {
        var dark = Dark();
        Color ink = dark ? Color.FromArgb(242, 242, 244) : Color.FromArgb(20, 20, 22), muted = dark ? Color.FromArgb(141, 141, 151) : Color.FromArgb(138, 138, 147);
        Color hover = dark ? Color.FromArgb(32, 32, 37) : Color.FromArgb(236, 235, 232);
        var s = DeviceDpi / 96f;
        _strip.Height = (int)(34 * s);
        _strip.BackColor = BackColor;
        _where.ForeColor = muted;
        _where.Font = new Font("Segoe UI", 9f);
        _where.Padding = new Padding((int)(8 * s), 0, 0, 0);
        _strip.Controls.Add(_where);
        Label Button(string glyph, string tip, Action click)
        {
            var b = new Label
            {
                Text = glyph, Dock = DockStyle.Right, Width = (int)(34 * s), TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = ink, Font = new Font("Segoe UI Symbol", 10f), Cursor = Cursors.Hand,
            };
            new ToolTip().SetToolTip(b, tip);
            b.MouseEnter += (_, _) => b.BackColor = hover;
            b.MouseLeave += (_, _) => b.BackColor = Color.Transparent;
            b.Click += (_, _) => click();
            _strip.Controls.Add(b);
            return b;
        }
        Button("↗", "Abrir en el navegador", () => OpenExternal(_pageUrl));
        Button("✕", "Cerrar", HideStage);
        void Drag(object? _, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks == 2) { Maximize(WindowState != FormWindowState.Maximized); return; }
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
        }
        _strip.MouseDown += Drag;
        _where.MouseDown += Drag;
        Controls.Add(_strip);
    }

    /// <summary>Maximizado sin bordes taparía la barra de tareas: se limita al área de trabajo de su pantalla.</summary>
    void Maximize(bool on)
    {
        if (on)
        {
            var sc = Screen.FromHandle(Handle);
            var wa = sc.WorkingArea;
            MaximizedBounds = new Rectangle(wa.X - sc.Bounds.X, wa.Y - sc.Bounds.Y, wa.Width, wa.Height);
        }
        WindowState = on ? FormWindowState.Maximized : FormWindowState.Normal;
    }

    // Aparece sin robar el foco a la ventana donde está trabajando.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || WindowState == FormWindowState.Maximized) return;
        var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
        int e = Edge + 2;
        bool l = p.X < e, r = p.X >= ClientSize.Width - e, t = p.Y < e, b = p.Y >= ClientSize.Height - e;
        m.Result = (l, r, t, b) switch
        {
            (true, _, true, _) => 13, (_, true, true, _) => 14, (true, _, _, true) => 16, (_, true, _, true) => 17,
            (true, _, _, _) => 10, (_, true, _, _) => 11, (_, _, true, _) => 12, (_, _, _, true) => 15,
            _ => m.Result,
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Cerrar = ocultar: la próxima vez abre al instante.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideStage(); }
        base.OnFormClosing(e);
    }

    void HideStage()
    {
        Hide();
        Closed?.Invoke();
    }

    async Task InitAsync()
    {
        if (_web.CoreWebView2 is not null) return;
        Directory.CreateDirectory(HostDir);
        Directory.CreateDirectory(DocsDir);
        using (var res = typeof(Stage).Assembly.GetManifestResourceStream("Colo.stage.html")!)
        using (var f = File.Create(Path.Combine(HostDir, "stage.html")))
            res.CopyTo(f);
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colo", "webview");
        var env = await CoreWebView2Environment.CreateAsync(null, data);
        await _web.EnsureCoreWebView2Async(env);
        var cw = _web.CoreWebView2;
        cw.Settings.AreDevToolsEnabled = false;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.IsPasswordAutosaveEnabled = false;
        cw.Settings.IsGeneralAutofillEnabled = false;
        cw.SetVirtualHostNameToFolderMapping("colo.stage", HostDir, CoreWebView2HostResourceAccessKind.DenyCors);
        cw.SetVirtualHostNameToFolderMapping("palco.colo", DocsDir, CoreWebView2HostResourceAccessKind.Allow);
        // Enlaces que abren otra ventana: al navegador de siempre.
        cw.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        cw.WebMessageReceived += (_, e) => OnMessage(e);
    }

    async Task EnsureHostAsync()
    {
        await InitAsync();
        if (!_page && _web.Source?.ToString() == HostUrl && _hostReady?.Task.IsCompleted == true) return;
        _page = false;
        _strip.Visible = false;
        _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _web.CoreWebView2.Navigate(HostUrl);
        await Task.WhenAny(_hostReady.Task, Task.Delay(5000));
    }

    void OnMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Solo el host habla con Colo (no una página abierta con page ni lo creado en el iframe).
        if (!e.Source.StartsWith("https://colo.stage/", StringComparison.Ordinal)) return;
        JsonNode? m;
        try { m = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        switch (m?["type"]?.GetValue<string>())
        {
            case "ready": _hostReady?.TrySetResult(); break;
            case "rendered": _rendered?.TrySetResult(m); break;
            case "close": HideStage(); break;
            case "big": Maximize(WindowState != FormWindowState.Maximized); break;
            case "drag":
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
                break;
            case "retry": Retry?.Invoke(); break;
            case "open": OpenExternal(m["url"]?.GetValue<string>() ?? ""); break;
            case "say": if (m["text"]?.GetValue<string>() is { Length: > 0 } s) Speak?.Invoke(s); break;
            case "ask": if (m["text"]?.GetValue<string>() is { Length: > 0 } q) Ask?.Invoke(q); break;
        }
    }

    static void OpenExternal(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    void Place(Rectangle near)
    {
        if (Visible) return;
        var wa = Screen.FromRectangle(near).WorkingArea;
        var w = Math.Min((int)(760 * DeviceDpi / 96f), (int)(wa.Width * 0.48));
        var h = (int)(wa.Height * 0.9);
        Bounds = new Rectangle(wa.Right - w - 14, wa.Top + (wa.Height - h) / 2, w, h);
    }

    /// <summary>Tamaño útil en px CSS (para decírselo al diseñador).</summary>
    public (int W, int H) ContentSize()
    {
        var s = DeviceDpi / 96f;
        return ((int)((Width - 2 * Edge) / s), (int)((Height - 2 * Edge) / s));
    }

    void Post(object m) => _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(m));

    /// <summary>Abre el palco con la pantalla de «creando»: el título y qué va a contar.</summary>
    public async Task LoadingAsync(string title, string brief, Rectangle near)
    {
        await EnsureHostAsync();
        Place(near);
        if (!Visible) Show();
        Post(new { type = "loading", title, brief });
    }

    public void Progress(int chars, string? note = null) => Post(new { type = "progress", chars, note });

    public void Failed(string text) => Post(new { type = "failed", text });

    /// <summary>Muestra la pieza y espera a que cuente cómo fue: los errores (vacío = bien).</summary>
    public async Task<List<string>> ShowDocAsync(string title, string doc, CancellationToken ct)
    {
        await EnsureHostAsync();
        var name = $"{++_n}-{DateTime.Now:HHmmss}.html";
        await File.WriteAllTextAsync(Path.Combine(DocsDir, name), doc, ct);
        Prune();
        if (!Visible) Show();
        _rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new { type = "show", src = $"https://palco.colo/{name}", title });
        var done = await Task.WhenAny(_rendered.Task, Task.Delay(15_000, ct));
        if (done != _rendered.Task) return [];
        var r = await _rendered.Task;
        var errors = (r["errors"] as JsonArray ?? []).Select(e => e?.GetValue<string>() ?? "").Where(e => e.Length > 0).ToList();
        if (r["blank"]?.GetValue<bool>() == true) errors.Add("la página quedó en blanco");
        return errors;
    }

    /// <summary>Una página tal cual (una letra, un PDF, un artículo): se navega a ella.</summary>
    public async Task<string> ShowPageAsync(string url, Rectangle near)
    {
        url = url.Trim();
        if (File.Exists(url)) url = new Uri(url).AbsoluteUri;
        else if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "file"))
            return "error: url no válida";
        await InitAsync();
        _page = true;
        _pageUrl = uri.Scheme == "file" ? "" : uri.AbsoluteUri;
        _where.Text = uri.Scheme == "file" ? Path.GetFileName(uri.LocalPath) : uri.Host.Replace("www.", "") + uri.AbsolutePath.TrimEnd('/');
        _strip.Visible = true;
        Place(near);
        if (!Visible) Show();
        _web.CoreWebView2.Navigate(uri.AbsoluteUri);
        return "ok: en el palco";
    }

    public void Toggle(bool big)
    {
        if (Visible) Maximize(big);
    }

    /// <summary>Guarda las últimas 30 creaciones (se pueden volver a abrir desde la carpeta).</summary>
    static void Prune()
    {
        try
        {
            foreach (var f in new DirectoryInfo(DocsDir).GetFiles("*.html").OrderByDescending(f => f.LastWriteTime).Skip(30))
                f.Delete();
        }
        catch { }
    }
}

/// <summary>
/// Puerta al palco desde el agente (otro hilo). stage show no espera al diseñador: abre el palco con «creando», lanza
/// el diseño en segundo plano y responde al momento, así el agente termina su respuesta. Si la pieza falla al cargar,
/// el diseñador la arregla una vez solo.
/// </summary>
sealed class StageHost(Control ui, AiClient ai, Settings settings)
{
    Stage? _stage;
    CancellationTokenSource? _work;
    string? _lastHtml;
    (string Title, string Brief, string Data, string? Previous)? _lastAsk;

    public event Action<string>? Ask;
    public event Action<string>? Speak;
    /// <summary>Aviso para la onda (el palco no salió).</summary>
    public event Action<string>? Problem;

    Task<T> OnUi<T>(Func<Task<T>> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ui.BeginInvoke(async () =>
            {
                try { tcs.TrySetResult(await f()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
        return tcs.Task;
    }

    Task OnUi(Action a) => OnUi(() => { a(); return Task.FromResult(0); });

    Stage Window()
    {
        if (_stage is { IsDisposed: false }) return _stage;
        _stage = new Stage();
        _stage.Ask += q => Ask?.Invoke(q);
        _stage.Speak += s => Speak?.Invoke(s);
        _stage.Retry += () => { if (_lastAsk is { } a) Start(a.Title, a.Brief, a.Data, a.Previous); };
        // Cerrar el palco también para el diseño en curso.
        _stage.Closed += () => _work?.Cancel();
        return _stage;
    }

    Rectangle Near() => ui.IsDisposed ? Screen.PrimaryScreen!.Bounds : ui.Bounds;

    /// <summary>Si el palco tapa ese punto, se oculta (el agente va a hacer clic ahí, en la ventana de debajo).</summary>
    public void Uncover(Point p)
    {
        if (_stage is not { IsDisposed: false } s) return;
        try { ui.Invoke(() => { if (s.Visible && s.Bounds.Contains(p)) s.Hide(); }); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }

    public async Task<string> RunAsync(ToolCall c, CancellationToken ct)
    {
        try
        {
            switch (c.Str("action"))
            {
                case "page":
                    _work?.Cancel();
                    return await OnUi(() => Window().ShowPageAsync(c.Str("url"), Near()));
                case "close":
                    _work?.Cancel();
                    await OnUi(() => { if (_stage is { IsDisposed: false }) _stage.Hide(); });
                    return "ok";
                case "bigger":
                case "smaller":
                    var big = c.Str("action") == "bigger";
                    await OnUi(() => _stage?.Toggle(big));
                    return "ok";
                default:
                    var brief = c.Str("brief").Trim();
                    if (brief.Length == 0) return "error: falta brief (qué contar)";
                    var edit = c.Str("action") == "edit" && _lastHtml is not null;
                    Start(c.Str("title").Trim(), brief, c.Str("data"), edit ? _lastHtml : null);
                    return "ok: el palco se está creando (sale en unos segundos); termina tú ya con done";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin WebView2 (raro en Windows 10/11) el palco no puede abrir.
            Log.Write("palco: " + ex.Message);
            return "error: no pude abrir el palco: " + ex.Message;
        }
    }

    void Start(string title, string brief, string data, string? previous)
    {
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();
        _lastAsk = (title, brief, data, previous);
        _ = Task.Run(() => DesignAsync(title, brief, data, previous, cts.Token));
    }

    async Task DesignAsync(string title, string brief, string data, string? previous, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await OnUi(async () => { await Window().LoadingAsync(title, previous is null ? brief : "Cambio: " + brief, Near()); return 0; });
            var (w, h) = await OnUi(() => Task.FromResult(Window().ContentSize()));
            var last = DateTime.MinValue;
            void Progress(int n)
            {
                if ((DateTime.Now - last).TotalMilliseconds < 200) return;
                last = DateTime.Now;
                _ = OnUi(() => _stage?.Progress(n));
            }
            var html = await StageDesigner.CreateAsync(ai, StageDesigner.Request(settings, title, brief, data, w, h, previous), Progress, ct);
            var errors = await OnUi(() => Window().ShowDocAsync(title, StageDoc.Wrap(html), ct));
            if (errors.Count > 0 && !ct.IsCancellationRequested)
            {
                // Falló al cargar: el diseñador lo arregla una vez, sin que el agente tenga que hacer nada.
                Log.Write("palco: errores, arreglando · " + string.Join(" | ", errors));
                await OnUi(async () => { await Window().LoadingAsync(title, "Ajustando un detalle…", Near()); return 0; });
                html = await StageDesigner.CreateAsync(ai, StageDesigner.Request(settings, title, "", "", w, h, html, errors), Progress, ct);
                errors = await OnUi(() => Window().ShowDocAsync(title, StageDoc.Wrap(html), ct));
            }
            _lastHtml = html;
            Log.Write($"palco: «{title}» en {sw.Elapsed.TotalSeconds:0.0} s, {html.Length} caracteres" + (errors.Count > 0 ? " · aún con errores: " + string.Join(" | ", errors) : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("palco: no salió · " + ex.Message);
            try { await OnUi(() => _stage?.Failed("No salió: " + ex.Message)); } catch { }
            Problem?.Invoke("El palco no salió: " + ex.Message);
        }
    }
}
