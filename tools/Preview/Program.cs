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
        // La gota sacudida (aplastada y estirada) y la frase a media entrada.
        p.Mode = Mode.Busy; p.Level = 0.38; p.Energy = 1; p.Caption = "«abre chrome»"; p.CaptionColor = Theme.Ink;
        foreach (var (name, sq, rise, scale) in new[] { ("squash", 0.9f, 5f, 0.93f), ("stretch", -0.8f, -1.5f, 1.03f) })
        {
            p.Squash = sq; p.CaptionRise = rise; p.CaptionScale = scale;
            using var bmp = p.Paint();
            Save(bmp, $"wave-{name}@{s}", s);
        }
        p.Squash = 0; p.CaptionRise = 0; p.CaptionScale = 1;

        using var c1 = AiCursor.Draw("Clic «Buscar»", s);
        Save(c1, $"cursor@{s}", s);
        // El clic cuadro a cuadro: se hunde, ondas y chispas que se abren y se apagan.
        foreach (var t in new[] { 0.08f, 0.22f, 0.4f, 0.65f })
        {
            var look = AiCursor.Look.Still;
            look.Clicks = [t];
            look.Arrow = t < 0.15f ? 0.85f : t < 0.3f ? 1.05f : 1;
            using var c = AiCursor.Draw("Clic «Buscar»", s, look);
            Save(c, $"cursor-click-{(int)(t * 100):00}@{s}", s);
        }
        // La etiqueta brotando y cambiando de texto.
        foreach (var (scale, mix, width) in new[] { (0.55f, 1f, 0f), (1.06f, 1f, 0f) })
        {
            var look = AiCursor.Look.Still;
            look.TagScale = scale; look.TagAlpha = Math.Min(1, scale);
            using var c = AiCursor.Draw("Escribiendo…", s, look);
            Save(c, $"cursor-tag-{(int)(scale * 100)}@{s}", s);
        }
        {
            var look = AiCursor.Look.Still;
            look.Prev = "Clic «Buscar»"; look.Mix = 0.5f; look.TagWidth = 0;
            using var c = AiCursor.Draw("Escribiendo…", s, look);
            Save(c, $"cursor-tag-mix@{s}", s);
        }
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

if (args[0] == "glide")
{
    // El ratón de Colo en Windows real: cada paso al ritmo del monitor, sin tirones, y termina exacto.
    // Ritmo puro del compositor y costo de pintar el cursor, para separar monitor lento de dibujo lento.
    double Cadence(Action? each)
    {
        var w = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++) { each?.Invoke(); Colo.Native.WaitFrame(); }
        return w.Elapsed.TotalMilliseconds / 30;
    }
    var cursor = AiCursor.Create();
    var refresh = Cadence(null);
    Console.WriteLine($"refresco solo: {refresh:0.0} ms · moviendo el ratón: {Cadence(() => Colo.Native.SetCursorPos(300, 300)):0.0} ms" +
        $" · pintando el cursor: {Cadence(() => cursor.Follow(new Point(300, 300))):0.0} ms");
    cursor.Say("Probando");
    Thread.Sleep(300);
    var move = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 30; i++) cursor.Follow(new Point(300 + i, 300));
    var moveMs = move.Elapsed.TotalMilliseconds / 30;
    cursor.Pulse();
    var paint = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 30; i++) cursor.Say("Probando " + i);
    Console.WriteLine($"mover el cursor visible: {moveMs:0.00} ms · repintarlo: {paint.Elapsed.TotalMilliseconds / 30:0.00} ms" +
        $" · deslizando con el cursor: {Cadence(() => cursor.Follow(new Point(320, 300))):0.0} ms por cuadro");
    Thread.Sleep(400);
    var ok = true;
    var targets = new[] { new Point(200, 200), new Point(900, 600), new Point(910, 605), new Point(150, 500) };
    var from = new Point(500, 300);
    Colo.Native.SetCursorPos(from.X, from.Y);
    foreach (var to in targets)
    {
        var stamps = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cursor.Say("Probando");
        Input.Glide(from, to, q => { stamps.Add(sw.Elapsed.TotalMilliseconds); cursor.Follow(q); });
        cursor.Pulse();
        Colo.Native.GetCursorPos(out var end);
        var gaps = stamps.Zip(stamps.Skip(1), (a, b) => b - a).ToList();
        var worst = gaps.Count > 0 ? gaps.Max() : 0;
        var mean = gaps.Count > 0 ? gaps.Average() : 0;
        var expected = Motion.Duration(Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2)));
        // Termina exacto y sin tirones: cada paso cerca del refresco de esta pantalla (las VM sin GPU componen
        // a la mitad cuando algo se mueve), y ningún hueco grande.
        var fine = end.x == to.X && end.y == to.Y && (expected == 0 || (worst < 70 && mean <= refresh * 2.2));
        Console.WriteLine($"{from} → {to}: {sw.ElapsedMilliseconds} ms (esperado {expected:0}), {stamps.Count} cuadros, " +
            $"cada {mean:0.0} ms (peor {worst:0.0}), termina en ({end.x},{end.y}) {(fine ? "" : "← FALLA")}");
        ok &= fine;
        from = to;
    }
    cursor.Glide(new Point(400, 400), "Clic «Buscar»");
    cursor.Pulse();
    Thread.Sleep(400);
    cursor.Away();
    Thread.Sleep(400);
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
