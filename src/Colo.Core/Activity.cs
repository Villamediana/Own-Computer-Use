namespace Colo.Core;

/// <summary>
/// Qué ventana tuvo delante el usuario y cuándo (últimas horas, solo en memoria): para «¿qué estaba haciendo?»
/// o «vuelve a lo de antes». Se alimenta mirando la ventana activa cada pocos segundos.
/// </summary>
public sealed class Activity
{
    sealed class Span(DateTime from, string window)
    {
        public DateTime From = from, To = from;
        public string Window = window;
    }

    public TimeSpan Keep { get; init; } = TimeSpan.FromHours(4);
    /// <summary>Lo que estuvo delante menos que esto (pasar de largo con alt+tab) no cuenta.</summary>
    public TimeSpan Glance { get; init; } = TimeSpan.FromSeconds(5);

    readonly List<Span> _spans = [];
    readonly object _lock = new();

    public void Seen(string window, DateTime now)
    {
        if (window.Length == 0) return;
        lock (_lock)
        {
            if (_spans.Count > 0 && _spans[^1].Window == window && now - _spans[^1].To < TimeSpan.FromMinutes(2))
                _spans[^1].To = now;
            else
                _spans.Add(new Span(now, window));
            var cut = now - Keep;
            _spans.RemoveAll(s => s.To < cut);
        }
    }

    public string Summary(DateTime now, int minutes)
    {
        minutes = Math.Clamp(minutes <= 0 ? 60 : minutes, 1, (int)Keep.TotalMinutes);
        var from = now - TimeSpan.FromMinutes(minutes);
        List<(DateTime From, DateTime To, string Window)> spans;
        lock (_lock)
            spans = _spans.Where(s => s.To >= from && s.To - s.From >= Glance).Select(s => (s.From < from ? from : s.From, s.To, s.Window)).ToList();
        // Lo mismo seguido tras quitar los vistazos es un solo rato.
        var merged = new List<(DateTime From, DateTime To, string Window)>();
        foreach (var s in spans)
            if (merged.Count > 0 && merged[^1].Window == s.Window) merged[^1] = (merged[^1].From, s.To, s.Window);
            else merged.Add(s);
        if (merged.Count == 0) return $"ok: nada registrado en los últimos {minutes} min";
        var lines = merged.TakeLast(40).Select(s => $"{s.From:HH:mm}–{s.To:HH:mm} ({Minutes(s.To - s.From)}) {s.Window}");
        var top = merged.GroupBy(s => s.Window).Select(g => (Window: g.Key, Time: g.Aggregate(TimeSpan.Zero, (t, s) => t + (s.To - s.From))))
            .OrderByDescending(x => x.Time).Take(5).Select(x => $"{x.Window} ({Minutes(x.Time)})");
        return $"ok: últimos {minutes} min (ahora {now:HH:mm})\n" + string.Join("\n", lines) + "\nMás tiempo en: " + string.Join(" · ", top);
    }

    static string Minutes(TimeSpan t) => t.TotalMinutes < 1 ? $"{t.TotalSeconds:0} s" : $"{t.TotalMinutes:0} min";
}
