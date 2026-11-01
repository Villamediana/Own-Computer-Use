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
        var h = Regex.Match(doc, @"<head[^>]*>", RegexOptions.IgnoreCase);
        if (h.Success) return doc.Insert(h.Index + h.Length, head);
        var html = Regex.Match(doc, @"<html[^>]*>", RegexOptions.IgnoreCase);
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
