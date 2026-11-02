using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Colo.Core;

/// <summary>
/// Lo que se monta en el palco no lo escribe el agente: le pasa a un diseñador (otra llamada al mismo modelo, con su
/// propio prompt fijo y más razonamiento) qué contar y los datos reales. El diseñador tiene libertad total de forma
/// y devuelve un documento HTML completo; aquí se le pone la caja de herramientas (importmap) y el puente con Colo.
/// Así el prompt del agente sigue corto (lo rápido sigue rápido) y lo visual sale con calma y en segundo plano.
/// </summary>
public static class StageDoc
{
    public const string Origin = "https://palco.colo";

    public static readonly Dictionary<string, string> Imports = new()
    {
        ["three"] = "https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js",
        ["three/addons/"] = "https://cdn.jsdelivr.net/npm/three@0.170.0/examples/jsm/",
        ["gsap"] = "https://cdn.jsdelivr.net/npm/gsap@3.12.5/index.js",
        ["gsap/"] = "https://cdn.jsdelivr.net/npm/gsap@3.12.5/",
        ["d3"] = "https://cdn.jsdelivr.net/npm/d3@7.9.0/+esm",
        ["chart.js"] = "https://cdn.jsdelivr.net/npm/chart.js@4.4.6/+esm",
        ["chart.js/auto"] = "https://cdn.jsdelivr.net/npm/chart.js@4.4.6/auto/+esm",
        ["echarts"] = "https://cdn.jsdelivr.net/npm/echarts@5.5.1/+esm",
        ["maplibre-gl"] = "https://cdn.jsdelivr.net/npm/maplibre-gl@4.7.1/+esm",
        ["leaflet"] = "https://cdn.jsdelivr.net/npm/leaflet@1.9.4/+esm",
        ["roughjs"] = "https://cdn.jsdelivr.net/npm/roughjs@4.6.6/+esm",
        ["animejs"] = "https://cdn.jsdelivr.net/npm/animejs@3.2.2/+esm",
        ["motion"] = "https://cdn.jsdelivr.net/npm/motion@11.11.17/+esm",
        ["matter-js"] = "https://cdn.jsdelivr.net/npm/matter-js@0.20.0/+esm",
        ["tone"] = "https://cdn.jsdelivr.net/npm/tone@15.0.4/+esm",
        ["katex"] = "https://cdn.jsdelivr.net/npm/katex@0.16.11/+esm",
        ["marked"] = "https://cdn.jsdelivr.net/npm/marked@14.1.3/+esm",
        ["mermaid"] = "https://cdn.jsdelivr.net/npm/mermaid@11.4.0/+esm",
        ["canvas-confetti"] = "https://cdn.jsdelivr.net/npm/canvas-confetti@1.9.3/+esm",
    };

    /// <summary>
    /// Pone delante, en el head del documento del diseñador: importmap, el puente y los avisos (errores de JS, si quedó
    /// en blanco). Acepta también un fragmento sin html/head.
    /// </summary>
    public static string Wrap(string doc)
    {
        doc = Clean(doc);
        var map = "{\"imports\":{" + string.Join(",", Imports.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}}";
        var head = "<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + $"<script type=\"importmap\">{map}</script><script>{Bridge}</script>";
        // Un importmap propio rompería el nuestro (solo vale el primero, antes de los módulos): fuera.
        doc = Regex.Replace(doc, @"<script[^>]*type\s*=\s*[""']importmap[""'][^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var h = Regex.Match(doc, @"<head(\s[^>]*)?>", RegexOptions.IgnoreCase);
        if (h.Success) return doc.Insert(h.Index + h.Length, head);
        var html = Regex.Match(doc, @"<html(\s[^>]*)?>", RegexOptions.IgnoreCase);
        if (html.Success) return doc.Insert(html.Index + html.Length, "<head>" + head + "</head>");
        return "<!doctype html><html><head>" + head + "</head><body>" + doc + "</body></html>";
    }

    /// <summary>Sin el ```html … ``` ni el texto alrededor que a veces añade el modelo.</summary>
    public static string Clean(string text)
    {
        var t = text.Trim();
        var fence = Regex.Match(t, @"```(?:html)?\s*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (fence.Success) t = fence.Groups[1].Value.Trim();
        var start = Regex.Match(t, @"<!doctype html|<html", RegexOptions.IgnoreCase);
        if (start.Success && start.Index > 0) t = t[start.Index..];
        var end = t.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        if (end > 0) t = t[..(end + 7)];
        return t;
    }

    /// <summary>
    /// colo.say(texto) lo lee en voz alta (narrar); colo.open(url) abre en el navegador; colo.ask(texto) propone una
    /// pregunta de vuelta (el palco pide confirmarla con un clic); colo.python() carga Pyodide; colo.theme.
    /// </summary>
    const string Bridge = """
(function(){var errs=[];function post(m){try{parent.postMessage(Object.assign({colo:1},m),"*")}catch(e){}}
function add(t){t=String(t).slice(0,300);if(errs.length<6&&errs.indexOf(t)<0)errs.push(t)}
addEventListener("error",function(e){var t=e.target;if(t&&t!==window&&(t.src||t.href)){if(!/fonts\.g|favicon/.test(t.src||t.href))add("no cargó "+(t.src||t.href))}else add((e.message||"error")+(e.lineno?" (línea "+e.lineno+")":""))},true);
addEventListener("unhandledrejection",function(e){add("promesa: "+(e.reason&&(e.reason.message||e.reason)))});
var ce=console.error;console.error=function(){add(Array.prototype.join.call(arguments," "));ce.apply(console,arguments)};
var py=null;
window.colo={ask:function(t){post({type:"ask",text:String(t).slice(0,500)})},open:function(u){post({type:"open",url:String(u)})},
say:function(t){post({type:"say",text:String(t).slice(0,600)})},
theme:matchMedia("(prefers-color-scheme: dark)").matches?"dark":"light",
python:function(){return py||(py=import("https://cdn.jsdelivr.net/pyodide/v0.26.4/full/pyodide.mjs").then(function(m){return m.loadPyodide()}))}};
function report(){var b=document.body,txt=(b&&b.innerText||"").trim().length,
media=document.querySelectorAll("canvas,svg,img,video,iframe").length;post({type:"rendered",errors:errs,blank:txt<3&&media===0})}
addEventListener("load",function(){setTimeout(report,1800)});})();
""";
}

/// <summary>El diseñador del palco: una llamada al modelo que devuelve una pieza HTML hecha para este pedido.</summary>
public static class StageDesigner
{
    public const string Prompt = """
Eres el diseñador del palco de Colo: director de arte y creative technologist de primer nivel. Cada pedido es una
pieza única, hecha a medida, que se ve en una ventana junto a la pantalla del usuario (tamaño en el pedido).
Libertad total de forma: infografía editorial, escena 3D, ilustración animada, pizarra dibujada a mano, panel vivo,
mapa narrado, simulación interactiva, galería cinematográfica, línea de tiempo… elige lo que mejor cuente ESTO.

Listón (Apple keynote, NYT Graphics, The Pudding, Stripe, Linear):
- Una idea principal que se entiende en 2 segundos. Jerarquía tipográfica fuerte: titular grande y con intención,
  cifras protagonistas enormes (tabular-nums), texto secundario discreto. Espacio generoso, todo alineado a una rejilla.
- Paleta deliberada: fondo con carácter (claro cálido o oscuro profundo según el tema y colo.theme), un acento y
  neutros; el color significa algo. Nada de arcoíris, glow barato, sombras pesadas ni emojis decorativos.
- Movimiento con intención: coreografía de entrada escalonada con easing suave, números que cuentan hasta su valor,
  trazos que se dibujan, transiciones entre estados. Fluido (60 fps); respeta prefers-reduced-motion.
- Ocupa la ventana entera (100vw × 100vh), sin scroll salvo contenido largo de verdad; se adapta al redimensionar.
- Detalles de oficio: hover con el valor exacto, leyendas integradas en el gráfico (no cajas aparte), anotaciones que
  explican el porqué, fuente y fecha al pie, pequeñas.
- Explicar cómo funciona algo: escenas que se construyen una tras otra, cada una narrada con colo.say("…") en el
  idioma del usuario; arranca sola; controles discretos ◀ ▶ ⏸ ↺.
- Interactivo cuando ayuda: sliders, conmutadores, arrastrar, girar el 3D. Todo control hace algo real.

Caja de herramientas (importmap ya puesto, import por nombre en <script type="module">):
three y 'three/addons/…' (OrbitControls, postprocessing…), gsap y 'gsap/ScrollTrigger.js', 'gsap/MotionPathPlugin.js'…,
d3, chart.js/auto, echarts, maplibre-gl (estilos https://tiles.openfreemap.org/styles/positron | liberty | dark;
coordenadas [lng, lat]; CSS https://cdn.jsdelivr.net/npm/maplibre-gl@4.7.1/dist/maplibre-gl.css), leaflet, roughjs
(trazo a mano), animejs, motion, matter-js (física), tone (sonido), katex (CSS
https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.css), marked, mermaid, canvas-confetti.
Cualquier otro paquete npm: import desde https://esm.sh/<paquete>@<versión>. Python de verdad (numpy, pandas,
matplotlib…): const py = await colo.python(); await py.loadPackage(['numpy']); py.runPython(…).
Fuentes de Google Fonts con <link> (Inter, Inter Tight, Fraunces, Instrument Serif, Space Grotesk, JetBrains Mono,
Caveat para letra a mano…).
Puente: colo.say(texto) habla en voz alta; colo.open(url) abre en el navegador; colo.ask(texto) para un botón que
el usuario pulsa si quiere preguntar algo más (nunca al cargar); colo.theme es 'dark' o 'light'.

Datos: usa SOLO los del pedido (cifras, fechas, fuentes, URLs de imágenes y vídeos). Nunca inventes números, fotos ni
enlaces; si falta algo, diséñalo sin ello o márcalo como ilustrativo. YouTube:
<iframe src="https://www.youtube-nocookie.com/embed/ID" allow="autoplay; encrypted-media; picture-in-picture" allowfullscreen>.

Técnica: responde SOLO con el documento HTML completo (<!doctype html> … </html>), sin explicación ni ```. Sin
localStorage ni cookies; sin importmap propio. Todo el texto visible en el idioma del usuario.
""";

    /// <summary>La llamada al diseñador: pieza nueva, cambio sobre la anterior o arreglo de errores.</summary>
    public static JsonObject Request(Settings s, string title, string brief, string data, int width, int height,
        string? previous = null, IReadOnlyList<string>? errors = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ventana: {width}×{height} px.");
        if (title.Length > 0) sb.AppendLine($"Título: {title}");
        if (previous is not null)
        {
            sb.AppendLine().AppendLine("Pieza actual:").AppendLine(previous);
            if (errors is { Count: > 0 })
                sb.AppendLine().AppendLine("Al cargarla dio estos errores; devuelve el documento completo corregido, igual de cuidado:")
                    .AppendLine(string.Join("\n", errors.Select(e => "- " + e)));
            if (brief.Length > 0) sb.AppendLine().AppendLine("Cambio pedido: " + brief);
        }
        else sb.AppendLine().AppendLine("Pedido: " + brief);
        if (data.Trim().Length > 0) sb.AppendLine().AppendLine("Datos:").AppendLine(data.Trim());
        var body = new JsonObject
        {
            ["model"] = s.Model,
            ["instructions"] = Prompt,
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = sb.ToString() }),
            }),
            ["store"] = false,
            ["prompt_cache_key"] = "colo-palco",
        };
        // Más razonamiento que el agente: aquí la calidad pesa más que el segundo, y no bloquea nada.
        if (!string.IsNullOrEmpty(s.ReasoningEffort)) body["reasoning"] = new JsonObject { ["effort"] = "medium" };
        return body;
    }

    /// <summary>Pide la pieza en streaming; progress recibe los caracteres que van llegando.</summary>
    public static async Task<string> CreateAsync(AiClient ai, JsonObject request, Action<int>? progress, CancellationToken ct)
    {
        var text = new StringBuilder();
        var count = 0;
        await foreach (var item in ai.StreamAsync(request, ct, d => { count += d.Length; progress?.Invoke(count); }))
            if (item["type"]?.GetValue<string>() == "message")
                foreach (var c in item["content"] as JsonArray ?? [])
                    text.Append(c?["text"]?.GetValue<string>());
        var html = StageDoc.Clean(text.ToString());
        if (!Regex.IsMatch(html, @"<(body|div|canvas|svg|main|section)\b", RegexOptions.IgnoreCase))
            throw new AiException("el diseñador no devolvió una página");
        return html;
    }
}

/// <summary>
/// Cambiar una pieza del palco como un agente de código (Claude Code, Aider, SWE-agent): el diseñador no la reescribe;
/// busca (search) y lee (read) solo lo que necesita, cambia trozos exactos (replace, insert), y al terminar (done) Colo
/// recarga el palco; si al cargar hay errores, se los devuelve y sigue hasta arreglarlos. rewrite queda para cuando
/// el cambio es la pieza entera. Un cambio pequeño cuesta y tarda una fracción de rehacerla.
/// </summary>
public static class StageEditor
{
    public const int MaxSteps = 16, MaxReloads = 3;

    public const string Prompt = """
Eres el diseñador del palco de Colo y cambias una pieza HTML que ya está en pantalla, como un agente de código: no
la reescribas. Busca (search) y lee (read) lo que necesites, cambia lo justo con replace (old exacto y único: añade
líneas de alrededor si se repite) o insert, y termina con done. Colo recarga la pieza: si al cargar hay errores te
los devuelve y los arreglas igual. Varias llamadas a la vez cuando no dependen una de otra.
rewrite (el documento entero) solo si el cambio es otra pieza distinta, no para retocar.
Mantén el nivel de diseño, el idioma y los datos reales; nada inventado. La caja de herramientas (three, gsap, d3,
chart.js/auto, echarts, maplibre-gl, leaflet, roughjs, animejs, motion, matter-js, tone, katex, marked, mermaid,
canvas-confetti; esm.sh para otros) y el puente colo.say/open/ask/theme ya están puestos: no añadas importmap.
""";

    public static readonly JsonArray Tools = (JsonArray)JsonNode.Parse("""
[{"type":"function","name":"search","description":"Líneas de la pieza que contienen query (sin distinguir mayúsculas; regex=true para expresión regular), con su número.",
  "parameters":{"type":"object","properties":{"query":{"type":"string"},"regex":{"type":"boolean"}},"required":["query"]}},
 {"type":"function","name":"read","description":"Las líneas from..to (hasta 200), con su número.",
  "parameters":{"type":"object","properties":{"from":{"type":"integer"},"to":{"type":"integer"}},"required":["from","to"]}},
 {"type":"function","name":"replace","description":"Cambia el texto old (exacto, espacios incluidos, y que aparezca una sola vez) por new.",
  "parameters":{"type":"object","properties":{"old":{"type":"string"},"new":{"type":"string"}},"required":["old","new"]}},
 {"type":"function","name":"insert","description":"Inserta text después de la línea after (0 = al principio).",
  "parameters":{"type":"object","properties":{"after":{"type":"integer"},"text":{"type":"string"}},"required":["after","text"]}},
 {"type":"function","name":"rewrite","description":"Sustituye la pieza entera por html (solo si el cambio es otra pieza).",
  "parameters":{"type":"object","properties":{"html":{"type":"string"}},"required":["html"]}},
 {"type":"function","name":"done","description":"Terminado: Colo recarga el palco y te dice si hubo errores.",
  "parameters":{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}}]
""")!;

    /// <summary>La pieza como líneas que se editan.</summary>
    public sealed class Doc(string html)
    {
        public string Text { get; private set; } = html.Replace("\r\n", "\n");
        public bool Changed { get; private set; }
        string[] Lines => Text.Split('\n');

        static string Cut(string s) => s.Length > 220 ? s[..220] + "…" : s;

        public string Search(string query, bool regex)
        {
            if (query.Length == 0) return "error: falta query";
            Regex? re = null;
            if (regex)
                try { re = new Regex(query, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException e) { return "error: regex no válida: " + e.Message; }
            var hits = Lines.Select((l, i) => (l, n: i + 1))
                .Where(x => re?.IsMatch(x.l) ?? x.l.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) return "nada";
            return string.Join("\n", hits.Take(40).Select(x => $"{x.n}: {Cut(x.l.Trim())}")) + (hits.Count > 40 ? $"\n…({hits.Count} en total)" : "");
        }

        public string Read(int from, int to)
        {
            var lines = Lines;
            from = Math.Clamp(from, 1, lines.Length);
            to = Math.Clamp(Math.Min(to, from + 199), from, lines.Length);
            return string.Join("\n", Enumerable.Range(from, to - from + 1).Select(n => $"{n}: {lines[n - 1]}"))
                + (to < lines.Length ? $"\n…(la pieza sigue hasta la línea {lines.Length})" : "");
        }

        public string Replace(string old, string @new)
        {
            old = old.Replace("\r\n", "\n");
            @new = @new.Replace("\r\n", "\n");
            if (old.Length == 0) return "error: old vacío";
            var at = Text.IndexOf(old, StringComparison.Ordinal);
            if (at < 0)
            {
                // Lo más típico: espacios distintos. Se dice dónde está su primera línea (sin mirar espacios), para leerla.
                static string Squash(string x) => Regex.Replace(x, @"\s+", " ").Trim();
                var first = Squash(old.Split('\n')[0]);
                var where = first.Length == 0 ? [] : Lines.Select((l, i) => (l, n: i + 1)).Where(x => Squash(x.l).Contains(first, StringComparison.Ordinal))
                    .Take(8).Select(x => $"{x.n}: {Cut(x.l.Trim())}").ToList();
                return "error: old no está tal cual" + (where.Count > 0 ? $" (su primera línea aparece en:\n{string.Join("\n", where)}\nléela con read y copia exacto)" : "");
            }
            var again = Text.IndexOf(old, at + 1, StringComparison.Ordinal);
            if (again >= 0)
            {
                var lines = new List<int>();
                for (var i = at; i >= 0 && lines.Count < 8; i = Text.IndexOf(old, i + 1, StringComparison.Ordinal)) lines.Add(LineOf(i));
                return $"error: old aparece más de una vez (líneas {string.Join(", ", lines)}): añade líneas de alrededor para que sea único";
            }
            Text = Text[..at] + @new + Text[(at + old.Length)..];
            Changed = true;
            var start = LineOf(at);
            return $"ok: cambiado en las líneas {start}–{start + @new.Count(c => c == '\n')}";
        }

        public string Insert(int after, string text)
        {
            var lines = Lines.ToList();
            after = Math.Clamp(after, 0, lines.Count);
            lines.InsertRange(after, text.Replace("\r\n", "\n").Split('\n'));
            Text = string.Join("\n", lines);
            Changed = true;
            return $"ok: insertado después de la línea {after}";
        }

        public string Rewrite(string html)
        {
            var clean = StageDoc.Clean(html);
            if (!Regex.IsMatch(clean, @"<(body|div|canvas|svg|main|section)\b", RegexOptions.IgnoreCase)) return "error: eso no es una página";
            Text = clean.Replace("\r\n", "\n");
            Changed = true;
            return "ok: pieza sustituida";
        }

        int LineOf(int index) => Text.AsSpan(0, index).Count('\n') + 1;

        /// <summary>
        /// Índice para empezar sin leerlo todo: dónde están el estilo, los scripts, las secciones, los títulos, los ids y
        /// las funciones.
        /// </summary>
        public string Outline()
        {
            var marks = new Regex(@"<(style|/style|script|/script|header|main|section|article|footer|nav|h1|h2|h3|canvas|svg|figure)\b|\sid=""|function\s+\w+|new\s+(Chart|THREE\.\w+|maplibregl\.Map|L\.map)\b|echarts\.init|gsap\.timeline|d3\.select",
                RegexOptions.IgnoreCase);
            var lines = Lines;
            var picked = lines.Select((l, i) => (l, n: i + 1)).Where(x => marks.IsMatch(x.l)).Take(70).Select(x => $"{x.n}: {Cut(x.l.Trim())}");
            return $"{lines.Length} líneas, {Text.Length} caracteres. Índice:\n" + string.Join("\n", picked);
        }
    }

    /// <summary>
    /// Cambia la pieza html según ask. reload carga el resultado en el palco y devuelve los errores (vacío = bien).
    /// Devuelve la pieza final (y si la recargó con éxito).
    /// </summary>
    public static async Task<(string Html, bool Shown)> EditAsync(AiClient ai, Settings s, string html, string title, string ask, string data,
        int width, int height, Func<string, CancellationToken, Task<List<string>>> reload, Action<string>? progress, CancellationToken ct)
    {
        var doc = new Doc(html);
        var first = new StringBuilder();
        first.AppendLine($"Ventana: {width}×{height} px.");
        if (title.Length > 0) first.AppendLine($"Título: {title}");
        first.AppendLine().AppendLine("Cambio pedido: " + ask);
        if (data.Trim().Length > 0) first.AppendLine().AppendLine("Datos:").AppendLine(data.Trim());
        first.AppendLine().AppendLine("Pieza actual: " + doc.Outline());
        var input = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = first.ToString() }),
        });
        var reloads = 0;
        var shown = false;
        for (var step = 0; step < MaxSteps; step++)
        {
            var body = new JsonObject
            {
                ["model"] = s.Model,
                ["instructions"] = Prompt,
                ["input"] = input.DeepClone(),
                ["tools"] = Tools.DeepClone(),
                ["store"] = false,
                // Todo lo anterior de la conversación se repite tal cual en cada paso: el proveedor lo cachea.
                ["prompt_cache_key"] = "colo-palco-edit",
            };
            if (!string.IsNullOrEmpty(s.ReasoningEffort)) body["reasoning"] = new JsonObject { ["effort"] = "low" };
            var calls = new List<JsonNode>();
            await foreach (var item in ai.StreamAsync(body, ct))
                if (item["type"]?.GetValue<string>() == "function_call") calls.Add(item);
            if (calls.Count == 0) break;
            foreach (var call in calls)
            {
                var name = call["name"]?.GetValue<string>() ?? "";
                var id = call["call_id"]?.GetValue<string>() ?? $"c{step}";
                JsonObject args;
                try { args = JsonNode.Parse(call["arguments"]?.GetValue<string>() ?? "{}") as JsonObject ?? []; }
                catch { args = []; }
                var c = new ToolCall(name, args);
                string output;
                if (name == "done")
                {
                    progress?.Invoke("Recargando…");
                    var errors = await reload(doc.Text, ct);
                    shown = errors.Count == 0;
                    if (shown) return (doc.Text, true);
                    if (++reloads >= MaxReloads) return (doc.Text, false);
                    output = "Al recargar dio estos errores; arréglalos y vuelve a llamar done:\n" + string.Join("\n", errors.Select(e => "- " + e));
                }
                else
                {
                    output = name switch
                    {
                        "search" => doc.Search(c.Str("query"), c.Bool("regex")),
                        "read" => doc.Read(c.Int("from") ?? 1, c.Int("to") ?? 200),
                        "replace" => doc.Replace(c.Str("old"), c.Str("new")),
                        "insert" => doc.Insert(c.Int("after") ?? 0, c.Str("text")),
                        "rewrite" => doc.Rewrite(c.Str("html")),
                        _ => "error: herramienta desconocida",
                    };
                    progress?.Invoke(name switch
                    {
                        "search" => $"Buscando «{(c.Str("query").Length > 30 ? c.Str("query")[..30] + "…" : c.Str("query"))}»",
                        "read" => "Leyendo",
                        "rewrite" => "Rehaciendo la pieza",
                        _ => "Cambiando",
                    });
                }
                // La conversación se reenvía entera (store=false): la llamada y su resultado, sin ids del servidor.
                input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = call["arguments"]?.GetValue<string>() ?? "{}" });
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = output });
            }
        }
        // Se acabaron los pasos (o el modelo dejó de llamar herramientas): se muestra lo que haya.
        if (doc.Changed && !shown) shown = (await reload(doc.Text, ct)).Count == 0;
        return (doc.Text, shown);
    }
}
