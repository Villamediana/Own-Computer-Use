using System.Net;
using Colo.Core;
using Xunit;

namespace Colo.Tests;

public sealed class StageTests
{
    [Fact]
    public void Designer_output_is_cleaned_and_gets_the_toolbox()
    {
        const string reply = """
Claro, aquí está:
```html
<!doctype html><html lang="pt"><head><title>Dólar</title><script type="importmap">{"imports":{"x":"y"}}</script></head>
<body><h1>R$ 5,43</h1><script type="module">import * as THREE from 'three';</script></body></html>
```
Espero que te guste.
""";
        var clean = StageDoc.Clean(reply);
        Assert.StartsWith("<!doctype html>", clean);
        Assert.EndsWith("</html>", clean);
        var doc = StageDoc.Wrap(reply);
        Assert.Contains("<head><meta charset=\"utf-8\">", doc);
        Assert.Contains("\"three\":\"https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js\"", doc);
        Assert.Contains("\"roughjs\":", doc);
        Assert.Contains("window.colo=", doc);
        Assert.DoesNotContain("\"x\":\"y\"", doc); // su importmap fuera: rompería el nuestro
        Assert.Equal(1, doc.Split("type=\"importmap\"").Length - 1);
        // Un fragmento suelto también vale.
        Assert.StartsWith("<!doctype html><html><head><meta", StageDoc.Wrap("<div>hola</div>"));
        Assert.Contains("<html lang=\"es\"><head><meta", StageDoc.Wrap("<html lang=\"es\"><body>x</body></html>"));
        // <header> no es <head>.
        Assert.StartsWith("<!doctype html><html><head><meta", StageDoc.Wrap("<header>Dólar</header><main>5,40</main>"));
    }

    [Fact]
    public void Designer_request_new_edit_and_repair()
    {
        var s = new Settings { Model = "gpt-6-luna", ReasoningEffort = "low" };
        var r = StageDesigner.Request(s, "Dólar hoje", "cotação com a história do mês", "5,43 em 01/11 (BCB)", 740, 860);
        var text = r["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Ventana: 740×860 px.", text);
        Assert.Contains("Pedido: cotação com a história do mês", text);
        Assert.Contains("5,43 em 01/11 (BCB)", text);
        Assert.Equal("medium", r["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("colo-palco", r["prompt_cache_key"]!.GetValue<string>());
        Assert.Equal(StageDesigner.Prompt, r["instructions"]!.GetValue<string>());

        var edit = StageDesigner.Request(s, "", "em barras", "", 740, 860, "<html>antes</html>")["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Pieza actual:\n<html>antes</html>", edit.Replace("\r", ""));
        Assert.Contains("Cambio pedido: em barras", edit);

        var fix = StageDesigner.Request(s, "", "", "", 740, 860, "<html>x</html>", ["promesa: boom"])["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("- promesa: boom", fix);
        Assert.Null(StageDesigner.Request(new Settings { Model = "m", ReasoningEffort = "" }, "", "x", "", 1, 1)["reasoning"]);
    }

    [Fact]
    public void Agent_prompt_stays_short()
    {
        Assert.Contains(Tools.Build([]), t => t!["name"]!.GetValue<string>() == "stage");
        Assert.Contains("stage show", Tools.Instructions([]));
        Assert.DoesNotContain("importmap", Tools.Instructions([]));
    }

    const string Piece = """
<!doctype html><html><head><style>
  h1 { color:#e11d48; }
  .bar { color:#e11d48; height: 20px; }
</style></head><body>
<h1 id="t">Dólar hoy</h1>
<script type="module">
  const x = { y: 5.4 };
  document.querySelector("#t").dataset.v = x.y;
</script>
</body></html>
""";

    [Fact]
    public void The_editor_searches_reads_and_changes_exact_pieces()
    {
        var d = new StageEditor.Doc(Piece);
        Assert.Contains("<style>", d.Outline());
        Assert.Contains("5: <h1 id=\"t\">Dólar hoy</h1>", d.Outline());
        Assert.Equal("2: h1 { color:#e11d48; }\n3: .bar { color:#e11d48; height: 20px; }", d.Search("#E11D48", false));
        Assert.StartsWith("5: <h1", d.Read(5, 5));
        Assert.StartsWith("error: old aparece más de una vez (líneas 2, 3)", d.Replace("color:#e11d48;", "color:#2563eb;"));
        Assert.Contains("su primera línea aparece en", d.Replace("h1 {  color:#e11d48; }", "x"));     // espacios distintos
        Assert.Equal("ok: cambiado en las líneas 2–2", d.Replace("h1 { color:#e11d48; }", "h1 { color:#2563eb; }"));
        Assert.Equal("ok: insertado después de la línea 5", d.Insert(5, "<p>fuente: BCB</p>"));
        Assert.Contains("6: <p>fuente: BCB</p>", d.Read(5, 7));
        Assert.True(d.Changed);
        Assert.StartsWith("error", d.Rewrite("hola"));
    }

    [Fact]
    public async Task Editing_is_a_loop_like_a_coding_agent_and_fixes_its_own_errors()
    {
        var s = new Settings { Provider = Provider.OpenAI, ApiKey = "k", Model = "m", ReasoningEffort = "low" };
        var bodies = new List<string>();
        var replies = new Queue<string>([
            CoreTests.Calls(("search", """{"query":"#e11d48"}"""), ("replace", """{"old":"  .bar { color:#e11d48;","new":"  .bar { color:#2563eb;"}""")),
            CoreTests.Calls(("done", """{"summary":"barras azules"}""")),
            CoreTests.Calls(("replace", """{"old":"x.y;","new":"x.y.toFixed(2);"}"""), ("done", """{"summary":"arreglado"}""")),
        ]);
        var ai = new AiClient(s, new HttpClient(new FakeHandler((_, body) => { lock (bodies) bodies.Add(System.Text.RegularExpressions.Regex.Unescape(body)); return (HttpStatusCode.OK, replies.Dequeue()); })));
        var reloads = new List<string>();
        var notes = new List<string>();
        var (html, shown) = await StageEditor.EditAsync(ai, s, Piece, "Dólar hoy", "barras azules", "", 760, 900,
            (doc, _) => { reloads.Add(doc); return Task.FromResult(reloads.Count == 1 ? new List<string> { "x.y.fixed is not a function" } : new List<string>()); },
            notes.Add, default);
        Assert.True(shown);
        Assert.Equal(2, reloads.Count);                               // recargó, vio el error, lo arregló y recargó
        Assert.Contains(".bar { color:#2563eb;", html);
        Assert.Contains("h1 { color:#e11d48; }", html);                 // lo demás, intacto
        Assert.Contains("x.y.toFixed(2);", html);
        Assert.Equal(3, bodies.Count);
        Assert.DoesNotContain("<h1 id=", bodies[0].Split("Índice")[0]);  // no manda la pieza entera…
        Assert.Contains("Índice", bodies[0]);                          // …sino su índice
        Assert.Contains("ok: cambiado en las líneas 3", bodies[1]);     // ve el resultado de sus cambios
        Assert.Contains("x.y.fixed is not a function", bodies[2]);      // y los errores al recargar
        Assert.Contains("colo-palco-edit", bodies[0]);
        Assert.Contains("Buscando «#e11d48»", notes);
    }
}
