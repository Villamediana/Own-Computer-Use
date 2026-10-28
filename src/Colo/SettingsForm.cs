using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Colo.Core;

namespace Colo;

sealed class SettingsForm : Form
{
    readonly Settings _s;
    readonly Segmented _provider = new("Azure AI Foundry", "OpenAI");
    readonly Segmented _engine = new("En tu PC · gratis", "API");
    readonly TextBox _endpoint = new() { BorderStyle = BorderStyle.None };
    readonly TextBox _key = new() { BorderStyle = BorderStyle.None, UseSystemPasswordChar = true };
    readonly ComboBox _model = Combo(editable: true);
    readonly ComboBox _voiceModel = Combo(editable: true);
    readonly ComboBox _effort = Combo(editable: false);
    readonly ComboBox _mic = Combo(editable: false);
    readonly TrackBar _sens = new() { Minimum = 1, Maximum = 10, TickStyle = TickStyle.None, AutoSize = false, Height = 28, Width = 400 };
    readonly ToggleRow _speak = new("Leer respuestas en voz alta");
    readonly ToggleRow _autostart = new("Abrir al iniciar Windows");
    readonly Label _connStatus = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 11, 0, 0) };
    readonly Field _endpointField, _voiceField;
    static readonly string[] Efforts = ["none", "minimal", "low", "medium", "high", "no enviar"];

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
        Margin = new Padding(0, 18, 0, 7),
    };

    public SettingsForm(Settings s)
    {
        _s = s;
        Text = "Colo";
        Font = Theme.Text(9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(32, 22, 32, 28);
        TopMost = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        FlowLayoutPanel Column(int left = 0) => new()
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Theme.Bg,
            Margin = new Padding(left, 0, 0, 0),
        };

        var page = Column();
        Controls.Add(page);
        page.Controls.Add(new Label { Text = "Ajustes", Font = Theme.Display(20f), AutoSize = true, Margin = new Padding(0, 0, 0, 0) });

        var columns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Theme.Bg };
        page.Controls.Add(columns);
        var left = Column();
        var right = Column(40);
        columns.Controls.Add(left);
        columns.Controls.Add(right);

        // ── conexión
        left.Controls.Add(Title("Proveedor"));
        left.Controls.Add(_provider);
        left.Controls.Add(Title("Endpoint"));
        _endpointField = new Field(_endpoint);
        left.Controls.Add(_endpointField);
        left.Controls.Add(Title("API key"));
        left.Controls.Add(new Field(_key));

        var connectRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0), BackColor = Theme.Bg };
        var connect = new PillButton("Conectar", primary: false);
        connectRow.Controls.Add(connect);
        connectRow.Controls.Add(_connStatus);
        left.Controls.Add(connectRow);

        left.Controls.Add(Title("Modelo"));
        left.Controls.Add(new Field(_model));
        left.Controls.Add(Title("Razonamiento"));
        left.Controls.Add(new Field(_effort));

        // ── voz
        right.Controls.Add(Title("Escuchar con"));
        right.Controls.Add(_engine);
        right.Controls.Add(Title("Modelo de transcripción"));
        _voiceField = new Field(_voiceModel);
        right.Controls.Add(_voiceField);
        right.Controls.Add(Title("Micrófono"));
        right.Controls.Add(new Field(_mic));
        right.Controls.Add(Title("Sensibilidad"));
        right.Controls.Add(_sens);
        right.Controls.Add(_speak);
        right.Controls.Add(_autostart);

        var save = new PillButton("Guardar", primary: true) { Margin = new Padding(0, 28, 0, 0) };
        page.Controls.Add(save);

        // valores actuales
        _provider.Index = _s.Provider == Provider.OpenAI ? 1 : 0;
        _engine.Index = _s.SpeechEngine == SpeechEngine.Api ? 1 : 0;
        _endpoint.Text = _s.Endpoint;
        _endpoint.PlaceholderText = "https://mi-recurso.openai.azure.com";
        _key.Text = _s.ApiKey;
        _model.Text = _s.Model;
        _voiceModel.Text = _s.TranscribeModel;
        _effort.Items.AddRange(Efforts);
        _effort.SelectedItem = string.IsNullOrEmpty(_s.ReasoningEffort) ? "no enviar" : _s.ReasoningEffort;
        if (_effort.SelectedIndex < 0) _effort.SelectedItem = "low";
        _mic.Items.Add("Predeterminado");
        try { _mic.Items.AddRange(Mic.Devices().Cast<object>().ToArray()); } catch { }
        _mic.SelectedIndex = Math.Clamp(_s.MicDevice + 1, 0, _mic.Items.Count - 1);
        _sens.Value = Math.Clamp(_s.Sensitivity, 1, 10);
        _speak.Toggle.On = _s.SpeakReplies;
        _autostart.Toggle.On = _s.StartWithWindows;

        _provider.Changed += (_, _) => Sync();
        _engine.Changed += (_, _) => Sync();
        Sync();

        connect.Click += async (_, _) => await ConnectAsync(connect);
        save.Click += (_, _) =>
        {
            Apply(_s);
            DialogResult = DialogResult.OK;
        };
    }

    void Sync()
    {
        _endpoint.PlaceholderText = _provider.Index == 0 ? "https://mi-recurso.openai.azure.com" : "https://api.openai.com/v1";
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
        var effort = _effort.SelectedItem as string ?? "low";
        s.ReasoningEffort = effort == "no enviar" ? "" : effort;
        s.MicDevice = _mic.SelectedIndex - 1;
        s.Sensitivity = _sens.Value;
        s.SpeakReplies = _speak.Toggle.On;
        s.StartWithWindows = _autostart.Toggle.On;
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
            _connStatus.MaximumSize = new Size(260, 0);
            _connStatus.Text = ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
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
