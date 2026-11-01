using System.Text.Json.Nodes;
using Colo.Core;
using Xunit;

namespace Colo.Tests;

public sealed class AgendaTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"colo-agenda-{Guid.NewGuid():N}.json");
    static readonly DateTime Now = new(2026, 11, 2, 10, 0, 0); // lunes

    public void Dispose() { try { File.Delete(_path); } catch { } }

    static ToolCall Call(string json) => new("agenda", JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void Add_list_done_and_it_persists()
    {
        var a = new Agenda(_path);
        Assert.StartsWith("ok: #1", a.Run(Call("""{"action":"add","text":"Revisar contrato","project":"Tower Lease","due":"2026-11-02 17:00"}"""), Now));
        Assert.StartsWith("ok: ya estaba", a.Run(Call("""{"action":"add","text":"revisar contrato","project":"tower lease"}"""), Now));
        a.Run(Call("""{"action":"add","text":"Fraldas","list":"compras"}"""), Now);
        Assert.Contains("[Tower Lease] Revisar contrato · para hoy 17:00", a.Run(Call("""{"action":"list","what":"today"}"""), Now));
        Assert.Contains("(compras) Fraldas", a.Run(Call("""{"action":"list","list":"Compras"}"""), Now));
        Assert.Equal("ok: #1 hecho", a.Run(Call("""{"action":"done","item":1}"""), Now));
        var again = new Agenda(_path);
        Assert.DoesNotContain("Revisar", again.List("open", "", "", Now));
        Assert.Contains("Revisar", again.List("done", "", "", Now));
        Assert.StartsWith("error: no hay", again.Run(Call("""{"action":"done","item":99}"""), Now));
    }

    [Fact]
    public void Reminders_fire_once_and_repeating_ones_move_on()
    {
        var a = new Agenda(_path);
        a.Add("Ligar pro pediatra", "", "", null, Now.AddMinutes(5), "", "", "", Now);
        a.Add("Remédio", "", "", null, Now.AddMinutes(1), "daily", "", "", Now);
        Assert.Empty(a.Fire(Now));
        var due = a.Fire(Now.AddMinutes(6));
        Assert.Equal(2, due.Count);
        Assert.Empty(a.Fire(Now.AddMinutes(7)));
        Assert.Equal("Aviso que le diste hace 4 min: #1 Ligar pro pediatra · #2 Remédio", a.RecentNotice(Now.AddMinutes(10)));
        Assert.Null(a.RecentNotice(Now.AddMinutes(30)));
        var med = a.Items.Single(i => i.Text == "Remédio");
        Assert.Equal(Now.AddDays(1).AddMinutes(1), med.Remind);
        Assert.Null(a.Items.Single(i => i.Text.StartsWith("Ligar")).Remind);
        // Hecho en algo que se repite: no se cierra, pasa a la próxima.
        Assert.StartsWith("ok: hecho; la próxima", a.Done("remédio", Now.AddMinutes(8)));
        Assert.False(a.Items.Single(i => i.Text == "Remédio").Done);
    }

    [Fact]
    public void Weekdays_skip_the_weekend_and_long_gaps_do_not_pile_up()
    {
        var friday = new DateTime(2026, 11, 6, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0), Agenda.After(friday, "weekdays", friday));
        Assert.Equal(new DateTime(2026, 11, 20, 9, 0, 0), Agenda.After(friday, "daily", new DateTime(2026, 11, 19, 12, 0, 0)));
        Assert.Equal(new DateTime(2026, 12, 6, 9, 0, 0), Agenda.After(friday, "monthly", friday));
    }

    [Fact]
    public void Projects_aliases_and_search_without_accents()
    {
        var a = new Agenda(_path);
        Assert.Equal("ok: proyecto «Tower Finder» creado", a.Run(Call("""{"action":"project","project":"Tower Finder","status":"produção","aliases":"TF, finder"}"""), Now));
        a.Run(Call("""{"action":"add","text":"Migração para o Postgres","project":"tf"}"""), Now);
        Assert.StartsWith("ok: ya estaba", a.Run(Call("""{"action":"add","text":"Migração para o Postgres","project":"Tower Finder"}"""), Now));
        Assert.Equal("Tower Finder", a.Items[0].Project);
        Assert.Equal("Proyectos: Tower Finder (TF, finder)", a.ProjectsLine());
        a.MoreSearch = q => q.Contains("postgres") ? ["Acta 01/11: decidimos migrar al Postgres"] : [];
        var found = a.Search("migracao postgres", Now);
        Assert.Contains("#1 [Tower Finder] Migração", found);
        Assert.Contains("Acta 01/11", a.Search("postgres", Now));
        Assert.Contains("1 abiertas", a.List("projects", "", "", Now));
        Assert.Contains("Migração", a.List("open", "finder", "", Now));
        a.Run(Call("""{"action":"project","project":"TF","summary":"Banco no PG desde outubro"}"""), Now);
        Assert.Single(a.Projects);
        Assert.Equal("produção", a.Projects[0].Status);
    }

    [Fact]
    public void Edit_by_text_and_clear_the_reminder()
    {
        var a = new Agenda(_path);
        a.Add("Mandar ata", "", "", null, Now.AddHours(1), "", "", "", Now);
        Assert.Contains("aviso hoy 11:00", a.List("open", "", "", Now));
        Assert.StartsWith("ok: #1", a.Run(Call("""{"action":"edit","item":"ata","remind":"2026-11-03 09:00","who":"Thais"}"""), Now));
        Assert.Contains("Mandar ata · Thais · aviso mañana 09:00", a.List("open", "", "", Now));
        a.Run(Call("""{"action":"edit","item":1,"remind":""}"""), Now);
        Assert.Null(a.Items[0].Remind);
        Assert.StartsWith("error: repeat", a.Run(Call("""{"action":"add","text":"x","repeat":"hourly"}"""), Now));
    }

    [Fact]
    public void Module_prompt_only_goes_when_its_group_is_on()
    {
        var newGroups = Tools.Groups.Where(g => g.Prompt.Length > 0).Select(g => g.Id).ToList();
        Assert.Contains("agenda", newGroups);
        // Apagados los módulos nuevos, las instrucciones son las de antes: no pesan nada.
        Assert.Equal(Tools.System, Tools.Instructions(newGroups));
        Assert.Contains("Agenda\n", Tools.Instructions([]));
        Assert.DoesNotContain(Tools.Build(["agenda"]), t => t!["name"]!.GetValue<string>() == "agenda");
        // Lo que suman todos juntos se mantiene pequeño (va en caché, pero cada carácter cuenta en el primer turno).
        var extra = Tools.Instructions([]).Length - Tools.System.Length;
        Assert.True(extra < 3200, $"los módulos suman {extra} caracteres");
    }

    [Fact]
    public void Doing_a_repeating_task_after_its_reminder_rang_keeps_the_next_reminder()
    {
        var a = new Agenda(_path);
        a.Run(Call("""{"action":"add","text":"Remédio","due":"2026-11-02 09:00","remind":"2026-11-02 09:00","repeat":"daily"}"""), Now);
        Assert.Single(a.Fire(Now)); // sonó a las 9; el aviso pasa a mañana
        a.Done("1", Now);
        var it = a.Items.Single();
        Assert.Equal(new DateTime(2026, 11, 3, 9, 0, 0), it.Due);
        Assert.Equal(new DateTime(2026, 11, 3, 9, 0, 0), it.Remind); // no pasado mañana
    }

    [Fact]
    public void An_alias_added_later_brings_its_tasks_to_the_project()
    {
        var a = new Agenda(_path);
        a.Run(Call("""{"action":"add","text":"Subir o índice","project":"TF"}"""), Now);
        a.Project("Tower Finder", "", "", "", Now);
        a.Project("Tower Finder", "", "", "TF, torre", Now);
        Assert.Equal("Tower Finder", a.Items.Single().Project);
    }
}
