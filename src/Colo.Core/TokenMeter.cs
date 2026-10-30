using System.Text.Json;

namespace Colo.Core;

/// <summary>Tokens de una respuesta del modelo (de <c>usage</c> en response.completed).</summary>
public sealed record Usage(long Input, long Cached, long Output, string Model = "")
{
    public long Total => Input + Output;
}

/// <summary>Lo usado de un modelo: tokens y segundos de audio (transcripción, GPT-Live).</summary>
public sealed class ModelUse
{
    public long Input { get; set; }
    public long Cached { get; set; }
    public long Output { get; set; }
    public double Seconds { get; set; }
    public long Calls { get; set; }
}

/// <summary>Cuenta lo gastado: hoy y desde siempre, también por modelo. Se guarda en un JSON propio.</summary>
public sealed class TokenMeter
{
    sealed class Saved
    {
        public Dictionary<string, ModelUse> Models { get; set; } = [];
        public Dictionary<string, ModelUse> DayModels { get; set; } = [];
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
            NewDay();
            _s.DayInput += u.Input;
            _s.DayOutput += u.Output;
            _s.TotalInput += u.Input;
            _s.TotalCached += u.Cached;
            _s.TotalOutput += u.Output;
            if (u.Model.Length > 0)
                foreach (var m in Both(u.Model))
                {
                    m.Input += u.Input;
                    m.Cached += u.Cached;
                    m.Output += u.Output;
                    m.Calls++;
                }
            Save();
        }
    }

    /// <summary>Segundos de audio de un modelo que cobra por minuto (transcripción, GPT-Live).</summary>
    public void AddAudio(string model, double seconds)
    {
        if (model.Length == 0 || seconds <= 0) return;
        lock (_lock)
        {
            NewDay();
            foreach (var m in Both(model))
            {
                m.Seconds += seconds;
                m.Calls++;
            }
            Save();
        }
    }

    void NewDay()
    {
        _s.Models ??= [];
        _s.DayModels ??= [];
        if (_s.Day == Today) return;
        _s.Day = Today;
        _s.DayInput = _s.DayOutput = 0;
        _s.DayModels.Clear();
    }

    IEnumerable<ModelUse> Both(string model)
    {
        foreach (var d in new[] { _s.Models, _s.DayModels })
        {
            if (!d.TryGetValue(model, out var m)) d[model] = m = new ModelUse();
            yield return m;
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_s));
        }
        catch { }
    }

    /// <summary>Copia de lo usado por modelo: hoy y desde siempre.</summary>
    public (Dictionary<string, ModelUse> Today, Dictionary<string, ModelUse> All) ByModel()
    {
        lock (_lock)
        {
            NewDay();
            static Dictionary<string, ModelUse> Copy(Dictionary<string, ModelUse> d) => d.ToDictionary(kv => kv.Key, kv => new ModelUse
            {
                Input = kv.Value.Input, Cached = kv.Value.Cached, Output = kv.Value.Output, Seconds = kv.Value.Seconds, Calls = kv.Value.Calls,
            });
            return (Copy(_s.DayModels), Copy(_s.Models));
        }
    }

    /// <summary>Dólares de hoy y de siempre con estos precios (los modelos sin precio no suman).</summary>
    public (double Today, double All) Cost(IReadOnlyDictionary<string, Price>? mine)
    {
        var (today, all) = ByModel();
        static double Sum(Dictionary<string, ModelUse> d, IReadOnlyDictionary<string, Price>? mine) =>
            d.Sum(kv => Prices.For(kv.Key, mine)?.Cost(kv.Value) ?? 0);
        return (Sum(today, mine), Sum(all, mine));
    }

    /// <summary>0,0042 → «US$ 0,0042»; 1,5 → «US$ 1,50».</summary>
    public static string Usd(double v) => "US$ " + (v < 0.01 && v > 0 ? v.ToString("0.0000") : v.ToString("0.00"));

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
