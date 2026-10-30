using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Colo.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Colo;

/// <summary>
/// Ajustes: una página web (settings.html) dentro de WebView2, el motor de Edge que ya trae Windows.
/// La página habla con esta ventana por mensajes JSON: ready / listModels / mcpTools / openFolder / save / close.
/// Sin botón de guardar: cada cambio llega como «save» y se guarda al momento; lo pesado (micrófono, MCP,
/// atajos) se aplica al cerrar.
/// </summary>
sealed class SettingsWindow : Form
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    readonly Settings _s;
    readonly Memory? _memory;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
    readonly CancellationTokenSource _closing = new();
    string _voiceStatus = "";

    /// <summary>Hubo al menos un cambio guardado mientras estuvo abierta.</summary>
    public bool Changed { get; private set; }
    /// <summary>Llegó un cambio y ya está copiado a los ajustes.</summary>
    public event Action? Saved;
    bool _flushed;

    /// <summary>La página cargó y ya recibe mensajes.</summary>
    public event Action? Ready;
    /// <summary>Pidieron «Grabar 5 s y analizar».</summary>
    public event Action? RecordTest;
    public void ProbeResult(object result) => Post(new { type = "probe", result });
    /// <summary>Pidieron «Probar voz» con lo que hay en pantalla (aún sin guardar).</summary>
    public event Action<Settings>? SayTest;
    /// <summary>La página está grabando un atajo: el actual no debe dispararse.</summary>
    public event Action<bool>? RecordingKeys;

    // Prueba del micrófono en vivo (Ajustes › Voz). Se llaman desde el hilo de la interfaz.
    public void VoiceLevel(double rms) => Post(new { type = "voiceLevel", v = Math.Clamp(Math.Sqrt(rms) * 3.4, 0, 1) });
    public void VoiceVad(bool on) => Post(new { type = "voiceVad", on });
    public void VoiceHeard(string text, long ms) => Post(new { type = "voiceHeard", text, ms });
    public void VoiceStatus(string text)
    {
        _voiceStatus = text;
        Post(new { type = "voiceStatus", text });
    }

    public SettingsWindow(Settings s, Memory? memory = null)
    {
        _s = s;
        _memory = memory;
        Text = "Colo · Ajustes";
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(980, 700);
        MinimumSize = new Size(820, 560);
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Controls.Add(_web);
        Shown += async (_, _) => await InitAsync();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    async Task InitAsync()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colo", "webview");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "No pude abrir los Ajustes: falta Microsoft Edge WebView2 en este PC.\n\n" +
                "Se instala sin permisos de administrador desde https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" + ex.Message,
                "Colo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.Cancel;
            return;
        }

        var cw = _web.CoreWebView2;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.Settings.AreDevToolsEnabled = false;
        cw.Settings.IsZoomControlEnabled = false;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.AreBrowserAcceleratorKeysEnabled = false;
        cw.Settings.IsPasswordAutosaveEnabled = false;
        cw.Settings.IsGeneralAutofillEnabled = false;
        cw.NewWindowRequested += (_, e) => e.Handled = true;
        cw.NavigationStarting += (_, e) =>
        {
            // Solo la propia página; ningún enlace navega fuera.
            if (!e.Uri.StartsWith("data:", StringComparison.Ordinal) && e.Uri != "about:blank") e.Cancel = true;
        };
        cw.WebMessageReceived += async (_, e) => await OnMessageAsync(e.WebMessageAsJson);
        cw.NavigateToString(Page());
    }

    static string Page()
    {
        using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("Colo.settings.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    void Post(object message)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    async Task OnMessageAsync(string json)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(json); } catch { return; }
        switch (msg?["type"]?.GetValue<string>())
        {
            case "ready":
                Post(new
                {
                    type = "init",
                    settings = JsonSerializer.SerializeToNode(_s, Json),
                    mics = SafeMics(),
                    groups = Tools.Groups.Select(g => new { id = g.Id, title = g.Title, summary = g.Summary }),
                    version = Application.ProductVersion.Split('+')[0],
                    printsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Colo"),
                    logFile = Log.FilePath,
                    memory = _memory?.Facts ?? [],
                });
                if (_voiceStatus.Length > 0) Post(new { type = "voiceStatus", text = _voiceStatus });
                Ready?.Invoke();
                break;

            case "listModels":
                await ListModelsAsync(msg);
                break;

            case "mcpTools":
                await McpToolsAsync(msg);
                break;

            case "openFolder":
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Colo");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true })?.Dispose();
                break;

            case "openLog":
                if (File.Exists(Log.FilePath))
                    Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Log.FilePath}\"") { UseShellExecute = true })?.Dispose();
                break;

            case "save":
                Apply(msg["settings"]);
                break;

            case "forgetFact":
                var fact = msg["text"]?.GetValue<string>() ?? "";
                if (fact.Length > 0) Log.Write($"memoria: olvidado desde Ajustes «{fact}» → {_memory?.Forget(fact)}");
                break;

            case "listVoices":
                await ListVoicesAsync(msg["key"]?.GetValue<string>() ?? "");
                break;

            case "sayTest":
                var trial = msg["settings"]?.Deserialize<Settings>(Json);
                if (trial is not null) SayTest?.Invoke(trial);
                break;

            case "recordingKeys":
                RecordingKeys?.Invoke(msg["on"]?.GetValue<bool>() == true);
                break;

            case "recordTest":
                RecordTest?.Invoke();
                break;

            case "close":
                Close();
                break;
        }
    }

    static object[] SafeMics()
    {
        try { return Mic.Devices().Select(d => (object)new { id = d.Id, name = d.Name }).ToArray(); }
        catch (Exception ex) { Log.Write("micrófonos: no pude listarlos · " + ex.Message); return []; }
    }

    async Task ListModelsAsync(JsonNode msg)
    {
        // «voice»: la lista para la transcripción, con la conexión de la voz.
        var target = msg["for"]?.GetValue<string>() ?? "model";
        var probe = new Settings
        {
            Provider = msg["provider"]?.GetValue<string>() == "OpenAI" ? Provider.OpenAI : Provider.Azure,
            Endpoint = msg["endpoint"]?.GetValue<string>() ?? "",
            ApiKey = msg["apiKey"]?.GetValue<string>() ?? "",
        };
        try
        {
            var models = await new AiClient(probe).ListModelsAsync(_closing.Token);
            Post(new { type = "models", ok = true, @for = target, models = models.Select(m => new { id = m.Id, @base = m.BaseModel }) });
        }
        catch (Exception ex) when (!_closing.IsCancellationRequested)
        {
            var hint = probe.Provider == Provider.Azure ? " · puedes escribir el nombre del deployment a mano" : "";
            Post(new { type = "models", ok = false, @for = target, error = Short(ex.Message) + hint });
        }
    }

    async Task ListVoicesAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { Post(new { type = "voices", ok = false, error = "Pon la API key de ElevenLabs" }); return; }
        try
        {
            var voices = await new ElevenLabsTts().VoicesAsync(key, _closing.Token);
            Post(new { type = "voices", ok = true, voices = voices.Select(v => new { id = v.Id, name = v.Name, about = v.About }) });
        }
        catch (Exception ex) when (!_closing.IsCancellationRequested)
        {
            Post(new { type = "voices", ok = false, error = Short(ex.Message) });
        }
    }

    async Task McpToolsAsync(JsonNode msg)
    {
        var cfg = msg["server"]?.Deserialize<McpServerConfig>(Json);
        if (cfg is null) return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            await using var client = await McpClient.ConnectAsync(cfg, cts.Token);
            var tools = await client.ListToolsAsync(cts.Token);
            Post(new { type = "mcpTools", ok = true, tools = tools.Select(t => new { name = t.Name, description = t.Description }) });
        }
        catch (Exception ex) when (!_closing.IsCancellationRequested)
        {
            Post(new { type = "mcpTools", ok = false, error = Short(ex is OperationCanceledException ? "No respondió en 90 s" : ex.Message) });
        }
    }

    static string Short(string s) => s.Length > 180 ? s[..180] + "…" : s;

    /// <summary>Lo que se edita en Ajustes; la posición de la onda no se toca.</summary>
    static void CopyEditable(Settings from, Settings to)
    {
        to.Provider = from.Provider;
        to.Endpoint = from.Endpoint?.Trim() ?? "";
        to.ApiKey = from.ApiKey?.Trim() ?? "";
        to.Model = from.Model?.Trim() ?? "";
        to.ReasoningEffort = from.ReasoningEffort ?? "low";
        to.SpeechEngine = from.SpeechEngine;
        to.LocalModel = SpeechModels.Find(from.LocalModel).Id;
        to.Language = string.IsNullOrWhiteSpace(from.Language) ? "es" : from.Language;
        to.TranscribeModel = from.TranscribeModel?.Trim() ?? "";
        to.ElevenLabsKey = from.ElevenLabsKey?.Trim() ?? "";
        to.ElevenLabsModel = string.IsNullOrWhiteSpace(from.ElevenLabsModel) ? "scribe_v2" : from.ElevenLabsModel.Trim();
        to.MicId = from.MicId ?? "";
        to.Sensitivity = Math.Clamp(from.Sensitivity, 1, 10);
        to.TalkHotkey = Colo.Core.Keys.Hold(from.TalkHotkey ?? "", out _) ? from.TalkHotkey!.Trim() : to.TalkHotkey;
        to.DictateHotkey = string.IsNullOrWhiteSpace(from.DictateHotkey) ? ""
            : Colo.Core.Keys.Hold(from.DictateHotkey, out _) ? from.DictateHotkey.Trim() : to.DictateHotkey;
        to.VoiceOwn = from.VoiceOwn;
        to.VoiceProvider = from.VoiceProvider;
        to.VoiceEndpoint = from.VoiceEndpoint?.Trim() ?? "";
        to.VoiceKey = from.VoiceKey?.Trim() ?? "";
        to.Live = from.Live;
        to.LiveModel = string.IsNullOrWhiteSpace(from.LiveModel) ? "gpt-live-1" : from.LiveModel.Trim();
        to.LiveVoice = string.IsNullOrWhiteSpace(from.LiveVoice) ? "marin" : from.LiveVoice.Trim();
        to.SpeakReplies = from.SpeakReplies;
        to.ReplyVoice = from.ReplyVoice;
        to.ElevenVoiceId = from.ElevenVoiceId?.Trim() ?? "";
        to.ElevenVoiceName = from.ElevenVoiceName?.Trim() ?? "";
        to.ElevenTtsModel = string.IsNullOrWhiteSpace(from.ElevenTtsModel) ? ElevenLabsTts.Models[0] : from.ElevenTtsModel.Trim();
        to.StartWithWindows = from.StartWithWindows;
        to.Vision = from.Vision;
        to.DisabledTools = from.DisabledTools ?? [];
        to.McpServers = from.McpServers ?? [];
    }

    void Apply(JsonNode? settings)
    {
        Settings? edited;
        try { edited = settings?.Deserialize<Settings>(Json); } catch { return; }
        if (edited is null) return;
        CopyEditable(edited, _s);
        Changed = true;
        Saved?.Invoke();
    }

    /// <summary>
    /// Antes de cerrar, recoge lo que la página aún no mandó (se escribe con una pausa de ~0,4 s): si se cierra
    /// con la X justo después de teclear, también queda guardado.
    /// </summary>
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_flushed || e.Cancel || _web.CoreWebView2 is null) return;
        e.Cancel = true;
        _flushed = true;
        try
        {
            var json = await _web.ExecuteScriptAsync("window.flushSave ? flushSave() : null");
            var node = JsonNode.Parse(json);
            if (node is JsonObject) Apply(node);
        }
        catch (Exception ex) { Log.Write("ajustes: no pude recoger lo último al cerrar · " + ex.Message); }
        BeginInvoke(Close);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing.Cancel();
        base.OnFormClosed(e);
    }
}
