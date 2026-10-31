using System.Text.Json;

namespace Colo.Core;

/// <summary>
/// Un recuerdo. Where vacío = vale siempre; si no, solo va en los turnos cuya ventana, programa, URL o carpeta lo
/// contienen (lo del sistema de chamados no ocupa sitio mientras escribe en Word).
/// </summary>
public sealed record Fact(string Text, string Where = "");

/// <summary>
/// Lo que Colo sabe del usuario entre días (cómo llama a sus cosas, dónde está cada sistema, qué app usa para
/// qué…). Lo decide el modelo con remember/forget: si es nuevo, si corrige o completa uno viejo (replaces) y qué
/// dejar cuando se llena. Se guarda en un JSON.
/// </summary>
public sealed class Memory
{
    public const int MaxFacts = 60, MaxLength = 240;

    readonly string _path;
    readonly object _lock = new();
    List<Fact> _facts = [];

    public Memory(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                // Hasta la 1.13 era una lista de frases.
                _facts = json.TrimStart().StartsWith("[\"") || json.Trim() == "[]"
                    ? (JsonSerializer.Deserialize<List<string>>(json) ?? []).Select(t => new Fact(t)).ToList()
                    : JsonSerializer.Deserialize<List<Fact>>(json) ?? [];
            }
        }
        catch { _facts = []; }
    }

    /// <summary>El texto de todos (Ajustes los lista y deja olvidarlos).</summary>
    public IReadOnlyList<string> Facts { get { lock (_lock) return _facts.Select(f => f.Text).ToList(); } }

    public IReadOnlyList<Fact> All { get { lock (_lock) return [.. _facts]; } }

    /// <summary>Los que valen en este momento: los generales y los de este sitio (context = ventana, programa, URL…).</summary>
    public IReadOnlyList<Fact> For(string context)
    {
        var here = Extract.Fold(context);
        lock (_lock)
            return _facts.Where(f => f.Where.Length == 0 || here.Contains(Extract.Fold(f.Where))).ToList();
    }

    public string Remember(string fact, string where = "", string replaces = "")
    {
        fact = fact.Trim();
        where = where.Trim();
        if (fact.Length == 0) return "error: fact vacío";
        if (fact.Length > MaxLength) return $"error: más corto (máximo {MaxLength} letras): solo el dato";
        lock (_lock)
        {
            var old = replaces.Trim().Length > 0 ? Find(replaces) : -1;
            if (replaces.Trim().Length > 0 && old < 0) return $"error: no tenía «{replaces}» (mira «Lo que sabes del usuario»)";
            var same = _facts.FindIndex(f => string.Equals(f.Text, fact, StringComparison.OrdinalIgnoreCase));
            if (same >= 0 && same != old)
            {
                if (old >= 0) { _facts.RemoveAt(old); Save(); return "ok: ya lo sabía; quité el viejo"; }
                if (_facts[same].Where != where) { _facts[same] = _facts[same] with { Where = where }; Save(); }
                return "ok: ya lo sabía";
            }
            if (old >= 0)
            {
                _facts[old] = new Fact(fact, where);
                Save();
                return "ok: actualizado";
            }
            // Llena: el modelo decide qué sobra (junta o borra) en vez de perder el más viejo sin mirar.
            if (_facts.Count >= MaxFacts)
                return $"error: memoria llena ({MaxFacts}). Junta dos en uno (replaces) o forget lo que ya no sirva, y repite.";
            _facts.Add(new Fact(fact, where));
            Save();
        }
        return "ok: guardado";
    }

    int Find(string text)
    {
        text = text.Trim();
        var i = _facts.FindIndex(f => string.Equals(f.Text, text, StringComparison.OrdinalIgnoreCase));
        if (i < 0 && text.Length > 0) i = _facts.FindIndex(f => f.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
        return i;
    }

    /// <summary>Quita el recuerdo igual o el que contiene ese texto.</summary>
    public string Forget(string fact)
    {
        lock (_lock)
        {
            var i = Find(fact);
            if (i < 0) return "error: no tenía eso guardado";
            _facts.RemoveAt(i);
            Save();
        }
        return "ok: olvidado";
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_facts));
        }
        catch { }
    }
}
