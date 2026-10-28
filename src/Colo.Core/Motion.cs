using System;

namespace Colo.Core;

/// <summary>
/// Cómo se mueve el ratón de Colo: tiempo según la distancia (como una mano, Fitts), curva de jerk
/// mínimo (arranca y frena suave) y un arco leve en lugar de una recta.
/// </summary>
public static class Motion
{
    /// <summary>Duración en ms de un deslizamiento de <paramref name="dist"/> píxeles físicos.</summary>
    public static double Duration(double dist)
    {
        if (dist < 2) return 0;
        return Math.Clamp(110 + 62 * Math.Log2(1 + dist / 28), 140, 420);
    }

    /// <summary>Jerk mínimo: velocidad 0 al principio y al final, sin tirones de aceleración.</summary>
    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * t * (10 + t * (-15 + 6 * t));
    }

    /// <summary>
    /// Punto en el instante t (0–1) del camino de (x0,y0) a (x1,y1): curva de Bézier cuadrática con el
    /// control desplazado a un lado, como el arco natural del brazo. El desvío crece con la distancia y se topa.
    /// Sin <paramref name="curved"/> va en recta (arrastres: lo de en medio también cuenta).
    /// </summary>
    public static (double X, double Y) At(double x0, double y0, double x1, double y1, double t, bool curved = true)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var k = Ease(t);
        if (dist < 1 || !curved) return (x0 + dx * k, y0 + dy * k);
        // Siempre hacia el mismo lado de la marcha (normal a la izquierda), para que se vea constante.
        var bend = Math.Min(dist * 0.08, 36);
        var cx = x0 + dx / 2 + dy / dist * bend;
        var cy = y0 + dy / 2 - dx / dist * bend;
        var u = 1 - k;
        return (u * u * x0 + 2 * u * k * cx + k * k * x1, u * u * y0 + 2 * u * k * cy + k * k * y1);
    }
}
