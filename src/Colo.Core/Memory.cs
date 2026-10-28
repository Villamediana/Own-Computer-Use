using System.Text.Json;

namespace Colo.Core;

/// <summary>
/// Lo que Colo sabe del usuario entre días (cómo llama a sus cosas, qué app usa para qué…). Lo decide el
/// modelo con remember/forget; se guarda en un JSON y va en cada petición.
/// </summary>
public sealed class Memory
{
    public const int MaxFacts = 40, MaxLength = 200;

    readonly string _path;
    readonly object _lock = new();
    List<string> _facts = [];

    public Memory(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _facts = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? []; }
        catch { _facts = []; }
    }

    public IReadOnlyList<string> Facts { get { lock (_lock) return [.. _facts]; } }

    public string Remember(string fact)
    {
        fact = fact.Trim();
        if (fact.Length == 0) return "error: fact vacío";
        if (fact.Length > MaxLength) fact = fact[..MaxLength];
        lock (_lock)
        {
            if (_facts.Contains(fact, StringComparer.OrdinalIgnoreCase)) return "ok: ya lo sabía";
            _facts.Add(fact);
            if (_facts.Count > MaxFacts) _facts.RemoveAt(0);
            Save();
        }
        return "ok: guardado";
    }

    /// <summary>Quita el recuerdo igual o el que contiene ese texto.</summary>
    public string Forget(string fact)
    {
        fact = fact.Trim();
        lock (_lock)
        {
            var i = _facts.FindIndex(f => string.Equals(f, fact, StringComparison.OrdinalIgnoreCase));
            if (i < 0 && fact.Length > 0) i = _facts.FindIndex(f => f.Contains(fact, StringComparison.OrdinalIgnoreCase));
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
