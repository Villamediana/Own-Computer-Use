namespace Colo.Core;

/// <summary>Programas por el nombre con que el usuario los llama, contra los nombres de Inicio.</summary>
public static class Apps
{
    /// <summary>Un nombre de programa («outlook», «Outlook (new)»), no una URL, una ruta, un archivo ni un protocolo (ms-settings:).</summary>
    public static bool IsName(string target)
    {
        var t = target.Trim();
        if (t.Length == 0 || t.Contains("://") || t.Contains('\\') || t.Contains('/') || t.Contains(':')) return false;
        // notepad.exe, informe.pdf, www.algo.com: Windows sabe qué hacer con eso tal cual.
        var dot = t.LastIndexOf('.');
        return dot < 0 || dot == t.Length - 1 || t[(dot + 1)..].Any(c => !char.IsLetterOrDigit(c));
    }

    /// <summary>
    /// Los de Inicio que encajan con el nombre, sin acentos ni mayúsculas: el exacto primero, luego los que empiezan
    /// por él y luego los que tienen todas sus palabras. Los desinstaladores no cuentan.
    /// </summary>
    public static List<string> Matching(IEnumerable<string> installed, string name)
    {
        var want = Extract.Fold(name.Trim());
        var words = want.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        return installed
            .Select(n => (Name: n, Folded: Extract.Fold(n)))
            .Where(a => words.All(a.Folded.Contains) && !a.Folded.Contains("uninstall") && !a.Folded.Contains("desinstal"))
            .GroupBy(a => a.Name).Select(g => g.First())
            .OrderBy(a => a.Folded == want ? 0 : a.Folded.StartsWith(want) ? 1 : 2)
            .ThenBy(a => a.Name.Length)
            .Select(a => a.Name)
            .ToList();
    }
}
