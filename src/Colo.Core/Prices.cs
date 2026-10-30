namespace Colo.Core;

/// <summary>Precio en dólares: por millón de tokens (entrada, entrada en caché, salida) y por minuto de audio.</summary>
public sealed record Price(double Input = 0, double Cached = 0, double Output = 0, double PerMinute = 0)
{
    public double Cost(ModelUse u) =>
        (Math.Max(0, u.Input - u.Cached) * Input + u.Cached * Cached + u.Output * Output) / 1_000_000 + u.Seconds / 60 * PerMinute;
}

/// <summary>
/// Precios de lista (documentación de OpenAI y ElevenLabs, octubre de 2026). No hay una API pública de precios:
/// si tu contrato es otro o el modelo no está, se cambia en Ajustes › Costo (<see cref="Settings.Prices"/>).
/// </summary>
public static class Prices
{
    public const string AsOf = "octubre de 2026";

    public static readonly IReadOnlyDictionary<string, Price> Known = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-6-luna"] = new(Input: 0.10, Cached: 0.01, Output: 0.50),
        ["gpt-live-1"] = new(PerMinute: 0.05),
        ["gpt-4o-mini-transcribe"] = new(PerMinute: 0.003),
        ["gpt-4o-transcribe"] = new(PerMinute: 0.006),
        ["whisper"] = new(PerMinute: 0.006),
        ["scribe_v2"] = new(PerMinute: 0.22 / 60),
        ["scribe_v1"] = new(PerMinute: 0.22 / 60),
        [Local] = new(),
    };

    /// <summary>Whisper en el propio PC: no cuesta nada.</summary>
    public const string Local = "whisper (en tu PC)";

    public static Price? For(string model, IReadOnlyDictionary<string, Price>? mine) =>
        mine is not null && mine.TryGetValue(model, out var p) ? p : Known.TryGetValue(model, out var k) ? k : null;
}
