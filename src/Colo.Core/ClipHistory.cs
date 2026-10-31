namespace Colo.Core;

/// <summary>Lo último que copió el usuario (solo texto, solo en memoria): «pega lo que copié antes de eso».</summary>
public sealed class ClipHistory
{
    public const int Max = 15, MaxLength = 20_000;

    readonly List<(DateTime At, string Text)> _items = [];
    readonly object _lock = new();

    public void Add(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxLength) text = text[..MaxLength];
        lock (_lock)
        {
            _items.RemoveAll(i => i.Text == text);
            _items.Insert(0, (now, text));
            if (_items.Count > Max) _items.RemoveAt(_items.Count - 1);
        }
    }

    /// <summary>1 = lo último copiado, 2 = lo de antes…; null si no hay tantos.</summary>
    public string? Get(int n)
    {
        lock (_lock) return n >= 1 && n <= _items.Count ? _items[n - 1].Text : null;
    }

    public string List(DateTime now)
    {
        lock (_lock)
        {
            if (_items.Count == 0) return "ok: no he visto copiar nada desde que arrancó Colo";
            return "ok: lo último copiado (1 = lo más reciente; clipboard get con n lo trae entero)\n" + string.Join("\n", _items.Select((i, k) =>
            {
                var t = i.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (t.Length > 120) t = t[..120] + $"… ({i.Text.Length} letras)";
                var ago = now - i.At;
                var when = ago.TotalMinutes < 1 ? "ahora" : ago.TotalHours < 1 ? $"hace {ago.TotalMinutes:0} min" : $"{i.At:HH:mm}";
                return $"{k + 1}) {when} · {t}";
            }));
        }
    }
}
