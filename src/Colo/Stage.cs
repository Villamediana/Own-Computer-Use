using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Colo.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Colo;

/// <summary>
/// El palco: ventana al lado de la pantalla donde el modelo muestra lo que crea (gráficos, lousa animada, 3D,
/// mapas, vídeos…) o una página (una letra, un PDF). Aparece sin quitar el foco, así el dictado y los clics siguen
/// donde estaban. Se crea la primera vez que hace falta y se queda caliente, oculta.
///
/// Seguridad: el host vive en https://colo.stage y lo creado en https://palco.colo (otra carpeta, otro origen) dentro
/// de un iframe; solo se aceptan mensajes del host, y una pregunta de vuelta al modelo pide un clic.
/// Módulo aparte: si se quita (con la herramienta stage), nada más cambia.
/// </summary>
sealed class Stage : Form
{
    const string HostUrl = "https://colo.stage/stage.html";
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colo", "stage");
    static readonly string HostDir = Path.Combine(Root, "host");
    static readonly string DocsDir = Path.Combine(Root, "docs");

    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
    TaskCompletionSource? _hostReady;
    TaskCompletionSource<JsonNode>? _rendered;
    int _n;
    bool _page;

    /// <summary>La persona confirmó una pregunta que propuso la página.</summary>
    public event Action<string>? Ask;
    /// <summary>La página pide leer algo en voz alta (narrar una explicación).</summary>
    public event Action<string>? Speak;

    public Stage()
    {
        Text = "Colo · Palco";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        MinimumSize = new Size(360, 300);
        Controls.Add(_web);
    }

    // Aparece sin robar el foco a la ventana donde está trabajando.
    protected override bool ShowWithoutActivation => true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Cerrar = ocultar: la próxima vez abre al instante.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
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
        cw.DocumentTitleChanged += (_, _) => { if (_page) Text = "Colo · " + cw.DocumentTitle; };
        cw.WebMessageReceived += (_, e) => OnMessage(e);
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
            case "close": Hide(); break;
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
        var w = Math.Min((int)(720 * DeviceDpi / 96f), (int)(wa.Width * 0.46));
        var h = (int)(wa.Height * 0.88);
        Bounds = new Rectangle(wa.Right - w - 16, wa.Top + (wa.Height - h) / 2, w, h);
    }

    /// <summary>Muestra lo creado y espera a que la página cuente cómo fue (errores, si corrió, si quedó vacía).</summary>
    public async Task<string> ShowDocAsync(string title, string doc, Rectangle near, CancellationToken ct)
    {
        await InitAsync();
        var name = $"{++_n}-{DateTime.Now:HHmmss}.html";
        await File.WriteAllTextAsync(Path.Combine(DocsDir, name), doc, ct);
        Prune();
        if (_page || _web.Source?.ToString() != HostUrl)
        {
            _page = false;
            _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _web.CoreWebView2.Navigate(HostUrl);
            await Task.WhenAny(_hostReady.Task, Task.Delay(5000, ct));
        }
        Text = "Colo · " + (title.Length > 0 ? title : "Palco");
        Place(near);
        if (!Visible) Show();
        _rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "show", src = $"https://palco.colo/{name}" }));
        var done = await Task.WhenAny(_rendered.Task, Task.Delay(12_000, ct));
        if (done != _rendered.Task) return "ok: en el palco (la página tardó en cargar; no sé si quedó bien)";
        return Report(await _rendered.Task);
    }

    /// <summary>El resultado para el modelo: con error, el agente sigue y lo corrige en el siguiente paso.</summary>
    public static string Report(JsonNode r)
    {
        var errors = (r["errors"] as JsonArray ?? []).Select(e => e?.GetValue<string>() ?? "").Where(e => e.Length > 0).ToList();
        var ran = r["ran"]?.GetValue<bool>() == true;
        var blank = r["blank"]?.GetValue<bool>() == true;
        if (errors.Count == 0 && ran && !blank) return "ok: en el palco";
        var why = string.Join("; ", errors);
        if (!ran) why = (why.Length > 0 ? why + "; " : "") + "el js no llegó a ejecutarse entero (¿un import que no existe?)";
        if (blank) why = (why.Length > 0 ? why + "; " : "") + "la página quedó en blanco";
        return $"error: se ve en el palco pero falla: {why}. Corrige y vuelve a llamar show con la página entera.";
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
        Text = "Colo · " + uri.Host;
        Place(near);
        if (!Visible) Show();
        _web.CoreWebView2.Navigate(uri.AbsoluteUri);
        return "ok: en el palco";
    }

    public void Toggle(bool big)
    {
        if (!Visible) return;
        WindowState = big ? FormWindowState.Maximized : FormWindowState.Normal;
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
/// Puerta al palco desde el agente (otro hilo): crea la ventana en el hilo de la interfaz la primera vez y le pasa
/// las órdenes. stage skill no toca la ventana.
/// </summary>
sealed class StageHost(Control ui)
{
    Stage? _stage;

    public event Action<string>? Ask;
    public event Action<string>? Speak;

    Task<T> OnUi<T>(Func<Task<T>> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.BeginInvoke(async () =>
        {
            try { tcs.TrySetResult(await f()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    Stage Window()
    {
        if (_stage is { IsDisposed: false }) return _stage;
        _stage = new Stage();
        _stage.Ask += q => Ask?.Invoke(q);
        _stage.Speak += s => Speak?.Invoke(s);
        return _stage;
    }

    Rectangle Near() => ui.IsDisposed ? Screen.PrimaryScreen!.Bounds : ui.Bounds;

    public async Task<string> RunAsync(ToolCall c, CancellationToken ct)
    {
        try
        {
            switch (c.Str("action"))
            {
                case "skill":
                    return StageSkills.Read(c.Str("name"));
                case "page":
                    return await OnUi(() => Window().ShowPageAsync(c.Str("url"), Near()));
                case "close":
                    return await OnUi(() => { if (_stage is { IsDisposed: false }) _stage.Hide(); return Task.FromResult("ok"); });
                case "bigger":
                case "smaller":
                    var big = c.Str("action") == "bigger";
                    return await OnUi(() => { _stage?.Toggle(big); return Task.FromResult("ok"); });
                default:
                    var html = c.Str("html");
                    if (html.Trim().Length == 0 && c.Str("js").Trim().Length == 0) return "error: falta html";
                    var doc = StageDoc.Build(c.Str("title"), c.Str("css"), html, c.Str("js"));
                    return await OnUi(() => Window().ShowDocAsync(c.Str("title"), doc, Near(), ct));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin WebView2 (raro en Windows 10/11) el palco no puede abrir.
            Log.Write("palco: " + ex.Message);
            return "error: no pude abrir el palco: " + ex.Message;
        }
    }
}
