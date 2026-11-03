using System.Text;
using Colo.Core;
using Xunit;

namespace Colo.Tests;

public sealed class VaultTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"colo-vault-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_path); } catch { } }

    static byte[] Flip(byte[] b) => b.Select(x => (byte)(x ^ 0x5A)).Reverse().ToArray();

    Vault Open() => new(_path, Flip, Flip);

    [Fact]
    public void Secret_never_on_disk_in_clear_and_comes_back()
    {
        var v = Open();
        string? saved = null;
        v.Saved += s => saved = s;
        Assert.StartsWith("ok:", v.Save("ssh servidor", "ssh", "miguel", "10.0.0.5", "", "S3nh@-Mu1t0-S3cr3t4"));
        Assert.Equal("S3nh@-Mu1t0-S3cr3t4", saved);
        Assert.DoesNotContain("S3nh@", File.ReadAllText(_path));
        Assert.DoesNotContain("S3nh@", Encoding.UTF8.GetString(File.ReadAllBytes(_path)));
        Assert.Equal("S3nh@-Mu1t0-S3cr3t4", Open().Reveal("ssh servidor", out var e));
        Assert.Equal("10.0.0.5", e!.Host);
    }

    [Fact]
    public void Model_cannot_overwrite_an_entry_without_replace()
    {
        var v = Open();
        v.Save("Servidor Local", "ssh", "u0", "192.168.15.188", "", "senha-ssh");
        var r = v.Save("Servidor Local", "note", "", "", "", "miguel@vertis.com.br", replace: false);
        Assert.StartsWith("error: ya existe «Servidor Local» ssh", r);
        Assert.Equal("senha-ssh", Open().Reveal("Servidor Local", out _));
        Assert.StartsWith("ok: actualicé", v.Save("Servidor Local", "ssh", "u0", "192.168.15.188", "", "nueva", replace: true));
        Assert.Equal("nueva", Open().Reveal("Servidor Local", out _));
    }

    [Fact]
    public void Model_sees_names_never_secrets()
    {
        var v = Open();
        v.Save("Freshservice", "login", "miguel@vertis", "vertis.freshservice.com", "freshservice", "hunter2!x");
        var line = string.Join(" ", v.Lines());
        Assert.Equal("«Freshservice» login miguel@vertis@vertis.freshservice.com (en freshservice)", line);
        Assert.DoesNotContain("hunter2", line);
        var agent = new Agent(new AiClient(new Settings()), new Settings { Model = "m" }, new FakeComputer()) { Vault = v };
        var text = agent.BuildRequest(["entra no fresh"], [], new Observation([], 0, 0, "x", []))["input"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Cofre: «Freshservice»", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void Find_by_partial_name_or_host_update_and_forget()
    {
        var v = Open();
        v.Save("ssh servidor casa", "ssh", "root", "192.168.0.10", "", "a");
        v.Save("VPN trabalho", "password", "", "", "", "b");
        Assert.Equal("ssh servidor casa", v.Find("servidor")!.Name);
        Assert.Equal("ssh servidor casa", v.Find("192.168.0.10")!.Name);
        Assert.Null(v.Find("a")); // ambiguo: los dos lo tienen
        Assert.StartsWith("ok: actualicé", v.Save("VPN TRABALHO", "password", "", "", "", "c"));
        Assert.Equal("c", v.Reveal("vpn trabalho", out _));
        Assert.Equal(2, v.Entries.Count);
        Assert.StartsWith("ok:", v.Forget("vpn"));
        Assert.Single(Open().Entries);
        Assert.StartsWith("error:", v.Save("x", "password", "", "", "", ""));
    }

    [Fact]
    public async Task Spoken_secret_does_not_travel_again_after_saving()
    {
        var bodies = new List<string>();
        var step = 0;
        var h = new FakeHandler((_, body) =>
        {
            lock (bodies) bodies.Add(body);
            return (System.Net.HttpStatusCode.OK, Interlocked.Increment(ref step) == 1
                ? CoreTests.Calls(("vault", """{"action":"save","name":"ssh casa","host":"10.0.0.5","secret":"pato-azul-77"}"""))
                : CoreTests.Calls(("done", """{"message":"Guardado."}""")));
        });
        var s = new Settings { Provider = Provider.Azure, Endpoint = "https://x.openai.azure.com/openai/deployments/x?api-version=1", ApiKey = "k", Model = "m" };
        var agent = new Agent(new AiClient(s, new HttpClient(h)), s, new FakeComputer()) { SettleMs = 0, Vault = Open() };
        await agent.Hear("guarda o ssh da casa 10.0.0.5 senha pato-azul-77");
        Assert.Contains("pato-azul-77", bodies[0]);
        Assert.DoesNotContain("pato-azul-77", bodies[1]);
    }
}
