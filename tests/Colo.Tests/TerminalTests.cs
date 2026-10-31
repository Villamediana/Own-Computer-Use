using System.Text.Json.Nodes;
using Colo.Core;
using Xunit;

namespace Colo.Tests;

public class TerminalTests
{
    [Theory]
    [InlineData("WindowsTerminal", "Windows PowerShell", Shell.PowerShell)]
    [InlineData("WindowsTerminal", "Administrador: PowerShell", Shell.PowerShell)]
    [InlineData("WindowsTerminal", "Símbolo del sistema", Shell.Cmd)]
    [InlineData("WindowsTerminal", "miguel@srv: ~/Colo", Shell.Unix)]
    [InlineData("WindowsTerminal", "Ubuntu", Shell.Unix)]
    [InlineData("WindowsTerminal", "claude", Shell.PowerShell)]
    [InlineData("cmd", "C:\\WINDOWS\\system32\\cmd.exe", Shell.Cmd)]
    [InlineData("pwsh", "anything", Shell.PowerShell)]
    [InlineData("mintty", "MINGW64:/c/Users/m", Shell.Unix)]
    [InlineData("chrome", "PowerShell docs - Google Chrome", Shell.None)]
    public void Kind_comes_from_program_and_title(string process, string title, Shell expected) =>
        Assert.Equal(expected, Terminal.Kind(process, title));

    [Theory]
    [InlineData("PS C:\\Users\\m> ", true)]
    [InlineData("C:\\Users\\m>", true)]
    [InlineData("miguel@srv:~/Colo$ ", true)]
    [InlineData("root@srv:/# ", true)]
    [InlineData("❯ ", true)]
    [InlineData(">>> ", true)]
    [InlineData("PS C:\\Users\\m> npm install", false)]
    [InlineData("added 120 packages in 3s", false)]
    [InlineData("Compiling… 45%", false)]
    public void Prompt_back_reads_the_last_line(string last, bool expected) =>
        Assert.Equal(expected, Terminal.PromptBack("salida anterior\n" + last + "\n\n"));

    [Theory]
    [InlineData("rm -rf node_modules", true)]
    [InlineData("Remove-Item .\\out -Recurse", true)]
    [InlineData("del *.log", true)]
    [InlineData("git push --force origin main", true)]
    [InlineData("git reset --hard HEAD~1", true)]
    [InlineData("echo hola > notas.txt", true)]
    [InlineData("taskkill /im chrome.exe /f", true)]
    [InlineData("Get-Process | Format-Table", false)]
    [InlineData("ipconfig", false)]
    [InlineData("git status", false)]
    [InlineData("dir 2>&1", false)]
    [InlineData("Get-ChildItem 2>$null", false)]
    [InlineData("echo hola >> notas.txt", false)]
    [InlineData("pkill-like-name --help", false)]
    public void Dangerous_commands_need_confirmation(string cmd, bool expected) =>
        Assert.Equal(expected, Terminal.Dangerous(cmd));

    [Fact]
    public void Tail_and_clip_keep_the_end()
    {
        Assert.Equal("c\nd", Terminal.Tail("a\r\nb\r\nc\r\nd\r\n\r\n   \n", 2));
        var clipped = Terminal.Clip(new string('x', 5000) + "ERROR al final", 1000);
        Assert.EndsWith("ERROR al final", clipped);
        Assert.Contains("cortadas", clipped);
        Assert.True(clipped.Length < 1100);
    }

    [Fact]
    public async Task Run_returns_output_exit_code_and_cuts_on_timeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var ok = await Terminal.RunAsync("/bin/sh", "-c \"echo hola; echo error 1>&2\"", null, TimeSpan.FromSeconds(10), default);
        Assert.StartsWith("ok:", ok);
        Assert.Contains("hola", ok);
        Assert.Contains("error", ok);
        var bad = await Terminal.RunAsync("/bin/sh", "-c \"exit 3\"", null, TimeSpan.FromSeconds(10), default);
        Assert.Contains("código 3", bad);
        var slow = await Terminal.RunAsync("/bin/sh", "-c \"echo empiezo; sleep 20\"", null, TimeSpan.FromSeconds(1), default);
        Assert.Contains("lo corté", slow);
        Assert.Contains("empiezo", slow);
    }

    [Fact]
    public void Terminal_hint_goes_only_when_a_terminal_is_in_front()
    {
        var agent = new Agent(new AiClient(new Settings()), new Settings { Model = "m" }, new FakeComputer());
        string Text(Observation o) => agent.BuildRequest(["hola"], [], o)["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        var term = Text(new Observation([], 0, 0, "Windows PowerShell  [WindowsTerminal]", [], App: "windowsterminal"));
        Assert.Contains("Terminal PowerShell", term);
        Assert.Contains("ctrl+r", term);
        var web = Text(new Observation([], 0, 0, "Google  [chrome]", [], App: "chrome"));
        Assert.DoesNotContain("Terminal PowerShell", web);
        var tools = Tools.Build(null).Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("terminal", tools);
        Assert.DoesNotContain("terminal", Tools.Build(["terminal"]).Select(t => t!["name"]!.GetValue<string>()));
    }
}
