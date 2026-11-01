using System.Text.Json.Nodes;
using Colo.Core;
using Xunit;

namespace Colo.Tests;

public sealed class StageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"colo-skills-{Guid.NewGuid():N}");

    public void Dispose() { StageSkills.Load(null); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Builtin_skills_load_with_descriptions()
    {
        StageSkills.Load(null);
        var names = StageSkills.All.Select(s => s.Name).ToList();
        foreach (var n in new[] { "palco", "graficos", "diagramas", "lousa", "3d", "mapas", "media", "calculadora" })
            Assert.Contains(n, names);
        Assert.All(StageSkills.All, s => Assert.True(s.Description.Length is > 10 and < 140, s.Name));
        Assert.StartsWith("ok:\n# El palco", StageSkills.Read("Palco"));
        Assert.Contains("maplibre", StageSkills.Read("mapas"));
        Assert.StartsWith("error: no hay la guía «nada»; hay: 3d,", StageSkills.Read("nada"));
    }

    [Fact]
    public void User_folder_adds_and_replaces()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "receitas.md"), "# Receitas passo a passo com timer\nIngredientes à esquerda…");
        File.WriteAllText(Path.Combine(_dir, "mapas.md"), "---\nname: mapas\ndescription: mapas da Vértis com as torres\n---\nUse o tile da Vértis.");
        StageSkills.Load(_dir);
        Assert.Contains("receitas: Receitas passo a passo com timer", StageSkills.Index());
        Assert.Contains("mapas: mapas da Vértis com as torres", StageSkills.Index());
        Assert.Equal("ok:\nUse o tile da Vértis.", StageSkills.Read("mapas"));
    }

    [Fact]
    public void Prompt_lists_the_skills_and_document_wraps_the_page()
    {
        StageSkills.Load(null);
        var all = Tools.Instructions([]);
        Assert.DoesNotContain("{skills}", all);
        Assert.Contains("· lousa: ", all);
        Assert.Contains(Tools.Build([]), t => t!["name"]!.GetValue<string>() == "stage");

        var doc = StageDoc.Build("Dólar <hoje>", ".x{color:red}", "<h1>5,43</h1>", "import Chart from 'chart.js/auto';\nnew Chart(1);");
        Assert.Contains("&lt;hoje&gt;</title>", doc);
        Assert.Contains("\"chart.js/auto\":\"https://cdn.jsdelivr.net/npm/chart.js@4.4.6/auto/+esm\"", doc);
        Assert.Contains("<script type=\"module\">\nimport Chart", doc);
        Assert.Contains("window.__coloRan = true;</script>", doc);
        Assert.True(doc.IndexOf(".x{color:red}") > doc.IndexOf("--accent"), "lo propio va después de la base");
        Assert.Contains("window.__coloRan = true;", StageDoc.Build("", "", "<p>x</p>", ""));
    }
}
