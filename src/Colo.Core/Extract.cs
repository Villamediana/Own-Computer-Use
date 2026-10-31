using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Colo.Core;

/// <summary>Datos sueltos (correos, teléfonos, enlaces…) sacados del texto de una ventana, sin pasar por el modelo.</summary>
public static class Extract
{
    public static readonly string[] Kinds = ["email", "phone", "url", "date", "id", "money"];

    static readonly Dictionary<string, Regex> Patterns = new()
    {
        ["email"] = new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.Compiled),
        // +55 (11) 98765-4321, (11) 3456-7890, 11 98765 4321, +1 415 555 0100…
        ["phone"] = new(@"(?<![\w/.-])(\+\d{1,3}[\s.-]?)?(\(\d{2,3}\)\s?|\d{2,3}[\s.-])?\d{4,5}[\s.-]?\d{4}(?![\w/-]|\.\d)", RegexOptions.Compiled),
        ["url"] = new(@"\b(https?://|www\.)[^\s<>""'«»]+[^\s<>""'«».,;:!?)\]]", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        ["date"] = new(@"\b(\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}|\d{4}-\d{2}-\d{2}|\d{1,2} de [a-zç]+( de \d{4})?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // CPF, CNPJ, protocolos y números de pedido/chamado (con letras y números o 6+ cifras).
        ["id"] = new(@"\b(\d{3}\.\d{3}\.\d{3}-\d{2}|\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}|[A-Z]{2,5}[-#]?\d{3,}|#\d{3,}|\d{6,})\b", RegexOptions.Compiled),
        ["money"] = new(@"(R\$|US\$|\$|€|£)\s?\d{1,3}([.,]\d{3})*([.,]\d{2})?|\b\d{1,3}([.,]\d{3})*[.,]\d{2}\s?(reais|dólares|euros|BRL|USD|EUR)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    /// <summary>Sin acentos y en minúsculas, para comparar lo que dijo con lo que hay en pantalla.</summary>
    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// Lo que encaja con kind, sin repetidos y en orden de aparición, con un poco de contexto. near: solo los que
    /// tienen ese texto a menos de 200 letras (todas sus palabras), el más cercano primero.
    /// </summary>
    public static List<(string Value, string Context)> Find(string text, string kind, string near = "", int max = 15)
    {
        if (!Patterns.TryGetValue(kind, out var re)) return [];
        var folded = Fold(text);
        var words = Fold(near).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToArray();
        var hits = new List<(string Value, string Context, int Distance, int Order)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (Match m in re.Matches(text))
        {
            var value = m.Value.Trim().TrimEnd('.', ',', ';');
            if (kind == "phone" && value.Count(char.IsDigit) < 8) continue;
            var distance = 0;
            if (words.Length > 0)
            {
                // Folded tiene la misma longitud que el original salvo en raros casos; se acota por si acaso.
                var from = Math.Clamp(m.Index - 200, 0, folded.Length);
                var to = Math.Clamp(m.Index + m.Length + 200, from, folded.Length);
                var around = folded[from..to];
                if (!words.All(around.Contains)) continue;
                var center = Math.Clamp(m.Index - from, 0, around.Length);
                distance = words.Max(w => Nearest(around, w, center));
            }
            if (!seen.Add(value)) continue;
            hits.Add((value, Context(text, m.Index, m.Length), distance, order++));
        }
        return hits.OrderBy(h => h.Distance).ThenBy(h => h.Order).Take(max).Select(h => (h.Value, h.Context)).ToList();
    }

    static int Nearest(string s, string w, int center)
    {
        var best = int.MaxValue;
        for (var i = s.IndexOf(w, StringComparison.Ordinal); i >= 0; i = s.IndexOf(w, i + 1, StringComparison.Ordinal))
            best = Math.Min(best, Math.Abs(i - center));
        return best;
    }

    static string Context(string text, int index, int length)
    {
        var from = Math.Max(0, index - 60);
        var to = Math.Min(text.Length, index + length + 60);
        var c = Regex.Replace(text[from..to], @"\s+", " ").Trim();
        return (from > 0 ? "…" : "") + c + (to < text.Length ? "…" : "");
    }

    /// <summary>Resultado para el modelo.</summary>
    public static string Report(string text, string kind, string near)
    {
        if (!Patterns.ContainsKey(kind)) return $"error: kind debe ser {string.Join(", ", Kinds)}";
        var hits = Find(text, kind, near);
        if (hits.Count == 0)
            return near.Length > 0
                ? $"ok: ningún {kind} cerca de «{near}» en esta ventana (puede estar plegado: ábrelo o pasa el ratón por el nombre y vuelve a probar)"
                : $"ok: ningún {kind} en esta ventana";
        return "ok:\n" + string.Join("\n", hits.Select(h => $"{h.Value}  —  {h.Context}"));
    }
}
