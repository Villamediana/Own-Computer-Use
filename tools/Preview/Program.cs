using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Colo;
using Colo.Core;

// Uso: Preview shots <carpeta>   → PNG de la onda y del cursor
//      Preview run <endpoint> "<orden>"  → bucle real contra un endpoint (OpenAI-compatible)
var outDir = args.Length > 1 ? args[1] : ".";
if (args[0] == "shots")
{
    Directory.CreateDirectory(outDir);
    void Save(Bitmap fg, string name, float s)
    {
        using var bg = new Bitmap(fg.Width + (int)(80 * s), fg.Height + (int)(60 * s));
        using (var g = Graphics.FromImage(bg))
        {
            using var lg = new LinearGradientBrush(new Rectangle(0, 0, bg.Width, bg.Height), Color.FromArgb(236, 239, 244), Color.FromArgb(214, 220, 230), 70f);
            g.FillRectangle(lg, 0, 0, bg.Width, bg.Height);
            g.DrawImage(fg, (int)(40 * s), (int)(30 * s));
        }
        bg.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
    }
    foreach (var s in new[] { 1f, 1.5f })
    {
        var p = new WavePainter { Scale = s };
        (string name, Mode mode, double level, double energy, string caption)[] states =
        [
            ("listening", Mode.Listening, 0.12, 0, ""),
            ("hearing", Mode.Listening, 0.85, 0, ""),
            ("heard", Mode.Busy, 0.38, 0.6, "«abre chrome y entra en youtube»"),
            ("busy", Mode.Busy, 0.38, 1, ""),
            ("reply", Mode.Listening, 0.12, 0, "¿Lo envío a Marcela o solo lo dejo escrito?"),
            ("paused", Mode.Paused, 0, 0, ""),
            ("problem", Mode.Problem, 0, 0, "Toca para configurar"),
        ];
        foreach (var (name, mode, level, energy, caption) in states)
        {
            p.Mode = mode; p.Level = level; p.Energy = energy; p.T = 1.3; p.Caption = caption;
            p.CaptionColor = mode == Mode.Problem ? Theme.Danger : Theme.Ink;
            using var bmp = p.Paint();
            Save(bmp, $"wave-{name}@{s}", s);
        }
        using var c1 = AiCursor.Draw("Clic «Buscar»", 0, 0, s);
        Save(c1, $"cursor@{s}", s);
        using var c2 = AiCursor.Draw("Escribiendo…", 14, 0.5f, s);
        Save(c2, $"cursor-click@{s}", s);
    }
    Console.WriteLine("ok");
    return;
}

if (args[0] == "settings")
{
    Directory.CreateDirectory(outDir);
    System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
    var st = new Settings
    {
        Endpoint = "https://latamfoundryai.openai.azure.com", ApiKey = "x", Model = "gpt-5.6-luna",
        DisabledTools = ["files"],
        McpServers =
        [
            new() { Name = "Archivos", Command = @"npx -y @modelcontextprotocol/server-filesystem C:\Users\miguel", Tools = ["read_file", "list_directory"] },
            new() { Name = "CRM", Kind = McpKind.Url, Url = "https://crm.vertis/mcp", Enabled = false },
        ],
    };
    using var f = new SettingsForm(st);
    f.Show();
    for (var i = 0; i < 5; i++)
    {
        f.ShowPage(i);
        System.Windows.Forms.Application.DoEvents();
        using var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
        bmp.Save(Path.Combine(outDir, $"settings-{i}.png"), ImageFormat.Png);
    }
    using var m = new McpServerForm(st.McpServers[0]);
    m.Show();
    System.Windows.Forms.Application.DoEvents();
    using (var bmp = new Bitmap(m.ClientSize.Width, m.ClientSize.Height))
    {
        m.DrawToBitmap(bmp, new Rectangle(Point.Empty, m.ClientSize));
        bmp.Save(Path.Combine(outDir, "mcp-form.png"), ImageFormat.Png);
    }
    Console.WriteLine("ok");
    return;
}

if (args[0] == "run")
{
    var settings = new Settings { Provider = Provider.OpenAI, Endpoint = args[1], ApiKey = "sk-preview", Model = "preview" };
    var pc = new WindowsComputer();
    var agent = new Agent(new AiClient(settings), settings, pc);
    agent.Event += e => Console.WriteLine($"{e.Kind}: {e.Text}");
    await agent.Hear(args[2]);
    Console.WriteLine("fin");
}
