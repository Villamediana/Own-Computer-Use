using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Colo.Core;

namespace Colo;

/// <summary>Ajustes con barra lateral: Modelo · Voz · Herramientas · MCP · General.</summary>
sealed class SettingsForm : Form
{
    const int ContentW = 520;

    readonly Settings _s;
    readonly List<McpServerConfig> _servers;

    // modelo
    readonly Segmented _provider = new("Azure AI Foundry", "OpenAI") { Width = ContentW };
    readonly TextBox _endpoint = new() { BorderStyle = BorderStyle.None };
    readonly TextBox _key = new() { BorderStyle = BorderStyle.None, UseSystemPasswordChar = true };
    readonly ComboBox _model = Combo(editable: true);
    readonly Segmented _effort = new("none", "minimal", "low", "medium", "high") { Width = ContentW };
    readonly Label _connStatus = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 11, 0, 0), MaximumSize = new Size(380, 0) };
    // voz
    readonly Segmented _engine = new("En tu PC · gratis", "Modelo de la API") { Width = ContentW };
    readonly ComboBox _voiceModel = Combo(editable: true);
    readonly ComboBox _mic = Combo(editable: false);
    readonly TrackBar _sens = new() { Minimum = 1, Maximum = 10, TickStyle = TickStyle.None, AutoSize = false, Height = 28, Width = ContentW };
    readonly ToggleRow _speak = new("Leer las respuestas en voz alta");
    // herramientas
    readonly Segmented _vision = new("Cuando haga falta", "Siempre") { Width = ContentW };
    readonly Dictionary<string, ToggleRow> _groups = [];
    // mcp
    readonly FlowLayoutPanel _mcpList = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    // general
    readonly ToggleRow _autostart = new("Abrir al iniciar Windows", "Colo queda escuchando desde que enciendes el PC");

    readonly Field _voiceField;
    readonly Panel[] _pages;

    static readonly string[] EffortValues = ["none", "minimal", "low", "medium", "high"];

    static ComboBox Combo(bool editable) => new()
    {
        DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
    };

    static Label Title(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.Semibold(9.5f),
        ForeColor = Theme.Ink,
        Margin = new Padding(0, 16, 0, 7),
    };

    static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentW, 0),
        Font = Theme.Text(8.75f),
        ForeColor = Theme.Muted,
        Margin = new Padding(0, 6, 0, 0),
    };

    static Field Wide(Control inner) => new(inner) { Width = ContentW };

    public SettingsForm(Settings s)
    {
        _s = s;
        _servers = s.McpServers.Select(Clone).ToList();
        Text = "Colo · Ajustes";
        Font = Theme.Text(9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(820, 680);
        TopMost = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ── barra lateral
        var side = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = Color.FromArgb(249, 249, 250), Padding = new Padding(14, 22, 14, 14) };
        var brand = new Label { Text = "Ajustes", Font = Theme.Display(15f), AutoSize = true, Location = new Point(24, 22) };
        var nav = new NavList("Modelo", "Voz", "Herramientas", "MCP", "General") { Location = new Point(12, 68), Width = 186 };
        side.Controls.Add(brand);
        side.Controls.Add(nav);

        // ── pie con botones
        var foot = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 70,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 16, 32, 0),
            BackColor = Theme.Bg,
        };
        var save = new PillButton("Guardar", primary: true);
        var cancel = new PillButton("Cancelar", primary: false) { Margin = new Padding(0, 0, 10, 0) };
        foot.Controls.Add(save);
        foot.Controls.Add(cancel);

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        _pages =
        [
            Page("Modelo", "El cerebro que decide qué hacer en la pantalla.", ModelPage()),
            Page("Voz", "Cómo te escucha. Siempre está abierto; no hace falta tocar nada.", VoicePage()),
            Page("Herramientas", "Lo que Colo puede usar. Menos herramientas = peticiones más pequeñas.", ToolsPage()),
            Page("MCP", "Conecta servidores MCP y elige qué tools puede usar Colo.", McpPage()),
            Page("General", "", GeneralPage()),
        ];
        foreach (var p in _pages) content.Controls.Add(p);

        Controls.Add(content);
        Controls.Add(foot);
        Controls.Add(side);

        nav.Changed += (_, _) => ShowPage(nav.Index);
        ShowPage(0);

        LoadValues();
        _voiceField = (Field)_voiceModel.Parent!;
        _provider.Changed += (_, _) => Sync();
        _engine.Changed += (_, _) => Sync();
        Sync();

        save.Click += (_, _) =>
        {
            Apply(_s);
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        CancelButton = null;
    }

    internal void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
    }

    static Panel Page(string title, string subtitle, Control body)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(36, 26, 24, 0), BackColor = Theme.Bg, Visible = false };
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Bg, Location = new Point(36, 26) };
        col.Controls.Add(new Label { Text = title, Font = Theme.Display(18f), AutoSize = true, Margin = new Padding(0) });
        if (subtitle.Length > 0)
            col.Controls.Add(new Label { Text = subtitle, AutoSize = true, MaximumSize = new Size(ContentW, 0), ForeColor = Theme.Muted, Margin = new Padding(0, 4, 0, 4) });
        col.Controls.Add(body);
        page.Controls.Add(col);
        return page;
    }

    static FlowLayoutPanel Column() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        BackColor = Theme.Bg,
        Margin = new Padding(0),
    };

    Control ModelPage()
    {
        var c = Column();
        c.Controls.Add(Title("Proveedor"));
        c.Controls.Add(_provider);
        c.Controls.Add(Title("Endpoint"));
        c.Controls.Add(Wide(_endpoint));
        c.Controls.Add(Title("API key"));
        c.Controls.Add(Wide(_key));
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0), BackColor = Theme.Bg };
        var connect = new PillButton("Conectar", primary: false);
        connect.Click += async (_, _) => await ConnectAsync(connect);
        row.Controls.Add(connect);
        row.Controls.Add(_connStatus);
        c.Controls.Add(row);
        c.Controls.Add(Title("Modelo"));
        c.Controls.Add(Wide(_model));
        c.Controls.Add(Title("Razonamiento"));
        c.Controls.Add(_effort);
        c.Controls.Add(Hint("Menos razonamiento responde antes. Para clics y escritura, none o low van bien."));
        return c;
    }

    Control VoicePage()
    {
        var c = Column();
        c.Controls.Add(Title("Transcripción"));
        c.Controls.Add(_engine);
        c.Controls.Add(Hint("En tu PC usa Parakeet v3: rápido, sin internet y sin gastar tokens."));
        c.Controls.Add(Title("Modelo de transcripción de la API"));
        c.Controls.Add(Wide(_voiceModel));
        c.Controls.Add(Title("Micrófono"));
        c.Controls.Add(Wide(_mic));
        c.Controls.Add(Title("Sensibilidad"));
        c.Controls.Add(_sens);
        _speak.Margin = new Padding(0, 14, 0, 0);
        c.Controls.Add(_speak);
        return c;
    }

    Control ToolsPage()
    {
        var c = Column();
        c.Controls.Add(Title("Ver la pantalla"));
        c.Controls.Add(_vision);
        c.Controls.Add(Hint("«Cuando haga falta» manda la lista de botones y campos como texto y la captura solo si el modelo la pide: más rápido y más barato."));
        c.Controls.Add(Title("Herramientas"));
        foreach (var g in Tools.Groups)
        {
            var row = new ToggleRow(g.Title, g.Summary, ContentW);
            _groups[g.Id] = row;
            c.Controls.Add(row);
        }
        return c;
    }

    Control McpPage()
    {
        var c = Column();
        c.Controls.Add(_mcpList);
        var add = new PillButton("Añadir servidor", primary: false) { Margin = new Padding(0, 6, 0, 0) };
        add.Click += (_, _) =>
        {
            var cfg = new McpServerConfig();
            if (EditServer(cfg)) { _servers.Add(cfg); RenderServers(); }
        };
        c.Controls.Add(add);
        RenderServers();
        return c;
    }

    Control GeneralPage()
    {
        var c = Column();
        c.Controls.Add(_autostart);
        c.Controls.Add(Title("Atajos"));
        c.Controls.Add(Hint("Ctrl+Alt+Espacio   pausar / escuchar\nCtrl+Alt+X   detener lo que está haciendo\nClic en la onda   pausar · arrastrar   mover · clic derecho   menú"));
        c.Controls.Add(Title("Prints"));
        c.Controls.Add(Hint(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Colo")));
        return c;
    }

    void RenderServers()
    {
        _mcpList.SuspendLayout();
        _mcpList.Controls.Clear();
        if (_servers.Count == 0)
            _mcpList.Controls.Add(Hint("Aún no hay servidores. Un servidor MCP le da a Colo herramientas nuevas: archivos, correo, bases de datos, tu CRM…"));
        foreach (var srv in _servers)
        {
            var card = new Card { Width = ContentW, Height = 72 };
            var name = new Label { Text = srv.Name.Length > 0 ? srv.Name : "(sin nombre)", Font = Theme.Semibold(10f), AutoSize = true, Location = new Point(16, 13) };
            var where = srv.Kind == McpKind.Url ? srv.Url : srv.Command;
            if (where.Length > 40) where = where[..40] + "…";
            var tools = srv.Tools is null ? "todas las tools" : srv.Tools.Count == 1 ? "1 tool" : $"{srv.Tools.Count} tools";
            var info = new Label { Text = $"{where}  ·  {tools}", ForeColor = Theme.Muted, Font = Theme.Text(8.75f), AutoSize = false, AutoEllipsis = true, Height = 20, Width = ContentW - 230, Location = new Point(16, 38) };
            var toggle = new Toggle { On = srv.Enabled };
            toggle.Changed += (_, _) => srv.Enabled = toggle.On;
            var edit = new LinkText("Editar");
            var remove = new LinkText("Quitar", danger: true);
            edit.Click += (_, _) => { if (EditServer(srv)) RenderServers(); };
            remove.Click += (_, _) => { _servers.Remove(srv); RenderServers(); };
            card.Controls.AddRange([name, info, toggle, edit, remove]);
            card.Layout += (_, _) =>
            {
                toggle.Location = new Point(card.Width - toggle.Width - 16, (card.Height - toggle.Height) / 2);
                remove.Location = new Point(toggle.Left - remove.Width - 18, (card.Height - remove.Height) / 2);
                edit.Location = new Point(remove.Left - edit.Width - 12, (card.Height - edit.Height) / 2);
            };
            _mcpList.Controls.Add(card);
        }
        _mcpList.ResumeLayout();
    }

    bool EditServer(McpServerConfig cfg)
    {
        using var f = new McpServerForm(cfg);
        return f.ShowDialog(this) == DialogResult.OK;
    }

    static McpServerConfig Clone(McpServerConfig c) =>
        JsonSerializer.Deserialize<McpServerConfig>(JsonSerializer.Serialize(c))!;

    void LoadValues()
    {
        _provider.Index = _s.Provider == Provider.OpenAI ? 1 : 0;
        _engine.Index = _s.SpeechEngine == SpeechEngine.Api ? 1 : 0;
        _endpoint.Text = _s.Endpoint;
        _key.Text = _s.ApiKey;
        _model.Text = _s.Model;
        _voiceModel.Text = _s.TranscribeModel;
        var effort = Array.IndexOf(EffortValues, _s.ReasoningEffort);
        _effort.Index = effort >= 0 ? effort : 2;
        _mic.Items.Add("Predeterminado");
        try { _mic.Items.AddRange(Mic.Devices().Cast<object>().ToArray()); } catch { }
        _mic.SelectedIndex = Math.Clamp(_s.MicDevice + 1, 0, _mic.Items.Count - 1);
        _sens.Value = Math.Clamp(_s.Sensitivity, 1, 10);
        _speak.Toggle.On = _s.SpeakReplies;
        _autostart.Toggle.On = _s.StartWithWindows;
        _vision.Index = _s.Vision == Vision.Always ? 1 : 0;
        foreach (var (id, row) in _groups) row.Toggle.On = !_s.DisabledTools.Contains(id);
    }

    void Sync()
    {
        _endpoint.PlaceholderText = _provider.Index == 0 ? "https://mi-recurso.openai.azure.com" : "https://api.openai.com/v1  (opcional)";
        _voiceField.Enabled = _engine.Index == 1;
    }

    static string Id(ComboBox c) =>
        c.SelectedItem is ModelInfo m && m.ToString() == c.Text ? m.Id : c.Text.Split("  ·  ")[0].Trim();

    void Apply(Settings s)
    {
        s.Provider = _provider.Index == 1 ? Provider.OpenAI : Provider.Azure;
        s.Endpoint = _endpoint.Text.Trim();
        s.ApiKey = _key.Text.Trim();
        s.Model = Id(_model);
        s.SpeechEngine = _engine.Index == 1 ? SpeechEngine.Api : SpeechEngine.Local;
        s.TranscribeModel = Id(_voiceModel);
        s.ReasoningEffort = EffortValues[_effort.Index];
        s.MicDevice = _mic.SelectedIndex - 1;
        s.Sensitivity = _sens.Value;
        s.SpeakReplies = _speak.Toggle.On;
        s.StartWithWindows = _autostart.Toggle.On;
        s.Vision = _vision.Index == 1 ? Vision.Always : Vision.Auto;
        s.DisabledTools = _groups.Where(g => !g.Value.Toggle.On).Select(g => g.Key).ToList();
        s.McpServers = _servers;
    }

    async Task ConnectAsync(PillButton button)
    {
        var probe = new Settings();
        Apply(probe);
        button.Enabled = false;
        _connStatus.ForeColor = Theme.Muted;
        _connStatus.Text = "Conectando…";
        try
        {
            var models = await new AiClient(probe).ListModelsAsync();
            Fill(_model, models, probe.Model);
            Fill(_voiceModel, models, probe.TranscribeModel);
            _connStatus.ForeColor = Theme.Blue;
            _connStatus.Text = models.Count == 1 ? "1 modelo" : $"{models.Count} modelos";
            if (models.Count > 0 && _model.Text.Length == 0) _model.DroppedDown = true;
        }
        catch (Exception ex)
        {
            _connStatus.ForeColor = Theme.Danger;
            _connStatus.Text = ex.Message.Length > 140 ? ex.Message[..140] + "…" : ex.Message;
        }
        finally { button.Enabled = true; }
    }

    static void Fill(ComboBox c, List<ModelInfo> models, string current)
    {
        c.Items.Clear();
        c.Items.AddRange(models.Cast<object>().ToArray());
        var match = models.FirstOrDefault(m => m.Id == current);
        if (match is not null) c.SelectedItem = match;
        else c.Text = current;
    }
}
