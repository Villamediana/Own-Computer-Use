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
}
