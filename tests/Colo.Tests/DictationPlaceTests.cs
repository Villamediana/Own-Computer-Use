using Colo.Core;
using Xunit;

namespace Colo.Tests;

public class DictationPlaceTests
{
    static (int Choice, string Text) Run(int count, params string[] chunks)
    {
        var p = new Dictation.Place(count);
        var text = string.Concat(chunks.Select(p.Feed)) + p.Flush();
        return (p.Choice, text);
    }

    [Fact]
    public void Choice_line_is_read_across_chunks_and_never_typed()
    {
        Assert.Equal((2, "npm test"), Run(3, "@", "2", "\n", "npm ", "test"));
        Assert.Equal((3, "Oi Felipe, tudo bem?"), Run(3, "@3\nOi Felipe", ", tudo bem?"));
        Assert.Equal((2, "ok"), Run(3, "@2 ok"));
        Assert.Equal((1, "sem lugar"), Run(3, "sem ", "lugar"));
        Assert.Equal((1, "texto"), Run(2, "@9\ntexto")); // fuera de rango → el primero
    }

    [Fact]
    public void Text_waits_until_the_place_is_decided()
    {
        var p = new Dictation.Place(2);
        Assert.Equal("", p.Feed("@"));
        Assert.False(p.Decided);
        Assert.Equal("", p.Feed("2"));
        Assert.Equal("ls", p.Feed("\nls"));
        Assert.True(p.Decided);
        Assert.Equal(" -la", p.Feed(" -la"));
        Assert.Equal("git status", Dictation.WithoutPlace("@1\ngit status"));
    }

    [Fact]
    public void Request_lists_places_only_when_there_is_a_choice()
    {
        var s = new Settings { Model = "m" };
        string Text(IReadOnlyList<string>? places) => Dictation.Request(s, "roda os testes", "Chrome", places)["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Equal("[Chrome]\nroda os testes", Text(null));
        Assert.Equal("[Chrome]\nroda os testes", Text(["só um"]));
        var two = Text(["«Buscar» em «Google» [chrome]", "terminal «claude» [WindowsTerminal]"]);
        Assert.Contains("Onde escrever:\n@1 «Buscar» em «Google» [chrome]\n@2 terminal «claude»", two);
        Assert.EndsWith("roda os testes", two);
    }
}
