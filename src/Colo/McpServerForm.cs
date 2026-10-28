using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Colo.Core;

namespace Colo;

/// <summary>Alta / edición de un servidor MCP: conecta, lista sus tools y deja elegir cuáles usa Colo.</summary>
sealed class McpServerForm : Form
{
    const int W = 500;
    readonly McpServerConfig _cfg;
    readonly TextBox _name = new() { BorderStyle = BorderStyle.None };
    readonly Segmented _kind = new("Comando local", "URL") { Width = W };
    readonly TextBox _target = new() { BorderStyle = BorderStyle.None };
    readonly TextBox _extra = new() { BorderStyle = BorderStyle.None, Multiline = true, ScrollBars = ScrollBars.Vertical };
    readonly Label _targetTitle = Title("Comando");
    readonly Label _extraTitle = Title("Variables de entorno");
    readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 11, 0, 0), MaximumSize = new Size(340, 0) };
    readonly CheckedListBox _tools = new()
    {
        BorderStyle = BorderStyle.None,
        CheckOnClick = true,
        IntegralHeight = false,
        BackColor = Theme.Fill,
        Height = 170,
        Width = W - 24,
    };
    readonly ToggleRow _all = new("Usar todas las tools", null, W);
    List<McpTool> _found = [];

    static Label Title(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.Semibold(9.5f),
        Margin = new Padding(0, 16, 0, 7),
    };

    public McpServerForm(McpServerConfig cfg)
    {
        _cfg = cfg;
        Text = "Servidor MCP";
        Font = Theme.Text(9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(32, 24, 32, 26);
        TopMost = true;

        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Bg, Location = new Point(32, 24) };
        Controls.Add(col);
        col.Controls.Add(new Label { Text = "Servidor MCP", Font = Theme.Display(17f), AutoSize = true, Margin = new Padding(0) });
        col.Controls.Add(Title("Nombre"));
        col.Controls.Add(new Field(_name) { Width = W });
        col.Controls.Add(Title("Tipo"));
        col.Controls.Add(_kind);
        col.Controls.Add(_targetTitle);
        col.Controls.Add(new Field(_target) { Width = W });
        col.Controls.Add(_extraTitle);
        col.Controls.Add(new Field(_extra) { Width = W, Height = 64 });

        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 16, 0, 0), BackColor = Theme.Bg };
        var connect = new PillButton("Conectar y ver tools", primary: false);
        connect.Click += async (_, _) => await ConnectAsync(connect);
        row.Controls.Add(connect);
        row.Controls.Add(_status);
        col.Controls.Add(row);

        _all.Margin = new Padding(0, 12, 0, 4);
        col.Controls.Add(_all);
        var listWrap = new Field(_tools) { Width = W, Height = 186, Padding = new Padding(12, 8, 8, 8) };
        col.Controls.Add(listWrap);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 22, 0, 0), BackColor = Theme.Bg };
        var save = new PillButton("Guardar", primary: true);
        var cancel = new PillButton("Cancelar", primary: false) { Margin = new Padding(10, 0, 0, 0) };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        col.Controls.Add(buttons);

        _name.Text = cfg.Name;
        _kind.Index = cfg.Kind == McpKind.Url ? 1 : 0;
        _target.Text = cfg.Kind == McpKind.Url ? cfg.Url : cfg.Command;
        _extra.Text = (cfg.Kind == McpKind.Url ? cfg.Headers : cfg.Env).Replace("\n", "\r\n");
        _all.Toggle.On = cfg.Tools is null;
        foreach (var t in cfg.Tools ?? []) _tools.Items.Add(t, true);
        SyncKind();
        SyncAll();

        _kind.Changed += (_, _) => SyncKind();
        _all.Toggle.Changed += (_, _) => SyncAll();
        save.Click += (_, _) =>
        {
            if (!Apply(_cfg)) return;
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
    }

    void SyncKind()
    {
        var url = _kind.Index == 1;
        _targetTitle.Text = url ? "URL" : "Comando";
        _target.PlaceholderText = url ? "https://servidor/mcp" : @"npx -y @modelcontextprotocol/server-filesystem C:\Users\yo";
        _extraTitle.Text = url ? "Cabeceras (una por línea: Nombre: valor)" : "Variables de entorno (una por línea: CLAVE=valor)";
    }

    void SyncAll() => _tools.Enabled = !_all.Toggle.On;

    bool Apply(McpServerConfig c)
    {
        var url = _kind.Index == 1;
        var target = _target.Text.Trim();
        string? problem = target.Length == 0 ? (url ? "Falta la URL" : "Falta el comando")
            : url && !(Uri.TryCreate(target, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http")) ? "URL inválida"
            : null;
        if (problem is not null)
        {
            _status.ForeColor = Theme.Danger;
            _status.Text = problem;
            return false;
        }
        c.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : (url ? new Uri(target).Host : target.Split(' ')[0]);
        c.Kind = url ? McpKind.Url : McpKind.Command;
        c.Url = url ? _target.Text.Trim() : "";
        c.Command = url ? "" : _target.Text.Trim();
        var extra = _extra.Text.Replace("\r\n", "\n").Trim();
        c.Headers = url ? extra : "";
        c.Env = url ? "" : extra;
        c.Tools = _all.Toggle.On ? null : _tools.CheckedItems.Cast<string>().ToList();
        return true;
    }

    async Task ConnectAsync(PillButton button)
    {
        var probe = new McpServerConfig();
        if (!Apply(probe)) return;
        button.Enabled = false;
        _status.ForeColor = Theme.Muted;
        _status.Text = "Conectando…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await using var client = await McpClient.ConnectAsync(probe, cts.Token);
            _found = await client.ListToolsAsync(cts.Token);
            var chosen = _cfg.Tools is null ? null : new HashSet<string>(_tools.CheckedItems.Cast<string>());
            _tools.Items.Clear();
            foreach (var t in _found) _tools.Items.Add(t.Name, chosen is null || chosen.Contains(t.Name));
            _status.ForeColor = Theme.Blue;
            _status.Text = _found.Count == 1 ? "1 tool" : $"{_found.Count} tools";
        }
        catch (Exception ex)
        {
            _status.ForeColor = Theme.Danger;
            _status.Text = ex.Message.Length > 160 ? ex.Message[..160] + "…" : ex.Message;
        }
        finally { button.Enabled = true; }
    }
}
