using System.Text.Json.Nodes;
using Colo.Core;
using Xunit;

namespace Colo.Tests;

public class ScreenControlTests
{
    static Settings Azure() => new() { Provider = Provider.Azure, Endpoint = "https://x.openai.azure.com", ApiKey = "k", Model = "m" };

    static string Turn(Observation obs)
    {
        var s = Azure();
        var agent = new Agent(new AiClient(s), s, new FakeComputer());
        return agent.BuildRequest(["revisa mis correos"], [], obs)["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
    }

    [Fact]
    public void Program_names_are_told_apart_from_urls_files_and_protocols()
    {
        Assert.True(Apps.IsName("outlook"));
        Assert.True(Apps.IsName("Outlook (new)"));
        Assert.True(Apps.IsName("WhatsApp"));
        Assert.False(Apps.IsName("notepad.exe"));
        Assert.False(Apps.IsName("www.google.com"));
        Assert.False(Apps.IsName("https://outlook.office.com"));
        Assert.False(Apps.IsName("ms-settings:display"));
        Assert.False(Apps.IsName(@"C:\Users\m\informe.pdf"));
        Assert.False(Apps.IsName("~/Descargas"));
    }

    [Fact]
    public void Both_outlooks_show_up_and_the_exact_name_comes_first()
    {
        // En el notebook del Miguel: abría el clásico (se queda en el inicio de sesión) y nunca veía el nuevo.
        string[] start = ["Outlook (classic)", "Outlook (new)", "Uninstall Outlook", "Google Chrome", "OneNote"];
        var both = Apps.Matching(start, "outlook");
        Assert.Equal(2, both.Count);
        Assert.Contains("Outlook (classic)", both);
        Assert.Contains("Outlook (new)", both);
        Assert.Equal("Outlook (new)", Apps.Matching(start, "outlook (NEW)")[0]);
        Assert.Equal(["Google Chrome"], Apps.Matching(start, "chrome"));
        Assert.Empty(Apps.Matching(start, "excel"));
        Assert.Equal("Excel", Apps.Matching(["Excel 2016 Tools", "Excel"], "excel")[0]);
    }

    [Fact]
    public void Folded_accounts_and_their_folders_are_visible_in_the_turn()
    {
        var obs = new Observation([], 0, 0, "Caixa de Entrada - Outlook  [olk]",
        [
            new Mark(1, "nodo", "miguel@vertistorres.com.br", "", "desplegado"),
            new Mark(2, "nodo", "Caixa de Entrada", "", "elegido", "miguel@vertistorres.com.br"),
            new Mark(3, "nodo", "Projetos", "", "plegado", "miguel@vertistorres.com.br › Caixa de Entrada"),
            new Mark(4, "nodo", "inovacao@vertistorres.com.br", "", "plegado"),
        ], App: "olk");
        var text = Turn(obs);
        Assert.Contains("[1] nodo «miguel@vertistorres.com.br» (desplegado)\n", text);
        Assert.Contains("[2] nodo «Caixa de Entrada» (elegido) en «miguel@vertistorres.com.br»", text);
        Assert.Contains("[3] nodo «Projetos» (plegado) en «miguel@vertistorres.com.br › Caixa de Entrada»", text);
        Assert.Contains("[4] nodo «inovacao@vertistorres.com.br» (plegado)\n", text);
        // La dica del Outlook va en el turno: cuentas plegables, list, y cómo se escribe un correo.
        Assert.Contains("expand", text);
        Assert.Contains("read_text list", text);
        Assert.Contains("Para/CC", text);
    }

    [Fact]
    public void Mail_in_the_browser_gets_the_same_help_as_the_app()
    {
        Assert.Contains("Para/CC", AppHints.For("chrome", "Correo - Outlook  [chrome]", "https://outlook.office.com/mail/inbox"));
        Assert.Contains("Para/CC", AppHints.For("msedge", "Outlook  [msedge]", "outlook.cloud.microsoft/mail/"));
        Assert.Contains("Gmail", AppHints.For("chrome", "Inbox  [chrome]", "https://mail.google.com/mail/u/0/#inbox"));
        Assert.Equal("", AppHints.For("chrome", "Google  [chrome]", "https://www.google.com/"));
        Assert.Contains("Para/CC", AppHints.For("OUTLOOK", "Caixa de Entrada - Outlook  [OUTLOOK]"));

        var obs = new Observation([], 0, 0, "Correo - Outlook  [chrome]", [new Mark(1, "botón", "Novo email")],
            App: "chrome", Details: ["URL: https://outlook.office.com/mail/"]);
        Assert.Contains("Para/CC", Turn(obs));
    }

    [Fact]
    public void Tools_can_expand_read_whole_rows_and_open_tells_what_came_up()
    {
        var tools = Tools.Build(null);
        JsonNode Tool(string n) => tools.First(t => t!["name"]!.GetValue<string>() == n)!;
        Assert.NotNull(Tool("click")["parameters"]!["properties"]!["expand"]);
        Assert.Contains("list", Tool("read_text")["parameters"]!["properties"]!["what"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("parecido", Tool("open")["description"]!.GetValue<string>());

        var obs = new Observation([], 0, 0, "Outlook", [new Mark(3, "nodo", "inovacao@vertistorres.com.br", "", "plegado")]);
        Assert.Equal("Despliega [3] «inovacao@vertistorres.com.br»",
            Agent.Describe(new ToolCall("click", (JsonObject)JsonNode.Parse("""{"mark":3,"expand":true}""")!), obs));
        Assert.Equal("Pliega [3] «inovacao@vertistorres.com.br»",
            Agent.Describe(new ToolCall("click", (JsonObject)JsonNode.Parse("""{"mark":3,"expand":false}""")!), obs));
        Assert.Equal("Clic [3] «inovacao@vertistorres.com.br»",
            Agent.Describe(new ToolCall("click", (JsonObject)JsonNode.Parse("""{"mark":3}""")!), obs));
        Assert.Equal("Lee la lista", Agent.Describe(new ToolCall("read_text", (JsonObject)JsonNode.Parse("""{"what":"list"}""")!), obs));
    }

    [Fact]
    public void Writing_rules_ask_for_a_ready_to_send_text_in_first_person()
    {
        Assert.Contains("primera persona", Tools.System);
        Assert.Contains("nunca su orden copiada", Tools.System);
        Assert.Contains("nombre exacto", Tools.System);
    }
}

public class TreeRowsTests
{
    [Fact]
    public void An_expanded_account_is_clicked_on_its_own_row_not_on_a_folder_in_the_middle()
    {
        var account = System.Drawing.Rectangle.FromLTRB(0, 100, 300, 400);   // abarca sus carpetas
        var inbox = System.Drawing.Rectangle.FromLTRB(20, 132, 300, 164);
        var sent = System.Drawing.Rectangle.FromLTRB(20, 164, 300, 196);
        var other = System.Drawing.Rectangle.FromLTRB(0, 400, 300, 432);      // la cuenta siguiente, plegada
        System.Drawing.Rectangle[] all = [account, inbox, sent, other];
        Assert.Equal(System.Drawing.Rectangle.FromLTRB(0, 100, 300, 132), TreeRows.Own(account, all));
        Assert.Null(TreeRows.Own(inbox, all));
        Assert.Null(TreeRows.Own(other, all));
    }
}
