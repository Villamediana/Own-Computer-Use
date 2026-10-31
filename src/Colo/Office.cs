using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Colo.Core;

namespace Colo;

/// <summary>
/// Word y Excel por COM (el Word/Excel ya abierto del usuario): títulos, secciones, buscar, escribir y formato;
/// celdas, fórmulas y filtros. Más rápido y seguro que buscar por la pantalla en un documento grande.
/// </summary>
static class Office
{
    const int MaxRead = 15_000;

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    static object? Active(string progId)
    {
        if (CLSIDFromProgID(progId, out var id) != 0) return null;
        return GetActiveObject(ref id, IntPtr.Zero, out var o) == 0 ? o : null;
    }

    /// <summary>Todo el trabajo COM en un hilo STA, con tope de tiempo (Office ocupado no cuelga a Colo).</summary>
    static async Task<string> Run(Func<string> work, CancellationToken ct)
    {
        var task = Task.Run(() =>
        {
            try { return Desktop.Sta(work); }
            catch (COMException ex) when ((uint)ex.HResult is 0x8001010A or 0x80010001)
            {
                return "error: Office está ocupado (¿editando una celda o con un diálogo abierto? Esc y vuelve a probar)";
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
            {
                return "error: Office no dejó hacerlo (" + ex.Message + ")";
            }
        }, ct);
        if (await Task.WhenAny(task, Task.Delay(15_000, ct)) != task) return "error: Office tardó demasiado en contestar";
        return await task;
    }

    static string Clean(string s) => s.Replace("\r\a", "\t").Replace('\a', ' ').Replace('\r', '\n').Replace('\v', '\n').Replace('\f', '\n');

    static string Cut(string s, int max = MaxRead) => s.Length <= max ? s : s[..max] + $"\n…(cortado: faltan {s.Length - max} letras)";

    // ───────────────────────── Word

    public static Task<string> DocAsync(ToolCall c, CancellationToken ct) => Run(() =>
    {
        dynamic? word = Active("Word.Application");
        if (word is null) return "error: Word no está abierto. doc es para Word; en otros programas usa read_text, find_on_screen o browser find.";
        if ((int)word.Documents.Count == 0) return "error: Word no tiene ningún documento abierto";
        dynamic doc = word.ActiveDocument;
        dynamic sel = word.Selection;
        var action = c.Str("action");
        if (action is "goto" or "find" or "write" or "format") Front((object)word);
        switch (action)
        {
            case "outline":
            {
                var outline = Outline((object)doc);
                if (outline.Count == 0) return $"ok: «{doc.Name}» no tiene títulos (estilos Título 1, 2…). Usa find o read all.";
                var sb = new StringBuilder($"ok: «{doc.Name}», {outline.Count} títulos");
                if (Section((object)sel) is { } here) sb.Append($". El cursor está en «{here}»");
                sb.AppendLine();
                foreach (var (n, level, text) in outline.Take(150))
                    sb.Append(new string(' ', (level - 1) * 2)).Append(n).Append(". ").AppendLine(text);
                return sb.ToString().TrimEnd();
            }
            case "goto":
                return GoToHeading((object)word, (object)doc, c.Str("target"), out var heading) ? $"ok: cursor en «{heading}»" : heading;
            case "read":
            {
                var what = c.Str("what");
                if (what == "selection")
                {
                    string t = sel.Text ?? "";
                    return (int)sel.Start == (int)sel.End || t.Length == 0 ? "ok: (no hay nada seleccionado)" : "ok:\n" + Cut(Clean(t));
                }
                if (what == "all") return $"ok: «{doc.Name}» entero\n" + Cut(Clean((string)doc.Content.Text));
                if (c.Str("target").Length > 0 && !GoToHeading((object)word, (object)doc, c.Str("target"), out var err)) return err;
                var section = SectionRange((object)sel);
                if (section is null) return "error: el cursor no está bajo ningún título; usa read all o goto";
                return $"ok: sección «{Section((object)sel)}»\n" + Cut(Clean((string)section.Text));
            }
            case "find":
            {
                var text = c.Str("text");
                if (text.Length == 0) return "error: falta text";
                var hits = new List<(int Start, int End, string Context)>();
                dynamic r = doc.Content;
                r.Find.ClearFormatting();
                r.Find.Text = text.Length > 255 ? text[..255] : text;
                r.Find.Forward = true;
                r.Find.Wrap = 0; // wdFindStop
                r.Find.MatchCase = false;
                while (hits.Count < 50 && (bool)r.Find.Execute())
                {
                    string para = r.Paragraphs.Item(1).Range.Text ?? "";
                    hits.Add(((int)r.Start, (int)r.End, Short(Clean(para).Trim(), text)));
                    r.Collapse(0); // wdCollapseEnd
                }
                if (hits.Count == 0) return $"ok: «{text}» no aparece en el documento";
                var k = Math.Clamp(c.Int("n") ?? 1, 1, hits.Count);
                dynamic found = doc.Range(hits[k - 1].Start, hits[k - 1].End);
                found.Select();
                word.ActiveWindow.ScrollIntoView(found, true);
                return $"ok: {hits.Count}{(hits.Count == 50 ? "+" : "")} veces; seleccionada la {k}\n"
                    + string.Join("\n", hits.Take(15).Select((h, i) => $"{i + 1}) {h.Context}"));
            }
            case "write":
            {
                var text = c.Str("text");
                if (text.Length == 0) return "error: falta text";
                var paragraphs = OfficeText.WordParagraphs(text);
                if (c.Str("at") != "section_end")
                {
                    sel.TypeText(paragraphs);
                    return "ok: escrito en el cursor";
                }
                if (c.Str("target").Length > 0 && !GoToHeading((object)word, (object)doc, c.Str("target"), out var err)) return err;
                var section = SectionRange((object)sel);
                if (section is null) return "error: el cursor no está bajo ningún título; usa goto o escribe en el cursor";
                int end = section.End;
                dynamic last = doc.Range(end - 1, end - 1).Paragraphs.Item(1);
                var afterHeading = (int)last.OutlineLevel < 10; // sección vacía: el último párrafo es el título
                dynamic at = doc.Range(end - 1, end - 1);
                at.InsertAfter("\r" + paragraphs);
                dynamic added = doc.Range(end, (int)at.End);
                if (afterHeading) added.Style = -1; // wdStyleNormal: el texto nuevo no hereda el título
                doc.Range((int)at.End, (int)at.End).Select();
                word.ActiveWindow.ScrollIntoView(added, true);
                return $"ok: escrito al final de «{Section((object)sel) ?? "la sección"}»";
            }
            case "format":
            {
                var style = c.Str("style");
                switch (style)
                {
                    case "bold": sel.Font.Bold = (int)sel.Font.Bold == -1 ? 0 : 1; break;
                    case "italic": sel.Font.Italic = (int)sel.Font.Italic == -1 ? 0 : 1; break;
                    case "underline": sel.Font.Underline = (int)sel.Font.Underline == 0 ? 1 : 0; break;
                    case "bullets": sel.Range.ListFormat.ApplyBulletDefault(); break;
                    case "numbers": sel.Range.ListFormat.ApplyNumberDefault(); break;
                    default:
                        int? id = style switch
                        {
                            "heading1" => -2, "heading2" => -3, "heading3" => -4, "title" => -63, "normal" => -1, _ => null,
                        };
                        if (id is null) return $"error: style desconocido «{style}»";
                        sel.Style = id.Value;
                        break;
                }
                return "ok";
            }
            default:
                return $"error: acción desconocida «{action}»";
        }
    }, ct);

    static void Front(object application)
    {
        dynamic app = application;
        try
        {
            var h = new IntPtr((long)app.ActiveWindow.Hwnd);
            if (h != IntPtr.Zero && Native.GetForegroundWindow() != h) Native.Focus(h);
        }
        catch { }
    }

    static List<(int N, int Level, string Text)> Outline(object document)
    {
        dynamic doc = document;
        var items = new List<string>();
        if (doc.GetCrossReferenceItems(1) is Array arr) // wdRefTypeHeading
            foreach (var x in arr) items.Add(x?.ToString() ?? "");
        return OfficeText.Outline(items);
    }

    /// <summary>La sección del cursor (su título y lo que cuelga de él); null si no hay título antes.</summary>
    static dynamic? SectionRange(object selection)
    {
        dynamic sel = selection;
        try { return sel.Bookmarks.Item("\\HeadingLevel").Range; }
        catch (COMException) { return null; }
    }

    static string? Section(object selection)
    {
        dynamic sel = selection;
        try
        {
            string t = SectionRange((object)sel)?.Paragraphs.Item(1).Range.Text ?? "";
            t = Clean(t).Trim();
            return t.Length == 0 ? null : t;
        }
        catch (COMException) { return null; }
    }

    /// <summary>Lleva el cursor al título pedido (texto o número del esquema), buscándolo entre los párrafos de título.</summary>
    static bool GoToHeading(object application, object document, string target, out string result)
    {
        dynamic word = application, doc = document;
        var outline = Outline((object)doc);
        if (outline.Count == 0) { result = "error: el documento no tiene títulos; usa find"; return false; }
        var n = OfficeText.Match(outline, target);
        if (n == 0)
        {
            result = $"error: no hay ningún título como «{target}». Títulos: " + string.Join(" · ", outline.Take(40).Select(o => $"{o.N}. {o.Text}"));
            return false;
        }
        var text = outline[n - 1].Text;
        // Si el mismo título se repite, cuántos iguales hay antes en el esquema.
        var skip = outline.Take(n - 1).Count(o => o.Text == text);
        dynamic r = doc.Content;
        r.Find.ClearFormatting();
        r.Find.Text = text.Length > 255 ? text[..255] : text;
        r.Find.Forward = true;
        r.Find.Wrap = 0;
        while ((bool)r.Find.Execute())
        {
            if ((int)r.Paragraphs.Item(1).OutlineLevel < 10 && skip-- == 0)
            {
                dynamic start = doc.Range((int)r.Paragraphs.Item(1).Range.Start, (int)r.Paragraphs.Item(1).Range.Start);
                start.Select();
                word.ActiveWindow.ScrollIntoView(r.Paragraphs.Item(1).Range, true);
                result = text;
                return true;
            }
            r.Collapse(0);
        }
        result = $"error: no encontré el título «{text}» en el texto";
        return false;
    }

    /// <summary>A1:D20 (sin los $ de la dirección absoluta).</summary>
    static string Addr(object range) => ((string)((dynamic)range).Address).Replace("$", "");

    static string Short(string para, string want)
    {
        if (para.Length <= 140) return para;
        var i = Math.Max(0, Extract.Fold(para).IndexOf(Extract.Fold(want), StringComparison.Ordinal));
        var from = Math.Clamp(i - 50, 0, para.Length);
        var len = Math.Min(140, para.Length - from);
        return (from > 0 ? "…" : "") + para.Substring(from, len) + (from + len < para.Length ? "…" : "");
    }

    // ───────────────────────── Excel

    public static Task<string> SheetAsync(ToolCall c, CancellationToken ct) => Run(() =>
    {
        dynamic? xl = Active("Excel.Application");
        if (xl is null) return "error: Excel no está abierto";
        dynamic? wb = xl.ActiveWorkbook;
        if (wb is null) return "error: Excel no tiene ningún libro abierto";
        dynamic ws = xl.ActiveSheet;
        var action = c.Str("action");
        if (action is "write" or "goto" or "filter") Front((object)xl);
        switch (action)
        {
            case "read":
            {
                dynamic r;
                if (c.Str("range").Length > 0) r = ws.Range[c.Str("range")];
                else
                {
                    dynamic? s = null;
                    try { s = (int)xl.Selection.Count > 1 ? xl.Selection : null; } catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    r = s ?? ws.UsedRange;
                }
                int rows = r.Rows.Count, cols = r.Columns.Count;
                dynamic part = r.Resize[Math.Min(rows, 60), Math.Min(cols, 26)];
                object? v = part.Value;
                var values = v as object?[,] ?? new object?[,] { { v } };
                var sheets = new List<string>();
                foreach (var sh in wb.Worksheets) sheets.Add((string)sh.Name);
                return $"ok: libro «{wb.Name}», hoja «{ws.Name}» (hojas: {string.Join(" · ", sheets)}), {Addr(r)}\n"
                    + OfficeText.Table(values, (int)r.Row, (int)r.Column, totalRows: rows, totalCols: cols);
            }
            case "write":
            {
                var start = c.Str("range");
                if (start.Length == 0) start = (string)Addr(xl.ActiveCell);
                var rows = new List<List<string>>();
                if (c.Args["values"] is JsonArray table)
                    foreach (var row in table)
                        rows.Add(row is JsonArray cells
                            ? cells.Select(x => x is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : x?.ToJsonString() ?? "").ToList()
                            : [row is JsonValue one && one.TryGetValue<string>(out var t) ? t : row?.ToJsonString() ?? ""]);
                else if (c.Args["value"] is not null) rows.Add([c.Str("value")]);
                if (rows.Count == 0) return "error: faltan values";
                var width = rows.Max(r => r.Count);
                var data = new object[rows.Count, width];
                for (var i = 0; i < rows.Count; i++)
                    for (var j = 0; j < width; j++)
                        data[i, j] = j < rows[i].Count ? OfficeText.Value(rows[i][j]) : "";
                dynamic target = ws.Range[start].Cells[1, 1].Resize[rows.Count, width];
                target.Formula = data;
                string first = target.Cells[1, 1].Text ?? "";
                var formula = rows[0][0].TrimStart().StartsWith('=') ? $" (la primera da «{first}»)" : "";
                return $"ok: escrito en {Addr(target)}{formula}";
            }
            case "goto":
            {
                if (c.Str("sheet").Length > 0) { wb.Worksheets.Item(c.Str("sheet")).Activate(); ws = xl.ActiveSheet; }
                if (c.Str("range").Length > 0) xl.Goto(ws.Range[c.Str("range")], true);
                return $"ok: hoja «{ws.Name}», celda {Addr(xl.ActiveCell)}";
            }
            case "filter":
            {
                dynamic region = c.Str("range").Length > 0 ? ws.Range[c.Str("range")] : xl.ActiveCell.CurrentRegion;
                var criteria = c.Str("criteria");
                if (criteria.Length == 0)
                {
                    if ((bool)ws.FilterMode) ws.ShowAllData();
                    return "ok: sin filtros";
                }
                var column = c.Str("column").Trim();
                if (column.Length == 0 && c.Int("column") is int ci) column = ci.ToString();
                int field;
                int firstCol = region.Column, cols = region.Columns.Count;
                if (int.TryParse(column, out var num)) field = num;
                else if (column.Length <= 3 && column.All(char.IsLetter))
                    field = (int)ws.Range[column + "1"].Column - firstCol + 1;
                else
                {
                    field = 0;
                    for (var j = 1; j <= cols && field == 0; j++)
                        if (Extract.Fold(region.Cells[1, j].Text?.ToString() ?? "").Trim() == Extract.Fold(column).Trim()) field = j;
                    for (var j = 1; j <= cols && field == 0; j++)
                        if (Extract.Fold(region.Cells[1, j].Text?.ToString() ?? "").Contains(Extract.Fold(column).Trim())) field = j;
                }
                if (field < 1 || field > cols) return $"error: no encontré la columna «{column}» en {Addr(region)}";
                region.AutoFilter(field, criteria);
                return $"ok: filtrado {Addr(region)} por la columna {field} = «{criteria}»";
            }
            default:
                return $"error: acción desconocida «{action}»";
        }
    }, ct);
}
