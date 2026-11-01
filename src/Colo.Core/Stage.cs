using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Colo.Core;

/// <summary>Una guía del palco: cómo construir un tipo de visual (gráficos, lousa animada, 3D, mapas…).</summary>
public sealed record StageSkill(string Name, string Description, string Text);

/// <summary>
/// Las guías («skills») del palco, al estilo de OpenIntelligentUI: el prompt solo lleva su nombre y para qué
/// sirven; el modelo lee la que necesita (stage skill) antes de crear. Vienen dentro de Colo y se pueden añadir o
/// cambiar dejando un .md en la carpeta del usuario (mismo nombre = reemplaza).
/// </summary>
public static class StageSkills
{
    static Dictionary<string, StageSkill> _all = Builtin();

    public static IReadOnlyCollection<StageSkill> All => _all.Values;

    /// <summary>Las propias más las de la carpeta del usuario (se llama al arrancar).</summary>
    public static void Load(string? userDir)
    {
        var all = Builtin();
        if (userDir is not null && Directory.Exists(userDir))
            foreach (var f in Directory.EnumerateFiles(userDir, "*.md"))
            {
                try
                {
                    var s = Parse(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f));
                    all[s.Name] = s;
                }
                catch { }
            }
        _all = all;
    }

    /// <summary>Una línea por guía para el prompt: «nombre: para qué».</summary>
    public static string Index() => string.Join("\n", _all.Values.OrderBy(s => s.Name).Select(s => $"  · {s.Name}: {s.Description}"));

    public static string Read(string name)
    {
        var key = Extract.Fold(name.Trim()).Replace(' ', '-');
        if (_all.TryGetValue(key, out var s)) return "ok:\n" + s.Text;
        return $"error: no hay la guía «{name}»; hay: {string.Join(", ", _all.Keys.Order())}";
    }

    static Dictionary<string, StageSkill> Builtin()
    {
        var asm = typeof(StageSkills).Assembly;
        var all = new Dictionary<string, StageSkill>();
        foreach (var res in asm.GetManifestResourceNames().Where(r => r.StartsWith("skills/", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(stream);
            var s = Parse(Path.GetFileNameWithoutExtension(res), reader.ReadToEnd());
            all[s.Name] = s;
        }
        return all;
    }

    /// <summary>Front-matter opcional («---\nname: …\ndescription: …\n---»); sin él, el nombre del archivo y la 1ª línea.</summary>
    public static StageSkill Parse(string file, string text)
    {
        var name = file;
        var description = "";
        var body = text.Replace("\r\n", "\n");
        var fm = Regex.Match(body, @"\A---\n(.*?)\n---\n", RegexOptions.Singleline);
        if (fm.Success)
        {
            foreach (var line in fm.Groups[1].Value.Split('\n'))
            {
                var kv = line.Split(':', 2);
                if (kv.Length < 2) continue;
                var v = kv[1].Trim().Trim('"');
                if (kv[0].Trim() == "name") name = v;
                else if (kv[0].Trim() == "description") description = v;
            }
            body = body[fm.Length..];
        }
        if (description.Length == 0) description = body.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim('#', ' ') ?? "";
        return new StageSkill(Extract.Fold(name).Replace(' ', '-'), description, body.Trim());
    }
}

/// <summary>
/// La página que el modelo crea para el palco, envuelta en el documento base: tema (claro/oscuro), estilos de
/// base, importmap de librerías (three, gsap, d3, chart.js, leaflet) y el puente <c>colo</c> (ask, open, say). El
/// envoltorio avisa al palco de cómo fue: errores de JS, si el código llegó a ejecutarse y si quedó en blanco.
/// </summary>
public static class StageDoc
{
    public const string Origin = "https://palco.colo";

    public static readonly Dictionary<string, string> Imports = new()
    {
        ["three"] = "https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js",
        ["three/addons/"] = "https://cdn.jsdelivr.net/npm/three@0.170.0/examples/jsm/",
        ["gsap"] = "https://cdn.jsdelivr.net/npm/gsap@3.12.5/index.js",
        ["d3"] = "https://cdn.jsdelivr.net/npm/d3@7.9.0/+esm",
        ["chart.js"] = "https://cdn.jsdelivr.net/npm/chart.js@4.4.6/+esm",
        ["chart.js/auto"] = "https://cdn.jsdelivr.net/npm/chart.js@4.4.6/auto/+esm",
        ["leaflet"] = "https://cdn.jsdelivr.net/npm/leaflet@1.9.4/+esm",
        ["maplibre-gl"] = "https://cdn.jsdelivr.net/npm/maplibre-gl@4.7.1/+esm",
    };

    public static string Build(string title, string css, string html, string js)
    {
        var map = "{\"imports\":{" + string.Join(",", Imports.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}}";
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>");
        sb.Append("<script type=\"importmap\">").Append(map).Append("</script>");
        sb.Append("<style>").Append(Base).Append("</style>");
        sb.Append("<script>").Append(Bridge).Append("</script>");
        sb.Append("<style>").Append(css).Append("</style></head><body>");
        sb.Append(html);
        if (js.Trim().Length > 0)
            // Módulo: puede usar import y await arriba. La última línea marca que llegó al final (si un import
            // falla, el módulo entero no corre y el palco lo cuenta).
            sb.Append("<script type=\"module\">\n").Append(js).Append("\n;window.__coloRan = true;</script>");
        else
            sb.Append("<script>window.__coloRan = true;</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    const string Base = """
:root{color-scheme:light dark;--bg:#ffffff;--surface:#f6f6f7;--ink:#111113;--ink-2:#3f3f46;--muted:#84848c;--line:#e7e7ea;
--accent:#2563eb;--c1:#2563eb;--c2:#0d9488;--c3:#d97706;--c4:#db2777;--c5:#7c3aed;--c6:#65a30d;--good:#16a34a;--bad:#dc2626;
--radius:14px;--font:"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;--display:"Segoe UI Variable Display","Segoe UI",system-ui,sans-serif;
--mono:"Cascadia Mono",Consolas,monospace}
@media (prefers-color-scheme:dark){:root{--bg:#141416;--surface:#1d1d20;--ink:#f4f4f5;--ink-2:#d4d4d8;--muted:#9a9aa3;--line:#2c2c31;
--accent:#60a5fa;--c1:#60a5fa;--c2:#2dd4bf;--c3:#fbbf24;--c4:#f472b6;--c5:#a78bfa;--c6:#a3e635}}
*{box-sizing:border-box}html,body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 var(--font)}
body{padding:22px 24px 28px}h1{font:600 24px/1.2 var(--display);letter-spacing:-.02em;margin:0 0 6px}
h2{font:600 16px/1.3 var(--display);margin:22px 0 8px}p{margin:0 0 10px}.muted,small{color:var(--muted)}
a{color:var(--accent)}img,video,canvas,svg{max-width:100%}
.card{background:var(--surface);border:1px solid var(--line);border-radius:var(--radius);padding:16px 18px}
.grid{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(min(100%,14rem),1fr))}
.big{font:600 44px/1.05 var(--display);letter-spacing:-.03em;font-variant-numeric:tabular-nums}
button{font:inherit;border:1px solid var(--line);background:var(--surface);color:var(--ink);border-radius:10px;padding:7px 13px;cursor:pointer}
button:hover{border-color:var(--accent)}input,select{font:inherit;border:1px solid var(--line);background:var(--bg);color:var(--ink);border-radius:10px;padding:7px 10px}
table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}th,td{text-align:left;padding:8px 10px;border-bottom:1px solid var(--line)}
th{font-weight:600;color:var(--ink-2)}.source{margin-top:18px;font-size:12px;color:var(--muted)}
@media (prefers-reduced-motion:reduce){*{animation-duration:.01ms!important;transition-duration:.01ms!important}}
""";

    /// <summary>
    /// Puente y avisos. colo.ask(text) propone una pregunta (el palco pide confirmarla con un clic), colo.open(url)
    /// abre en el navegador, colo.say(text) la lee en voz alta (para narrar una explicación). colo.color(i) da el
    /// color i de la paleta.
    /// </summary>
    const string Bridge = """
(function(){var errs=[];function post(m){try{parent.postMessage(Object.assign({colo:1},m),"*")}catch(e){}}
function add(t){t=String(t).slice(0,300);if(errs.length<6&&errs.indexOf(t)<0)errs.push(t)}
addEventListener("error",function(e){if(e.target&&e.target!==window&&(e.target.src||e.target.href))add("no cargó "+(e.target.src||e.target.href));else add((e.message||"error")+(e.lineno?" (línea "+e.lineno+")":""))},true);
addEventListener("unhandledrejection",function(e){add("promesa: "+(e.reason&&(e.reason.message||e.reason)))});
var ce=console.error;console.error=function(){add(Array.prototype.join.call(arguments," "));ce.apply(console,arguments)};
window.colo={ask:function(t){post({type:"ask",text:String(t).slice(0,500)})},open:function(u){post({type:"open",url:String(u)})},
say:function(t){post({type:"say",text:String(t).slice(0,600)})},
color:function(i){return getComputedStyle(document.documentElement).getPropertyValue("--c"+(1+((i|0)%6))).trim()}};
function report(){var b=document.body,txt=(b&&b.innerText||"").trim().length,
media=document.querySelectorAll("canvas,svg,img,video,iframe").length;post({type:"rendered",errors:errs,ran:!!window.__coloRan,blank:txt<3&&media===0})}
addEventListener("load",function(){var t0=Date.now();(function wait(){if(window.__coloRan||Date.now()-t0>5000)setTimeout(report,700);else setTimeout(wait,200)})()});})();
""";
}
