using System;

namespace Colo.Core;

/// <summary>
/// Muelle amortiguado en forma cerrada: de 0 a 1 en función del tiempo, pasándose un poco y volviendo
/// (el «bounce»). Sin estado: cada cuadro se calcula desde cuándo empezó, así no depende del ritmo.
/// </summary>
public static class Spring
{
    /// <param name="ms">Tiempo desde el arranque.</param>
    /// <param name="period">Lo que tarda una oscilación; más corto = más vivo.</param>
    /// <param name="damping">0–1: bajo rebota más, 1 llega sin pasarse.</param>
    public static double Step(double ms, double period = 380, double damping = 0.5)
    {
        if (ms <= 0) return 0;
        var w = 2 * Math.PI / period;
        var z = Math.Clamp(damping, 0.05, 0.999);
        var wd = w * Math.Sqrt(1 - z * z);
        var decay = Math.Exp(-z * w * ms);
        return 1 - decay * (Math.Cos(wd * ms) + z * w / wd * Math.Sin(wd * ms));
    }

    /// <summary>Si ya se quedó quieto (para dejar de animar).</summary>
    public static bool Settled(double ms, double period = 380, double damping = 0.5) =>
        ms > 0 && Math.Exp(-Math.Clamp(damping, 0.05, 0.999) * 2 * Math.PI / period * ms) < 0.004;

    /// <summary>
    /// Un golpe que oscila y se apaga: empieza en 0, sube a ~1 y rebota alrededor de 0 (para «squash & stretch»).
    /// </summary>
    public static double Kick(double ms, double period = 380, double damping = 0.35)
    {
        if (ms <= 0) return 0;
        var w = 2 * Math.PI / period;
        var z = Math.Clamp(damping, 0.05, 0.999);
        var wd = w * Math.Sqrt(1 - z * z);
        // Normalizado para que el primer pico valga ~1.
        var peak = Math.Atan(Math.Sqrt(1 - z * z) / z) / wd;
        return Math.Exp(-z * w * ms) * Math.Sin(wd * ms) / (Math.Exp(-z * w * peak) * Math.Sin(wd * peak));
    }

    /// <summary>De a hasta b con el muelle.</summary>
    public static double Lerp(double a, double b, double ms, double period = 380, double damping = 0.5) =>
        a + (b - a) * Step(ms, period, damping);
}
