using System.Text.Json;

namespace Colo.Core;

/// <summary>Tokens de una respuesta del modelo (de <c>usage</c> en response.completed).</summary>
public sealed record Usage(long Input, long Cached, long Output)
{
    public long Total => Input + Output;
}

/// <summary>Cuenta los tokens gastados: hoy y desde siempre. Se guarda en un JSON propio.</summary>
public sealed class TokenMeter
{
    sealed class Saved
    {
        public string Day { get; set; } = "";
        public long DayInput { get; set; }
        public long DayOutput { get; set; }
        public long TotalInput { get; set; }
        public long TotalCached { get; set; }
        public long TotalOutput { get; set; }
    }

    readonly string _path;
    readonly object _lock = new();
    Saved _s = new();

    public TokenMeter(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) ?? new(); }
        catch { _s = new(); }
    }

    static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    public void Add(Usage u)
    {
        lock (_lock)
        {
            if (_s.Day != Today) { _s.Day = Today; _s.DayInput = _s.DayOutput = 0; }
            _s.DayInput += u.Input;
            _s.DayOutput += u.Output;
            _s.TotalInput += u.Input;
            _s.TotalCached += u.Cached;
            _s.TotalOutput += u.Output;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_s));
            }
            catch { }
        }
    }

    public long TodayTokens { get { lock (_lock) return _s.Day == Today ? _s.DayInput + _s.DayOutput : 0; } }
    public long AllTokens { get { lock (_lock) return _s.TotalInput + _s.TotalOutput; } }

    /// <summary>12.400 → «12,4 mil», 1.300.000 → «1,3 M».</summary>
    public static string Short(long n) => n switch
    {
        < 1_000 => n.ToString(),
        < 1_000_000 => $"{n / 1000.0:0.#} mil",
        _ => $"{n / 1_000_000.0:0.#} M",
    };
}
