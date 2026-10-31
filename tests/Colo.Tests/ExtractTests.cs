using Colo.Core;
using Xunit;

namespace Colo.Tests;

public class ExtractTests
{
    const string Thread = """
De: Ana Souza <ana.souza@vertistorres.com.br>
Para: Miguel <miguel@vertistorres.com.br>
Enviado: 28/10/2026 14:02
Assunto: RE: contrato torre SP-0123

Oi Miguel, segue o valor de R$ 12.345,67 do aditivo. Protocolo CHM-48211.
Qualquer coisa me liga no +55 (11) 98765-4321.

De: João Pereira <joao.p@fornecedor.com>
Telefone: (21) 3456-7890 · CNPJ 12.345.678/0001-90
Veja https://portal.fornecedor.com/pedidos/77, ok?
""";

    [Fact]
    public void Email_near_a_name_picks_that_person()
    {
        var ana = Extract.Find(Thread, "email", "Ana Souza");
        Assert.Equal("ana.souza@vertistorres.com.br", ana[0].Value);
        var joao = Extract.Find(Thread, "email", "joão");
        Assert.Equal("joao.p@fornecedor.com", joao[0].Value);
        Assert.Equal(3, Extract.Find(Thread, "email").Count);
    }

    [Fact]
    public void Phones_ids_money_urls_and_dates()
    {
        var phones = Extract.Find(Thread, "phone").Select(h => h.Value).ToList();
        Assert.Contains("+55 (11) 98765-4321", phones);
        Assert.Contains("(21) 3456-7890", phones);
        Assert.DoesNotContain(phones, p => p.Contains("2026"));
        var ids = Extract.Find(Thread, "id").Select(h => h.Value).ToList();
        Assert.Contains("CHM-48211", ids);
        Assert.Contains("12.345.678/0001-90", ids);
        Assert.Equal("R$ 12.345,67", Extract.Find(Thread, "money")[0].Value);
        Assert.Equal("https://portal.fornecedor.com/pedidos/77", Extract.Find(Thread, "url")[0].Value);
        Assert.Equal("28/10/2026", Extract.Find(Thread, "date")[0].Value);
    }

    [Fact]
    public void Report_says_when_nothing_is_there()
    {
        Assert.StartsWith("ok: ningún email cerca de «Carlos»", Extract.Report(Thread, "email", "Carlos"));
        Assert.StartsWith("error:", Extract.Report(Thread, "cpf", ""));
        Assert.Contains("ana.souza@", Extract.Report(Thread, "email", "ana"));
    }
}
