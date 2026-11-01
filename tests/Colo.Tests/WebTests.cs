using System.Text;
using Colo.Core;
using Xunit;

namespace Colo.Tests;

public sealed class WebTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"colo-web-{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void Bing_results_resolve_the_real_url()
    {
        var hits = Web.ParseBing(Fixture("bing.html"));
        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.StartsWith("https://", h.Url));
        Assert.DoesNotContain(hits, h => h.Url.Contains("bing.com"));
        Assert.Contains("Dólar", hits[0].Title);
        Assert.NotEmpty(hits[0].Snippet);
    }

    [Fact]
    public void DuckDuckGo_results_unwrap_uddg()
    {
        const string html = """
<a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.bcb.gov.br%2Fcotacoes&amp;rut=x">Cotações &amp; boletins</a>
<a class="result__snippet" href="x">PTAX do <b>dólar</b> hoje</a>
""";
        var hits = Web.ParseDuckDuckGo(html);
        Assert.Equal(new WebHit("Cotações & boletins", "https://www.bcb.gov.br/cotacoes", "PTAX do dólar hoje"), hits.Single());
    }

    [Fact]
    public void YouTube_videos_from_initial_data()
    {
        var v = Web.ParseYouTube(Fixture("youtube.html"));
        Assert.Equal(3, v.Count);
        Assert.Equal(11, v[0].Id.Length);
        Assert.Matches(@"^\d+:\d\d", v[0].Length);
        Assert.NotEmpty(v[0].Title);
        Assert.NotEmpty(v[0].Channel);
    }

    [Fact]
    public void Page_text_keeps_content_and_structured_data_and_drops_chrome()
    {
        const string html = """
<html><head><title>Dólar hoje | Banco</title><style>.x{}</style>
<script type="application/ld+json">{"@type":"ExchangeRateSpecification","currentExchangeRate":{"price":5.43}}</script>
<script>var tracking = 1;</script></head>
<body><nav>Menu Início Contato</nav>
<div><h1>Cotação</h1><p>O dólar fechou em <b>R$&nbsp;5,43</b>.</p><ul><li>Alta de 0,4%</li><li>Mínima 5,40</li></ul>
<table><tr><td>Compra</td><td>5,43</td></tr></table></div>
<footer>© 2026</footer></body></html>
""";
        var t = Web.PageText(html);
        Assert.StartsWith("# Dólar hoje | Banco", t);
        Assert.Contains("\"price\":5.43", t);
        Assert.Contains("## Cotação", t);
        Assert.Contains("O dólar fechou em R$ 5,43 .", t);
        Assert.Contains("- Alta de 0,4%", t);
        Assert.Contains("| Compra | 5,43", t);
        Assert.DoesNotContain("Menu", t);
        Assert.DoesNotContain("tracking", t);
        Assert.DoesNotContain("© 2026", t);
    }

    [Fact]
    public void Old_pages_in_latin1_decode_right()
    {
        var latin = Encoding.Latin1.GetBytes("<html><head><meta charset=\"iso-8859-1\"></head><body>Cotação</body></html>");
        Assert.Contains("Cotação", Web.Decode(latin, null));
        Assert.Contains("Cotação", Web.Decode(latin, "ISO-8859-1"));
        Assert.Contains("Cotação", Web.Decode(Encoding.UTF8.GetBytes("<p>Cotação</p>"), null));
        Assert.Contains("Cotação", Web.Decode(Encoding.UTF8.GetBytes("<p>Cotação</p>"), "bogus-charset"));
    }

    [Fact]
    public void Long_pages_come_in_slices()
    {
        var text = new string('a', Web.PageChars + 100);
        var first = Web.Slice(text, 0);
        Assert.EndsWith($"…(sigue: offset={Web.PageChars} de {Web.PageChars + 100})", first);
        Assert.DoesNotContain("sigue", Web.Slice(text, Web.PageChars));
    }

    [Fact]
    public void Download_names_and_html_disguised_as_pdf()
    {
        var u = new Uri("https://site.gov.br/docs/Edital%202026.pdf?x=1");
        Assert.Equal("Edital 2026.pdf", Web.FileName("", null, u, "application/pdf"));
        Assert.Equal("contrato.pdf", Web.FileName("contrato", null, u, "application/pdf"));
        Assert.Equal("relatorio.xlsx", Web.FileName("", "\"relatorio.xlsx\"", u, "application/octet-stream"));
        Assert.Equal("site.gov.br.pdf", Web.FileName("", null, new Uri("https://site.gov.br/"), "application/pdf"));
        Assert.Equal("a_b.pdf", Web.FileName("a/b", null, u, "application/pdf"));

        Directory.CreateDirectory(_dir);
        var fake = Path.Combine(_dir, "x.pdf");
        File.WriteAllText(fake, "<!DOCTYPE html><html>login</html>");
        Assert.Contains("devolvió una página", Web.Mismatch(fake, ".pdf"));
        File.WriteAllBytes(fake, Encoding.ASCII.GetBytes("%PDF-1.7 ..."));
        Assert.Null(Web.Mismatch(fake, ".pdf"));
        var docx = Path.Combine(_dir, "x.docx");
        File.WriteAllText(docx, "<html>");
        Assert.NotNull(Web.Mismatch(docx, ".docx"));
    }

    [Fact]
    public void Commons_images_are_listed_in_order()
    {
        const string json = """
{"query":{"pages":{"9":{"title":"File:Trem B.jpg","index":2,"imageinfo":[{"thumburl":"https://upload.wikimedia.org/b.jpg?utm=1","extmetadata":{"Artist":{"value":"<a href='x'>Ana</a>"}}}]},
"7":{"title":"File:Trem A.png","index":1,"imageinfo":[{"thumburl":"https://upload.wikimedia.org/a.png"}]}}}}
""";
        var s = Web.FormatImages(json);
        Assert.Contains("1. Trem A — https://upload.wikimedia.org/a.png", s);
        Assert.Contains("2. Trem B — https://upload.wikimedia.org/b.jpg (autor: Ana)", s);
        Assert.Equal("ok: nada", Web.FormatImages("{}"));
    }
}
