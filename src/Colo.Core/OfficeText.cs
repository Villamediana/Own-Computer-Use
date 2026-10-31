using System.Globalization;
using System.Text;

namespace Colo.Core;

/// <summary>Lo que no depende de Office: tablas de Excel como texto, valores a escribir y el esquema de Word.</summary>
public static class OfficeText
{
    /// <summary>A, B… Z, AA…</summary>
    public static string Column(int n)
    {
        var s = "";
        for (; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    /// <summary>Una tabla (como la da Range.Value, base 1) con letras de columna y números de fila; celdas largas cortadas.</summary>
    public static string Table(object?[,] values, int firstRow, int firstCol, int maxRows = 60, int maxCols = 26,
        int totalRows = 0, int totalCols = 0)
    {
        var rows = Math.Max(values.GetLength(0), totalRows);
        var cols = Math.Max(values.GetLength(1), totalCols);
        var r0 = values.GetLowerBound(0);
        var c0 = values.GetLowerBound(1);
        var sb = new StringBuilder();
        sb.Append('\t').AppendJoin('\t', Enumerable.Range(0, Math.Min(values.GetLength(1), maxCols)).Select(i => Column(firstCol + i))).AppendLine();
        for (var r = 0; r < Math.Min(values.GetLength(0), maxRows); r++)
        {
            sb.Append(firstRow + r);
            for (var c = 0; c < Math.Min(values.GetLength(1), maxCols); c++)
            {
                var text = Cell(values[r0 + r, c0 + c]);
                sb.Append('\t').Append(text.Length > 60 ? text[..60] + "…" : text);
            }
            sb.AppendLine();
        }
        if (rows > maxRows || cols > maxCols)
            sb.AppendLine($"…(la tabla tiene {rows} filas × {cols} columnas; pide un range para ver el resto)");
        return sb.ToString().TrimEnd();
    }

    static readonly Dictionary<int, string> Errors = new()
    {
        [-2146826281] = "#DIV/0!", [-2146826246] = "#N/A", [-2146826259] = "#NAME?", [-2146826288] = "#NULL!",
        [-2146826252] = "#NUM!", [-2146826265] = "#REF!", [-2146826273] = "#VALUE!",
    };

    static string Cell(object? v) => v switch
    {
        null => "",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("dd/MM/yyyy") : d.ToString("dd/MM/yyyy HH:mm"),
        double x => x.ToString("0.##########", CultureInfo.InvariantCulture),
        bool b => b ? "VERDADERO" : "FALSO",
        // Los errores de Excel llegan como enteros (CVErr).
        int i => Errors.TryGetValue(i, out var e) ? e : i.ToString(CultureInfo.InvariantCulture),
        _ => (v.ToString() ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '),
    };

    /// <summary>Lo que se escribe en una celda: fórmula tal cual, número si lo es (punto o coma decimal), si no texto.</summary>
    public static object Value(string s)
    {
        s = s.Trim();
        if (s.StartsWith('=')) return s;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !s.Contains(',')) return d;
        // 1.234,56 o 12,5 (forma brasileña/española).
        if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.GetCultureInfo("pt-BR"), out d)
            && (s.Contains(',') || !s.Contains('.'))) return d;
        return s;
    }

    /// <summary>
    /// Esquema de Word: GetCrossReferenceItems trae los títulos con sangría según el nivel. Devuelve número,
    /// nivel y texto, en orden.
    /// </summary>
    public static List<(int N, int Level, string Text)> Outline(IEnumerable<string> items)
    {
        var list = new List<(int, int, string)>();
        var n = 0;
        foreach (var raw in items)
        {
            var text = raw.TrimEnd();
            if (text.Trim().Length == 0) continue;
            var indent = text.Length - text.TrimStart().Length;
            list.Add((++n, Math.Min(9, indent / 2 + 1), text.Trim()));
        }
        return list;
    }

    /// <summary>El título que mejor encaja con lo pedido: número de la lista, texto igual, que lo contiene o con más palabras en común.</summary>
    public static int Match(List<(int N, int Level, string Text)> outline, string want)
    {
        want = want.Trim();
        if (int.TryParse(want, out var n) && n >= 1 && n <= outline.Count) return n;
        var w = Extract.Fold(want);
        if (w.Length == 0) return 0;
        var exact = outline.FirstOrDefault(o => Extract.Fold(o.Text) == w);
        if (exact.N > 0) return exact.N;
        var contains = outline.FirstOrDefault(o => Extract.Fold(o.Text).Contains(w));
        if (contains.N > 0) return contains.N;
        var words = w.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 2).ToArray();
        var best = outline.Select(o => (o.N, Score: words.Count(Extract.Fold(o.Text).Contains))).OrderByDescending(x => x.Score).FirstOrDefault();
        return best.Score > 0 ? best.N : 0;
    }

    /// <summary>Texto con saltos de línea como los entiende Word (\r = párrafo).</summary>
    public static string WordParagraphs(string text) => text.Replace("\r\n", "\r").Replace('\n', '\r');
}
