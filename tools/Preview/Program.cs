using System;
using System.IO;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
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

if (args[0] == "menu")
{
    // El menú contextual dibujado en un bitmap para revisar su aspecto.
    Directory.CreateDirectory(outDir);
    var menu = new System.Windows.Forms.ContextMenuStrip
    {
        Renderer = new MenuRenderer(), Font = Theme.Text(10f), ShowImageMargin = false, ShowCheckMargin = false,
        Padding = new System.Windows.Forms.Padding(0, 5, 0, 5), BackColor = Color.White,
    };
    foreach (var t in new[] { "Pausar", "Detener lo que hace", "Olvidar contexto", "-", "Ajustes…", "Salir" })
    {
        if (t == "-") { menu.Items.Add(new System.Windows.Forms.ToolStripSeparator()); continue; }
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(t) { Padding = new System.Windows.Forms.Padding(6, 5, 18, 5), Tag = t == "Salir" ? "danger" : null });
    }
    menu.Show(new Point(100, 100));
    ((System.Windows.Forms.ToolStripMenuItem)menu.Items[1]).Select();
    System.Windows.Forms.Application.DoEvents();
    using var bmp = new Bitmap(menu.Width, menu.Height);
    menu.DrawToBitmap(bmp, new Rectangle(Point.Empty, menu.Size));
    bmp.Save(Path.Combine(outDir, "menu.png"), ImageFormat.Png);
    Console.WriteLine("ok");
    return;
}

if (args[0] == "voice")
{
    // Mismo camino que el micrófono: PCM 16 kHz en bloques de 40 ms → detector por volumen → Whisper.
    // Uso: Preview voice <modelo.bin> <audio.wav>. Todo queda también en colo.log (en Wine la consola se pierde).
    void Say(string t) { Console.WriteLine(t); Log.Write("preview: " + t); }
    var audio = Wav.Read(args[2], out var rate);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    WhisperNative.Prepare();
    Say($"librerías: {sw.ElapsedMilliseconds} ms");
    sw.Restart();
    Say("cargando whisper…");
    using var stt = new LocalStt(args[1]) { Language = "es" };
    Say($"whisper cargado: {sw.ElapsedMilliseconds} ms");
    using var listener = new Listener(7);
    var phrases = new List<float[]>();
    listener.Utterance += phrases.Add;
    var a16 = audio; // el wav de prueba ya es 16 kHz
    var pcm = new short[24000].Concat(a16.Select(f => (short)(f * 32767))).Concat(new short[40000]).ToArray();
    for (var i = 0; i < pcm.Length; i += 640) listener.Feed(pcm.AsSpan(i, Math.Min(640, pcm.Length - i)));
    foreach (var p in phrases)
    {
        sw.Restart();
        Say($"frase {p.Length / 16000.0:0.0}s → «{stt.Transcribe(p)}» ({sw.ElapsedMilliseconds} ms)");
    }
    Say(phrases.Count == 0 ? "SIN FRASES" : "ok");
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
