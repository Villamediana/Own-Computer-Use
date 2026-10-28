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
//      Preview e2e <carpeta> <prueba.html>  → Colo entero en Windows real con órdenes de texto (ver E2e.cs)
if (args[0] == "e2e") return E2e.Run(args[1], args[2]);
//      Preview marks <prueba.html>  → elementos de UI Automation del Bloc y de Chrome (sin modelo)
if (args[0] == "marks") return E2e.Marks(args[1]);
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
    return 0;
}

if (args[0] == "menu")
{
    // El menú de la onda dibujado en PNG: abierto con el ratón encima de una fila, y a media animación.
    Directory.CreateDirectory(outDir);
    using var menu = new PopMenu();
    menu.Load("Escuchando", "Tokens · hoy 48,2 mil · total 1,3 M", Theme.Blue,
    [
        new("Pausar", PopMenu.Icon.Pause, () => { }),
        new("Detener lo que hace", PopMenu.Icon.Stop, () => { }),
        new("Ajustes…", PopMenu.Icon.Settings, () => { }, GapBefore: true),
        new("Salir", PopMenu.Icon.Power, () => { }, Danger: true),
    ], hover: 1);
    foreach (var (name, shown) in new[] { ("menu", 1f), ("menu-entrando", 0.55f) })
    {
        using var fg = menu.Draw(shown);
        using var bg = new Bitmap(fg.Width + 40, fg.Height + 40);
        using (var g = Graphics.FromImage(bg))
        {
            using var lg = new LinearGradientBrush(new Rectangle(0, 0, bg.Width, bg.Height), Color.FromArgb(236, 239, 244), Color.FromArgb(214, 220, 230), 70f);
            g.FillRectangle(lg, 0, 0, bg.Width, bg.Height);
            g.DrawImage(fg, 20, 20);
        }
        bg.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
    }
    Console.WriteLine("ok");
    return 0;
}

if (args[0] == "wake")
{
    // «Luna» dentro del .exe único (ONNX Runtime nativo incluido): Preview wake <carpeta con luna.pcm y lua.pcm>
    var ok = true;
    foreach (var (name, should) in new[] { ("luna", true), ("lua", false) })
    {
        // Un detector nuevo por grabación: recuerda los últimos 1,28 s y el «luna» anterior contaría.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var model = WakeModel.Bundled();
        Console.WriteLine($"modelo cargado en {sw.ElapsedMilliseconds} ms");
        var bytes = File.ReadAllBytes(Path.Combine(args[1], name + ".pcm"));
        var pcm = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
        sw.Restart();
        var best = 0f;
        var n = 0;
        for (var i = 0; i + WakeModel.Chunk <= pcm.Length; i += WakeModel.Chunk, n++) best = Math.Max(best, model.Score(pcm.AsSpan(i, WakeModel.Chunk)));
        var fired = best >= WakeWord.Cutoff;
        Console.WriteLine($"{name}: máx {best:0.000} → {(fired ? "despierta" : "no")} · {sw.Elapsed.TotalMilliseconds / n:0.0} ms por trozo de 80 ms");
        ok &= fired == should;
    }
    Console.WriteLine(ok ? "ok" : "FALLA");
    return ok ? 0 : 1;
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
    return 0;
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
return 0;
