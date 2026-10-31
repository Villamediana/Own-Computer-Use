using Colo.Core;
using Xunit;

namespace Colo.Tests;

public sealed class MemoryTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"colo-mem-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_path); } catch { } }

    [Fact]
    public void Old_list_of_phrases_still_loads()
    {
        File.WriteAllText(_path, """["Su navegador es Comet","«Fresh» es Freshservice"]""");
        var m = new Memory(_path);
        Assert.Equal(["Su navegador es Comet", "«Fresh» es Freshservice"], m.Facts);
        Assert.Equal("ok: guardado", m.Remember("Usa Outlook nuevo", "outlook"));
        var again = new Memory(_path);
        Assert.Equal(3, again.All.Count);
        Assert.Equal("outlook", again.All[2].Where);
    }

    [Fact]
    public void Facts_of_a_place_only_go_there()
    {
        var m = new Memory(_path);
        m.Remember("Sistema de chamados = https://vertis.freshservice.com/helpdesk");
        m.Remember("En Freshservice, «mis chamados» es el filtro Mis tickets abiertos", "freshservice");
        Assert.Single(m.For("Documento1 - Word [winword]"));
        Assert.Equal(2, m.For("Tickets - Freshservice - Google Chrome [chrome] URL: vertis.freshservice.com/a").Count);
    }

    [Fact]
    public void Replaces_updates_instead_of_piling_up()
    {
        var m = new Memory(_path);
        m.Remember("Chamados en https://old.example.com");
        Assert.Equal("ok: actualizado", m.Remember("Chamados en https://vertis.freshservice.com", replaces: "old.example"));
        Assert.Equal(["Chamados en https://vertis.freshservice.com"], m.Facts);
        Assert.StartsWith("error: no tenía", m.Remember("x", replaces: "no existe"));
        Assert.Equal("ok: ya lo sabía", m.Remember("chamados en https://vertis.freshservice.com"));
    }

    [Fact]
    public void Full_memory_asks_the_model_to_choose_and_long_facts_are_refused()
    {
        var m = new Memory(_path);
        for (var i = 0; i < Memory.MaxFacts; i++) Assert.Equal("ok: guardado", m.Remember($"dato {i}"));
        Assert.StartsWith("error: memoria llena", m.Remember("uno más"));
        Assert.Equal(Memory.MaxFacts, m.Facts.Count);
        Assert.Equal("ok: actualizado", m.Remember("dato 0 y dato 1 juntos", replaces: "dato 0"));
        Assert.StartsWith("error: más corto", m.Remember(new string('x', Memory.MaxLength + 1)));
    }

    [Fact]
    public void Request_carries_the_url_and_only_the_facts_that_apply()
    {
        var m = new Memory(_path);
        m.Remember("General");
        m.Remember("Solo en Freshservice", "freshservice");
        var agent = new Agent(new AiClient(new Settings()), new Settings { Model = "m" }, new FakeComputer()) { Memory = m };
        string Text(Observation o) => agent.BuildRequest(["hola"], [], o)["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        var web = Text(new Observation([], 0, 0, "Tickets - Comet  [comet]", [], App: "comet", Details: ["URL: vertis.freshservice.com/helpdesk"]));
        Assert.Contains("URL: vertis.freshservice.com", web);
        Assert.Contains("Solo en Freshservice (en freshservice)", web);
        var word = Text(new Observation([], 0, 0, "Doc  [winword]", [], App: "winword"));
        Assert.Contains("General", word);
        Assert.DoesNotContain("Solo en Freshservice", word);
    }

    [Fact]
    public void Activity_summary_skips_glances_and_merges()
    {
        var a = new Activity();
        var t = new DateTime(2026, 10, 31, 10, 0, 0);
        void Hold(string w, int seconds) { for (var i = 0; i <= seconds; i += 2) a.Seen(w, t.AddSeconds(i)); t = t.AddSeconds(seconds + 2); }
        Hold("Outlook", 600);
        Hold("Teams", 2); // vistazo
        Hold("Outlook", 120);
        Hold("Contrato.docx - Word", 900);
        var s = a.Summary(t, 60);
        Assert.DoesNotContain("Teams", s);
        Assert.Contains("10:00–10:12 (12 min) Outlook", s);
        Assert.Contains("Contrato.docx - Word (15 min)", s);
        Assert.StartsWith("ok: nada", new Activity().Summary(t, 30));
    }

    [Fact]
    public void Clip_history_newest_first_without_repeats()
    {
        var h = new ClipHistory();
        var t = DateTime.Now;
        h.Add("uno", t); h.Add("dos", t); h.Add("uno", t); h.Add("  ", t);
        Assert.Equal("uno", h.Get(1));
        Assert.Equal("dos", h.Get(2));
        Assert.Null(h.Get(3));
        for (var i = 0; i < 30; i++) h.Add($"x{i}", t);
        Assert.Null(h.Get(ClipHistory.Max + 1));
        Assert.Contains("1) ahora · x29", h.List(t));
    }
}
